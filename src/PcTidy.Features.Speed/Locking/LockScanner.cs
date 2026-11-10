using System.Diagnostics;
using System.IO.Enumeration;
using PcTidy.Features.Speed.Common;
using PcTidy.Features.Speed.Sources;

namespace PcTidy.Features.Speed.Locking;

/// <summary>
/// "What's locking this file?" (approved feature 5): for the file or folder in <see cref="ScanContext.Roots"/>,
/// asks Windows' Restart Manager which apps have it open (IInUseChecker, batched for folders) and offers to ask
/// those apps to close, politely. Never system processes, services or security software, and never a forced kill.
/// </summary>
public sealed class LockScanner : IFeatureScanner
{
    public const string ScannerId = "speed.locking";
    public const string HoldersGroup = "locking-apps";
    /// <summary>Folders are checked through at most this many files inside them (never following junctions).</summary>
    public const int MaxFilesPerFolder = 2000;

    private readonly Func<ScanContext, SpeedEnvironment> _environment;

    public LockScanner(Func<ScanContext, SpeedEnvironment>? environment = null) => _environment = environment ?? SpeedEnvironment.For;

    public string FeatureId => ScannerId;
    public string Title => "What's locking this?";
    public Release Release => Release.V2;
    public bool IncludedInOptimizeAll => false;

    public async Task<ScanResult> ScanAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var env = _environment(ctx);
        var targets = (ctx.Roots ?? Array.Empty<string>()).Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
        var notes = new List<string>();
        if (targets.Count == 0)
            return Result(ctx, sw, Array.Empty<ScanItem>(), new[] { "Pick a file or folder to see which apps are using it." });

        progress?.Report(new ScanProgress("Asking Windows which apps have it open…", 0.2));
        var verdicts = await ctx.InUse.CheckAsync(targets, ct).ConfigureAwait(false);
        var inUse = verdicts.Where(v => v.InUse).Select(v => v.Path).ToList();
        if (inUse.Count == 0)
        {
            notes.Add(targets.Count == 1 ? $"Nothing is using {Path.GetFileName(targets[0].TrimEnd('\\'))} right now. You can move, rename or delete it." : "Nothing is using these right now.");
            return Result(ctx, sw, Array.Empty<ScanItem>(), notes);
        }

        progress?.Report(new ScanProgress("Finding the apps…", 0.6));
        var files = await Task.Run(() => inUse.SelectMany(p => FilesOf(p, ct)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), ct).ConfigureAwait(false);
        var holders = await Task.Run(() => env.Locks.Find(files, ct), ct).ConfigureAwait(false);
        var session = env.Processes.CurrentSessionId;
        var running = holders.Count == 0 ? Array.Empty<ProcessSnapshot>() : env.Processes.Snapshot();
        var items = holders.Select(h => HolderItem(h, session, running, env.Files, ctx.Locations.WindowsDir)).ToList();
        if (items.Count == 0)
        {
            var apps = verdicts.SelectMany(v => v.Apps).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            items.Add(new ScanItem
            {
                Id = "speed.locking:unknown",
                Title = apps.Count > 0 ? string.Join(", ", apps) : "Something PC Tidy can't see",
                Reason = "It's in use, but by a service, another account, or an app running as admin, so PC Tidy can't tell which one. Restarting the PC frees it.",
                Badge = Badge.Check,
                DisabledReason = "PC Tidy can only close apps it can see in your session.",
            });
        }
        progress?.Report(new ScanProgress("Done", 1));
        return Result(ctx, sw, items, notes);
    }

    private ScanResult Result(ScanContext ctx, Stopwatch sw, IReadOnlyList<ScanItem> items, IReadOnlyList<string> notes) => new()
    {
        FeatureId = ScannerId, Title = Title, Depth = ctx.Depth, Duration = sw.Elapsed, Notes = notes,
        Groups = items.Count == 0 ? Array.Empty<ScanGroup>() : new[]
        {
            new ScanGroup
            {
                Id = HoldersGroup, Title = "Apps using it", Badge = Badge.Check, Items = items,
                Description = "Close asks the app to close, like clicking its X. Save your work in it first.",
            },
        },
    };

    internal static ScanItem HolderItem(LockHolder h, int currentSession, IReadOnlyList<ProcessSnapshot> running, IFileFactsProvider files, string windowsDir)
    {
        var path = running.FirstOrDefault(p => p.Pid == h.Pid)?.ImagePath;
        var facts = path is null ? FileFacts.None(null) : files.Get(path);
        var asProcess = new ProcessSnapshot(h.Pid, h.ProcessName, path, h.SessionId, h.StartUtc, null, 0, null, h.Kind == LockHolderKind.MainWindow, null);

        string? why = h.Kind switch
        {
            LockHolderKind.Service => "A Windows service has it open. PC Tidy doesn't stop services; restarting the PC frees it.",
            LockHolderKind.Critical => "Part of Windows. PC Tidy never closes it; restarting the PC frees it.",
            LockHolderKind.Explorer => "Windows Explorer has it open. Close the Explorer window or preview pane showing it.",
            _ => null,
        };
        if (why is null && Running.BackgroundAppRules.IsProtected(asProcess, facts, windowsDir))
            why = "Part of Windows or your security or anti-cheat software. PC Tidy never closes these.";
        if (why is null && h.Pid == Environment.ProcessId)
            why = "That's PC Tidy itself. Go back and try again in a moment.";
        if (why is null && (h.Kind != LockHolderKind.MainWindow || h.SessionId != currentSession))
            why = "This app has no window PC Tidy can ask to close. Close it yourself (check the tray), or restart the PC.";

        var name = h.AppName;
        return new ScanItem
        {
            Id = $"speed.locking:{h.Pid}",
            Title = name,
            Detail = path ?? h.ProcessName,
            Reason = why ?? $"{name} has it open. Close asks it to close, like clicking its X; save your work in it first.",
            Badge = Badge.Check,
            OwnerApp = facts.Publisher,
            Details = new Dictionary<string, string>
            {
                ["Program"] = h.ProcessName,
                ["Process ID"] = h.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Publisher"] = facts.Publisher ?? "Unknown",
            },
            Actions = why is null ? new[] { SpeedPlanItems.CloseApp(name, h.Pid, h.ProcessName, h.StartUtc) } : Array.Empty<PlanItem>(),
            DisabledReason = why,
        };
    }

    private static FileAttributes? AttributesOf(string path)
    {
        try { return File.GetAttributes(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    /// <summary>The file itself, or up to <see cref="MaxFilesPerFolder"/> files inside a folder (no junctions followed).</summary>
    internal static IEnumerable<string> FilesOf(string path, CancellationToken ct)
    {
        if (AttributesOf(path) is not { } attributes) yield break;
        if ((attributes & FileAttributes.Directory) == 0) { yield return path; yield break; }
        if ((attributes & FileAttributes.ReparsePoint) != 0) yield break;

        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = 0 };
        var stack = new Stack<string>();
        stack.Push(path);
        var count = 0;
        while (stack.Count > 0 && count < MaxFilesPerFolder)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            List<(string Path, bool IsDir, bool IsReparse)> entries;
            try
            {
                entries = new FileSystemEnumerable<(string, bool, bool)>(dir,
                    (ref FileSystemEntry e) => (e.ToFullPath(), e.IsDirectory, (e.Attributes & FileAttributes.ReparsePoint) != 0), options).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var (p, isDir, isReparse) in entries)
            {
                if (isReparse) continue;
                if (isDir) { stack.Push(p); continue; }
                if (count++ >= MaxFilesPerFolder) yield break;
                yield return p;
            }
        }
    }
}
