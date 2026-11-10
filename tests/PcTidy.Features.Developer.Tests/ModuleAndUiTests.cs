using System.Windows;
using System.Windows.Controls;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Developer.UI;
using PcTidy.UI.Flow;
using Xunit;

namespace PcTidy.Features.Developer.Tests;

/// <summary>The module's identity, and the custom page loading every tab at 1000 px wide with and without dev tools.</summary>
[Collection("developer-testbed")]
public class ModuleAndUiTests
{
    [Fact]
    public void Module_is_the_developer_section_and_stays_out_of_optimize_everything()
    {
        var m = new DeveloperModule();
        Assert.Equal("developer", m.Id);
        Assert.Equal("Developer", m.Title);
        Assert.Equal(NavGroup.CleanUp, m.Group);
        Assert.Equal("Code24", m.Icon);
        Assert.Equal(Release.V2, m.Release);
        Assert.Equal(ModuleRisk.Check, m.Risk);
        var scanner = Assert.IsType<DeveloperScanner>(Assert.Single(m.Scanners));
        Assert.Equal("developer", scanner.FeatureId);
        Assert.False(scanner.IncludedInOptimizeAll);
        Assert.False(string.IsNullOrWhiteSpace(m.Summary));
    }

    [Fact]
    public void Developer_page_loads_every_tab_at_1000px_with_and_without_tools()
    {
        using var full = new DevSandbox("ui-full");
        full.Put(Path.Combine(full.K.ProgramFiles, "nodejs", "npm.cmd"), "@echo off");
        full.Put(Path.Combine(full.K.ProgramFiles, "Docker", "Docker", "resources", "bin", "docker.exe"), "MZ");
        using var empty = new DevSandbox("ui-empty", SandboxProfile.Minimal);
        Exception? failure = null;
        // WPF resources belong to the thread that made the Application: both pages are built on one STA thread.
        var thread = new Thread(() =>
        {
            try
            {
                EnsureAppResources();
                CheckPage(full, withTools: true);
                CheckPage(empty, withTools: false);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static void CheckPage(DevSandbox s, bool withTools)
    {
        var page = (DeveloperPage)new DeveloperModule(s.Environment, new FixedClock(s.Now)).CreatePage(s.Host)!;
        var tabs = (PcTidy.UI.Controls.Segmented)page.FindName("Tabs");
        var host = (ContentControl)page.FindName("TabHost");
        for (int i = 0; i < 4; i++)
        {
            tabs.SelectedIndex = i;
            page.Measure(new Size(1000, 700));
            page.Arrange(new Rect(0, 0, 1000, 700));
            page.UpdateLayout();
            var tab = Assert.IsType<DevTabView>(host.Content);
            Assert.True(tab.DesiredSize.Width <= 1000);
            var ready = (PcTidy.UI.Controls.EmptyState)tab.FindName("ReadyEmpty");
            var toolTab = i is 1 or 2;
            if (toolTab && !withTools) Assert.Equal("Not available on this PC", ready.Title);
            else Assert.NotEqual("Not available on this PC", ready.Title);
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
