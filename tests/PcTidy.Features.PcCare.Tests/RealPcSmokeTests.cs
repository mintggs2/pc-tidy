using PcTidy.Features.PcCare.Scanning;
using PcTidy.Features.PcCare.Source;
using Xunit.Abstractions;

namespace PcTidy.Features.PcCare.Tests;

/// <summary>
/// Opt-in, read-only check of the real source on the PC running the tests (set PCTIDY_PCCARE_REAL_READ=1).
/// It only reads (WMI, registry, event log, browser preference files, the winget upgrade listing); the DNS test is off.
/// Without the variable it does nothing, so the normal test run stays deterministic and never touches the real PC.
/// </summary>
public class RealPcSmokeTests
{
    private readonly ITestOutputHelper _out;
    public RealPcSmokeTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task Real_read_only_scan_finishes_without_errors_when_enabled()
    {
        if (Environment.GetEnvironmentVariable("PCTIDY_PCCARE_REAL_READ") != "1") return;
        var locations = new PcTidy.Safety.WindowsKnownLocations(new PcTidy.Core.AppSettings());
        var source = new WindowsPcCareSource(locations);
        var snapshot = await source.ReadAsync(new PcCareScanOptions { RunDnsTest = false, CheckAppUpdates = true }, null, CancellationToken.None);
        var report = PcCareResultBuilder.Build(snapshot, new PcCareBuildContext
        {
            NowUtc = DateTime.UtcNow, DataRoot = locations.DataRoot, SystemDrive = locations.SystemDrive,
            RedactTerms = PcCareScanner.RedactTerms(locations),
        });
        foreach (var (tab, note) in report.TabNotes) _out.WriteLine($"[{tab}] {note}");
        foreach (var g in report.Result.Groups) _out.WriteLine($"{g.Id}: {g.Items.Count} rows");
        foreach (var f in Analysis.BrowserAudit.Extensions(snapshot.Browsers!))
            _out.WriteLine($"  ext {f.Profile.Browser} loc={f.Extension.Location} store={f.Extension.FromStore}: {string.Join("; ", f.Flags.Select(x => x.Text))}");
        _out.WriteLine($"Antivirus: {report.Antivirus?.Situation}; winget: {snapshot.Winget?.State} {snapshot.Winget?.Upgrades.Count}");
        _out.WriteLine($"Restore: {snapshot.Restore?.Access}; crashes: {snapshot.Crashes?.Events.Count} events, dumps {snapshot.Crashes?.MinidumpAccess}");
        _out.WriteLine($"Wi-Fi: {snapshot.Wlan?.Available} {snapshot.Wlan?.Profiles.Count}; profiles: {snapshot.Profiles?.Profiles.Count}; features: {snapshot.Features?.Features.Count}");
        _out.WriteLine($"Browsers: {snapshot.Browsers?.Profiles.Count} profiles, {snapshot.Browsers?.Programs.Count} programs; GPUs: {snapshot.Health?.Gpus.Count}");
        Assert.NotNull(snapshot.Security);
        Assert.All(report.Result.AllItems, i => Assert.False(i.DefaultSelected));
    }
}
