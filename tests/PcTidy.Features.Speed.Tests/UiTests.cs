using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PcTidy.Features.Speed.UI;

namespace PcTidy.Features.Speed.Tests;

/// <summary>
/// One long-lived STA thread with a running dispatcher for every WPF test: the Application (and its resources)
/// belongs to that thread, and async page code can finish its continuations there.
/// </summary>
public static class Sta
{
    private static readonly Lazy<Dispatcher> Ui = new(() =>
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Dark });
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/PcTidy.UI;component/Themes/TidyDark.xaml") });
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/PcTidy.UI;component/Themes/Tidy.xaml") });
            ready.Set();
            Dispatcher.Run();
        }) { IsBackground = true, Name = "speed-tests-ui" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return dispatcher!;
    });

    public static T Run<T>(Func<T> work) => Ui.Value.Invoke(work);
    public static void Run(Action work) => Ui.Value.Invoke(work);

    /// <summary>Lets queued work (scan continuations, layout) run for a while.</summary>
    public static void Pump(TimeSpan time)
    {
        var until = DateTime.UtcNow + time;
        while (DateTime.UtcNow < until)
        {
            Ui.Value.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(25);
        }
    }
}

public class UiTests
{
    [Fact]
    public void Speed_page_loads_every_tab_at_1000px()
    {
        using var sb = new SpeedSandbox(SandboxProfile.Default);
        var page = Sta.Run(() => (SpeedPage)new SpeedModule().CreatePage(sb.Host)!);
        for (var i = 0; i < 6; i++)
        {
            var index = i;
            Sta.Run(() =>
            {
                ((PcTidy.UI.Controls.Segmented)page.FindName("Tabs")).SelectedIndex = index;
                page.Measure(new Size(1000, 700));
                page.Arrange(new Rect(0, 0, 1000, 700));
                page.UpdateLayout();
            });
            Sta.Pump(TimeSpan.FromMilliseconds(400)); // auto-scans run against the sandbox (reads nothing real)
            Sta.Run(() =>
            {
                page.Measure(new Size(1000, 700));
                page.Arrange(new Rect(0, 0, 1000, 700));
                page.UpdateLayout();
                var tab = Assert.IsType<SpeedTabView>(((ContentControl)page.FindName("TabHost")).Content);
                Assert.True(tab.DesiredSize.Width <= 1000, $"tab {index} is {tab.DesiredSize.Width} px wide");
                Assert.False(string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(tab)));
            });
        }

        // The Settings tab scanned by itself and shows its cards (the sandbox has a Balanced plan and Game Mode on).
        Sta.Run(() => ((PcTidy.UI.Controls.Segmented)page.FindName("Tabs")).SelectedIndex = 2);
        Sta.Pump(TimeSpan.FromMilliseconds(600));
        Sta.Run(() =>
        {
            var tab = (SpeedTabView)((ContentControl)page.FindName("TabHost")).Content;
            Assert.Equal(Visibility.Visible, ((FrameworkElement)tab.FindName("ResultsState")).Visibility);
        });
    }

    [Fact]
    public void Sticky_bar_caption_says_what_happens()
    {
        Assert.Equal("", SpeedTabView.Caption(Array.Empty<PlanItem>()));
        var close = Common.SpeedPlanItems.CloseApp("Editor", 1234, "editor", null);
        Assert.StartsWith("Apps are asked to close", SpeedTabView.Caption(new[] { close }));
        var service = Common.SpeedPlanItems.ServiceToManual("Helper", "HelperSvc");
        Assert.Contains("asks for admin once", SpeedTabView.Caption(new[] { service }));
    }
}
