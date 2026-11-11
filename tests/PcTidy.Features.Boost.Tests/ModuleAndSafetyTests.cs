using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Boost.Detection;
using PcTidy.Features.Boost.Planning;
using PcTidy.Hosting;
using Xunit;

namespace PcTidy.Features.Boost.Tests;

public class ModuleAndSafetyTests
{
    [Fact]
    public void Module_metadata()
    {
        var m = new BoostModule();
        Assert.Equal("boost", m.Id);
        Assert.Equal("Game boost", m.Title);
        Assert.Equal(NavGroup.SpeedUp, m.Group);
        Assert.Equal("Rocket24", m.Icon);
        Assert.Equal(Release.V3, m.Release);
        Assert.Empty(m.Scanners);
        Assert.False(string.IsNullOrWhiteSpace(m.Summary));
    }

    [Fact]
    public void Settings_round_trip_and_default_to_off()
    {
        var app = new AppSettings();
        var fresh = BoostSettings.Load(app);
        Assert.False(fresh.Enabled);
        Assert.False(fresh.PauseOneDrive);
        Assert.Empty(fresh.Games);

        fresh.Enabled = true;
        fresh.SetGame("steam:1", "One", true);
        fresh.SetGame("steam:2", "Two", false);
        fresh.AppsToClose.Add(new BoostAppChoice { ProcessName = "Discord", DisplayName = "Discord" });
        fresh.Store(app);

        var loaded = BoostSettings.Load(app);
        Assert.True(loaded.Enabled);
        Assert.Equal(new[] { "steam:1" }, loaded.EnabledGameKeys());
        Assert.Single(loaded.AppsToClose);
        Assert.Equal(TimeSpan.FromSeconds(3), BoostSettings.ClampPoll(1));
        Assert.Equal(TimeSpan.FromSeconds(5), BoostSettings.ClampPoll(60));
    }

    [Fact]
    public async Task Boost_plan_passes_the_real_safety_guard_and_simulates_in_a_sandbox()
    {
        using var rig = new BoostRig();
        using var host = PcTidyHost.CreateSandbox(Path.Combine(rig.Folder.Root, "pc"), SandboxProfile.Default);
        rig.StartGame();
        var snapshot = new ProcessPoller(rig.Processes).Snapshot();
        var match = GameMatcher.Match(new[] { rig.Game }, snapshot, null)!;
        var plan = BoostPlanner.PlanBoost(match, rig.Settings, snapshot, rig.Processes.WindowPids, rig.CommandLines, rig.WindowsDir, DateTime.UtcNow).Plan!;

        foreach (var item in plan.Items)
        {
            var verdict = host.Safety.Check(item);
            Assert.True(verdict.Allowed, $"{item.Kind} {item.ToolId}: {verdict.Reason}");
        }

        // Simulate only: describes what would happen, changes nothing (the fake pids may exist on this PC).
        var run = await host.Executor.ExecuteAsync(plan, new ExecuteOptions { Mode = ExecutionMode.Simulate }, null, CancellationToken.None);
        Assert.Equal(RunStatus.Simulated, run.Status);
        Assert.All(run.Steps, s => Assert.NotEqual(StepStatus.Done, s.Status));
    }

    [Fact]
    public void Page_loads_at_1000px_on_a_pc_with_and_without_launchers()
    {
        using var t = new TestFolder();
        Exception? failure = null;
        // WPF resources belong to the thread that made the Application: pages are built on one STA thread.
        var thread = new Thread(() =>
        {
            try
            {
                EnsureAppResources();
                foreach (var profile in new[] { SandboxProfile.Default, SandboxProfile.Minimal })
                {
                    using var host = PcTidyHost.CreateSandbox(Path.Combine(t.Root, profile == SandboxProfile.Minimal ? "min" : "def"), profile);
                    host.Add<IFlowHost>(new FakeFlowHost());
                    var page = (System.Windows.FrameworkElement)new BoostModule().CreatePage(host)!;
                    Assert.IsAssignableFrom<PcTidy.UI.Flow.IFixedHeightPage>(page);
                    page.Measure(new System.Windows.Size(1000, 700));
                    page.Arrange(new System.Windows.Rect(0, 0, 1000, 700));
                    page.UpdateLayout();
                    Assert.True(page.DesiredSize.Width <= 1000);
                    // "Good to know" cards: text only, C12 (closing background apps) first.
                    var tips = (System.Windows.Controls.ItemsControl)page.FindName("TipsList");
                    var cards = tips.Items.Cast<PcTidy.Features.Boost.UI.TipCard>().ToList();
                    Assert.Equal(new[] { "C12", "C1", "C7" }, cards.Select(c => c.Id));
                }
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static void EnsureAppResources()
    {
        var app = System.Windows.Application.Current ?? new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        if (app.Resources.Contains("TidyCard")) return;
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Dark });
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
        app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary { Source = new Uri("pack://application:,,,/PcTidy.UI;component/Themes/TidyDark.xaml") });
        app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary { Source = new Uri("pack://application:,,,/PcTidy.UI;component/Themes/Tidy.xaml") });
    }

    [Fact]
    public void The_real_guard_refuses_closing_anti_cheat_even_if_a_plan_asked()
    {
        using var t = new TestFolder();
        using var host = PcTidyHost.CreateSandbox(t.Root, SandboxProfile.Minimal);
        foreach (var name in new[] { "EasyAntiCheat", "BEService", "vgc", "FACEIT", "explorer" })
        {
            var item = new PlanItem
            {
                Kind = ActionKind.CloseApp, FeatureId = "boost", Description = "x", Reversible = false,
                Args = new() { ["pid"] = "123456", ["processName"] = name },
            };
            Assert.False(host.Safety.Check(item).Allowed, name);
        }
    }
}
