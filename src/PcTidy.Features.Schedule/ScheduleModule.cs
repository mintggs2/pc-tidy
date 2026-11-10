using PcTidy.Features.Schedule.Schedules;
using PcTidy.Features.Schedule.View;
using PcTidy.UI.Flow;

namespace PcTidy.Features.Schedule;

/// <summary>
/// Section 8 · Schedule &amp; reports (v3): weekly scan and monthly checkup schedules (report only), the after-run
/// reports, before/after history, disk health history and the tweak drift check. It has no scanner of its own: it
/// reports on the other sections and never offers anything to clean.
/// </summary>
public sealed class ScheduleModule : IFeatureModule, IModuleRiskInfo
{
    public string Id => ScheduleIds.FeatureId;
    public string Title => "Schedule & reports";
    public string Summary => "Weekly scans and a monthly checkup that only look, a report after every run, and before/after numbers.";
    public NavGroup Group => NavGroup.You;
    public string Icon => "CalendarClock24";
    public int Order => 20;
    public Release Release => Release.V3;
    public ModuleRisk Risk => ModuleRisk.Safe;
    public IReadOnlyList<IFeatureScanner> Scanners { get; } = Array.Empty<IFeatureScanner>();

    /// <summary>Needs IKnownLocations, AppSettings, ISystemInfo (real PC) and IFlowHost; IRunJournal enables the tweak check.</summary>
    public object? CreatePage(IServiceProvider services) => new SchedulePage(services);
}
