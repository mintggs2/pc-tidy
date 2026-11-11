using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using PcTidy.Core;
using PcTidy.Core.Model;

namespace PcTidy.UI.Flow.Resume;

/// <summary>What saving one section's results did: the file it went to and its size, or why it wasn't kept.</summary>
public sealed record SectionWrite(string? FileName, long Bytes, bool TooBig)
{
    public static readonly SectionWrite TooBigToKeep = new(null, 0, true);
    public static readonly SectionWrite Failed = new(null, 0, false);
    public bool Saved => FileName is not null;
}

/// <summary>
/// Where scan checkpoints are kept. One checkpoint per flow: a small header (what Home and the offers read) plus one
/// file per finished section's results. Saving the header replaces the flow's previous one.
/// </summary>
public interface IScanCheckpointStore
{
    /// <summary>The flow's checkpoint with every saved section's results, or null when there is none or any part of it can't be read (a damaged checkpoint is removed).</summary>
    ScanCheckpoint? Load(string flow);
    /// <summary>Only the flow's header (sections carry <see cref="CheckpointSection.ResultFile"/> but no results): cheap, for offers and notes.</summary>
    ScanCheckpoint? LoadHeader(string flow);
    /// <summary>Every readable checkpoint header, newest first.</summary>
    IReadOnlyList<ScanCheckpoint> LoadAll();
    /// <summary>
    /// Writes one section's results to their own file, giving up as soon as they pass <paramref name="maxBytes"/>
    /// (nothing that big is ever built in memory). The header doesn't point at it until <see cref="Save"/>.
    /// </summary>
    SectionWrite SaveSection(string flow, int index, ScanResult result, long maxBytes);
    /// <summary>
    /// Saves the header (atomically for the file store). A section that has results but no file yet gets one first.
    /// False when it couldn't be saved; the scan carries on regardless.
    /// </summary>
    bool Save(ScanCheckpoint checkpoint);
    void Delete(string flow);
}

/// <summary>The JSON setup: the run journal's (enums as text, nulls left out), without the computed totals of the scan records.</summary>
public static class ScanCheckpointJson
{
    public static readonly JsonSerializerOptions Options = new(AppSettings.Json)
    {
        WriteIndented = false,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { DropComputed } },
    };

    /// <summary>ScanResult.AllItems and ScanGroup.TotalBytes are worked out from the groups: saving them would double the file.</summary>
    private static void DropComputed(JsonTypeInfo info)
    {
        if (info.Kind != JsonTypeInfoKind.Object) return;
        string? drop = info.Type == typeof(ScanResult) ? nameof(ScanResult.AllItems) : info.Type == typeof(ScanGroup) ? nameof(ScanGroup.TotalBytes) : null;
        if (drop is null) return;
        for (int i = info.Properties.Count - 1; i >= 0; i--)
            if (info.Properties[i].Name == drop) info.Properties.RemoveAt(i);
    }

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    /// <summary>
    /// Serializes <paramref name="value"/> into <paramref name="output"/> (null = only count) in small chunks, and
    /// throws <see cref="CheckpointTooBigException"/> as soon as more than <paramref name="maxBytes"/> were written.
    /// Memory use stays at a chunk, whatever the size of the value. Returns the bytes written.
    /// </summary>
    public static long SerializeCapped<T>(T value, Stream? output, long maxBytes)
    {
        var sink = new CappedBufferWriter(output, maxBytes);
        using (var writer = new Utf8JsonWriter(sink, new JsonWriterOptions { Indented = false, SkipValidation = true }))
        {
            JsonSerializer.Serialize(writer, value, Options);
            writer.Flush();
        }
        return sink.Written;
    }

    /// <summary>Counts every chunk Utf8JsonWriter hands over and passes it on; stops at the cap.</summary>
    private sealed class CappedBufferWriter(Stream? output, long maxBytes) : IBufferWriter<byte>
    {
        private byte[] _buffer = new byte[16 * 1024];
        public long Written { get; private set; }

        public void Advance(int count)
        {
            Written += count;
            if (Written > maxBytes) throw new CheckpointTooBigException();
            output?.Write(_buffer, 0, count);
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            // A single huge string asks for room for its worst case (6 bytes a char); it can't fit if even 1 byte a char doesn't.
            if (sizeHint > (1 << 20) && (sizeHint - 16) / 6 > maxBytes - Written) throw new CheckpointTooBigException();
            if (sizeHint > _buffer.Length) _buffer = new byte[Math.Max(sizeHint, _buffer.Length * 2)];
            return _buffer;
        }

        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
    }
}

/// <summary>Thrown by <see cref="ScanCheckpointJson.SerializeCapped{T}"/> when a value passes its cap.</summary>
public sealed class CheckpointTooBigException() : Exception("Too big to keep for resuming.");

/// <summary>
/// The deep check every checkpoint read goes through (3.1.1 review L5). A well-formed file can still hold nulls where
/// the app expects values ("Sections":[null], "ScannerIds":null, a null group or item): any of them and the whole
/// checkpoint is ignored and removed, so a damaged or tampered file only ever costs a fresh scan, never a crash.
/// </summary>
public static class CheckpointValidation
{
    private static readonly Regex ResultFileName = new(@"^\d{1,4}-[0-9a-f]{8}\.json$", RegexOptions.CultureInvariant);

    public static bool IsResultFileName(string? name) => name is not null && ResultFileName.IsMatch(name);

    /// <summary>The header: key, titles, sections (no nulls, indices in range and distinct), samples, result file names.</summary>
    public static bool IsValidHeader(ScanCheckpoint? cp)
    {
        if (cp is null || cp.Format != ScanCheckpoint.CurrentFormat) return false;
        var k = cp.Key;
        if (k is null || k.Flow is null || k.DataRoot is null || k.AppVersion is null) return false;
        if (k.ScannerIds is null || k.ScannerIds.Any(s => s is null)) return false;
        if (k.Roots is null || k.Roots.Any(r => r is null)) return false;
        if (cp.Title is null || cp.NavigateTo is null || cp.Sections is null) return false;
        if (cp.TotalSections != k.ScannerIds.Count || cp.Sections.Count > cp.TotalSections) return false;
        var seen = new HashSet<int>();
        foreach (var s in cp.Sections)
        {
            if (s is null || s.FeatureId is null || s.Title is null || s.Samples is null) return false;
            if (s.Index < 0 || s.Index >= k.ScannerIds.Count || !seen.Add(s.Index)) return false;
            if (s.Samples.Any(x => x is null || x.Path is null)) return false;
            if (s.ResultFile is not null && !IsResultFileName(s.ResultFile)) return false;
            if (s.Result is not null && !IsValidResult(s.Result)) return false;
        }
        return true;
    }

    /// <summary>A section's results: groups, items and plan items all there, with the text the pages show.</summary>
    public static bool IsValidResult(ScanResult? r)
    {
        if (r is null || r.FeatureId is null || r.Title is null || r.Groups is null || r.Notes is null || r.Notes.Any(n => n is null)) return false;
        foreach (var g in r.Groups)
        {
            if (g is null || g.Id is null || g.Title is null || g.Items is null) return false;
            foreach (var i in g.Items)
            {
                if (i is null || i.Id is null || i.Title is null || i.Reason is null || i.Actions is null) return false;
                if (i.Details is not null && i.Details.Any(kv => kv.Key is null || kv.Value is null)) return false;
                if (i.RequiresClosedApps is not null && i.RequiresClosedApps.Any(a => a is null)) return false;
                foreach (var a in i.Actions)
                {
                    if (a is null || a.Id is null || a.FeatureId is null || a.Description is null) return false;
                    if (a.Args is not null && a.Args.Any(kv => kv.Key is null || kv.Value is null)) return false;
                    if (a.RequiresClosedApps is not null && a.RequiresClosedApps.Any(x => x is null)) return false;
                    if (a.DependsOn is not null && a.DependsOn.Any(x => x is null)) return false;
                }
            }
        }
        return true;
    }

    /// <summary>The header as saved: no results inside it (each section's results are in their own file).</summary>
    internal static ScanCheckpoint HeaderOnly(ScanCheckpoint cp)
        => cp with { Sections = cp.Sections.Select(s => s.Result is null ? s : s with { Result = null }).ToList() };

    internal static string NewResultFileName(int index) => $"{index}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}.json";
}

/// <summary>
/// Checkpoints in &lt;data folder&gt;\scan-checkpoints\: a small header &lt;flow&gt;.json (what Home and the offers
/// read) and each finished section's results in &lt;flow&gt;.sections\&lt;index&gt;-&lt;random&gt;.json. Every file is
/// written to a temp file, flushed and then renamed, and a section file gets a new name each time, so a crash
/// mid-write leaves the previous checkpoint (or none), never half a file or a header pointing at the wrong results.
/// Files the header no longer points at are removed after each save. A file that can't be read, is too big, is from
/// another format or fails <see cref="CheckpointValidation"/> makes the whole checkpoint ignored (and removed).
/// Nothing outside the folder is ever written.
/// </summary>
public sealed class FileScanCheckpointStore : IScanCheckpointStore
{
    /// <summary>All of one checkpoint's section files together are never more than this.</summary>
    public const long MaxFileBytes = 24L << 20;
    /// <summary>The header (titles, keys and up to 200 samples a section) is never read or written beyond this.</summary>
    public const long MaxHeaderBytes = 4L << 20;

    private readonly object _gate = new();

    public FileScanCheckpointStore(string dataRoot) => Folder = Path.Combine(dataRoot, "scan-checkpoints");

    public string Folder { get; }

    public string PathFor(string flow) => Path.Combine(Folder, SafeName(flow) + ".json");

    /// <summary>The folder of the flow's section files (inside <see cref="Folder"/>).</summary>
    public string SectionFolderFor(string flow) => Path.Combine(Folder, SafeName(flow) + ".sections");

    /// <summary>"cleanup", "combined", "privacy": letters, digits, '-' and '.' only, so a flow id can never leave the folder.</summary>
    public static string SafeName(string flow)
    {
        var chars = flow.Trim().ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' ? c : '-').ToArray();
        var name = new string(chars).Trim('.');
        return name.Length == 0 ? "flow" : name.Length > 60 ? name[..60] : name;
    }

    /// <summary>
    /// The checkpoint folder itself replaced by a junction/symlink: then PC Tidy neither reads, writes nor deletes anything
    /// there (deleting "unreadable" files would otherwise reach whatever the link points at). Resume is simply unavailable.
    /// </summary>
    private bool FolderIsLink => IsLink(Folder);

    public ScanCheckpoint? Load(string flow)
    {
        lock (_gate)
        {
            if (FolderIsLink) return null;
            var header = ReadHeader(PathFor(flow), flow);
            if (header is null) return null;
            var sections = new List<CheckpointSection>(header.Sections.Count);
            foreach (var s in header.Sections)
            {
                if (s.ResultFile is null) { sections.Add(s); continue; }
                var result = ReadSection(flow, s.ResultFile);
                if (result is null) { DeleteFlow(flow); return null; }
                sections.Add(s with { Result = result });
            }
            return header with { Sections = sections };
        }
    }

    public ScanCheckpoint? LoadHeader(string flow)
    {
        lock (_gate) return FolderIsLink ? null : ReadHeader(PathFor(flow), flow);
    }

    public IReadOnlyList<ScanCheckpoint> LoadAll()
    {
        lock (_gate)
        {
            try
            {
                if (!Directory.Exists(Folder) || FolderIsLink) return Array.Empty<ScanCheckpoint>();
                return Directory.EnumerateFiles(Folder, "*.json").ToList().Select(f => ReadHeader(f, null)).OfType<ScanCheckpoint>()
                    .OrderByDescending(c => c.UpdatedUtc).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<ScanCheckpoint>(); }
        }
    }

    private ScanCheckpoint? ReadHeader(string path, string? flow)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            if (info.Length is 0 or > MaxHeaderBytes) { DropHeader(path); return null; }
            var cp = JsonSerializer.Deserialize<ScanCheckpoint>(File.ReadAllBytes(path), ScanCheckpointJson.Options);
            if (!CheckpointValidation.IsValidHeader(cp) || cp!.Sections.Any(s => s.Result is not null)
                || (flow is not null && SafeName(cp.Key.Flow) != SafeName(flow))
                || !string.Equals(Path.GetFileName(path), SafeName(cp.Key.Flow) + ".json", StringComparison.OrdinalIgnoreCase))
            {
                DropHeader(path);
                return null;
            }
            return cp;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            // A damaged checkpoint only costs a fresh scan.
            DropHeader(path);
            return null;
        }
    }

    private ScanResult? ReadSection(string flow, string fileName)
    {
        try
        {
            if (!CheckpointValidation.IsResultFileName(fileName)) return null;
            var dir = SectionFolderFor(flow);
            if (IsLink(dir)) return null;
            var info = new FileInfo(Path.Combine(dir, fileName));
            if (!info.Exists || info.Length is 0 or > CheckpointRules.MaxSectionBytes) return null;
            var result = JsonSerializer.Deserialize<ScanResult>(File.ReadAllBytes(info.FullName), ScanCheckpointJson.Options);
            return CheckpointValidation.IsValidResult(result) ? result : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    public SectionWrite SaveSection(string flow, int index, ScanResult result, long maxBytes)
    {
        lock (_gate)
        {
            if (maxBytes <= 0) return SectionWrite.TooBigToKeep;
            var dir = SectionFolderFor(flow);
            var name = CheckpointValidation.NewResultFileName(index);
            var path = Path.Combine(dir, name);
            var tmp = path + ".tmp";
            try
            {
                Directory.CreateDirectory(Folder);
                if (FolderIsLink || IsLink(dir)) return SectionWrite.Failed; // never write through a link out of the folder
                Directory.CreateDirectory(dir);
                long bytes;
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024))
                {
                    bytes = ScanCheckpointJson.SerializeCapped(result, fs, Math.Min(maxBytes, CheckpointRules.MaxSectionBytes));
                    fs.Flush(flushToDisk: true);
                }
                File.Move(tmp, path, overwrite: false);
                return new SectionWrite(name, bytes, false);
            }
            catch (CheckpointTooBigException)
            {
                TryDelete(tmp);
                return SectionWrite.TooBigToKeep;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or JsonException or InvalidOperationException or ArgumentException)
            {
                TryDelete(tmp);
                return SectionWrite.Failed;
            }
        }
    }

    public bool Save(ScanCheckpoint checkpoint)
    {
        lock (_gate)
        {
            var flow = checkpoint.Key.Flow;
            // Results that have no file yet (a checkpoint built by hand) get one first; too big = not kept.
            var sections = new List<CheckpointSection>(checkpoint.Sections.Count);
            foreach (var s in checkpoint.Sections)
            {
                if (s.Result is null || s.ResultFile is not null) { sections.Add(s); continue; }
                var w = SaveSection(flow, s.Index, s.Result, CheckpointRules.MaxSectionBytes);
                sections.Add(w.Saved ? s with { ResultFile = w.FileName, ResultBytes = w.Bytes }
                    : s with { Result = null, Samples = Array.Empty<FileSample>(), NotSavedReason = w.TooBig ? "its results were too big to keep for resuming" : "its results couldn't be saved" });
            }
            var header = CheckpointValidation.HeaderOnly(checkpoint with { Sections = sections });
            var path = PathFor(flow);
            var tmp = path + ".tmp";
            try
            {
                Directory.CreateDirectory(Folder);
                if (FolderIsLink) return false;
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    ScanCheckpointJson.SerializeCapped(header, fs, MaxHeaderBytes);
                    fs.Flush(flushToDisk: true);
                }
                File.Move(tmp, path, overwrite: true);
                PruneSections(flow, header.Sections.Select(s => s.ResultFile).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase));
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or JsonException or CheckpointTooBigException or InvalidOperationException)
            {
                TryDelete(tmp);
                return false;
            }
        }
    }

    public void Delete(string flow)
    {
        lock (_gate) DeleteFlow(flow);
    }

    private void DeleteFlow(string flow)
    {
        if (FolderIsLink) return;
        var path = PathFor(flow);
        TryDelete(path);
        TryDelete(path + ".tmp");
        PruneSections(flow, keep: null);
    }

    /// <summary>A header that can't be used: it goes, and so do its section files.</summary>
    private void DropHeader(string path)
    {
        if (FolderIsLink) return;
        TryDelete(path);
        var name = Path.GetFileNameWithoutExtension(path);
        if (name.Length > 0) PruneSections(name, keep: null);
    }

    /// <summary>Removes section files the header doesn't point at (all of them when <paramref name="keep"/> is null).</summary>
    private void PruneSections(string flow, IReadOnlySet<string>? keep)
    {
        var dir = SectionFolderFor(flow);
        try
        {
            if (FolderIsLink || !Directory.Exists(dir)) return;
            if (IsLink(dir)) { Directory.Delete(dir, recursive: false); return; } // only the link itself, never what it points at
            // Only PC Tidy's own section files (and their .tmp), never anything else that might be in there.
            foreach (var f in Directory.EnumerateFiles(dir).ToList())
            {
                var name = Path.GetFileName(f);
                var own = CheckpointValidation.IsResultFileName(name)
                          || (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) && CheckpointValidation.IsResultFileName(name[..^4]));
                if (own && (keep is null || !keep.Contains(name))) TryDelete(f);
            }
            if (keep is null || keep.Count == 0) Directory.Delete(dir, recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* left for next time */ }
    }

    private static bool IsLink(string dir)
    {
        try { return Directory.Exists(dir) && (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* left for next time */ }
    }
}

/// <summary>Demo mode and tests: checkpoints in memory only (the demo writes nothing to disk), stored as the file store stores them.</summary>
public sealed class MemoryScanCheckpointStore : IScanCheckpointStore
{
    private readonly Dictionary<string, byte[]> _headers = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Flow, string File), byte[]> _sections = new();
    private readonly object _gate = new();

    private static string Name(string flow) => FileScanCheckpointStore.SafeName(flow);

    public ScanCheckpoint? Load(string flow)
    {
        lock (_gate)
        {
            var header = Header(flow);
            if (header is null) return null;
            var sections = new List<CheckpointSection>();
            foreach (var s in header.Sections)
            {
                if (s.ResultFile is null) { sections.Add(s); continue; }
                ScanResult? result = null;
                try
                {
                    if (_sections.TryGetValue((Name(flow), s.ResultFile), out var b))
                        result = JsonSerializer.Deserialize<ScanResult>(b, ScanCheckpointJson.Options);
                }
                catch (JsonException) { }
                if (!CheckpointValidation.IsValidResult(result)) { Remove(flow); return null; }
                sections.Add(s with { Result = result });
            }
            return header with { Sections = sections };
        }
    }

    public ScanCheckpoint? LoadHeader(string flow)
    {
        lock (_gate) return Header(flow);
    }

    private ScanCheckpoint? Header(string flow)
    {
        if (!_headers.TryGetValue(Name(flow), out var b)) return null;
        ScanCheckpoint? cp = null;
        try { cp = JsonSerializer.Deserialize<ScanCheckpoint>(b, ScanCheckpointJson.Options); }
        catch (JsonException) { }
        if (!CheckpointValidation.IsValidHeader(cp)) { Remove(flow); return null; }
        return cp;
    }

    public IReadOnlyList<ScanCheckpoint> LoadAll()
    {
        lock (_gate)
            return _headers.Keys.ToList().Select(Header).OfType<ScanCheckpoint>().OrderByDescending(c => c.UpdatedUtc).ToList();
    }

    public SectionWrite SaveSection(string flow, int index, ScanResult result, long maxBytes)
    {
        if (maxBytes <= 0) return SectionWrite.TooBigToKeep;
        try
        {
            using var ms = new MemoryStream();
            long bytes = ScanCheckpointJson.SerializeCapped(result, ms, Math.Min(maxBytes, CheckpointRules.MaxSectionBytes));
            var name = CheckpointValidation.NewResultFileName(index);
            lock (_gate) _sections[(Name(flow), name)] = ms.ToArray();
            return new SectionWrite(name, bytes, false);
        }
        catch (CheckpointTooBigException) { return SectionWrite.TooBigToKeep; }
    }

    /// <summary>Writes a header as it is, without checking it (tests use it to plant damaged checkpoints).</summary>
    public void SaveRaw(string flow, byte[] header)
    {
        lock (_gate) _headers[Name(flow)] = header;
    }

    public bool Save(ScanCheckpoint checkpoint)
    {
        var flow = checkpoint.Key.Flow;
        var sections = new List<CheckpointSection>();
        foreach (var s in checkpoint.Sections)
        {
            if (s.Result is null || s.ResultFile is not null) { sections.Add(s); continue; }
            var w = SaveSection(flow, s.Index, s.Result, CheckpointRules.MaxSectionBytes);
            sections.Add(w.Saved ? s with { ResultFile = w.FileName, ResultBytes = w.Bytes }
                : s with { Result = null, Samples = Array.Empty<FileSample>(), NotSavedReason = "its results were too big to keep for resuming" });
        }
        var header = CheckpointValidation.HeaderOnly(checkpoint with { Sections = sections });
        byte[] bytes;
        try
        {
            using var ms = new MemoryStream();
            ScanCheckpointJson.SerializeCapped(header, ms, FileScanCheckpointStore.MaxHeaderBytes);
            bytes = ms.ToArray();
        }
        catch (CheckpointTooBigException) { return false; }
        lock (_gate)
        {
            _headers[Name(flow)] = bytes;
            var keep = header.Sections.Select(s => s.ResultFile).OfType<string>().ToHashSet();
            foreach (var k in _sections.Keys.Where(k => k.Flow == Name(flow) && !keep.Contains(k.File)).ToList()) _sections.Remove(k);
        }
        return true;
    }

    public void Delete(string flow)
    {
        lock (_gate) Remove(flow);
    }

    private void Remove(string flow)
    {
        _headers.Remove(Name(flow));
        foreach (var k in _sections.Keys.Where(k => k.Flow == Name(flow)).ToList()) _sections.Remove(k);
    }

    /// <summary>How many checkpoints (flows) are saved.</summary>
    public int Count { get { lock (_gate) return _headers.Count; } }

    /// <summary>How many section files are saved, over all flows.</summary>
    public int SectionCount { get { lock (_gate) return _sections.Count; } }
}
