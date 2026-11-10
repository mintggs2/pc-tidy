using System.Runtime.ExceptionServices;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.SpaceMap.Index;
using PcTidy.Features.SpaceMap.View;

namespace PcTidy.Features.SpaceMap.Tests;

public class DiskTests
{
    [Fact]
    public void Real_junction_is_recorded_as_a_link_and_not_followed()
    {
        using var dir = new TestDir("junction");
        dir.File(@"outside\big.bin", 1_000_000);
        dir.File(@"scan\a\small.txt", 300);
        var link = Path.Combine(dir.Root, @"scan\to-outside");
        Assert.True(Junction.TryCreate(link, Path.Combine(dir.Root, "outside")), "couldn't create a test junction");

        var ix = new SpaceIndexScanner(new ManagedFileWalker(), new FakeSafety())
            .Scan(Path.Combine(dir.Root, "scan"), new SpaceScanOptions { IncludeUnscannedNode = false }, null, CancellationToken.None);
        int j = ix.Find(link);
        Assert.True(j > 0);
        Assert.True(ix.Has(j, SpaceNodeFlags.Link));
        Assert.Equal(0, ix.ChildCount(j));
        Assert.Equal(300, ix.Size(SpaceIndex.RootNode));
        Assert.Equal(1, ix.Summary.Links);
    }

    [Fact]
    public void Real_tree_with_long_paths_and_odd_names_scans_completely()
    {
        using var dir = new TestDir("odd");
        var deep = Path.Combine(Enumerable.Repeat("segment of a long folder name", 10).ToArray());
        var longFile = dir.File(Path.Combine(deep, "deep file.txt"), 10);
        dir.File("Ünïcødé 名前.txt", 20);
        dir.File("hidden.txt", 30);
        File.SetAttributes(Path.Combine(dir.Root, "hidden.txt"), FileAttributes.Hidden);

        var ix = new SpaceIndexScanner(new ManagedFileWalker(), new FakeSafety())
            .Scan(dir.Root, new SpaceScanOptions { IncludeUnscannedNode = false }, null, CancellationToken.None);
        Assert.True(longFile.Length > 260);
        int n = ix.Find(longFile);
        Assert.True(n > 0);
        Assert.True(ix.Has(n, SpaceNodeFlags.LongPath));
        Assert.Equal(60, ix.Size(SpaceIndex.RootNode));
        Assert.True(ix.Has(ix.Find(Path.Combine(dir.Root, "hidden.txt")), SpaceNodeFlags.Hidden));
        Assert.True(ix.Find(Path.Combine(dir.Root, "Ünïcødé 名前.txt")) > 0);
    }

    [Fact]
    public void Real_scan_reads_cluster_size_and_rounds_sizes_on_disk()
    {
        using var dir = new TestDir("cluster");
        dir.File("one.bin", 1);
        var ix = new SpaceIndexScanner(new ManagedFileWalker(), new FakeSafety())
            .Scan(dir.Root, new SpaceScanOptions { IncludeUnscannedNode = false }, null, CancellationToken.None);
        Assert.True(ix.Summary.ClusterSize >= 512);
        Assert.Equal(ix.Summary.ClusterSize, ix.SizeOnDisk(SpaceIndex.RootNode));
    }
}

public class ModuleAndPageTests
{
    [Fact]
    public void Module_identity()
    {
        var m = new SpaceMapModule();
        Assert.Equal("spacemap", m.Id);
        Assert.Equal("Space map", m.Title);
        Assert.Equal(NavGroup.CleanUp, m.Group);
        Assert.Equal("DataTreemap24", m.Icon);
        Assert.Equal(Release.V1, m.Release);
        Assert.IsType<SpaceMapScanner>(Assert.Single(m.Scanners));
        Assert.False(string.IsNullOrWhiteSpace(m.Summary));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Page_builds_on_any_pc(bool twoDrives)
    {
        using var dir = new TestDir("page");
        var loc = new SandboxKnownLocations(dir.Root, twoDrives ? SandboxProfile.Default : SandboxProfile.Minimal);
        var services = new Services
        {
            [typeof(IKnownLocations)] = loc,
            [typeof(ISafetyGuard)] = new FakeSafety(),
            [typeof(IFileWalker)] = new ManagedFileWalker(),
            [typeof(AppSettings)] = new AppSettings(),
            [typeof(IFlowHost)] = new RecordingFlowHost(),
            [typeof(ISystemInfo)] = new SandboxSystemInfo(loc),
        };
        RunSta(() =>
        {
            var page = new SpaceMapModule().CreatePage(services);
            var view = Assert.IsType<SpaceMapView>(page);
            view.Measure(new System.Windows.Size(1280, 800));
            view.Arrange(new System.Windows.Rect(0, 0, 1280, 800));
            var picker = (System.Windows.Controls.ComboBox)view.FindName("DrivePicker");
            Assert.Equal(loc.FixedDrives.Count + 1, picker.Items.Count); // drives + "Pick a folder…"
        });
    }

    private static void RunSta(Action action)
    {
        ExceptionDispatchInfo? error = null;
        var t = new Thread(() => { try { action(); } catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        error?.Throw();
    }

    private sealed class Services : Dictionary<Type, object>, IServiceProvider
    {
        public object? GetService(Type serviceType) => TryGetValue(serviceType, out var s) ? s : null;
    }

    private sealed class RecordingFlowHost : IFlowHost
    {
        public List<ActionPlan> Plans { get; } = new();
        public Task<RunRecord?> ReviewAndRunAsync(ActionPlan plan, CancellationToken ct) { Plans.Add(plan); return Task.FromResult<RunRecord?>(null); }
        public Task<RunRecord?> SimulateAsync(ActionPlan plan, CancellationToken ct) => Task.FromResult<RunRecord?>(null);
        public Task<RunRecord?> RunApprovedAsync(ActionPlan plan, CancellationToken ct) => Task.FromResult<RunRecord?>(null);
        public void OpenInExplorer(string path) { }
        public void ShowMessage(string title, string message, bool isError = false) { }
    }
}
