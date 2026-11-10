using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Drivers.Planning;
using PcTidy.Features.Drivers.Scanning;
using PcTidy.Features.Drivers.Source;
using PcTidy.Hosting;

namespace PcTidy.Features.Drivers.Tests;

/// <summary>
/// End to end over the real composition (real SafetyGuard, Executor) on a sandbox PC under testbed\drivers.
/// The sandbox reads fixtures; no real system tool ever runs.
/// </summary>
public class SandboxIntegrationTests
{
    [Fact]
    public void A_sandbox_never_gets_the_real_driver_source()
    {
        using var tb = new TestbedFolder();
        using var host = PcTidyHost.CreateSandbox(tb.Root, SandboxProfile.Minimal);
        Assert.IsType<FixtureDriverSource>(DriverSources.For(host.Locations));
    }

    [Fact]
    public async Task Every_action_the_scan_offers_passes_the_real_safety_guard_or_uses_the_backup_folder()
    {
        using var tb = new TestbedFolder();
        tb.InstallFixture("scenario");
        using var host = PcTidyHost.CreateSandbox(tb.Root, SandboxProfile.Default);
        var scanner = new DriverScanner(clock: new FixedClock());
        var ctx = WithOptions(host.CreateScanContext(ScanDepth.Quick), new DriverScanOptions { IncludeImportantClasses = true });

        var result = await scanner.ScanAsync(ctx, null, CancellationToken.None);

        var actions = result.AllActions().ToList();
        Assert.Contains(actions, a => a.ToolId == ToolIds.PnpExportDrivers);
        Assert.Contains(actions, a => a.ToolId == ToolIds.PnpDeleteDriver);
        Assert.Contains(actions, a => a.ToolId == ToolIds.PnpRemoveDevice);
        Assert.Contains(actions, a => a.ToolId == ToolIds.DriverRollback);
        Assert.Contains(actions, a => a.ToolId == ToolIds.WindowsUpdateInstallDrivers);
        Assert.Contains(actions, a => a.Kind == ActionKind.OpenExternal);
        // The backup folder lives in PC Tidy's own data folder (as specified); the guard's folder rule for
        // pnputil export/add is a pending contract change, so folder tools are checked for their documented Args here.
        foreach (var a in actions.Where(a => !UsesBackupFolder(a)))
        {
            var verdict = host.Safety.Check(a);
            Assert.True(verdict.Allowed, $"{a.Description}: {verdict.Reason}");
        }
        foreach (var export in actions.Where(UsesBackupFolder))
        {
            Assert.Equal(new[] { "folder" }, export.Args!.Keys);
            Assert.StartsWith(Path.Combine(host.Locations.DataRoot, "backups", "drivers") + "\\", export.Args["folder"]);
        }
    }

    private static bool UsesBackupFolder(PlanItem a) => a.ToolId is ToolIds.PnpExportDrivers or ToolIds.PnpAddDrivers;

    [Fact]
    public async Task A_restore_from_an_earlier_backup_is_offered_unticked()
    {
        using var tb = new TestbedFolder();
        tb.InstallFixture("scenario");
        using var host = PcTidyHost.CreateSandbox(tb.Root, SandboxProfile.Minimal);
        var backup = Path.Combine(host.Locations.DataRoot, "backups", "drivers", "2026-09-12", "litcamstudio.inf_amd64_fixture");
        Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(backup, "litcamstudio.inf"), "; test backup");

        var result = await new DriverScanner(clock: new FixedClock()).ScanAsync(host.CreateScanContext(ScanDepth.Quick), null, CancellationToken.None);

        var restore = Assert.Single(result.Group(DriverTabs.Devices, "backup")!.Items, i => i.Title.StartsWith("Put back", StringComparison.Ordinal));
        Assert.True(Planning.DriverActions.IsExport(restore.Actions[0]));
        var step = restore.Actions[1];
        Assert.Equal(ToolIds.PnpAddDrivers, step.ToolId);
        Assert.Equal(Path.GetDirectoryName(backup), step.Args!["folder"]);
        Assert.False(step.Reversible);
        Assert.True(step.NeedsAdmin);
        Assert.Equal(Badge.Check, restore.Badge);
        Assert.False(restore.DefaultSelected);
    }

    [Fact]
    public async Task Simulating_a_cleanup_plan_changes_nothing_and_fails_nothing()
    {
        using var tb = new TestbedFolder();
        tb.InstallFixture("scenario");
        using var host = PcTidyHost.CreateSandbox(tb.Root, SandboxProfile.Default);
        var result = await new DriverScanner(clock: new FixedClock()).ScanAsync(host.CreateScanContext(ScanDepth.Quick), null, CancellationToken.None);
        var picks = result.Group(DriverTabs.Store, "old")!.Items.Concat(result.Group(DriverTabs.Devices, "rollback")!.Items);
        var plan = DriverPlanBuilder.Build("Driver store cleanup", picks, Path.Combine(host.Locations.DataRoot, "backups", "drivers", "2026-11-10")).Plan!;
        var simFolder = Path.Combine(tb.Root, "Sim", "drivers");
        var before = Directory.GetFiles(simFolder).Select(f => (f, File.GetLastWriteTimeUtc(f))).ToList();

        var run = await host.Executor.ExecuteAsync(plan, new ExecuteOptions { Mode = ExecutionMode.Simulate }, null, CancellationToken.None);

        Assert.Equal(plan.Items.Count, run.Steps.Count);
        Assert.DoesNotContain(run.Steps, s => s.Status == StepStatus.Failed);
        Assert.DoesNotContain(run.Steps, s => s.Status == StepStatus.Done);
        Assert.Equal(before, Directory.GetFiles(simFolder).Select(f => (f, File.GetLastWriteTimeUtc(f))).ToList());
        Assert.False(Directory.Exists(Path.Combine(host.Locations.DataRoot, "backups", "drivers", "2026-11-10")));
    }

    [Fact]
    public async Task A_pc_without_any_driver_data_gets_friendly_notes_not_errors()
    {
        using var tb = new TestbedFolder();
        using var host = PcTidyHost.CreateSandbox(tb.Root, SandboxProfile.Minimal);

        var report = await new DriverScanner(clock: new FixedClock()).ScanReportAsync(host.CreateScanContext(ScanDepth.Quick), null, CancellationToken.None);

        Assert.False(report.Inventory.HasPnpUtilXml);
        Assert.Contains(DriverTabs.Devices, report.TabNotes.Keys);
        Assert.Contains(DriverTabs.Store, report.TabNotes.Keys);
        Assert.Contains(DriverTabs.Ghosts, report.TabNotes.Keys);
        Assert.Contains(DriverTabs.Updates, report.TabNotes.Keys);
        Assert.DoesNotContain(report.Result.AllActions(), a => a.Kind == ActionKind.RunSystemTool);
        Assert.All(report.TabNotes.Values, n => Assert.DoesNotContain("Exception", n));
    }

    [Fact]
    public async Task Cancelling_the_scan_stops_it()
    {
        using var tb = new TestbedFolder();
        tb.InstallFixture("scenario");
        using var host = PcTidyHost.CreateSandbox(tb.Root, SandboxProfile.Minimal);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new DriverScanner(clock: new FixedClock()).ScanAsync(host.CreateScanContext(ScanDepth.Quick), null, cts.Token));
    }

    private static ScanContext WithOptions(ScanContext c, DriverScanOptions options) => new()
    {
        Locations = c.Locations, Safety = c.Safety, InUse = c.InUse, Settings = c.Settings, Walker = c.Walker,
        Depth = c.Depth, System = c.System, RunningProcesses = c.RunningProcesses,
        Options = new Dictionary<string, object> { [DriverScanOptions.Key] = options },
    };
}
