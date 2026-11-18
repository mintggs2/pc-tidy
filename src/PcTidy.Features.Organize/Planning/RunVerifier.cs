using System.Collections.Concurrent;
using System.Diagnostics;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Organize.Detection;

namespace PcTidy.Features.Organize.Planning;

/// <summary>
/// The check around every Organize run (3.3, owner: "make sure no files are deleted or missing"). The shell calls it for
/// each confirmed real run that moves Organize files: the Organize page, Optimize everything, watch folders and one by one.
/// It only reads: names, sizes, dates, the file's identity on its drive and, for moves between drives, a fingerprint of
/// files up to <see cref="RunVerifier.HashLimit"/>. It never reads an online-only (OneDrive) file.
/// </summary>
public sealed class OrganizeMoveCheck(IFileWalker walker) : IMoveCheck
{
    private readonly ConcurrentDictionary<string, MoveCheckResult> _results = new(StringComparer.Ordinal);

    public bool Covers(ActionPlan plan) => RunVerifier.Covers(plan);

    public IMoveCheckSession? Before(ActionPlan plan, IProgress<string>? progress, CancellationToken ct)
    {
        var session = RunVerifier.Before(plan, walker, progress, ct);
        if (session is null) return null;
        session.Recorded = r => Remember(plan.RunId, r);
        return session;
    }

    public MoveCheckResult? ResultFor(string runId) => _results.TryGetValue(runId, out var r) ? r : null;

    private void Remember(string runId, MoveCheckResult result)
    {
        _results[runId] = result;
        if (_results.Count > 50) foreach (var old in _results.Keys.Order(StringComparer.Ordinal).Take(_results.Count - 50)) _results.TryRemove(old, out _);
    }
}

/// <summary>What was true right before the run: the files in the folders it touches, and each planned item's identity.</summary>
public sealed class MoveSession : IMoveCheckSession
{
    internal MoveSession(IFileWalker walker, IReadOnlyList<RunVerifier.Place> places, Dictionary<string, long>? files, int filesBefore,
        Dictionary<string, Planned> planned, int sizeOnly, int notFound)
    {
        _walker = walker;
        Places = places;
        _files = files;
        FilesBefore = filesBefore;
        _planned = planned;
        SizeOnly = sizeOnly;
        NotFound = notFound;
    }

    private readonly IFileWalker _walker;
    private readonly Dictionary<string, long>? _files; // null: too many to list (count only)
    private readonly Dictionary<string, Planned> _planned;
    /// <summary>Where each moved item ended up (filled by <see cref="After"/>, used by <see cref="AfterPutBack"/>).</summary>
    private readonly Dictionary<string, string> _finals = new(StringComparer.OrdinalIgnoreCase);

    internal Action<MoveCheckResult>? Recorded { get; set; }

    /// <summary>The folders the run touches: where the files are, and where they go.</summary>
    public IReadOnlyList<RunVerifier.Place> Places { get; }
    public int FilesBefore { get; }
    /// <summary>Files checked by size and date only (big files moved between drives, or unreadable ones).</summary>
    public int SizeOnly { get; }
    /// <summary>Planned files that were already gone when the run started.</summary>
    public int NotFound { get; }
    public int PlannedCount => _planned.Count;

    internal sealed record Planned(bool IsDirectory, long Size, DateTime LastWriteUtc, string? Hash, string? FileId,
        int TreeFiles, long TreeBytes, bool Quarantine);

    public MoveCheckResult After(RunRecord run, IProgress<string>? progress, CancellationToken ct)
    {
        var missing = new List<string>();
        var changed = new List<string>();
        var notes = new List<string>();
        var edited = new List<string>();
        var vanishedFirst = new List<string>();
        var unreadable = 0;
        var movedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // sources PC Tidy moved
        var movedFolders = new List<string>();
        var arrivedAt = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int quarantined = 0, done = 0;
        var steps = run.Steps.Where(s => s.Item.SourcePath is not null && _planned.ContainsKey(RunVerifier.Key(s.Item.SourcePath))).ToList();
        var tick = Stopwatch.StartNew();
        bool stopped = false;

        try
        {
            foreach (var step in steps)
            {
                ct.ThrowIfCancellationRequested();
                if (tick.ElapsedMilliseconds > 250) { progress?.Report($"Checking every moved file: {done:N0} of {steps.Count:N0}…"); tick.Restart(); }
                done++;
                var source = RunVerifier.Key(step.Item.SourcePath!);
                var was = _planned[source];
                var name = Path.GetFileName(source);
                if (step.Status == StepStatus.Done)
                {
                    if (was.IsDirectory) movedFolders.Add(source); else movedFiles.Add(source);
                    if (was.Quarantine) { quarantined += was.IsDirectory ? was.TreeFiles : 1; continue; } // the engine verified it into quarantine
                    var final = step.UndoData is { } u && u.TryGetValue(RunVerifier.FinalPathKey, out var f) && !string.IsNullOrEmpty(f) ? f : step.Item.DestinationPath;
                    if (final is null) { missing.Add($"{name}: PC Tidy doesn't know where it was moved."); continue; }
                    final = RunVerifier.Key(final);
                    _finals[source] = final;
                    if (was.IsDirectory) { CheckFolder(name, final, was, missing, notes, arrivedAt, ct); continue; }
                    arrivedAt.Add(final);
                    CheckFile(name, final, was, missing, changed, edited, ref unreadable, ct);
                }
                else if (step.Status is StepStatus.Skipped or StepStatus.Failed or StepStatus.Pending)
                {
                    // Not moved. If it's gone, the journal shows PC Tidy didn't move it ("It's no longer there"): a note.
                    if (!RunVerifier.Exists(source, was.IsDirectory)) vanishedFirst.Add(source);
                }
                else missing.Add($"{name}: PC Tidy couldn't tell whether it was moved.");
            }
        }
        catch (OperationCanceledException) { stopped = true; }

        int filesNow = 0;
        if (!stopped)
        {
            progress?.Report("Counting the files…");
            var listed = RunVerifier.List(Places, _walker, CancellationToken.None);
            filesNow = listed?.Count ?? RunVerifier.Count(Places, _walker, CancellationToken.None);
            if (_files is not null && listed is not null)
            {
                var gone = _files.Keys.Where(p => !listed.ContainsKey(p) && !movedFiles.Contains(p) && !movedFolders.Any(m => PathUtil.IsUnder(p, m))).ToList();
                var came = listed.Keys.Where(p => !_files.ContainsKey(p) && !arrivedAt.Contains(p)).ToList();
                if (gone.Count > 0) notes.Add($"{Files(gone.Count)} {Is(gone.Count)} gone, and PC Tidy's journal shows it didn't move {Them(gone.Count)} ({Names(gone)}). Another app or you removed or renamed {Them(gone.Count)} meanwhile.");
                if (came.Count > 0) notes.Add($"{Files(came.Count)} arrived meanwhile ({Names(came)}).");
            }
        }

        if (vanishedFirst.Count > 0) notes.Add($"{Files(vanishedFirst.Count)} {Is(vanishedFirst.Count)} gone before PC Tidy got to {Them(vanishedFirst.Count)}, so PC Tidy didn't move {Them(vanishedFirst.Count)} ({Names(vanishedFirst)}).");
        if (edited.Count > 0) notes.Add($"{Files(edited.Count)} {(edited.Count == 1 ? "was" : "were")} changed at {(edited.Count == 1 ? "its" : "their")} new place after the move, by you or an app ({Names(edited)}).");
        if (SizeOnly + unreadable > 0) notes.Add($"{Files(SizeOnly + unreadable)} {(SizeOnly + unreadable == 1 ? "was" : "were")} checked by size and date only.");
        if (NotFound > 0) notes.Add($"{Files(NotFound)} {Is(NotFound)} already gone when the run started.");
        if (quarantined > 0) notes.Add($"{Files(quarantined)} went to quarantine, where Undo can put {Them(quarantined)} back.");

        var problems = missing.Concat(changed).ToList();
        MoveCheckResult result;
        if (stopped)
        {
            int checkedCount = Math.Max(0, done - 1);
            var line = $"Check stopped after {checkedCount:N0} of {steps.Count:N0} moved files" + (problems.Count == 0 ? ", with no problem in those" : "");
            result = new MoveCheckResult(line, problems.Count == 0, problems, notes) { Stopped = true, Missing = missing.Count, Changed = changed.Count };
        }
        else
        {
            int filesAfter = filesNow + quarantined;
            var line = $"{FilesBefore:N0} file{(FilesBefore == 1 ? "" : "s")} before · {filesAfter:N0} after · {missing.Count:N0} missing"
                       + (changed.Count > 0 ? $" · {changed.Count:N0} changed" : "");
            result = new MoveCheckResult(line, problems.Count == 0, problems, notes) { Missing = missing.Count, Changed = changed.Count, FilesAfter = filesAfter };
        }
        Recorded?.Invoke(result);
        return result;
    }

    private static void CheckFile(string name, string final, Planned was, List<string> missing, List<string> changed, List<string> edited, ref int unreadable, CancellationToken ct)
    {
        var arrived = new FileInfo(FileIdentity.Io(final));
        if (!arrived.Exists) { Thread.Sleep(200); arrived.Refresh(); }
        if (!arrived.Exists) { missing.Add($"{name} isn't at its new place."); return; }
        bool editedSince = arrived.LastWriteTimeUtc != was.LastWriteUtc;

        // Same drive: the very same file (same identity), so its bytes can't differ unless it was edited.
        if (was.FileId is not null && FileIdentity.Id(final) == was.FileId)
        {
            if (arrived.Length != was.Size || editedSince) edited.Add(final);
            return;
        }
        bool same = arrived.Length == was.Size;
        if (same && was.Hash is { } hash && !CloudFile.IsPlaceholder(arrived.Attributes))
        {
            string? now = null;
            for (int attempt = 0; attempt < 2 && now is null; attempt++)
            {
                try { now = FileHash.Full(arrived.FullName, ct); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { if (attempt == 0) Thread.Sleep(300); }
            }
            if (now is null) { unreadable++; return; } // an app has it open: size and date matched
            same = string.Equals(now, hash, StringComparison.Ordinal);
        }
        if (same) return;
        if (editedSince) edited.Add(final); // a newer date: someone saved it after the move, not a damaged copy
        else changed.Add($"{name} isn't the same file at its new place.");
    }

    private void CheckFolder(string name, string final, Planned was, List<string> missing, List<string> notes, HashSet<string> arrivedAt, CancellationToken ct)
    {
        var tree = RunVerifier.Tree(final, _walker, ct);
        if (tree is null) { missing.Add($"The folder {name} isn't at its new place."); return; }
        foreach (var p in tree.Value.Paths) arrivedAt.Add(p);
        if (tree.Value.Files < was.TreeFiles) missing.Add($"The folder {name} has {was.TreeFiles - tree.Value.Files:N0} fewer file(s) at its new place.");
        else if (tree.Value.Files != was.TreeFiles || tree.Value.Bytes != was.TreeBytes) notes.Add($"The folder {name} changed at its new place after the move (files added or saved).");
    }

    public MoveCheckResult AfterPutBack(RunRecord run, UndoReport report, CancellationToken ct)
    {
        var problems = new List<string>();
        int back = 0, still = 0, lost = 0;
        foreach (var (source, final) in _finals)
        {
            ct.ThrowIfCancellationRequested();
            var was = _planned[source];
            var name = Path.GetFileName(source);
            if (RunVerifier.Exists(source, was.IsDirectory)) { back++; continue; }
            if (RunVerifier.Exists(final, was.IsDirectory))
            {
                still++;
                var why = report.Messages.FirstOrDefault(m => m.Contains(name, StringComparison.OrdinalIgnoreCase));
                problems.Add($"{name} is still at {Shorten(final)}." + (why is null ? "" : $" Undo said: {why}"));
            }
            else { lost++; problems.Add($"{name} wasn't found at its old place or its new one."); }
        }
        var line = $"{back:N0} put back" + (still > 0 ? $" · {still:N0} left at the new place" : "") + (lost > 0 ? $" · {lost:N0} not found" : "");
        var result = new MoveCheckResult(line, problems.Count == 0, problems, Array.Empty<string>()) { PutBack = true, AllAccountedFor = lost == 0, Missing = lost };
        Recorded?.Invoke(result);
        return result;
    }

    private string Shorten(string path)
    {
        var place = Places.Select(p => p.Folder).Where(f => PathUtil.IsUnder(path, f)).OrderBy(f => f.Length).FirstOrDefault();
        return place is null ? path : Path.Combine(Path.GetFileName(place), Path.GetRelativePath(place, path));
    }

    private static string Files(int n) => n == 1 ? "1 file" : $"{n:N0} files";
    private static string Is(int n) => n == 1 ? "is" : "are";
    private static string Them(int n) => n == 1 ? "it" : "them";
    private static string Names(IReadOnlyList<string> paths) =>
        string.Join(", ", paths.Take(3).Select(Path.GetFileName)) + (paths.Count > 3 ? $" and {paths.Count - 3:N0} more" : "");
}

/// <summary>Read-only helpers behind <see cref="OrganizeMoveCheck"/>.</summary>
public static class RunVerifier
{
    /// <summary>Files up to this size moved between drives get a fingerprint check too (same-drive moves are proved by identity).</summary>
    public const long HashLimit = 50L * 1024 * 1024;
    /// <summary>More files than this in the touched folders: they're counted, not listed one by one.</summary>
    public const int ListLimit = 250_000;

    internal const string FinalPathKey = "finalPath"; // the engine's UndoKeys.FinalPath

    /// <summary>A folder the run touches: only its own files, or (for a folder that moves whole) everything inside.</summary>
    public sealed record Place(string Folder, bool Recursive);

    /// <summary>The path as the plan and the walker spell it (no trimming of dots or spaces), for matching.</summary>
    internal static string Key(string path) => path.Length > 3 ? path.TrimEnd('\\') : path;

    internal static bool Exists(string path, bool folder) => folder ? Directory.Exists(FileIdentity.Io(path)) : File.Exists(FileIdentity.Io(path));

    public static bool Covers(ActionPlan plan) => plan.Items.Any(i => IsOrganize(i) && i.Kind is ActionKind.MovePath or ActionKind.RenamePath);

    private static bool IsOrganize(PlanItem i) => string.Equals(i.FeatureId, OrganizeModule.FeatureId, StringComparison.OrdinalIgnoreCase) && i.SourcePath is not null;

    /// <summary>Null when the plan has no Organize moves to check.</summary>
    public static MoveSession? Before(ActionPlan plan, IFileWalker walker, IProgress<string>? progress, CancellationToken ct)
    {
        if (!Covers(plan)) return null;
        var items = plan.Items.Where(i => IsOrganize(i) && i.Kind is ActionKind.MovePath or ActionKind.RenamePath or ActionKind.QuarantinePath).ToList();
        var planned = new Dictionary<string, MoveSession.Planned>(StringComparer.OrdinalIgnoreCase);
        var places = new List<Place>();
        int sizeOnly = 0, notFound = 0, n = 0;
        var tick = Stopwatch.StartNew();
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            if (tick.ElapsedMilliseconds > 250) { progress?.Report($"Noting every file first: {n:N0} of {items.Count:N0}…"); tick.Restart(); }
            n++;
            var source = Key(item.SourcePath!);
            bool quarantine = item.Kind == ActionKind.QuarantinePath;
            var dest = item.DestinationPath is null ? null : Key(item.DestinationPath);
            if (Path.GetDirectoryName(source) is { } srcFolder) places.Add(new Place(srcFolder, false));
            if (!quarantine && dest is not null && Path.GetDirectoryName(dest) is { } destFolder) places.Add(new Place(destFolder, false));

            if (item.IsDirectory || Directory.Exists(FileIdentity.Io(source)))
            {
                if (Tree(source, walker, ct) is not { } tree) { notFound++; continue; }
                places.Add(new Place(source, true));
                if (!quarantine && dest is not null) places.Add(new Place(dest, true));
                planned[source] = new(true, 0, default, null, null, tree.Files, tree.Bytes, quarantine);
                continue;
            }
            var info = new FileInfo(FileIdentity.Io(source));
            if (!info.Exists) { notFound++; continue; }
            string? hash = null, id = null;
            if (!quarantine && !CloudFile.IsPlaceholder(info.Attributes))
            {
                // Same drive and no link on the way: the move is a rename, and the file's identity proves it arrived intact.
                bool sameDrive = dest is not null && string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(dest), StringComparison.OrdinalIgnoreCase)
                                 && SafetyFolders.LinkOnTheWay(Path.GetDirectoryName(dest) ?? dest) is null;
                if (sameDrive) id = FileIdentity.Id(source);
                if (id is null)
                {
                    if (info.Length <= HashLimit)
                    {
                        try { hash = FileHash.Full(info.FullName, ct); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { sizeOnly++; }
                    }
                    else sizeOnly++;
                }
            }
            else if (!quarantine) sizeOnly++;
            planned[source] = new(false, info.Length, info.LastWriteTimeUtc, hash, id, 0, 0, quarantine);
        }
        if (planned.Count == 0) return null;

        var distinct = places.Distinct().ToList();
        progress?.Report("Counting the files…");
        var files = List(distinct, walker, ct);
        return new MoveSession(walker, distinct, files, files?.Count ?? Count(distinct, walker, ct), planned, sizeOnly, notFound);
    }

    /// <summary>Every file (path → size) in the places, links not followed; null when there are more than <see cref="ListLimit"/>.</summary>
    internal static Dictionary<string, long>? List(IReadOnlyList<Place> places, IFileWalker walker, CancellationToken ct)
    {
        var files = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        bool tooMany = false;
        foreach (var place in places)
        {
            if (tooMany) break;
            Walk(place, walker, ct, e =>
            {
                if (tooMany) return;
                files[Key(e.FullPath)] = e.Size;
                if (files.Count > ListLimit) tooMany = true;
            });
        }
        return tooMany ? null : files;
    }

    internal static int Count(IReadOnlyList<Place> places, IFileWalker walker, CancellationToken ct)
    {
        int n = 0;
        foreach (var place in places) Walk(place, walker, ct, _ => n++);
        return n; // a file in two overlapping places counts twice here; only used past ListLimit
    }

    private static void Walk(Place place, IFileWalker walker, CancellationToken ct, Action<FileEntry> onFile)
    {
        if (!Directory.Exists(FileIdentity.Io(place.Folder))) return;
        try
        {
            walker.Walk(place.Folder, new WalkOptions { Recurse = place.Recursive, IncludeHidden = true, IncludeSystem = true }, e =>
            {
                if (!e.IsDirectory) { onFile(e); return false; }
                return place.Recursive && !e.IsReparsePoint;
            }, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>A folder's files (count, bytes, paths); null when it doesn't exist.</summary>
    internal static (int Files, long Bytes, List<string> Paths)? Tree(string folder, IFileWalker walker, CancellationToken ct)
    {
        if (!Directory.Exists(FileIdentity.Io(folder))) return null;
        int n = 0; long bytes = 0;
        var paths = new List<string>();
        Walk(new Place(folder, true), walker, ct, e =>
        {
            n++; bytes += e.Size;
            if (paths.Count < ListLimit) paths.Add(Key(e.FullPath));
        });
        return (n, bytes, paths);
    }

    /// <summary>Files in the folder and its sub-folders (links are not followed).</summary>
    public static int CountFiles(string root, IFileWalker walker, CancellationToken ct) => Count(new[] { new Place(root, true) }, walker, ct);
}
