using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.UI.Flow;

namespace PcTidy.App.Composition.Demo;

/// <summary>A demo section in the rail. Uses the standard flow page when it has scanners, else a placeholder.</summary>
public sealed class DemoModule : IFeatureModule, IModuleRiskInfo
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Summary { get; init; }
    public required NavGroup Group { get; init; }
    public required string Icon { get; init; }
    public int Order { get; init; }
    public Release Release { get; init; } = Release.V1;
    public ModuleRisk Risk { get; init; } = ModuleRisk.Safe;
    public IReadOnlyList<IFeatureScanner> Scanners { get; init; } = Array.Empty<IFeatureScanner>();

    public object? CreatePage(IServiceProvider services)
        => Scanners.Count > 0 ? null : new DemoPlaceholderPage(Title, Summary, Icon);
}

/// <summary>Read-only demo scanner: returns sample groups after a short, believable delay with progress.</summary>
public sealed class DemoScanner : IFeatureScanner
{
    private readonly Func<IReadOnlyList<ScanGroup>> _groups;
    private readonly string[] _steps;

    public DemoScanner(string featureId, string title, Release release, bool includedInOptimizeAll, Func<IReadOnlyList<ScanGroup>> groups, params string[] steps)
    {
        FeatureId = featureId;
        Title = title;
        Release = release;
        IncludedInOptimizeAll = includedInOptimizeAll;
        _groups = groups;
        _steps = steps.Length > 0 ? steps : new[] { "Looking in the usual places…" };
    }

    public string FeatureId { get; }
    public string Title { get; }
    public Release Release { get; }
    public bool IncludedInOptimizeAll { get; }

    /// <summary>Total fake scan time; screenshot mode shortens it.</summary>
    public static int DurationMs { get; set; } = 1400;

    public async Task<ScanResult> ScanAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        int per = Math.Max(1, DurationMs / _steps.Length);
        for (int i = 0; i < _steps.Length; i++)
        {
            progress?.Report(new ScanProgress(_steps[i], (double)i / _steps.Length, (i + 1) * 1873, (i + 1) * 391L * 1024 * 1024));
            await Task.Delay(per, ct);
        }
        var groups = _groups();
        return new ScanResult
        {
            FeatureId = FeatureId, Title = Title, Depth = ctx.Depth, Duration = DateTime.UtcNow - started, Groups = groups,
            Notes = FeatureId == "cleanup" ? new[] { "Chrome is open, so a few of its cache files are in use. Close Chrome to free them too." } : Array.Empty<string>(),
        };
    }
}

/// <summary>
/// The demo rail: exactly the real app's sections (ids, titles, groups, order, icons, release and risk come from
/// <see cref="FeatureCatalog"/>), so the demo never drifts from the real rail. A few sections get sample scanners;
/// the rest show a short "no sample data" page. The real modules are only read for their names here: none of their
/// scanners or pages run in the demo.
/// </summary>
public static class DemoModules
{
    /// <summary>Sample scanners by section id.</summary>
    private static IReadOnlyDictionary<string, IFeatureScanner> SampleScanners() => new Dictionary<string, IFeatureScanner>(StringComparer.OrdinalIgnoreCase)
    {
        ["cleanup"] = new DemoScanner("cleanup", "Clean", Release.V1, true, DemoData.CleanupGroups,
            "Looking at temp files…", "Checking browser caches…", "Checking crash reports…", "Looking in Downloads…", "Adding it all up…"),
        ["organize"] = new DemoScanner("organize", "Organize", Release.V1, true, DemoData.OrganizeGroups,
            "Reading Downloads…", "Sorting by type…", "Checking for clashes…"),
        ["speed"] = new DemoScanner("speed", "Speed", Release.V2, true, DemoData.SpeedGroups, "Reading startup apps…", "Measuring impact…"),
        ["tweaks"] = new DemoScanner("tweaks", "Tweaks", Release.V2, true, DemoData.TweakGroups, "Reading current settings…", "Comparing with proven values…"),
        ["games"] = new DemoScanner("games", "Games", Release.V2, true, DemoData.GameGroups, "Finding game libraries…", "Looking for old clips…"),
        // v3 standard-flow sections, so a demo shows them with sample rows instead of a "No sample data" card.
        ["privacy"] = new DemoScanner("privacy", "Privacy sweep", Release.V3, false, DemoData.PrivacyGroups, "Reading recent-files lists…", "Finding browser cookies…"),
        ["notifications"] = new DemoScanner("notifications", "Notifications", Release.V3, false, DemoData.NotificationGroups, "Reading notification settings…", "Reading tips and suggestions…"),
    };

    public static IEnumerable<IFeatureModule> All() => From(FeatureCatalog.All());

    /// <summary>Demo stand-ins for the given real modules (same rail metadata, sample data or a "no sample data" page).</summary>
    public static IEnumerable<IFeatureModule> From(IEnumerable<IFeatureModule> real)
    {
        var samples = SampleScanners();
        foreach (var m in real)
        {
            yield return new DemoModule
            {
                Id = m.Id, Title = m.Title, Summary = m.Summary, Group = m.Group, Icon = m.Icon, Order = m.Order, Release = m.Release,
                Risk = (m as IModuleRiskInfo)?.Risk ?? ModuleRisk.Safe,
                Scanners = samples.TryGetValue(m.Id, out var s) ? new[] { s } : Array.Empty<IFeatureScanner>(),
            };
        }
    }
}
