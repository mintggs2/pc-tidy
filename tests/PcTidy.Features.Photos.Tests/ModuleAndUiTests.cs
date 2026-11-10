using System.Windows;
using System.Windows.Threading;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Photos.Photos;
using PcTidy.Features.Photos.Shared;
using PcTidy.Features.Photos.UI;
using PcTidy.TestbedGen;
using PcTidy.UI.Flow;
using Xunit;

namespace PcTidy.Features.Photos.Tests;

/// <summary>Module contracts, and the real XAML pages driven on an STA thread through a shell that runs the real engine.</summary>
public class ModuleAndUiTests
{
    [Fact]
    public void Modules_and_scanners_match_the_brief()
    {
        var photos = new PhotosModule();
        Assert.Equal(("photos", "Photos", NavGroup.Organize, "Image24", Release.V2), (photos.Id, photos.Title, photos.Group, photos.Icon, photos.Release));
        var desktop = new DesktopTidyModule();
        Assert.Equal(("desktoptidy", "Desktop tidy", NavGroup.Organize, "Desktop24", Release.V2), (desktop.Id, desktop.Title, desktop.Group, desktop.Icon, desktop.Release));
        foreach (var m in new IFeatureModule[] { photos, desktop })
        {
            var scanner = Assert.Single(m.Scanners);
            Assert.Equal(m.Id, scanner.FeatureId);
            Assert.False(scanner.IncludedInOptimizeAll);
            Assert.Equal(Release.V2, scanner.Release);
            Assert.False(string.IsNullOrWhiteSpace(m.Summary));
        }
        Assert.IsType<Photos.PhotosScanner>(photos.Scanners[0]);
        Assert.IsType<Desktop.DesktopTidyScanner>(desktop.Scanners[0]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Every_group_and_item_explains_itself_and_check_items_are_never_pre_ticked(bool defaultProfile)
    {
        using var pc = TestPc.Generated(defaultProfile ? "contract-default" : "contract-minimal", defaultProfile ? SandboxProfile.Default : SandboxProfile.Minimal);
        new PhotoSettings { Folders = new() { pc.K.Pictures, pc.K.Downloads }, IncludeSubfolders = true }.Store(pc.Host.Settings);
        foreach (var m in new IFeatureModule[] { new PhotosModule(), new DesktopTidyModule() })
        {
            var result = await m.Scanners[0].ScanAsync(pc.Context(ScanDepth.Deep), null, CancellationToken.None);
            Assert.NotEmpty(result.Groups);
            foreach (var g in result.Groups)
            {
                Assert.False(string.IsNullOrWhiteSpace(g.Description), g.Id);
                foreach (var i in g.Items)
                {
                    Assert.False(string.IsNullOrWhiteSpace(i.Reason), i.Title);
                    if (i.Badge != Badge.Safe) Assert.False(i.DefaultSelected, i.Title);
                    if (i.DisabledReason is not null) Assert.False(i.DefaultSelected, i.Title);
                    Assert.All(i.Actions, a =>
                    {
                        Assert.False(string.IsNullOrWhiteSpace(a.Description));
                        Assert.Equal(SortPermissions.PlanItemFeatureId, a.FeatureId);
                        Assert.True(a.Reversible);
                        Assert.Contains(a.Kind, new[] { ActionKind.CreateDirectory, ActionKind.MovePath, ActionKind.QuarantinePath });
                    });
                    // A thumbnail is only ever offered for a file that's really on disk (never an online-only file).
                    if (i.PreviewPath is { } preview) Assert.False(CloudFile.IsPlaceholder(File.GetAttributes(preview)), preview);
                }
            }
            // On the Default profile Pictures and Desktop are in OneDrive and not allowed: nothing can be ticked there.
            if (defaultProfile)
                Assert.All(result.AllItems.Where(i => i.Actions.Count > 0 && PathUtil.IsUnder(i.Actions.Last().SourcePath!, pc.K.UserProfile + "\\OneDrive")),
                    i => Assert.NotNull(i.DisabledReason));
        }
    }

    // ------------------------------------------------------------------ pages

    /// <summary>The shell's flow host, but running plans with the sandbox's real engine (no dialogs).</summary>
    private sealed class EngineShell(TestPc pc) : IShellFlowHost
    {
        public List<ActionPlan> Reviewed { get; } = new();
        public List<string> Navigated { get; } = new();
        public int QuarantineDays => 7;

        public async Task<RunRecord?> ReviewAndRunAsync(ActionPlan plan, CancellationToken ct)
        {
            Reviewed.Add(plan);
            return await pc.Host.Executor.ExecuteAsync(plan, TestPc.Real, null, ct);
        }

        public Task<RunRecord?> SimulateAsync(ActionPlan plan, CancellationToken ct)
            => pc.Host.Executor.ExecuteAsync(plan, TestPc.Real with { Mode = ExecutionMode.Simulate }, null, ct)!;

        public Task<RunRecord?> RunApprovedAsync(ActionPlan plan, CancellationToken ct) => throw new InvalidOperationException("Pages never run without a confirm.");
        public void OpenInExplorer(string path) { }
        public void ShowMessage(string title, string message, bool isError = false) => throw new Xunit.Sdk.XunitException($"{title}: {message}");
        public Task<FlowOutcome> ReviewAndRunAsync(IReadOnlyList<ActionPlan> plans, FlowOptions options, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<RunRecord>> SimulateAsync(IReadOnlyList<ActionPlan> plans, FlowOptions options, CancellationToken ct) => throw new NotSupportedException();

        public async Task<bool> UndoRunsAsync(IReadOnlyList<RunRecord> runs, CancellationToken ct)
        {
            bool all = true;
            foreach (var r in runs.Reverse()) all &= (await pc.Host.Undo.UndoRunAsync(r.RunId, null, ct)).Complete;
            return all;
        }

        public void NavigateTo(string pageKey) => Navigated.Add(pageKey);
    }

    private sealed class Services(TestPc pc, IShellFlowHost shell) : IServiceProvider
    {
        public object? GetService(Type t) => t == typeof(IFlowHost) || t == typeof(IShellFlowHost) ? shell : pc.Host.GetService(t);
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try { action(); } catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException("UI failed: " + error);
    }

    /// <summary>Runs the dispatcher until the task finishes (async commands continue on the UI thread).</summary>
    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    /// <summary>Lets queued UI work (the coalesced totals refresh) run.</summary>
    private static void DoEvents() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

    private static void Layout(FrameworkElement view, double width, double height)
    {
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        view.UpdateLayout();
    }

    [Fact]
    public void Photos_page_asks_first_for_onedrive_then_sorts_and_undoes()
    {
        using var pc = TestPc.Generated("ui-photos", SandboxProfile.Default);
        var before = TestbedBuilder.Manifest(pc.Root);
        var shell = new EngineShell(pc);
        RunSta(() =>
        {
            var view = (FrameworkElement)new PhotosModule().CreatePage(new Services(pc, shell))!;
            Assert.IsAssignableFrom<IFixedHeightPage>(view);
            Layout(view, 1000 - 280, 700); // a 1000 px window minus the navigation rail
            var vm = (PhotosViewModel)view.DataContext;
            Assert.Contains(vm.Folders, f => f.Label == "Pictures" && f.IsChecked && f.Note is not null);
            vm.IncludeSubfolders = true;

            Pump(vm.ScanCommand.ExecuteAsync(null));
            Assert.True(vm.HasPreview);
            Assert.Equal(2, vm.Step);
            Assert.NotNull(vm.PermissionMessage);
            Assert.Contains("nothing is uploaded again", vm.PermissionMessage);
            Assert.All(vm.AllRows, r => Assert.False(r.IsSelectable));
            Assert.False(vm.RunCommand.CanExecute(null));

            Pump(vm.AllowCommand.ExecuteAsync(null));
            Assert.True(CloudFolders.IsAllowed(pc.K.Pictures, pc.Host.Settings));
            Assert.Null(vm.PermissionMessage);
            Layout(view, 720, 700);
            DoEvents();
            Assert.StartsWith("Selected: Sort", vm.SelectedText);
            Assert.NotEmpty(vm.AfterRows);
            vm.ViewIndex = 1;
            Layout(view, 720, 700);
            vm.ViewIndex = 2;
            Layout(view, 720, 700);
            vm.ViewIndex = 0;

            // Ticking a flagged copy's quarantine unticks its move (they're alternatives for one file).
            var flag = vm.AllRows.First(r => r.Badge == Badge.Check);
            var move = vm.AllRows.Single(r => r.PairKey == flag.PairKey && !ReferenceEquals(r, flag));
            Assert.True(move.IsChecked);
            flag.IsChecked = true;
            Assert.False(move.IsChecked);
            DoEvents();
            Assert.Contains("clean", vm.SelectedText, StringComparison.OrdinalIgnoreCase);

            Pump(vm.SimulateCommand.ExecuteAsync(null));
            Pump(vm.RunCommand.ExecuteAsync(null));
            var plan = Assert.Single(shell.Reviewed);
            Assert.Equal(PhotosModule.FeatureId, plan.FeatureId);
            Assert.Equal(4, vm.Step);
            Assert.True(vm.IsDone);
            Assert.Contains("moved", vm.DoneText);
            Assert.True(vm.CanUndo);
            Pump(vm.UndoCommand.ExecuteAsync(null));
            Assert.False(vm.CanUndo);
        });
        var diff = pc.DiffFrom(before);
        Assert.True(diff.Identical, diff.Format());
    }

    [Fact]
    public void Desktop_page_loads_scans_and_hands_the_plan_to_the_shell()
    {
        using var pc = TestPc.Generated("ui-desktop", SandboxProfile.Minimal);
        var before = TestbedBuilder.Manifest(pc.Root);
        var shell = new EngineShell(pc);
        RunSta(() =>
        {
            var view = (FrameworkElement)new DesktopTidyModule().CreatePage(new Services(pc, shell))!;
            Layout(view, 720, 700);
            var vm = (DesktopTidyViewModel)view.DataContext;
            Assert.False(vm.DesktopInCloud);
            Assert.Equal(0, vm.ModeIndex);

            Pump(vm.ScanCommand.ExecuteAsync(null));
            Assert.True(vm.HasPreview);
            Assert.Null(vm.PermissionMessage);
            Assert.StartsWith("Move ", vm.PrimaryText);
            Assert.Contains(vm.SkippedRows, s => s.Name == "desktop.ini");
            Assert.NotEmpty(vm.BeforeRows);

            // Changing the mode clears the preview (it no longer says what would happen).
            vm.ModeIndex = 1;
            Assert.False(vm.HasPreview);
            Assert.Equal(Desktop.DesktopTidyMode.GroupByType, Desktop.DesktopTidySettings.Load(pc.Host.Settings).Mode);
            Pump(vm.ScanCommand.ExecuteAsync(null));
            Layout(view, 720, 700);

            Pump(vm.RunCommand.ExecuteAsync(null));
            var plan = Assert.Single(shell.Reviewed);
            Assert.Equal(DesktopTidyModule.FeatureId, plan.FeatureId);
            Assert.All(plan.Items, i => Assert.True(PathUtil.IsUnder(i.DestinationPath!, pc.K.Desktop)));
            Pump(vm.UndoCommand.ExecuteAsync(null));
        });
        var diff = pc.DiffFrom(before);
        Assert.True(diff.Identical, diff.Format());
    }
}
