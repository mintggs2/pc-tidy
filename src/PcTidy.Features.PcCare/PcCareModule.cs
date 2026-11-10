using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.PcCare.Planning;
using PcTidy.Features.PcCare.Scanning;
using PcTidy.Features.PcCare.Source;
using PcTidy.Features.PcCare.View;
using PcTidy.UI.Flow;

namespace PcTidy.Features.PcCare;

/// <summary>
/// Left-rail entry for PC care (Plan §8 "16 · PC care" + the approved v2 extras): app updates, Windows repair, network,
/// security, browsers, crashes, restore points, leftovers, shredder and the health report, on one custom page with tabs.
/// Every scan is read-only; every change goes through the standard confirm (IFlowHost) and is picked by hand.
/// </summary>
public sealed class PcCareModule : IFeatureModule, IModuleRiskInfo
{
    private readonly PcCareScanner _scanner;

    /// <param name="sourceFor">Data source override (tests, demos); default: real PC, or the snapshot in a sandbox.</param>
    /// <param name="clock">Clock override for tests.</param>
    /// <param name="reportSections">
    /// Extra headings for the health report from other sections (e.g. a Services summary). The shell passes them here, so the
    /// saved report already contains them and nothing needs to patch it before the confirm.
    /// </param>
    public PcCareModule(Func<IKnownLocations, IPcCareSource>? sourceFor = null, TimeProvider? clock = null,
        IEnumerable<Report.IHealthReportSectionSource>? reportSections = null)
    {
        _scanner = new PcCareScanner(sourceFor, clock, reportSections);
        Scanners = new IFeatureScanner[] { _scanner };
    }

    public string Id => PcCareActions.FeatureId;
    public string Title => "PC care";
    public string Summary => "Update apps, repair Windows, fix the network, check security, browsers and crashes, and export a health report.";
    public NavGroup Group => NavGroup.SpeedUp;
    public string Icon => "HeartPulse24";
    /// <summary>Speed up group: Speed · Tweaks · Drivers · Games · Apps · PC care (last).</summary>
    public int Order => 70;
    public Release Release => Release.V2;
    public ModuleRisk Risk => ModuleRisk.Check;
    public IReadOnlyList<IFeatureScanner> Scanners { get; }

    /// <summary>Needs IKnownLocations, ISafetyGuard, AppSettings and IFlowHost; IRunJournal (optional) feeds the report's run list.</summary>
    public object? CreatePage(IServiceProvider services) => new PcCareView(services, _scanner);
}
