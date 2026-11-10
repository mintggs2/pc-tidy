using System.Windows;
using System.Windows.Controls;
using PcTidy.Core;
using PcTidy.Features.Games.UI;
using Xunit;

namespace PcTidy.Features.Games.Tests;

/// <summary>The custom page loads with the app's resources and lays out at 1000 px wide, on a full and on an empty PC.</summary>
[Collection("games-testbed")]
public class UiSmokeTests
{
    [Fact]
    public void Games_page_loads_every_tab_at_1000px_with_and_without_games()
    {
        using var full = new GamesSandbox("ui-full", SandboxProfile.Default, generate: true);
        using var empty = new GamesSandbox("ui-empty", SandboxProfile.Minimal, generate: true);
        Exception? failure = null;
        // WPF resources belong to the thread that made the Application: both pages are built on one STA thread.
        var thread = new Thread(() =>
        {
            try
            {
                EnsureAppResources();
                CheckPage(full, withGames: true);
                CheckPage(empty, withGames: false);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static void CheckPage(GamesSandbox s, bool withGames)
    {
        var page = (GamesPage)new GamesModule().CreatePage(s.Host)!;
        var tabs = (PcTidy.UI.Controls.Segmented)page.FindName("Tabs");
        var host = (ContentControl)page.FindName("TabHost");
        for (int i = 0; i < 4; i++)
        {
            tabs.SelectedIndex = i;
            page.Measure(new Size(1000, 700));
            page.Arrange(new Rect(0, 0, 1000, 700));
            page.UpdateLayout();
            var tab = Assert.IsType<GamesTabView>(host.Content);
            Assert.True(tab.DesiredSize.Width <= 1000);
            var ready = (PcTidy.UI.Controls.EmptyState)tab.FindName("ReadyEmpty");
            if (!withGames && i is 0 or 2) Assert.Equal("Not available on this PC", ready.Title);
            if (withGames) Assert.NotEqual("Not available on this PC", ready.Title);
        }
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
