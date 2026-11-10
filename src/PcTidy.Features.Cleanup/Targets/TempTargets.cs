using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Cleanup.Scanning;

namespace PcTidy.Features.Cleanup.Targets;

/// <summary>
/// §7 #1 and #2: temp files and empty folders untouched for 2+ days.
/// Quick takes loose old files and folders that are old all the way through; Deep also looks inside
/// busy folders (some files still in use) and takes their old parts.
/// </summary>
internal sealed class TempTarget : ICleanupTarget
{
    private const int MinAgeDays = 2;
    private readonly Func<IKnownLocations, string> _root;
    private readonly bool _needsAdmin;

    private TempTarget(CleanupTargetInfo info, Func<IKnownLocations, string> root, bool needsAdmin)
    {
        Info = info;
        _root = root;
        _needsAdmin = needsAdmin;
    }

    public static TempTarget User() => new(CleanupTargets.UserTemp, k => k.UserTemp, needsAdmin: false);

    /// <summary>Windows temp: root taken from the carve-out table so it always matches what the SafetyGuard allows.</summary>
    public static TempTarget Windows() => new(CleanupTargets.WindowsTemp,
        k => CarveOuts.Find(CarveOuts.WindowsTemp)!.Roots(k).First(), needsAdmin: true);

    public CleanupTargetInfo Info { get; }

    public IReadOnlyList<ScanItem>? Collect(ScanSession s, CancellationToken ct)
    {
        var root = _root(s.Locations);
        var tree = s.Tree(root, ct);
        if (tree is null) return null;

        var picks = TreePlanner.Plan(tree, e => s.IsOlderThan(e, MinAgeDays),
            s.IsDeep ? MixedFolders.DescendDeepOnly : MixedFolders.Skip);
        var item = ItemFactory.Quarantine(s, Info, new ItemSpec
        {
            Title = Info.Title,
            Detail = root,
            NeedsAdmin = _needsAdmin,
            DeepLabel = "old files inside busy temp folders",
            ExtraDetails = new Dictionary<string, string> { ["Rule"] = "Untouched for 2+ days. Files in use are skipped." },
        }, picks);
        return new[] { item }.Present();
    }
}
