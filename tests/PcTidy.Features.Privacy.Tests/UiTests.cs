using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using PcTidy.Features.Privacy.Sweep;
using PcTidy.Features.Privacy.UI;
using PcTidy.UI.Flow;

namespace PcTidy.Features.Privacy.Tests;

/// <summary>Both pages built for real on an STA thread (WPF-UI + PC Tidy themes), laid out at 1000 px, scanned, and their shortcuts used.</summary>
public class UiTests
{
    private sealed class FakeShell : IShellFlowHost
    {
        public List<string> Navigated { get; } = new();
        public List<ActionPlan> Approved { get; } = new();
        public int QuarantineDays => 7;
        public Task<RunRecord?> ReviewAndRunAsync(ActionPlan plan, CancellationToken ct) => Task.FromResult<RunRecord?>(null);
        public Task<RunRecord?> SimulateAsync(ActionPlan plan, CancellationToken ct) => Task.FromResult<RunRecord?>(null);
        public Task<RunRecord?> RunApprovedAsync(ActionPlan plan, CancellationToken ct) { Approved.Add(plan); return Task.FromResult<RunRecord?>(null); }
        public void OpenInExplorer(string path) { }
        public void ShowMessage(string title, string message, bool isError = false) => throw new Xunit.Sdk.XunitException($"{title}: {message}");
        public Task<FlowOutcome> ReviewAndRunAsync(IReadOnlyList<ActionPlan> plans, FlowOptions options, CancellationToken ct) => Task.FromResult(FlowOutcome.Cancelled);
        public Task<IReadOnlyList<RunRecord>> SimulateAsync(IReadOnlyList<ActionPlan> plans, FlowOptions options, CancellationToken ct) => Task.FromResult<IReadOnlyList<RunRecord>>(Array.Empty<RunRecord>());
        public Task<bool> UndoRunsAsync(IReadOnlyList<RunRecord> runs, CancellationToken ct) => Task.FromResult(true);
        public void NavigateTo(string pageKey) => Navigated.Add(pageKey);
    }

    private sealed class Services(TestPc pc, IShellFlowHost shell) : IServiceProvider
    {
        public object? GetService(Type t) => t == typeof(IFlowHost) || t == typeof(IShellFlowHost) ? shell : pc.Host.GetService(t);
    }

    /// <summary>One STA thread for both pages: WPF's Application (and its resources) belongs to the thread that made it.</summary>
    [Fact]
    public void Both_pages_lay_out_at_1000px_scan_and_their_shortcuts_and_keep_list_work()
    {
        using var privacyPc = TestPc.Generated("ui-privacy", SandboxProfile.Default);
        using var notificationsPc = TestPc.HandMade("ui-notifications");
        RunSta(() =>
        {
            PrivacyPage(privacyPc);
            NotificationsPage(notificationsPc);
        });
    }

    private static void PrivacyPage(TestPc pc)
    {
        var shell = new FakeShell();
        var page = (PrivacyFlowPage)new PrivacyModule().CreatePage(new Services(pc, shell))!;
        Assert.IsAssignableFrom<IFixedHeightPage>(page);
        Layout(page, 1000 - 280, 700);

        Pump(page.Flow.StartScanAsync());
        Assert.Equal(FlowState.Preview, page.Flow.State);
        Assert.True(page.Flow.Selection.TotalItems > 0);
        Assert.Empty(page.Flow.Selection.CheckedActions()); // nothing ticked
        Layout(page, 720, 700);

        NavigationRequestedCheck(page, shell);
        Click(page, "Clipboard history");
        Assert.Equal("ms-settings:clipboard", shell.Approved.Single().Items.Single().Args!["target"]);

        var github = Find<CheckBox>(page).Single(c => (string)c.Content == "github.com");
        github.IsChecked = true;
        Assert.Contains("github.com", PrivacySettings.Load(pc.Host.Settings).KeepHosts());
        github.IsChecked = false;
        Assert.Empty(PrivacySettings.Load(pc.Host.Settings).KeepHosts());
    }

    private static void NotificationsPage(TestPc pc)
    {
        var shell = new FakeShell();
        var page = (PrivacyFlowPage)new NotificationsModule().CreatePage(new Services(pc, shell))!;
        Layout(page, 1000 - 280, 700);
        Pump(page.Flow.StartScanAsync());
        Assert.Equal(FlowState.Preview, page.Flow.State);
        // Sandbox: tips rows (all on in an empty test area) and the AI rows ("Not on this PC").
        Assert.True(page.Flow.Selection.TotalItems >= TipsCount());
        Assert.Empty(page.Flow.Selection.CheckedActions());
        Click(page, "Notification settings");
        Assert.Equal("ms-settings:notifications", shell.Approved.Single().Items.Single().Args!["target"]);
    }
    private static int TipsCount() => PcTidy.Catalog.Privacy.TipsCatalog.All.Count;

    private static void NavigationRequestedCheck(PrivacyFlowPage page, FakeShell shell)
    {
        PcTidy.Features.Privacy.Common.NavigationRequest? raised = null;
        page.NavigationRequested += r => raised = r;
        Click(page, "Browser history");
        Assert.Equal("cleanup.browser-privacy", raised?.GroupId);
        Assert.Equal("cleanup", shell.Navigated.Single());
    }

    private static void Click(DependencyObject root, string label)
    {
        var button = Find<ButtonBase>(root).First(b => Equals(b.Content, label));
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        DoEvents();
    }

    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T t) yield return t;
            foreach (var d in Find<T>(child)) yield return d;
        }
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try { EnsureAppResources(); action(); } catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException("UI failed: " + error);
    }

    private static void EnsureAppResources()
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (app.Resources.Contains("TidyCard")) return;
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Dark });
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/PcTidy.UI;component/Themes/TidyDark.xaml") });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/PcTidy.UI;component/Themes/Tidy.xaml") });
    }

    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private static void DoEvents() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

    private static void Layout(FrameworkElement view, double width, double height)
    {
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        view.UpdateLayout();
        Assert.True(view.DesiredSize.Width <= width + 0.5, $"Page wants {view.DesiredSize.Width} px at {width} px");
    }
}
