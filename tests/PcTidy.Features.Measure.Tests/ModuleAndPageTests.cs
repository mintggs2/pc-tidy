using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Measure.Compare;
using PcTidy.Features.Measure.Import;
using PcTidy.Features.Measure.UI;
using PcTidy.Hosting;
using Xunit;

namespace PcTidy.Features.Measure.Tests;

public class ModuleAndPageTests
{
    [Fact]
    public void Module_is_Measure_in_Speed_up_for_release_3_with_no_scanners()
    {
        var m = new MeasureModule();
        Assert.Equal("measure", m.Id);
        Assert.Equal("Measure", m.Title);
        Assert.Equal(NavGroup.SpeedUp, m.Group);
        Assert.Equal("DataLine24", m.Icon);
        Assert.Equal(Release.V3, m.Release);
        Assert.True(Enum.IsDefined(typeof(Wpf.Ui.Controls.SymbolRegular), m.Icon));
        Assert.Empty(m.Scanners);
    }

    [Fact]
    public async Task Module_emits_no_plan_items()
    {
        using var tmp = new TempFolder();
        using var host = PcTidyHost.CreateSandbox(tmp.Root);
        var m = new MeasureModule();
        foreach (var s in m.Scanners)
        {
            var r = await s.ScanAsync(host.CreateScanContext(ScanDepth.Quick), null, CancellationToken.None);
            Assert.Empty(r.AllItems);
        }
        Assert.DoesNotContain(m.Scanners, s => s.IncludedInOptimizeAll);
    }

    [Fact]
    public void Data_folder_is_measure_under_the_apps_data_root_unless_one_is_passed()
    {
        using var tmp = new TempFolder();
        using var host = PcTidyHost.CreateSandbox(tmp.Root);
        Assert.Equal(Path.Combine(host.Settings.DataRoot, "measure"), new MeasureModule().DataFolderFor(host));
        Assert.Equal(@"X:\given", new MeasureModule(@"X:\given").DataFolderFor(host));
    }

    [Fact]
    public void Feature_source_never_changes_the_system_except_its_own_data()
    {
        // Same rules as FeatureBoundaryTests, run on this project only.
        var forbidden = new[]
        {
            new Regex(@"\bFile\.(Delete|Move|Copy|Replace|WriteAll\w*|AppendAll\w*|Create|CreateText|AppendText|SetAttributes)\s*\("),
            new Regex(@"\bDirectory\.(Delete|Move|CreateDirectory|CreateSymbolicLink)\s*\("),
            new Regex(@"FileAccess\.(Write|ReadWrite)|FileMode\.(Create|CreateNew|Append|Truncate|OpenOrCreate)"),
            new Regex(@"\.(SetValue|DeleteValue|DeleteSubKey|DeleteSubKeyTree|CreateSubKey)\s*\("),
            new Regex(@"\bProcess\.Start\s*\(|ShellExecute"),
        };
        var dir = Path.Combine(SrcRoot(), "PcTidy.Features.Measure");
        var bad = new List<string>();
        var writers = new HashSet<string>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains(@"\obj\") && !f.Contains(@"\bin\")))
        {
            foreach (var line in File.ReadAllLines(file))
            {
                var t = line.TrimStart();
                if (t.StartsWith("//")) continue;
                if (!forbidden.Any(r => r.IsMatch(line))) continue;
                if (line.Contains("// boundary-ok:")) writers.Add(Path.GetFileName(file));
                else bad.Add(Path.GetFileName(file) + ": " + t);
            }
        }
        Assert.Empty(bad);
        Assert.Equal(new[] { "ComparisonStore.cs" }, writers.ToArray()); // the only writer: its own JSON in DataRoot
    }

    [Fact]
    public void Honesty_texts_say_what_isnt_proven_and_make_no_general_fps_claim()
    {
        var all = string.Join("\n", MeasureText.DoesNotProve.Concat(MeasureText.RecordSteps).Append(MeasureText.Subtitle).Append(MeasureText.ImportHelp));
        Assert.Contains(MeasureText.DoesNotProve, t => t.Contains("this game, this scene and this PC"));
        Assert.Contains(MeasureText.DoesNotProve, t => t.Contains("input latency"));
        Assert.Contains(MeasureText.DoesNotProve, t => t.Contains("doesn't mean harmless"));
        Assert.DoesNotMatch(new Regex(@"\+\s*\d+\s*%\s*FPS", RegexOptions.IgnoreCase), all);
        Assert.Contains(MeasureText.RecordSteps, s => s.Contains("3 runs of 60 to 120 seconds"));
        Assert.Contains(MeasureText.RecordSteps, s => s.Contains("warm-up"));
        Assert.Contains("doesn't include", MeasureText.LicenceNote);
        Assert.All(MeasureText.Tools, t => Assert.StartsWith("https://", t.Url));
        Assert.Contains(MeasureText.Tools, t => t.Name.Contains("PresentMon"));
    }

    [Fact]
    public void Page_lays_out_at_1024px_in_light_and_dark_with_names_on_everything_interactive()
    {
        foreach (var theme in new[] { Wpf.Ui.Appearance.ApplicationTheme.Light, Wpf.Ui.Appearance.ApplicationTheme.Dark })
        {
            RunSta(() =>
            {
                using var tmp = new TempFolder();
                using var host = PcTidyHost.CreateSandbox(tmp.Root);
                EnsureAppResources(theme);
                var module = new MeasureModule();
                module.LinkToRun("run-42");
                var page = Assert.IsType<MeasurePage>(module.CreatePage(host));
                Assert.Equal("run-42", page.LinkedRunId);
                Assert.True(page.ViewModel.HasLinkedRun);

                // Fill all three sets, one file with two processes so the process picker shows.
                var vm = page.ViewModel;
                for (int i = 0; i < 3; i++)
                {
                    vm.AddRun(RunSet.Before, Read(Synth.PresentMonV1(Synth.Rows("game.exe", Synth.Frames(3000, 10, 100 + i))
                        .Concat(Synth.Rows("launcher.exe", Synth.Frames(300, 16, 200 + i))))));
                    vm.AddRun(RunSet.After, Read(Synth.PresentMonV1(Synth.Rows("game.exe", Synth.Frames(3000, 9, 300 + i)))));
                }
                vm.AddRun(RunSet.BeforeAgain, Read(Synth.PresentMonV1(Synth.Rows("game.exe", Synth.Frames(3000, 10, 400)))));
                Assert.NotEqual(Verdict.NeedMoreRuns, vm.Result.Verdict);

                var window = new Window { Content = page, Width = 1024, Height = 900, WindowStyle = WindowStyle.None, ShowInTaskbar = false, Left = -20000 };
                try
                {
                    window.Show();
                    page.UpdateLayout();
                    Assert.True(page.ActualWidth <= 1024);
                    var interactive = Descendants(page).Where(d => d is ButtonBase or ComboBox or TextBox or Expander or Selector).OfType<FrameworkElement>()
                        .Where(e => e.IsVisible).ToList();
                    Assert.NotEmpty(interactive);
                    var unnamed = interactive.Where(e => string.IsNullOrWhiteSpace(AutomationProperties.GetName(e)) && !IsInsideNamedControl(e)).Select(e => e.GetType().Name + " " + (e as ContentControl)?.Content).ToList();
                    Assert.True(unnamed.Count == 0, "Unnamed: " + string.Join(", ", unnamed));
                    Assert.Contains(interactive, e => e is ComboBox); // the process picker
                    Assert.Contains(Descendants(page).OfType<PcTidy.UI.Controls.BadgePill>(), b => b.Text == ComparisonResult.ChipText(vm.Result.Verdict));
                }
                finally { window.Close(); }
            });
        }
    }

    [Fact]
    public void Tool_links_open_through_the_shell_as_an_OpenExternal_step_the_guard_allows()
    {
        RunSta(() =>
        {
            using var tmp = new TempFolder();
            using var host = PcTidyHost.CreateSandbox(tmp.Root);
            var flow = new FakeFlow();
            host.Add<IFlowHost>(flow);
            var vm = new MeasureViewModel(host, new Storage.ComparisonStore(Path.Combine(tmp.Root, "m")));
            vm.OpenToolCommand.ExecuteAsync(MeasureText.Tools[0]).GetAwaiter().GetResult();
            var item = Assert.Single(Assert.Single(flow.Plans).Items);
            Assert.Equal(ActionKind.OpenExternal, item.Kind);
            Assert.Equal("measure", item.FeatureId);
            Assert.Equal(MeasureText.Tools[0].Url, item.Args!["target"]);
            Assert.True(host.Safety.Check(item).Allowed);
        });
    }

    [Fact]
    public void Linking_a_run_after_the_page_exists_updates_it_and_saving_keeps_the_link()
    {
        RunSta(() =>
        {
            using var tmp = new TempFolder();
            using var host = PcTidyHost.CreateSandbox(tmp.Root);
            EnsureAppResources(Wpf.Ui.Appearance.ApplicationTheme.Dark);
            var module = new MeasureModule(Path.Combine(tmp.Root, "m"));
            var page = (MeasurePage)module.CreatePage(host)!;
            Assert.Null(page.LinkedRunId);
            module.LinkToRun("run-7");
            Assert.Equal("run-7", page.LinkedRunId);
            var vm = page.ViewModel;
            for (int i = 0; i < 3; i++) vm.AddRun(RunSet.Before, Read(Synth.PresentMonV1(Synth.Rows("game.exe", Synth.Frames(1000, 10, i)))));
            vm.Title = "Invented change";
            vm.SaveCommand.Execute(null);
            Assert.Equal("Saved.", vm.SaveStatus);
            var saved = Assert.Single(vm.Saved);
            Assert.Equal("run-7", saved.Comparison.LinkedRunId);
            vm.NewComparisonCommand.Execute(null);
            Assert.Empty(vm.BeforeRuns);
            vm.OpenSavedCommand.Execute(saved);
            Assert.Equal(3, vm.BeforeRuns.Count);
            Assert.Equal("Invented change", vm.Title);
        });
    }

    [Fact]
    public async Task Import_reads_files_into_the_chosen_set_and_lists_problems()
    {
        using var tmp = new TempFolder();
        using var host = PcTidyHost.CreateSandbox(tmp.Root);
        var vm = new MeasureViewModel(host, new Storage.ComparisonStore(Path.Combine(tmp.Root, "m")));
        var good = tmp.Write("a.csv", Synth.PresentMonV1(Synth.Rows("game.exe", Synth.Frames(500, 10, 1))));
        var cx = tmp.Write("b.json", Synth.CapFrameX("game.exe", Synth.Frames(500, 10, 2), Synth.Frames(500, 10, 3)));
        var bad = tmp.Write("c.csv", "nothing,here\n1,2\n");
        vm.TargetIndex = (int)RunSet.After;
        await vm.ImportFilesAsync(new[] { good, cx, bad });
        Assert.Empty(vm.BeforeRuns);
        Assert.Equal(3, vm.AfterRuns.Count);
        Assert.Contains("Added 3 run(s) to After", vm.ImportStatus);
        Assert.Contains("c.csv: This file has no frame-time column", vm.ImportStatus);
    }

    // ------------------------------------------------------------------ helpers

    private static RecordingFile Read(string csv)
    {
        var r = RecordingImporter.ImportText(csv, "run.csv");
        Assert.True(r.Ok, r.Problem);
        return r.File!;
    }

    private static bool IsInsideNamedControl(DependencyObject e)
    {
        // Parts of WPF-UI templates (e.g. a TextBox's clear button, a ComboBox's toggle) take the name of their owner.
        for (var p = VisualTreeHelper.GetParent(e); p is not null; p = VisualTreeHelper.GetParent(p))
            if (p is Control c && (c is ComboBox or TextBox or Expander or ButtonBase) && !string.IsNullOrWhiteSpace(AutomationProperties.GetName(c))) return true;
        return false;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    private static void RunSta(Action body) => StaRunner.Run(body);

    private static void EnsureAppResources(Wpf.Ui.Appearance.ApplicationTheme theme)
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Clear();
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = theme });
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
            var tidyTheme = theme == Wpf.Ui.Appearance.ApplicationTheme.Light ? "TidyLight.xaml" : "TidyDark.xaml";
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/PcTidy.UI;component/Themes/" + tidyTheme) });
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/PcTidy.UI;component/Themes/Tidy.xaml") });
    }

    private static string SrcRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src", "PcTidy.Features.Measure"))) d = d.Parent;
        return Path.Combine(d!.FullName, "src");
    }

    private sealed class FakeFlow : IFlowHost
    {
        public List<ActionPlan> Plans { get; } = new();
        public Task<RunRecord?> ReviewAndRunAsync(ActionPlan plan, CancellationToken ct) { Plans.Add(plan); return Task.FromResult<RunRecord?>(null); }
        public Task<RunRecord?> SimulateAsync(ActionPlan plan, CancellationToken ct) => Task.FromResult<RunRecord?>(null);
        public Task<RunRecord?> RunApprovedAsync(ActionPlan plan, CancellationToken ct) { Plans.Add(plan); return Task.FromResult<RunRecord?>(null); }
        public void OpenInExplorer(string path) { }
        public void ShowMessage(string title, string message, bool isError = false) { }
    }
}

/// <summary>One long-lived STA thread for every WPF test (WPF's Application belongs to the thread that made it).</summary>
internal static class StaRunner
{
    private static readonly System.Collections.Concurrent.BlockingCollection<(Action Body, TaskCompletionSource Done)> Queue = new();

    static StaRunner()
    {
        var t = new Thread(() =>
        {
            foreach (var (body, done) in Queue.GetConsumingEnumerable())
            {
                try { body(); done.SetResult(); }
                catch (Exception ex) { done.SetException(ex); }
            }
        }) { IsBackground = true, Name = "Measure tests STA" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    public static void Run(Action body)
    {
        var done = new TaskCompletionSource();
        Queue.Add((body, done));
        try { done.Task.GetAwaiter().GetResult(); }
        catch (Exception ex) { throw new Xunit.Sdk.XunitException(ex.ToString()); }
    }
}
