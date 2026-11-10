using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Cleanup.SystemState;

namespace PcTidy.Features.Cleanup.Scanning;

/// <summary>Size-only measurement of a folder (for items Windows' own tools clean).</summary>
internal readonly record struct FolderMeasure(bool Exists, long Bytes, long Files, bool PartlyUnreadable);

/// <summary>Everything one Cleanup scan shares: context, clock, notes, the never-touch guard and the Deep tally.</summary>
internal sealed class ScanSession
{
    private readonly List<string> _notes = new();
    private long _unreadableFolders;

    public ScanSession(ScanContext context, ISystemState system, DateTime nowUtc)
    {
        Context = context;
        SystemInfo = system;
        NowUtc = nowUtc;
        Apps = new RunningApps(context.RunningProcesses);
        Guard = new NeverTouch(context.Locations);
    }

    public ScanContext Context { get; }
    public IKnownLocations Locations => Context.Locations;
    public bool IsDeep => Context.Depth == ScanDepth.Deep;
    public DateTime NowUtc { get; }
    public ISystemState SystemInfo { get; }
    public RunningApps Apps { get; }
    public NeverTouch Guard { get; }
    public DeepTally Deep { get; } = new();
    public IReadOnlyList<string> Notes => _notes;
    public long UnreadableFolders => _unreadableFolders;

    public void AddNote(string note)
    {
        if (!_notes.Contains(note)) _notes.Add(note);
    }

    /// <summary>"Untouched for N days": uses the newer of created/modified (last-access is unreliable on Windows).</summary>
    public bool IsOlderThan(FileEntry e, int days) => e.LastTouchedUtc <= NowUtc.AddDays(-days);

    /// <summary>Lists a cleanup root. Null when it doesn't exist or is itself a link (never followed).</summary>
    public FolderTree? Tree(string root, CancellationToken ct, int maxDepth = int.MaxValue)
    {
        if (!IsPlainFolder(root)) return null;
        var tree = FolderTree.Build(Context.Walker, root, maxDepth, IsExcluded, ct);
        _unreadableFolders += tree.Stats.Inaccessible;
        if (tree.RootUnreadable)
            AddNote($"Couldn't look inside {root} without admin rights, so it was skipped.");
        return tree;
    }

    public FolderMeasure Measure(string root, CancellationToken ct)
    {
        if (!IsPlainFolder(root)) return new FolderMeasure(false, 0, 0, false);
        var stats = Context.Walker.Walk(root, new WalkOptions { IncludeSystem = true }, e => !IsExcluded(e.FullPath), ct);
        return new FolderMeasure(true, stats.Bytes, stats.Files, stats.Inaccessible > 0);
    }

    /// <summary>Immediate sub-folders (links skipped).</summary>
    public IReadOnlyList<FileEntry> ChildFolders(string path, CancellationToken ct)
    {
        var list = new List<FileEntry>();
        if (!IsPlainFolder(path)) return list;
        Context.Walker.Walk(path, new WalkOptions { Recurse = false, IncludeSystem = false }, e =>
        {
            if (e.IsDirectory && !e.IsReparsePoint) list.Add(e);
            return false;
        }, ct);
        return list;
    }

    /// <summary>Immediate files of a folder.</summary>
    public IReadOnlyList<FileEntry> ChildFiles(string path, CancellationToken ct)
    {
        var list = new List<FileEntry>();
        if (!IsPlainFolder(path)) return list;
        Context.Walker.Walk(path, new WalkOptions { Recurse = false, IncludeSystem = true }, e =>
        {
            if (!e.IsDirectory) list.Add(e);
            return false;
        }, ct);
        return list;
    }

    /// <summary>A single known file as a walker-style entry (attributes only; content is never opened).</summary>
    public static FileEntry? EntryOf(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return null;
            return new FileEntry(fi.FullName, fi.Name, false, fi.Length, fi.Attributes,
                fi.CreationTimeUtc, fi.LastWriteTimeUtc, fi.LastAccessTimeUtc, 1);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>Adds "Chrome is open..." to a reason (and a scan note) when one of the apps is running.</summary>
    public string WithOpenApps(string reason, IReadOnlyList<string>? processes)
    {
        var open = Apps.OpenAmong(processes);
        if (open.Count == 0) return reason;
        var names = string.Join(" and ", open);
        var verb = open.Count == 1 ? "is" : "are";
        AddNote($"{names} {verb} open: close it first (check the system tray too), or its cache files will be skipped. PC Tidy never closes apps for you.");
        return $"{reason} {names} {verb} open right now: close it first, or this will be skipped.";
    }

    private bool IsExcluded(string path)
        => Context.Settings.ScanExclusions.Count > 0 && Context.Settings.ScanExclusions.Any(x => PathUtil.IsUnder(path, x));

    /// <summary>Exists, is a folder, and is not itself a junction/symlink (links are never followed).</summary>
    private static bool IsPlainFolder(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            return (attrs & FileAttributes.Directory) != 0 && (attrs & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
