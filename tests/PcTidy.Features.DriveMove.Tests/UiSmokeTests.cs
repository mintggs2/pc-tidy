using System.Windows;
using System.Windows.Controls;
using PcTidy.Core;
using PcTidy.Features.DriveMove.UI;
using Xunit;

namespace PcTidy.Features.DriveMove.Tests;

/// <summary>The custom page loads with the app's resources and lays out at 1000 px wide, on a two-drive and a one-drive PC.</summary>
[Collection("drivemove-testbed")]
public class UiSmokeTests
{
    [Fact]
    public void Page_loads_every_tab_at_1000px_and_one_drive_PCs_get_not_available()
    {
        using var two = new DriveMoveSandbox("ui-two");
        using var one = new DriveMoveSandbox("ui-one", SandboxProfile.Minimal);
        Exception? failure = null;
        // WPF resources belong to the thread that made the Application: both pages are built on one STA thread.
        var thread = new Thread(() =>
        {
            try
            {
                EnsureAppResources();
                CheckPage(two, available: true);
                CheckPage(one, available: false);
                CheckResultsLayout(two);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static void CheckPage(DriveMoveSandbox s, bool available)
    {
        var page = (DriveMovePage)new DriveMoveModule().CreatePage(s.Host)!;
        var tabs = (PcTidy.UI.Controls.Segmented)page.FindName("Tabs");
        var host = (ContentControl)page.FindName("TabHost");
        for (int i = 0; i < 5; i++)
        {
            tabs.SelectedIndex = i;
            Layout(page);
            var tab = Assert.IsType<DriveMoveTabView>(host.Content);
            Assert.True(tab.DesiredSize.Width <= 1000);
            var ready = (PcTidy.UI.Controls.EmptyState)tab.FindName("ReadyEmpty");
            if (available) Assert.NotEqual("Not available on this PC", ready.Title);
            else
            {
                Assert.Equal("Not available on this PC", ready.Title);
                Assert.Contains("one drive", ready.Message);
            }
        }
    }

    /// <summary>A real scan result shown in a tab (results list, drive strip, sticky bar) still fits 1000 px.</summary>
    private static void CheckResultsLayout(DriveMoveSandbox s)
    {
        s.Put(Path.Combine(s.K.Videos, "clip.mp4"), DriveMoveSandbox.Bytes(5000, 1));
        var scanner = new DriveBalanceScanner();
        var result = scanner.ScanAsync(s.Context(), null, CancellationToken.None).GetAwaiter().GetResult();
        var tab = new DriveMoveTabView(s.Host, new TabSpec("Drive balance", "blurb", scanner, true, "", ShowDriveStrip: true));
        tab.Load(result);
        Layout(tab);
        Assert.True(tab.DesiredSize.Width <= 1000);
        var strip = (WrapPanel)tab.FindName("DriveStrip");
        Assert.Equal(2, strip.Children.Count);
        Assert.Equal(Visibility.Visible, ((FrameworkElement)tab.FindName("StickyBar")).Visibility);
    }

    private static void Layout(FrameworkElement e)
    {
        e.Measure(new Size(1000, 700));
        e.Arrange(new Rect(0, 0, 1000, 700));
        e.UpdateLayout();
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
}
