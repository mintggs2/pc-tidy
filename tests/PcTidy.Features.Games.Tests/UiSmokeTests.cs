using System.Windows;
using System.Windows.Controls;
using PcTidy.Core;
using PcTidy.Features.Games.Optimize;
using PcTidy.Features.Games.UI;
using Xunit;

namespace PcTidy.Features.Games.Tests;

/// <summary>The custom page loads with the app's resources and lays out at 1000 px wide, on a full and on an empty PC.</summary>
[Collection("games-testbed")]
public class UiSmokeTests
{
    /// <summary>
    /// One STA thread for every WPF check: the app resources belong to the thread that made the Application, so the
    /// pages and the Optimize view are all built on it.
    /// </summary>
    [Fact]
    public void Games_page_loads_every_tab_at_1000px_with_and_without_games()
    {
        using var full = new GamesSandbox("ui-full", SandboxProfile.Default, generate: true);
        using var empty = new GamesSandbox("ui-empty", SandboxProfile.Minimal, generate: true);
        var cs2 = OptimizeMasterDetailTests.Cs2Entry();
        var fortnite = OptimizeMasterDetailTests.NotInstalledEntry("fortnite");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                EnsureAppResources();
                CheckPage(full, withGames: true);
                CheckPage(empty, withGames: false);
                CheckOptimizeView(full, cs2, fortnite);
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
        Assert.Equal("Library|Clips & screenshots|Optimize|Cleanup|Tips", tabs.Items);
        for (int i = 0; i < 5; i++)
        {
            tabs.SelectedIndex = i;
            page.Measure(new Size(1000, 700));
            page.Arrange(new Rect(0, 0, 1000, 700));
            page.UpdateLayout();
            var content = Assert.IsAssignableFrom<UserControl>(host.Content);
            Assert.True(content.DesiredSize.Width <= 1000);
            switch (i)
            {
                case 2:
                    // Optimize is master/detail and always available: catalog games can be read before they're installed.
                    Assert.IsType<GameOptimizeView>(content);
                    break;
                case 4:
                    Assert.IsType<GuidanceCardsView>(content);
                    break;
                default:
                    var tab = Assert.IsType<GamesTabView>(content);
                    var ready = (PcTidy.UI.Controls.EmptyState)tab.FindName("ReadyEmpty");
                    if (!withGames && i == 0) Assert.Equal("Not available on this PC", ready.Title);
                    if (withGames) Assert.NotEqual("Not available on this PC", ready.Title);
                    break;
            }
        }
    }

    /// <summary>Optimize view: a game with its rows, the placebo shelf, the shader-cache switch off, search and filter.</summary>
    private static void CheckOptimizeView(GamesSandbox s, GameEntry cs2, GameEntry fortnite)
    {
        var view = new GameOptimizeView(s.Host);
        view.Load(new[] { fortnite, cs2 }, Array.Empty<string>());
        view.Measure(new Size(1000, 700));
        view.Arrange(new Rect(0, 0, 1000, 700));
        view.UpdateLayout();
        Assert.True(view.DesiredSize.Width <= 1000);

        var list = (ListBox)view.FindName("GameListBox");
        Assert.Equal(2, list.Items.Count);
        var first = Assert.IsType<GameListRow>(list.SelectedItem);
        Assert.Equal("Counter-Strike 2", first.Name); // "Not tuned" sorts before "Not installed"
        Assert.Equal("Not tuned", first.Status);

        var shader = (CheckBox)view.FindName("ShaderCacheCheck");
        Assert.False(shader.IsChecked);
        Assert.False(string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(shader)));
        var placebo = (ItemsControl)view.FindName("PlaceboList");
        Assert.Contains(placebo.Items.Cast<RecipeRow>(), r => r.Measured!.Contains("902.6 vs 900.7") && !r.IsSelectable);
        var settings = (ItemsControl)view.FindName("SettingsList");
        Assert.All(settings.Items.Cast<RecipeRow>(), r => Assert.False(r.IsChecked)); // nothing is ticked for you
        Assert.Null(view.BuildPlan()); // nothing ticked → nothing to preview

        // Search narrows the list; the filter "Installed" hides catalog-only games.
        ((Wpf.Ui.Controls.TextBox)view.FindName("SearchBox")).Text = "fort";
        Assert.Single(list.Items);
        ((Wpf.Ui.Controls.TextBox)view.FindName("SearchBox")).Text = "";
        ((PcTidy.UI.Controls.Segmented)view.FindName("FilterPicker")).SelectedIndex = 1;
        Assert.Equal("Counter-Strike 2", Assert.IsType<GameListRow>(Assert.Single(list.Items)).Name);
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
