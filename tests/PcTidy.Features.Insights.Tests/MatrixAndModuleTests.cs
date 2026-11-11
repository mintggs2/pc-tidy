using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Insights.Model;
using PcTidy.Features.Insights.Source;
using PcTidy.Hosting;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;
using Xunit;

namespace PcTidy.Features.Insights.Tests;

/// <summary>Card availability across simulated PCs (SandboxSystemInfo: laptop/desktop × NVIDIA/AMD/Intel/none), the module and the scan result.</summary>
public class MatrixAndModuleTests
{
    public static IEnumerable<object[]> Pcs()
    {
        foreach (var laptop in new[] { false, true })
            foreach (var gpu in new[] { "Nvidia", "Amd", "Intel", "Unknown" })
                yield return new object[] { gpu, laptop };
    }

    [Theory]
    [MemberData(nameof(Pcs))]
    public async Task Cards_adapt_to_the_simulated_pc(string gpu, bool laptop)
    {
        using var tb = new TestbedFolder();
        tb.Sim(gpu, laptop);
        using var host = PcTidyHost.CreateSandbox(tb.Root, SandboxProfile.Minimal);
        var src = new FakeSource
        {
            Memory = ReadOutcome<MemoryFacts>.Ok(new MemoryFacts(new[] { new MemoryModule("DIMM1", 8L << 30, 3200, 3200) }, 2)),
            Displays = { new DisplayFacts("Built-in display", 60, 120, 1920, 1080) },
        };
        var report = await new InsightsScanner(_ => src, new FixedClock()).ScanReportAsync(host.CreateScanContext(ScanDepth.Quick), null, CancellationToken.None);

        bool hasGpu = gpu != "Unknown";
        Assert.Equal(hasGpu, report.Find(InsightsIds.ResizableBar)!.IsAvailable);
        Assert.Equal(hasGpu, report.Find(InsightsIds.GpuReinstall)!.IsAvailable);
        if (hasGpu)
        {
            var guide = report.Find(InsightsIds.GpuReinstall)!;
            var vendorPage = guide.Links[0].Target;
            Assert.StartsWith("https://", vendorPage);
            Assert.Contains(gpu.ToLowerInvariant() switch { "nvidia" => "nvidia.com", "amd" => "amd.com", _ => "intel.com" }, vendorPage);
            Assert.Contains(guide.Links, l => l.Target == "https://www.wagnardsoft.com/");
            Assert.Empty(guide.Items); // never runs DDU
        }
        // One stick: "single channel" on a desktop; on a laptop it may be built in, so it's information.
        Assert.Equal(laptop ? InsightStatus.Info : InsightStatus.Attention, report.Find(InsightsIds.Ram)!.Status);
        Assert.Equal(laptop, report.Find(InsightsIds.RefreshRate)!.Explanation.Contains("On a laptop"));
        // Minimal profile: no games, no OneDrive.
        Assert.False(report.Find(InsightsIds.GamesOnHdd)!.IsAvailable);
        Assert.False(report.Find(InsightsIds.OneDriveFreeUp)!.IsAvailable);
        // Every card is present, never an error.
        Assert.Equal(24, report.Cards.Count);
        Assert.Empty(report.Notes);
    }

    [Fact]
    public async Task Intel_Arc_gets_the_resizable_bar_note()
    {
        using var tb = new TestbedFolder();
        tb.Sim("Intel", laptop: false);
        using var host = PcTidyHost.CreateSandbox(tb.Root, SandboxProfile.Minimal);
        var ctx = host.CreateScanContext(ScanDepth.Quick);
        var arc = new ArcSystem(ctx.System!);
        var report = await new InsightsScanner(_ => new FakeSource(), new FixedClock())
            .ScanReportAsync(new ScanContext { Locations = ctx.Locations, Safety = ctx.Safety, InUse = ctx.InUse, Settings = ctx.Settings, Walker = ctx.Walker, System = arc }, null, CancellationToken.None);
        var card = report.Find(InsightsIds.ResizableBar)!;
        Assert.Contains("Intel Arc", card.Headline);
        Assert.True(card.IsHeuristic); // unverified read: information only
        Assert.Empty(card.Items);
    }

    private sealed class ArcSystem(ISystemInfo inner) : ISystemInfo
    {
        public GpuVendor PrimaryGpu => GpuVendor.Intel;
        public IReadOnlyList<string> GpuNames => new[] { "Intel(R) Arc(TM) A770 Graphics" };
        public bool IsLaptop => inner.IsLaptop;
        public bool HasBattery => inner.HasBattery;
        public bool IsAdmin => inner.IsAdmin;
        public int WindowsBuild => inner.WindowsBuild;
        public bool RebootPending => inner.RebootPending;
        public IReadOnlyList<DriveDetails> Drives => inner.Drives;
        public IReadOnlySet<string> RunningProcesses() => inner.RunningProcesses();
    }

    [Fact]
    public void Module_is_wired_as_specified()
    {
        var m = new InsightsModule();
        Assert.Equal(("insights", "Insights", NavGroup.SpeedUp, Release.V3), (m.Id, m.Title, m.Group, m.Release));
        Assert.True(Enum.TryParse<SymbolRegular>(m.Icon, out _));
        Assert.NotEqual("Lightbulb24", m.Icon);
        var scanner = Assert.Single(m.Scanners);
        Assert.False(scanner.IncludedInOptimizeAll);
        Assert.Equal("insights", scanner.FeatureId);
    }

    [Fact]
    public async Task Default_source_in_a_sandbox_is_never_the_real_pc_and_every_card_still_renders()
    {
        using var tb = new TestbedFolder();
        using var host = PcTidyHost.CreateSandbox(tb.Root, SandboxProfile.Default);
        var result = await new InsightsScanner(clock: new FixedClock()).ScanAsync(host.CreateScanContext(ScanDepth.Quick), null, CancellationToken.None);

        var items = result.AllItems.ToList();
        Assert.Equal(24, items.Count(i => i.Id.StartsWith("insights.", StringComparison.Ordinal)));
        Assert.All(result.Groups, g => Assert.False(string.IsNullOrWhiteSpace(g.Description)));
        Assert.All(items, i => Assert.False(string.IsNullOrWhiteSpace(i.Reason)));
        Assert.All(items.Where(i => i.Badge is Badge.Check or Badge.Permanent), i => Assert.False(i.DefaultSelected));
        Assert.All(items.Where(i => i.Actions.Count == 0), i => Assert.False(i.DefaultSelected));
    }

    [Fact]
    public void Every_icon_name_used_by_a_card_exists_in_WPF_UI()
    {
        foreach (var icon in new[] { "DocumentSearch24", "WindowShield24", "Globe24", "Pulse24", "Ram20", "History24", "CloudArrowDown24", "FolderZip24", "Code24",
                     "Desktop24", "Games24", "DeveloperBoard24", "Timer24", "ShieldCheckmark24", "Wrench24", "DataUsage24", "Open24", "ArrowRight24",
                     // v3.1 measured cards and System info categories
                     "Filter24", "ShieldLock24", "Window24", "Temperature24", "PuzzlePiece24", "Board24", "HardDrive20", "Wifi124" })
            Assert.True(Enum.TryParse<SymbolRegular>(icon, out _), icon);
    }

    [Fact]
    public async Task Free_up_space_runs_and_undoes_through_the_real_engine()
    {
        using var tb = new TestbedFolder();
        using var host = PcTidyHost.CreateSandbox(tb.Root, SandboxProfile.Default);
        var file = TestbedFolder.BigOldFile(Path.Combine(host.Locations.CloudRoots[0], "Archive", "old-backup.vhd"), 60L << 20, FixedClock.Now.AddYears(-1));
        var before = File.GetAttributes(file);
        var report = await new InsightsScanner(_ => new FakeSource(), new FixedClock()).ScanReportAsync(host.CreateScanContext(ScanDepth.Quick), null, CancellationToken.None);
        var row = Assert.Single(report.Find(InsightsIds.OneDriveFreeUp)!.Items);

        var plan = PlanBuilder.FromSelection(InsightsIds.FeatureId, "OneDrive: free up space", new[] { row });
        var sim = await host.Executor.ExecuteAsync(plan, new ExecuteOptions { Mode = ExecutionMode.Simulate }, null, CancellationToken.None);
        Assert.Equal(RunStatus.Simulated, sim.Status);
        Assert.Equal(before, File.GetAttributes(file)); // simulate changes nothing

        var run = await host.Executor.ExecuteAsync(plan with { RunId = ActionPlan.NewRunId() }, new ExecuteOptions(), null, CancellationToken.None);
        var step = Assert.Single(run.Steps);
        Assert.Equal(StepStatus.Done, step.Status);
        Assert.NotEqual(before, File.GetAttributes(file));
        await host.Undo.UndoRunAsync(run.RunId, null, CancellationToken.None);
        Assert.Equal(before, File.GetAttributes(file));
        Assert.True(File.Exists(file)); // nothing is ever deleted
    }
}
