using Microsoft.Win32;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Apps.Inventory;
using PcTidy.Features.Apps.UI;
using PcTidy.Hosting;
using Xunit;

namespace PcTidy.Features.Apps.Tests;

/// <summary>The Apps page's view model end to end on a sandbox: list → uninstall → leftovers → quarantine.</summary>
public sealed class PageFlowTests : IDisposable
{
    private readonly Sandbox _box = new(SandboxProfile.Minimal);
    private readonly FakeRegistry _reg = new();
    private readonly PcTidyHost _host;
    private readonly FakeFlowHost _flow;

    public PageFlowTests()
    {
        _host = PcTidyHost.CreateSandbox(_box.Root, SandboxProfile.Minimal);
        _flow = new FakeFlowHost(_host);
        _host.Add<IFlowHost>(_flow);
    }

    public void Dispose()
    {
        _host.Dispose();
        _reg.Dispose();
        _box.Dispose();
    }

    private AppsViewModel ViewModel(FakeStore? store = null)
        => new(_host, new AppInventorySources { Locations = _box.Locations, Registry = _reg.Reader, Store = store ?? new FakeStore() });

    [Fact]
    public async Task Search_filter_and_sort_work_on_the_list()
    {
        var big = _box.Folder(Path.Combine(_box.Locations.ProgramFiles, "Big App"), "a.bin", "b.bin", "c.bin");
        _reg.UninstallEntry("HKLM64", "Big", ("DisplayName", "Big App"), ("InstallLocation", big), ("UninstallString", $"\"{big}\\u.exe\""));
        _reg.UninstallEntry("HKLM64", "Small", ("DisplayName", "Small App"), ("EstimatedSize", 1), ("UninstallString", "MsiExec.exe /X{EEEEEEEE-2222-3333-4444-555555555555}"));
        var vm = ViewModel(new FakeStore().Add("Clipchamp.Clipchamp", "Clipchamp"));
        await vm.LoadAsync();

        Assert.Equal(3, vm.Rows.Count);
        Assert.Equal("Big App", vm.Rows[0].Name); // measured 3.6 KB beats the 1 KB the small app reported
        Assert.Equal("measured", vm.Rows[0].SizeCaption);
        vm.FilterIndex = AppsViewModel.FilterPreinstalled;
        Assert.Equal("Clipchamp", Assert.Single(vm.Rows).Name);
        vm.FilterIndex = AppsViewModel.FilterAll;
        vm.SearchText = "small";
        Assert.Equal("Small App", Assert.Single(vm.Rows).Name);
        vm.SearchText = "";
        vm.SortIndex = AppsViewModel.SortName;
        Assert.Equal(new[] { "Big App", "Clipchamp", "Small App" }, vm.Rows.Select(r => r.Name));
    }

    [Fact]
    public async Task Uninstall_then_leftovers_then_quarantine()
    {
        var install = _box.Folder(Path.Combine(_box.Locations.ProgramFiles, "Contoso Editor"), "editor.exe");
        var leftover = _box.Folder(Path.Combine(_box.Locations.LocalAppData, "Contoso Software", "Contoso Editor"), "cache.bin");
        _reg.UninstallEntry("HKLM64", "ContosoEditor", ("DisplayName", "Contoso Editor"), ("Publisher", "Contoso Software"),
            ("InstallLocation", install), ("UninstallString", $"\"{install}\\editor.exe\" /uninstall"));
        // The fake "uninstaller": removes the entry the way a real one would.
        _flow.OnUninstall = () => Registry.CurrentUser.DeleteSubKeyTree($@"{_reg.Root}\HKLM64\{UninstallRegistryReader.UninstallPath}\ContosoEditor");

        var vm = ViewModel();
        await vm.LoadAsync();
        vm.Selected = vm.Rows.Single(r => r.Name == "Contoso Editor");
        Assert.True(vm.CanUninstall);
        Assert.Equal("Uninstall", vm.UninstallText);

        await vm.SimulateCommand.ExecuteAsync(null);
        Assert.Equal(ActionKind.RunUninstaller, Assert.Single(_flow.Simulated).Items.Single().Kind);

        await vm.UninstallCommand.ExecuteAsync(null);
        var uninstall = Assert.Single(_flow.Reviewed).Items.Single();
        Assert.True(uninstall.IsSystemChange);
        Assert.True(vm.ShowLeftovers);
        var row = Assert.Single(vm.Leftovers, l => l.Item.Detail!.Equals(leftover, StringComparison.OrdinalIgnoreCase));
        Assert.True(row.IsSelected); // very good match in Local AppData, allowed by the real guard

        await vm.RemoveLeftoversCommand.ExecuteAsync(null);
        Assert.False(Directory.Exists(leftover));
        Assert.False(vm.ShowLeftovers);
        Assert.Equal(RunStatus.Completed, _flow.Runs.Last().Status);
    }

    [Fact]
    public void The_page_builds_and_lays_out_at_1000_px()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var view = new AppsView(ViewModel());
                view.Measure(new System.Windows.Size(1000, 700));
                view.Arrange(new System.Windows.Rect(0, 0, 1000, 700));
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    /// <summary>Confirms everything; runs file plans with the real sandbox executor; fakes the uninstaller.</summary>
    private sealed class FakeFlowHost(PcTidyHost host) : IFlowHost
    {
        public List<ActionPlan> Reviewed { get; } = new();
        public List<ActionPlan> Simulated { get; } = new();
        public List<RunRecord> Runs { get; } = new();
        public Action? OnUninstall { get; set; }

        public async Task<RunRecord?> ReviewAndRunAsync(ActionPlan plan, CancellationToken ct)
        {
            Reviewed.Add(plan);
            RunRecord run;
            if (plan.Items.Any(i => i.Kind == ActionKind.RunUninstaller))
            {
                OnUninstall?.Invoke();
                run = new RunRecord
                {
                    RunId = plan.RunId, FeatureId = plan.FeatureId, Title = plan.Title, Status = RunStatus.Completed,
                    Steps = plan.Items.Select((i, n) => new StepRecord { Index = n, Item = i, Status = StepStatus.Done }).ToList(),
                };
            }
            else run = await host.Executor.ExecuteAsync(plan, new ExecuteOptions { Mode = ExecutionMode.Real }, null, ct);
            Runs.Add(run);
            return run;
        }

        public Task<RunRecord?> SimulateAsync(ActionPlan plan, CancellationToken ct)
        {
            Simulated.Add(plan);
            return Task.FromResult<RunRecord?>(null);
        }

        public Task<RunRecord?> RunApprovedAsync(ActionPlan plan, CancellationToken ct) => throw new InvalidOperationException("Apps never runs without a confirm.");
        public void OpenInExplorer(string path) { }
        public void ShowMessage(string title, string message, bool isError = false) { }
    }
}
