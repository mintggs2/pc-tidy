using System.Diagnostics;
using System.IO.Enumeration;
using System.Text;
using PcTidy.Core.Model;
using static PcTidy.Platform.NativeMethods;

namespace PcTidy.Platform;

/// <summary>
/// "Is this in use?" using Windows' Restart Manager (which apps hold the file open) plus an exclusive-open test.
/// Read-only: never changes, reads or downloads a file. Folders are checked through the files inside them
/// (up to <see cref="MaxFilesPerFolder"/>), never following junctions and skipping cloud placeholders for the open test.
/// </summary>
public sealed class RestartManagerInUseChecker : IInUseChecker
{
    /// <summary>Restart Manager slows down a lot with big sessions; Microsoft's samples batch a few hundred.</summary>
    internal const int BatchSize = 200;
    internal const int MaxFilesPerFolder = 2000;

    public Task<IReadOnlyList<InUseInfo>> CheckAsync(IReadOnlyList<string> paths, CancellationToken ct)
        => Task.Run(() => Check(paths, ct), ct);

    public bool IsLocked(string path)
    {
        try
        {
            var io = Literal(path);
            var attributes = File.GetAttributes(io);
            // Directories can't be opened this way; placeholders would download; reparse points would be followed.
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) return false;
            if (CloudFile.IsPlaceholder(attributes)) return false;

            using var _ = new FileStream(io, FileMode.Open, FileAccess.Read, FileShare.None, 1, FileOptions.None);
            return false;
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            // Missing, access denied, odd name: we can't tell, so it isn't reported as locked.
            return false;
        }
    }

    /// <summary>
    /// The literal extended form (<c>\\?\D:\...</c>): probes look at exactly the name they were given, like the
    /// SafetyGuard (no trimming of trailing dots or spaces). Restart Manager itself gets the plain form.
    /// </summary>
    internal static string Literal(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)) return path;
        var p = path.Length > 3 ? path.TrimEnd('\\') : path;
        return p.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + p[2..] : @"\\?\" + p;
    }

    private static string Plain(string path)
        => path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + path[8..]
           : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;

    private static bool IsSharingViolation(IOException ex)
    {
        var code = ex.HResult & 0xFFFF;
        return code is 32 or 33; // ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION
    }

    private IReadOnlyList<InUseInfo> Check(IReadOnlyList<string> paths, CancellationToken ct)
    {
        var units = paths.Select(p => new Unit(p, ExpandToFiles(p, ct))).ToList();

        // First pass: big batches. Only batches where Restart Manager finds something are re-checked per path,
        // because RmGetList answers for the whole session, not per file.
        var appsByUnit = new Dictionary<Unit, HashSet<string>>();
        foreach (var batch in Batch(units))
        {
            ct.ThrowIfCancellationRequested();
            var apps = QueryApps(batch.SelectMany(u => u.Files));
            if (apps.Count == 0) continue;
            if (batch.Count == 1) { appsByUnit[batch[0]] = apps; continue; }
            foreach (var unit in batch)
            {
                ct.ThrowIfCancellationRequested();
                var unitApps = QueryApps(unit.Files);
                if (unitApps.Count > 0) appsByUnit[unit] = unitApps;
            }
        }

        var results = new List<InUseInfo>(units.Count);
        foreach (var unit in units)
        {
            ct.ThrowIfCancellationRequested();
            var apps = appsByUnit.TryGetValue(unit, out var a) ? a.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList() : new List<string>();
            // Restart Manager can't see processes in other sessions or services when not elevated; the
            // exclusive-open test catches those for plain files.
            var locked = apps.Count == 0 && unit.IsFile && IsLocked(unit.Path);
            results.Add(new InUseInfo(unit.Path, apps.Count > 0 || locked, apps));
        }
        return results;
    }

    private sealed record Unit(string Path, IReadOnlyList<string> Files)
    {
        public bool IsFile => Files.Count == 1 && ReferenceEquals(Files[0], Path);
    }

    private static IReadOnlyList<string> ExpandToFiles(string path, CancellationToken ct)
    {
        try
        {
            var attributes = File.GetAttributes(Literal(path));
            if ((attributes & FileAttributes.Directory) == 0) return new[] { path };
            if ((attributes & FileAttributes.ReparsePoint) != 0) return Array.Empty<string>(); // never follow junctions
            return EnumerateFilesNoReparse(Literal(path), ct).Take(MaxFilesPerFolder).Select(Plain).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> EnumerateFilesNoReparse(string root, CancellationToken ct)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = 0 };
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            List<(string Path, bool IsDir, bool IsReparse)> entries;
            try
            {
                entries = new FileSystemEnumerable<(string, bool, bool)>(dir,
                    (ref FileSystemEntry e) => (e.ToFullPath(), e.IsDirectory, (e.Attributes & FileAttributes.ReparsePoint) != 0),
                    options).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var (p, isDir, isReparse) in entries)
            {
                if (isReparse) continue;
                if (isDir) stack.Push(p);
                else yield return p;
            }
        }
    }

    /// <summary>Groups units so each Restart Manager session has at most <see cref="BatchSize"/> files.</summary>
    private static IEnumerable<List<Unit>> Batch(IEnumerable<Unit> units)
    {
        var batch = new List<Unit>();
        var count = 0;
        foreach (var u in units.Where(u => u.Files.Count > 0))
        {
            if (batch.Count > 0 && count + u.Files.Count > BatchSize)
            {
                yield return batch;
                batch = new List<Unit>();
                count = 0;
            }
            batch.Add(u);
            count += u.Files.Count;
        }
        if (batch.Count > 0) yield return batch;
    }

    /// <summary>Display names of apps/services holding any of the files open (empty if none or on error).</summary>
    internal static HashSet<string> QueryApps(IEnumerable<string> files)
    {
        var apps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in files.Select(Plain).Chunk(BatchSize))
            foreach (var app in QueryChunk(chunk)) apps.Add(app);
        return apps;
    }

    private static IEnumerable<string> QueryChunk(string[] files)
    {
        var key = new StringBuilder(CCH_RM_SESSION_KEY + 1);
        if (RmStartSession(out var session, 0, key) != ERROR_SUCCESS) return Array.Empty<string>();
        try
        {
            if (RmRegisterResources(session, (uint)files.Length, files, 0, null, 0, null) != ERROR_SUCCESS)
                return Array.Empty<string>();

            // The list can grow between the two calls; retry a few times.
            uint count = 0, reasons = 0;
            RM_PROCESS_INFO[]? infos = null;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var rc = RmGetList(session, out var needed, ref count, infos, ref reasons);
                if (rc == ERROR_SUCCESS)
                    return (infos ?? Array.Empty<RM_PROCESS_INFO>()).Take((int)count).Select(DisplayName).Where(n => n.Length > 0).ToList();
                if (rc != ERROR_MORE_DATA) return Array.Empty<string>();
                infos = new RM_PROCESS_INFO[needed];
                count = needed;
            }
            return Array.Empty<string>();
        }
        finally
        {
            RmEndSession(session);
        }
    }

    private static string DisplayName(RM_PROCESS_INFO info)
    {
        if (!string.IsNullOrWhiteSpace(info.strAppName)) return info.strAppName.Trim();
        if (!string.IsNullOrWhiteSpace(info.strServiceShortName)) return info.strServiceShortName.Trim();
        try
        {
            using var p = Process.GetProcessById(info.Process.dwProcessId);
            return p.ProcessName;
        }
        catch
        {
            return "";
        }
    }
}
