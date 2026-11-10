using System.Windows;
using System.Windows.Threading;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Organize.Rules;
using PcTidy.Features.Organize.Triage;
using PcTidy.Features.Organize.UI;
using Xunit;

namespace PcTidy.Features.Organize.Tests;

/// <summary>The v3 pieces load from real XAML and hand ONE plan to the shell (presets, keyboard triage, Google Takeout).</summary>
public class V3UiTests
{
    private sealed class Services(Sandbox sb, IFlowHost flow) : IServiceProvider
    {
        public object? GetService(Type t)
            => t == typeof(IKnownLocations) ? sb.Locations
             : t == typeof(ISafetyGuard) ? sb.Safety
             : t == typeof(IInUseChecker) ? sb.InUse
             : t == typeof(IFileWalker) ? new ManagedFileWalker()
             : t == typeof(AppSettings) ? sb.AppSettings
             : t == typeof(IFlowHost) ? flow
             : null;
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

    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    [Fact]
    public void Triage_window_turns_keys_into_one_plan_for_the_confirm()
    {
        using var sb = new Sandbox(SandboxProfile.Minimal);
        sb.File("a.jpg", ageDays: 1); sb.File("b.pdf", ageDays: 2);
        var flow = new FakeFlowHost();
        RunSta(() =>
        {
            var view = (FrameworkElement)new OrganizeModule().CreatePage(new Services(sb, flow))!;
            var vm = (OrganizeViewModel)view.DataContext;
            var session = vm.CreateTriageSession();
            var triage = new TriageViewModel(session);
            var window = new TriageWindow(triage);
            window.Measure(new Size(900, 680));
            window.Arrange(new Rect(0, 0, 900, 680));
            Assert.Equal("a.jpg", triage.FileName);
            Assert.StartsWith("1 of 2:", triage.Announcement);
            Assert.Equal(9, triage.Targets.Count);
            Assert.StartsWith("1: move to", triage.Targets[0].AutomationName);

            bool finished = false;
            triage.Finished += () => finished = true;
            triage.Press(TriageKey.Target1 + (session.Targets.Single(t => t.Label == "Pictures").Number - 1));
            Assert.Equal("b.pdf", triage.FileName);
            triage.Press(TriageKey.Rename);
            Assert.True(triage.IsRenaming);
            Assert.Equal(TriageOutcome.Ignored, triage.Press(TriageKey.Keep)); // typing a name: keys go to the box
            triage.RenameText = "report";
            triage.ConfirmRenameCommand.Execute(null);
            Assert.False(triage.IsRenaming);
            Assert.Equal(2, triage.Decisions.Count);
            triage.FinishCommand.Execute(null);
            Assert.True(finished);

            Pump(vm.RunTriageAsync(session));
            var plan = Assert.Single(flow.Reviewed);
            Assert.Contains(plan.Items, i => i.Kind == ActionKind.MovePath && i.DestinationPath!.EndsWith(@"Pictures\a.jpg"));
            Assert.Contains(plan.Items, i => i.Kind == ActionKind.RenamePath && i.DestinationPath!.EndsWith("report.pdf"));
            window.Close();
        });
    }

    [Fact]
    public void Presets_window_loads_and_applying_a_preset_replaces_the_rules()
    {
        using var sb = new Sandbox(SandboxProfile.Minimal);
        var flow = new FakeFlowHost();
        RunSta(() =>
        {
            var view = (FrameworkElement)new OrganizeModule().CreatePage(new Services(sb, flow))!;
            var vm = (OrganizeViewModel)view.DataContext;
            var presets = new PresetsViewModel(vm.Rules.Select(r => r.Rule).ToList());
            var window = new PresetsWindow(presets);
            window.Measure(new Size(720, 620));
            window.Arrange(new Rect(0, 0, 720, 620));
            presets.SelectedPreset = RulePresets.Find(RulePresets.JohnnyDecimalId);
            presets.ApplyCommand.Execute(null);
            vm.ApplyRules(presets.Result!);
            Assert.Contains(vm.Rules, r => r.Target == @"20-29 Media\21 Pictures");
            Assert.Contains(OrganizeSettings.Load(sb.AppSettings).Rules, r => r.Action.Destination == @"20-29 Media\21 Pictures");
            window.Close();
        });
    }

    [Fact]
    public void Takeout_tab_scans_and_hands_one_plan_to_the_shell()
    {
        using var sb = new Sandbox(SandboxProfile.Minimal);
        var photos = Path.Combine(sb.Downloads, "Takeout", "Google Photos", "Photos from 2020");
        sb.File(Path.Combine(photos, "IMG_1.jpg"), "photo bytes", ageDays: 30);
        sb.File(Path.Combine(photos, "IMG_1.jpg.json"), "{\"title\":\"IMG_1.jpg\",\"photoTakenTime\":{\"timestamp\":\"1590000000\"}}", ageDays: 30);
        var flow = new FakeFlowHost();
        RunSta(() =>
        {
            var view = (FrameworkElement)new OrganizeModule().CreatePage(new Services(sb, flow))!;
            view.Measure(new Size(1200, 800));
            view.Arrange(new Rect(0, 0, 1200, 800));
            var vm = ((OrganizeViewModel)view.DataContext).Takeout;
            Assert.EndsWith(@"Pictures\Google Photos", vm.DestinationFolder);
            Assert.False(vm.ScanCommand.CanExecute(null));
            vm.SourceFolder = sb.Downloads;
            Pump(vm.ScanCommand.ExecuteAsync(null));
            Assert.True(vm.HasPreview);
            Assert.Equal("Sort 1 photo", vm.SortButtonText);
            Assert.Contains("1 info file moves along", vm.SummaryDetail);
            Pump(vm.SimulateCommand.ExecuteAsync(null));
            Pump(vm.SortCommand.ExecuteAsync(null));
            var plan = Assert.Single(flow.Reviewed);
            Assert.Equal(2, plan.Items.Count(i => i.Kind == ActionKind.MovePath));
            Assert.Equal(ActionKind.CreateDirectory, plan.Items[0].Kind);
        });
    }
}
