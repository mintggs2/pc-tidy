using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Insights.Source;
using PcTidy.Features.Insights.View;
using PcTidy.UI.Flow;

namespace PcTidy.Features.Insights;

/// <summary>
/// Left-rail entry for Insights (Plan v3 "Census additions"): read-only cards that explain what PC Tidy sees, including
/// "Scan this script", modified-Windows detection, stability and memory-test history, backup health, the hosts-file audit,
/// the Dev Drive advisor, the clean GPU driver guide, OneDrive "Free up space", compressing old folders and hardware checks.
/// Nothing changes the PC except through IFlowHost plans the user confirms.
/// </summary>
public sealed class InsightsModule : IFeatureModule, IModuleRiskInfo
{
    private readonly InsightsScanner _scanner;

    /// <param name="sourceFor">Fact source override (tests, demos); default: the real PC, or Sim\insights.json in a sandbox.</param>
    /// <param name="clock">Clock override for tests.</param>
    public InsightsModule(Func<IKnownLocations, IInsightsSource>? sourceFor = null, TimeProvider? clock = null)
    {
        _scanner = new InsightsScanner(sourceFor, clock);
        Scanners = new IFeatureScanner[] { _scanner };
    }

    public string Id => InsightsIds.FeatureId;
    public string Title => "Insights";
    public string Summary => "Scan a downloaded script before you run it, and see stability, backups, the hosts file, modified Windows and hardware settings explained.";
    public NavGroup Group => NavGroup.SpeedUp;
    public string Icon => "DataUsage24";
    /// <summary>Speed up group, after PC care (70).</summary>
    public int Order => 75;
    public Release Release => Release.V3;
    /// <summary>Reading is safe; the few actions are Check rows the user ticks by hand.</summary>
    public ModuleRisk Risk => ModuleRisk.Safe;
    public IReadOnlyList<IFeatureScanner> Scanners { get; }

    /// <summary>Needs IKnownLocations, ISafetyGuard, AppSettings and IFlowHost (IShellFlowHost for "Open Tweaks" links).</summary>
    public object? CreatePage(IServiceProvider services) => new InsightsView(services, _scanner);
}
