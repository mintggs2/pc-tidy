using System.Windows;
using System.Windows.Threading;
using PcTidy.Core;
using PcTidy.Features.Organize.Rules;
using PcTidy.Features.Organize.UI;
using Xunit;

namespace PcTidy.Features.Organize.Tests;

/// <summary>Loads the real XAML on an STA thread: XAML mistakes only show up at runtime otherwise.</summary>
public class UiSmokeTests
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

    /// <summary>Runs the dispatcher until <paramref name="task"/> finishes (async commands continue on the UI thread).</summary>
    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    [Fact]
    public void Organize_page_loads_scans_and_hands_the_plan_to_the_shell()
    {
        using var sb = new Sandbox(SandboxProfile.Minimal);
        sb.File("a.jpg"); sb.File("b.pdf"); sb.File("c.lnk");
        var flow = new FakeFlowHost();
        RunSta(() =>
        {
            var module = new OrganizeModule();
            var view = (FrameworkElement)module.CreatePage(new Services(sb, flow))!;
            view.Measure(new Size(1200, 800));
            view.Arrange(new Rect(0, 0, 1200, 800));
            var vm = (OrganizeViewModel)view.DataContext;

            Assert.Equal("Organize Downloads", vm.Title);
            Assert.Contains(vm.Folders, f => f.Label == "Desktop" && f.Note is null); // Minimal PC: Desktop isn't in OneDrive
            Pump(vm.ScanCommand.ExecuteAsync(null));
            Assert.True(vm.HasPreview);
            Assert.NotEmpty(vm.AfterRows);
            Assert.Contains(vm.SkippedRows, s => s.Name == "c.lnk");
            Assert.True(vm.MoveCommand.CanExecute(null));
            view.UpdateLayout();

            // Standard flow page: fixed height (only lists scroll), shared step bar on step 2, Quick/Deep switch.
            Assert.IsAssignableFrom<PcTidy.UI.Flow.IFixedHeightPage>(view);
            Assert.Equal(2, vm.Step);
            Assert.StartsWith("3 items found|", vm.StepCaptions);
            vm.DepthIndex = 1;
            Assert.True(vm.IsDeep);
            Assert.False(vm.HasPreview); // a new depth needs a new scan
            vm.DepthIndex = 0;
            Pump(vm.ScanCommand.ExecuteAsync(null));
            vm.DetailsIndex = 1;
            Assert.True(vm.ShowDetails);
            vm.DetailsIndex = 0;

            // Wide: rules beside the preview. Narrow (a 1000 px window): rules stacked above it.
            var preview = (FrameworkElement)view.FindName("PreviewCard");
            Assert.Equal(2, System.Windows.Controls.Grid.GetColumn(preview));
            view.Measure(new Size(700, 700));
            view.Arrange(new Rect(0, 0, 700, 700));
            view.UpdateLayout();
            Assert.Equal(2, System.Windows.Controls.Grid.GetRow(preview));
            Assert.Equal(0, System.Windows.Controls.Grid.GetColumn(preview));
            view.Measure(new Size(1200, 800));
            view.Arrange(new Rect(0, 0, 1200, 800));
            view.UpdateLayout();

            Pump(vm.SimulateCommand.ExecuteAsync(null));
            Pump(vm.MoveCommand.ExecuteAsync(null));
            var plan = Assert.Single(flow.Reviewed);
            Assert.Equal(2, plan.Items.Count(i => i.Kind == Core.Model.ActionKind.MovePath));

            // Reorder and toggle rules: settings are stored for the shell to save.
            var first = vm.Rules[0];
            vm.MoveRuleTo(first, 2);
            first.Enabled = false;
            var stored = OrganizeSettings.Load(sb.AppSettings);
            Assert.Equal(first.Rule.Id, stored.Rules[2].Id);
            Assert.False(stored.Rules[2].Enabled);

            // Rename tab.
            Pump(vm.Rename.LoadCommand.ExecuteAsync(null));
            Assert.NotEmpty(vm.Rename.Rows);
        });
    }

    [Fact]
    public void Rule_editor_loads_tests_and_validates()
    {
        using var sb = new Sandbox();
        sb.File("invoice.pdf");
        RunSta(() =>
        {
            var rule = DefaultRules.Create()[0];
            var vm = new RuleEditorViewModel(rule, false,
                r => RuleValidator.Validate(r, new[] { sb.Downloads }, sb.Safety, sb.AppSettings),
                _ => Task.FromResult(new RuleTestResult(new[] { "invoice.pdf → Downloads\\Documents\\Invoices" }, 0, Array.Empty<string>())));
            var window = new RuleEditorWindow { DataContext = vm };
            window.Measure(new Size(760, 720));
            window.Arrange(new Rect(0, 0, 760, 720));

            Assert.Equal(2, vm.Conditions.Count);
            Pump(vm.TestCommand.ExecuteAsync(null));
            Assert.Single(vm.TestMatches);
            Assert.Contains("Nothing was moved", vm.TestSummary);

            vm.Destination = "";
            vm.SaveCommand.Execute(null);
            Assert.Null(vm.Result);
            Assert.NotEmpty(vm.Problems);

            vm.Destination = @"Documents\Bills";
            vm.Conditions[0].ValuesText = "pdf, PDFX";
            var built = vm.BuildRule();
            Assert.Equal(new[] { "pdf", "PDFX" }, built.Conditions.Conditions[0].Values);
            Assert.Equal(@"Documents\Bills", built.Action.Destination);
            window.Close();
        });
    }
}
