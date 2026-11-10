using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Cleanup.Scanning;

namespace PcTidy.Features.Cleanup.Targets;

/// <summary>Roots of a carve-out, straight from the table the SafetyGuard honours (so scan and guard can't disagree).</summary>
internal static class CarveOutRoots
{
    public static IReadOnlyList<string> Of(string carveOutId, IKnownLocations k) => CarveOuts.Find(carveOutId)!.Roots(k).ToList();

    /// <summary>Folders under Windows or ProgramData need admin to change.</summary>
    public static bool NeedsAdmin(string path, IKnownLocations k)
        => PathUtil.IsUnder(path, k.WindowsDir) || PathUtil.IsUnder(path, k.ProgramData);
}

/// <summary>§7 #5: the old Windows web cache (INetCache). Rebuilt automatically.</summary>
internal sealed class InetCacheTarget : ICleanupTarget
{
    public CleanupTargetInfo Info => CleanupTargets.InetCache;

    public IReadOnlyList<ScanItem>? Collect(ScanSession s, CancellationToken ct)
    {
        var root = Path.Combine(s.Locations.LocalAppData, "Microsoft", "Windows", "INetCache");
        var tree = s.Tree(root, ct);
        if (tree is null) return null;
        return new[]
        {
            ItemFactory.Quarantine(s, Info, new ItemSpec { Title = "Windows web cache", Detail = root },
                TreePlanner.Plan(tree, _ => true, MixedFolders.Descend)),
        }.Present();
    }
}

/// <summary>§7 #7: crash dumps. Ticked only when older than 14 days; recent ones are listed unticked.</summary>
internal sealed class CrashDumpsTarget : ICleanupTarget
{
    private const int TickedAfterDays = 14;

    public CleanupTargetInfo Info => CleanupTargets.CrashDumps;

    public IReadOnlyList<ScanItem>? Collect(ScanSession s, CancellationToken ct)
    {
        var k = s.Locations;
        var appOld = new List<Pick>(); var appRecent = new List<Pick>();
        var winOld = new List<Pick>(); var winRecent = new List<Pick>();
        var anyPresent = false;

        foreach (var root in CarveOutRoots.Of(CarveOuts.CrashDumps, k))
        {
            var tree = s.Tree(root, ct);
            if (tree is null) continue;
            anyPresent = true;
            var windows = CarveOutRoots.NeedsAdmin(root, k);
            (windows ? winOld : appOld).AddRange(TreePlanner.Plan(tree, e => s.IsOlderThan(e, TickedAfterDays), MixedFolders.Descend));
            (windows ? winRecent : appRecent).AddRange(TreePlanner.Plan(tree, e => !s.IsOlderThan(e, TickedAfterDays), MixedFolders.Descend));
        }

        // MEMORY.DMP sits directly in the Windows folder: the carve-out allows exactly that one file.
        foreach (var (_, file) in CarveOuts.AllowedSingleFiles(k).Where(f => f.CarveOutId == CarveOuts.CrashDumps))
        {
            if (ScanSession.EntryOf(file) is not { } e || e.IsReparsePoint || e.IsSystem || e.IsCloudPlaceholder) continue;
            anyPresent = true;
            var pick = new Pick(Path.GetDirectoryName(file)!, e, e.Size, 1, false);
            (s.IsOlderThan(e, TickedAfterDays) ? winOld : winRecent).Add(pick);
        }
        if (!anyPresent) return null;

        const string recentReason = CleanupTargets.CrashDumpReason + " These are from the last 14 days, so keep them if you're looking into a crash.";
        return new[]
        {
            ItemFactory.Quarantine(s, Info, new ItemSpec { Title = "App crash dumps (older than 14 days)", Detail = Path.Combine(k.LocalAppData, "CrashDumps") }, appOld),
            ItemFactory.Quarantine(s, Info, new ItemSpec { Title = "Windows crash dumps (older than 14 days)", Detail = k.WindowsDir, NeedsAdmin = true }, winOld),
            ItemFactory.Quarantine(s, Info, new ItemSpec
            {
                Title = "Recent app crash dumps", Detail = Path.Combine(k.LocalAppData, "CrashDumps"),
                Badge = Badge.Check, DefaultSelected = false, Reason = recentReason,
            }, appRecent),
            ItemFactory.Quarantine(s, Info, new ItemSpec
            {
                Title = "Recent Windows crash dumps", Detail = k.WindowsDir, NeedsAdmin = true,
                Badge = Badge.Check, DefaultSelected = false, Reason = recentReason,
            }, winRecent),
        }.Present();
    }
}

/// <summary>§7 #8: Windows Error Reporting files older than 30 days (yours, and the shared ones that need admin).</summary>
internal sealed class ErrorReportsTarget : ICleanupTarget
{
    private const int MinAgeDays = 30;

    public CleanupTargetInfo Info => CleanupTargets.ErrorReports;

    public IReadOnlyList<ScanItem>? Collect(ScanSession s, CancellationToken ct)
    {
        var k = s.Locations;
        var mine = new List<Pick>(); var shared = new List<Pick>();
        var anyPresent = false;
        foreach (var root in CarveOutRoots.Of(CarveOuts.ErrorReports, k))
        {
            var tree = s.Tree(root, ct);
            if (tree is null) continue;
            anyPresent = true;
            (CarveOutRoots.NeedsAdmin(root, k) ? shared : mine)
                .AddRange(TreePlanner.Plan(tree, e => s.IsOlderThan(e, MinAgeDays), MixedFolders.Descend));
        }
        if (!anyPresent) return null;
        return new[]
        {
            ItemFactory.Quarantine(s, Info, new ItemSpec { Title = "Your error reports", Detail = Path.Combine(k.LocalAppData, "Microsoft", "Windows", "WER") }, mine),
            ItemFactory.Quarantine(s, Info, new ItemSpec
            {
                Title = "Shared error reports", Detail = Path.Combine(k.ProgramData, "Microsoft", "Windows", "WER"), NeedsAdmin = true,
            }, shared),
        }.Present();
    }
}

/// <summary>
/// §7 #9: old CBS persist logs and old DISM logs, 30+ days. The live CBS.log and dism.log are never listed
/// (Windows is writing them).
/// </summary>
internal sealed class WindowsLogsTarget : ICleanupTarget
{
    private const int MinAgeDays = 30;

    public CleanupTargetInfo Info => CleanupTargets.WindowsLogs;

    public IReadOnlyList<ScanItem>? Collect(ScanSession s, CancellationToken ct)
    {
        var roots = CarveOutRoots.Of(CarveOuts.WindowsLogs, s.Locations);
        var picks = new List<Pick>();
        var anyPresent = false;
        foreach (var root in roots)
        {
            var tree = s.Tree(root, ct);
            if (tree is null) continue;
            anyPresent = true;
            var isCbs = Path.GetFileName(root).Equals("CBS", StringComparison.OrdinalIgnoreCase);
            picks.AddRange(TreePlanner.Plan(tree, e => !e.IsDirectory && e.Depth == 1 && s.IsOlderThan(e, MinAgeDays)
                && (isCbs ? IsOldCbsLog(e.Name) : IsOldDismLog(e.Name)), MixedFolders.Skip));
        }
        if (!anyPresent) return null;
        return new[]
        {
            ItemFactory.Quarantine(s, Info, new ItemSpec { Title = Info.Title, Detail = Path.GetDirectoryName(roots[0]), NeedsAdmin = true }, picks),
        }.Present();
    }

    private static bool IsOldCbsLog(string name) => name.StartsWith("CbsPersist_", StringComparison.OrdinalIgnoreCase);

    private static bool IsOldDismLog(string name)
        => name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) && !name.Equals("dism.log", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// §7 #10: Windows Update download folder, 10+ days. Skipped entirely while a restart is pending or an update is
/// installing (Windows may still need those files).
/// </summary>
internal sealed class UpdateDownloadsTarget : ICleanupTarget
{
    private const int MinAgeDays = 10;
    /// <summary>Windows' update installer worker; while it runs an update is being installed.</summary>
    private const string UpdateInstallerProcess = "tiworker";

    public CleanupTargetInfo Info => CleanupTargets.UpdateDownloads;

    public IReadOnlyList<ScanItem>? Collect(ScanSession s, CancellationToken ct)
    {
        var root = CarveOutRoots.Of(CarveOuts.UpdateDownloads, s.Locations)[0];
        if (!Directory.Exists(root)) return null;

        string? busy = null;
        if (s.SystemInfo.IsRestartPending()) busy = "Windows is waiting for a restart to finish installing updates. Restart your PC, then scan again.";
        else if (s.Apps.IsRunning(UpdateInstallerProcess)) busy = "Windows is installing an update right now. Scan again when it's done.";
        if (busy is not null)
        {
            s.AddNote("Windows Update downloads were skipped: " + busy);
            return new[]
            {
                ItemFactory.Info(s, Info, new ItemSpec { Title = Info.Title, Detail = root, NeedsAdmin = true }, 0, 0, "Skipped", busy),
            };
        }

        var tree = s.Tree(root, ct);
        if (tree is null) return null;
        var picks = TreePlanner.Plan(tree, e => s.IsOlderThan(e, MinAgeDays), s.IsDeep ? MixedFolders.DescendDeepOnly : MixedFolders.Skip);
        return new[]
        {
            ItemFactory.Quarantine(s, Info, new ItemSpec
            {
                Title = Info.Title, Detail = root, NeedsAdmin = true, DeepLabel = "old parts of partly-used update downloads",
            }, picks),
        }.Present();
    }
}

/// <summary>§7 #12: GPU shader caches for whichever vendors' folders exist on this PC (NVIDIA, AMD, Intel, DirectX).</summary>
internal sealed class ShaderCacheTarget : ICleanupTarget
{
    public CleanupTargetInfo Info => CleanupTargets.ShaderCache;

    public IReadOnlyList<ScanItem>? Collect(ScanSession s, CancellationToken ct)
    {
        var items = new List<ScanItem?>();
        var anyPresent = false;
        foreach (var root in CarveOutRoots.Of(CarveOuts.ShaderCache, s.Locations))
        {
            var tree = s.Tree(root, ct);
            if (tree is null) continue;
            anyPresent = true;
            items.Add(ItemFactory.Quarantine(s, Info, new ItemSpec { Title = Label(root), Detail = root },
                TreePlanner.Plan(tree, _ => true, MixedFolders.Descend)));
        }
        return anyPresent ? items.Present() : null;
    }

    /// <summary>"NVIDIA DXCache" style label from the folder's last two names.</summary>
    private static string Label(string root)
    {
        var name = Path.GetFileName(root);
        var parent = Path.GetFileName(Path.GetDirectoryName(root)) ?? "";
        return name.Equals("D3DSCache", StringComparison.OrdinalIgnoreCase)
            ? "DirectX shader cache"
            : $"{parent.Replace(" Corporation", "", StringComparison.OrdinalIgnoreCase)} {name}";
    }
}
