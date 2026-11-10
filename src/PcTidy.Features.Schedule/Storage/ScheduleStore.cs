using System.Globalization;
using System.Text;
using System.Text.Json;
using PcTidy.Features.Schedule.DiskHealth;

namespace PcTidy.Features.Schedule.Storage;

/// <summary>
/// The ONLY code in this feature that writes, and only PC Tidy's own data inside the data folder:
///   &lt;DataRoot&gt;\reports\            run, scan and checkup reports (.md, and .html if chosen)
///   &lt;DataRoot&gt;\schedule\           metrics-history.json, disk-health.json, scan-history.json
/// Never user files; never outside those two folders (every path is checked before writing). Writes are atomic
/// (temp file, then replace, keeping the previous file as .bak) so a power cut never leaves half a history.
/// Histories are capped so they stay small. Copies into a folder the user picked (e.g. a vault) are NOT done here:
/// they are WriteTextFile plan items.
///
/// The app and the scheduled-scan process (a second PcTidy.exe) both append to the same histories (review CR-14), so
/// every read-modify-write holds a named mutex for this data folder, across processes. A history that can't be read
/// is never rewritten: a busy file is retried and then the append fails (the caller carries on without it), and a
/// damaged file falls back to its .bak, or is set aside under a new name before a fresh one starts. History is never
/// silently thrown away.
/// </summary>
public sealed class ScheduleStore
{
    /// <summary>Caps that keep each history small (about 10 years of daily samples, or a few thousand runs).</summary>
    public int MaxMetricsEntries { get; init; } = 3000;
    public int MaxDiskSamples { get; init; } = 800;
    public int MaxScanSummaries { get; init; } = 400;

    /// <summary>How long an append waits for the other PC Tidy process to finish its own append.</summary>
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>Tries for a file another program briefly holds open (antivirus, the indexer, the other PC Tidy).</summary>
    public int IoAttempts { get; init; } = 4;
    public TimeSpan IoRetryDelay { get; init; } = TimeSpan.FromMilliseconds(150);

    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private readonly object _gate = new();

    public ScheduleStore(string dataRoot)
    {
        DataRoot = PathUtil.Normalize(dataRoot);
        ReportsFolder = DataPaths.Reports(DataRoot);
        OwnFolder = Path.Combine(DataRoot, Schedules.ScheduleIds.FeatureId);
        MutexName = HistoryMutexName(DataRoot);
    }

    public string DataRoot { get; }
    public string ReportsFolder { get; }
    /// <summary>&lt;DataRoot&gt;\schedule</summary>
    public string OwnFolder { get; }

    private string MetricsFile => Path.Combine(OwnFolder, "metrics-history.json");
    private string DiskFile => Path.Combine(OwnFolder, "disk-health.json");
    private string ScanFile => Path.Combine(OwnFolder, "scan-history.json");

    // ------------------------------------------------------------------ reports

    /// <summary>Saves a report; returns the Markdown path (and the HTML path when given). Never overwrites another report.</summary>
    public (string Markdown, string? Html) SaveReport(DateTime timeUtc, string slug, string markdown, string? html)
    {
        var stamp = timeUtc.ToLocalTime().ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture);
        var baseName = $"{stamp} {SafeSlug(slug)}";
        lock (_gate)
        {
            // Under the same cross-process lock, so two processes saving in the same minute never pick the same name.
            (string, string?) saved = default;
            WithHistoryLock(() =>
            {
                var mdPath = PathUtil.NextFreeName(Path.Combine(ReportsFolder, baseName + ".md"));
                WriteAtomic(mdPath, markdown);
                string? htmlPath = null;
                if (html is not null)
                {
                    htmlPath = Path.ChangeExtension(mdPath, ".html");
                    if (File.Exists(htmlPath)) htmlPath = PathUtil.NextFreeName(htmlPath);
                    WriteAtomic(htmlPath, html);
                }
                saved = (mdPath, htmlPath);
            });
            return saved;
        }
    }

    /// <summary>Newest first. Reads only the first heading of each report for its title.</summary>
    public IReadOnlyList<ReportInfo> ListReports(int max = 50)
    {
        try
        {
            if (!Directory.Exists(ReportsFolder)) return Array.Empty<ReportInfo>();
            return new DirectoryInfo(ReportsFolder).EnumerateFiles("*.md")
                .Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(max)
                .Select(f =>
                {
                    var html = Path.ChangeExtension(f.FullName, ".html");
                    var (title, kind) = ReadHeading(f.FullName);
                    return new ReportInfo(f.FullName, File.Exists(html) ? html : null, f.LastWriteTimeUtc, title, kind);
                })
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<ReportInfo>(); }
    }

    // ------------------------------------------------------------------ histories

    public void AppendMetrics(MetricsHistoryEntry entry) => Append(MetricsFile, entry, MaxMetricsEntries);
    public IReadOnlyList<MetricsHistoryEntry> LoadMetrics() => Load<MetricsHistoryEntry>(MetricsFile);

    public void AppendDiskHealth(DiskHealthSample sample) => Append(DiskFile, sample, MaxDiskSamples);
    public IReadOnlyList<DiskHealthSample> LoadDiskHealth() => Load<DiskHealthSample>(DiskFile);

    public void AppendScanSummary(ScanSummary summary) => Append(ScanFile, summary, MaxScanSummaries);
    public IReadOnlyList<ScanSummary> LoadScanSummaries() => Load<ScanSummary>(ScanFile);

    /// <summary>True when there's no disk sample yet for today (local date): the daily sample is due.</summary>
    public bool DiskSampleDueToday(DateTime nowUtc)
        => !LoadDiskHealth().Any(s => s.TimeUtc.ToLocalTime().Date == nowUtc.ToLocalTime().Date);

    public bool DailyMetricsDueToday(DateTime nowUtc)
        => !LoadMetrics().Any(m => m.Kind == SampleKind.Daily && m.TimeUtc.ToLocalTime().Date == nowUtc.ToLocalTime().Date);

    /// <summary>
    /// Load, add, cap and write back, all under the cross-process history lock. Throws <see cref="IOException"/> when the
    /// lock or the file can't be had (the history is then left exactly as it was); callers treat history as optional.
    /// </summary>
    private void Append<T>(string file, T item, int cap)
    {
        lock (_gate)
            WithHistoryLock(() =>
            {
                var list = ReadForUpdate<T>(file);
                list.Add(item);
                if (list.Count > cap) list = list.Skip(list.Count - cap).ToList();
                WriteAtomic(file, JsonSerializer.Serialize(list, AppSettings.Json), keepBackup: true);
            });
    }

    /// <summary>For display and "is it due?": the history, or its .bak if the main file can't be read, or empty. Never writes.</summary>
    private IReadOnlyList<T> Load<T>(string file)
    {
        lock (_gate)
        {
            if (TryRead<T>(file, out var list, out _)) return list;
            return TryRead<T>(BackupOf(file), out var backup, out _) ? backup : Array.Empty<T>();
        }
    }

    /// <summary>
    /// The current list for an append. Missing = empty. Busy or locked after the retries = throw, so nothing is overwritten.
    /// Damaged JSON = the .bak if it reads; otherwise the damaged file is kept under a new name and a new history starts.
    /// </summary>
    private List<T> ReadForUpdate<T>(string file)
    {
        if (TryRead<T>(file, out var list, out var problem)) return list;
        if (problem is ReadProblem.Busy)
            throw new IOException("The history file is in use by another program right now, so it was left as it was.");
        if (File.Exists(BackupOf(file)) && TryRead<T>(BackupOf(file), out var backup, out _)) return backup;
        SetAside(file);
        return new List<T>();
    }

    private enum ReadProblem { None, Busy, Damaged }

    private bool TryRead<T>(string file, out List<T> list, out ReadProblem problem)
    {
        list = new List<T>();
        problem = ReadProblem.None;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (!File.Exists(file)) return true;
                list = JsonSerializer.Deserialize<List<T>>(File.ReadAllText(file), AppSettings.Json) ?? new List<T>();
                return true;
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                problem = ReadProblem.Damaged;
                return false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= Math.Max(1, IoAttempts)) { problem = ReadProblem.Busy; return false; }
                Thread.Sleep(IoRetryDelay);
            }
        }
    }

    /// <summary>Keeps a damaged history under "&lt;name&gt;.damaged-&lt;time&gt;.json" (inside the own folder) so nothing is lost.</summary>
    private void SetAside(string file)
    {
        var aside = PathUtil.NextFreeName(Path.Combine(Path.GetDirectoryName(file)!,
            $"{Path.GetFileNameWithoutExtension(file)}.damaged-{DateTime.UtcNow:yyyyMMddHHmmss}.json"));
        EnsureOwn(aside);
        Retry(() => File.Move(file, aside)); // boundary-ok: own data in DataRoot
    }

    private static string BackupOf(string file) => file + ".bak";

    // ------------------------------------------------------------------ cross-process lock

    /// <summary>"Local\PCTidy-schedule-&lt;hash of the data folder&gt;": one lock per data folder, shared by every PC Tidy process of this session.</summary>
    internal string MutexName { get; }

    internal static string HistoryMutexName(string dataRoot)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(PathUtil.Normalize(dataRoot).ToUpperInvariant()));
        return @"Local\PCTidy-schedule-" + Convert.ToHexString(hash, 0, 8);
    }

    private void WithHistoryLock(Action body)
    {
        using var mutex = new Mutex(false, MutexName);
        var owned = false;
        try
        {
            try { owned = mutex.WaitOne(LockTimeout); }
            catch (AbandonedMutexException) { owned = true; } // the other process ended mid-append: its write was atomic, carry on
            if (!owned) throw new IOException("Another PC Tidy window is updating the history right now, so this entry wasn't added.");
            body();
        }
        finally
        {
            if (owned) mutex.ReleaseMutex();
        }
    }

    // ------------------------------------------------------------------ writing

    private void EnsureOwn(string full)
    {
        if (!PathUtil.IsUnder(full, ReportsFolder) && !PathUtil.IsUnder(full, OwnFolder))
            throw new InvalidOperationException("Schedule & reports only writes inside its own data folders.");
    }

    /// <summary>Temp file, then replace. With <paramref name="keepBackup"/> the previous file becomes "&lt;file&gt;.bak".</summary>
    private void WriteAtomic(string path, string content, bool keepBackup = false)
    {
        var full = PathUtil.Normalize(path);
        EnsureOwn(full);
        var folder = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(folder); // boundary-ok: own data in DataRoot
        var tmp = full + ".tmp";
        Retry(() => File.WriteAllText(tmp, content, Utf8NoBom)); // boundary-ok: own data in DataRoot
        if (keepBackup && File.Exists(full))
            Retry(() => File.Replace(tmp, full, BackupOf(full), ignoreMetadataErrors: true)); // boundary-ok: own data in DataRoot
        else
            Retry(() => File.Move(tmp, full, overwrite: true)); // boundary-ok: own data in DataRoot
    }

    /// <summary>Retries a short file operation another program may briefly block; the last failure is thrown.</summary>
    private void Retry(Action op)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { op(); return; }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < Math.Max(1, IoAttempts))
            {
                Thread.Sleep(IoRetryDelay);
            }
        }
    }

    private static string SafeSlug(string slug)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(slug.Select(c => invalid.Contains(c) ? ' ' : c).ToArray()).Trim().TrimEnd('.');
        if (clean.Length > 60) clean = clean[..60].TrimEnd();
        return clean.Length == 0 ? "report" : clean;
    }

    private static (string Title, ReportKind Kind) ReadHeading(string path)
    {
        try
        {
            string? title = null;
            var kind = ReportKind.Run;
            foreach (var line in File.ReadLines(path).Take(20))
            {
                if (line.StartsWith("pctidy-kind:", StringComparison.Ordinal))
                    kind = Enum.TryParse<ReportKind>(line["pctidy-kind:".Length..].Trim(), true, out var k) ? k : kind;
                if (line.StartsWith("# ", StringComparison.Ordinal)) { title = System.Text.RegularExpressions.Regex.Replace(line[2..].Trim(), @"\\(.)", "$1"); break; }
            }
            return (title ?? Path.GetFileNameWithoutExtension(path), kind);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (Path.GetFileNameWithoutExtension(path), ReportKind.Run); }
    }
}
