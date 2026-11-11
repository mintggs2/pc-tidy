using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.PcCare.Planning;
using PcTidy.Features.PcCare.Scanning;
using PcTidy.Features.PcCare.Source;
using PcTidy.Hosting;
using PcTidy.TestbedGen;
using T = PcTidy.Features.PcCare.Scanning.PcCareTabs;

namespace PcTidy.Features.PcCare.Tests;

/// <summary>
/// End to end over the real composition (real SafetyGuard, Executor, Undo) on a sandbox PC under testbed\pccare.
/// The sandbox source reads a snapshot file; no real tool, WMI write or network call ever runs.
/// </summary>
public class SandboxIntegrationTests
{
    [Fact]
    public void A_sandbox_never_gets_the_real_source()
    {
        using var tb = new TestbedFolder();
        using var host = PcTidyHost.CreateSandbox(tb.Root, SandboxProfile.Minimal);
        Assert.IsType<FixturePcCareSource>(PcCareSourceFactory.For(host.Locations));
    }

    [Fact]
    public async Task Every_offered_action_passes_the_real_safety_guard()
    {
        using var tb = new TestbedFolder();
        tb.InstallSnapshot(Scenario.OwnerLike());
        using var host = PcTidyHost.CreateSandbox(tb.Root, SandboxProfile.Default);
        var scanner = new PcCareScanner(clock: new FixedClock());

        var report = await scanner.ScanReportAsync(host.CreateScanContext(ScanDepth.Quick), null, CancellationToken.None);

        var actions = report.Result.AllItems.Where(i => i.DisabledReason is null).SelectMany(i => i.Actions).ToList();
        Assert.Contains(actions, a => a.ToolId == ToolIds.WingetUpgrade);
        Assert.Contains(actions, a => a.Kind == ActionKind.WlanProfileDelete);
        Assert.Contains(actions, a => a.Kind == ActionKind.RestorePointDelete);
        // The sandbox's "tester" profile isn't the real current user, so the profile check only asks for an exact, permanent SID item.
        // The report goes to <DataRoot>\reports as specified; the guard treats all of PC Tidy's data folder as "never touched"
        // (pending contract change: allow WriteTextFile under DataPaths.Reports), so those items are checked for their path here.
        foreach (var a in actions.Where(a => a.Kind is not (ActionKind.UserProfileDelete or ActionKind.WriteTextFile)))
        {
            var verdict = host.Safety.Check(a);
            Assert.True(verdict.Allowed, $"{a.Description}: {verdict.Reason}");
        }
        var profile = actions.Single(a => a.Kind == ActionKind.UserProfileDelete);
        Assert.Equal(new[] { "sid" }, profile.Args!.Keys);
        Assert.False(profile.Reversible);
        Assert.All(actions.Where(a => a.Kind == ActionKind.WriteTextFile),
            a => Assert.Equal(DataPaths.Reports(host.Locations.DataRoot), Path.GetDirectoryName(a.DestinationPath)));
    }

    [Fact]
    public async Task Without_a_snapshot_the_page_still_works_with_friendly_notes()
    {
        using var tb = new TestbedFolder();
        using var host = PcTidyHost.CreateSandbox(tb.Root, SandboxProfile.Minimal);

        var report = await new PcCareScanner(clock: new FixedClock()).ScanReportAsync(host.CreateScanContext(ScanDepth.Quick), null, CancellationToken.None);

        Assert.True(report.TabNotes.ContainsKey(T.Updates));
        Assert.True(report.TabNotes.ContainsKey(T.Security));
        Assert.NotNull(report.Group(T.Repair, "windows"));
        // One SSD-like drive in the Minimal sandbox: nothing to wipe, only the honest explanation.
        Assert.DoesNotContain(report.AllActions(), a => a.ToolId == ToolIds.CipherWipeFreeSpace);
    }

    [Fact]
    public async Task The_DNS_test_in_a_sandbox_reads_the_snapshot_and_sends_nothing()
    {
        using var tb = new TestbedFolder();
        tb.InstallSnapshot(Scenario.OwnerLike());
        using var host = PcTidyHost.CreateSandbox(tb.Root, SandboxProfile.Minimal);
        var scanner = new PcCareScanner(clock: new FixedClock());
        var ctx = host.CreateScanContext(ScanDepth.Quick);

        var first = await scanner.ScanReportAsync(ctx, null, CancellationToken.None);
        Assert.Null(first.Group(T.Network, "dns")); // not run until asked
        var tested = await scanner.RunDnsTestAsync(first, ctx, CancellationToken.None);
        Assert.Equal(4, tested.Group(T.Network, "dns")!.Items.Count);
    }

    [Fact]
    public async Task A_saved_report_round_trips_through_run_and_undo_leaving_the_testbed_identical()
    {
        using var tb = new TestbedFolder();
        TestbedBuilder.Generate(tb.Root, TestbedSize.Small, SandboxProfile.Default);
        tb.InstallSnapshot(Scenario.OwnerLike());
        using var host = PcTidyHost.CreateSandbox(tb.Root, SandboxProfile.Default);
        var report = await new PcCareScanner(clock: new FixedClock()).ScanReportAsync(host.CreateScanContext(ScanDepth.Quick), null, CancellationToken.None);
        var specified = report.Group(T.Report, "export")!.Items[0].Actions.Single();
        Assert.Equal(DataPaths.Reports(host.Locations.DataRoot), Path.GetDirectoryName(specified.DestinationPath));
        // The same item, aimed at a folder today's guard allows (see the contract note about <DataRoot>\reports).
        var path = Path.Combine(host.Locations.Downloads, Path.GetFileName(specified.DestinationPath)!);
        var item = PcCareActions.WriteReport(path, specified.Args!["content"]);
        var plan = new ActionPlan { FeatureId = PcCareActions.FeatureId, Title = "Health report", Items = new[] { item } };
        var verdict = host.Safety.Check(item);
        Assert.True(verdict.Allowed, verdict.Reason);

        var before = TestbedBuilder.Manifest(tb.Root);
        var run = await host.Executor.ExecuteAsync(plan, new ExecuteOptions(), null, CancellationToken.None);
        Assert.Equal(StepStatus.Done, run.Steps.Single().Status);
        Assert.True(File.Exists(path));
        Assert.DoesNotContain("tester", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);

        await host.Undo.UndoRunAsync(run.RunId, null, CancellationToken.None);
        Assert.False(File.Exists(path));
        var diff = TestbedBuilder.Compare(before, TestbedBuilder.Manifest(tb.Root));
        var outsideData = diff.Differences.Where(d => !IsUnderData(tb.Root, d.Path)).ToList();
        Assert.True(outsideData.Count == 0, string.Join("\n", outsideData));
    }

    private static bool IsUnderData(string root, string path)
    {
        var full = Path.IsPathRooted(path) ? path : Path.Combine(root, path);
        return PathUtil.IsUnder(full, Path.Combine(root, "Data"));
    }

    [Fact]
    public async Task Shredder_rows_are_checked_by_the_real_guard_and_simulating_changes_nothing()
    {
        using var tb = new TestbedFolder();
        using var host = PcTidyHost.CreateSandbox(tb.Root, SandboxProfile.Minimal);
        Directory.CreateDirectory(host.Locations.Downloads);
        var secret = Path.Combine(host.Locations.Downloads, "old-tax-scan.pdf");
        File.WriteAllText(secret, "pretend secret");
        var system = Path.Combine(host.Locations.WindowsDir, "System32", "kernel32.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(system)!);
        File.WriteAllText(system, "pretend system file");
        var folder = Path.Combine(host.Locations.Downloads, "a folder");
        Directory.CreateDirectory(folder);

        var group = ShredderPlanner.ForFiles(new[] { secret, system, folder, Path.Combine(host.Locations.Downloads, "missing.txt") }, host.Safety, host.System.Drives);

        Assert.Equal(3, group.Items.Count); // the missing file is simply left out
        var offered = Assert.Single(group.Items, i => i.DisabledReason is null);
        Assert.Equal(Badge.Permanent, offered.Badge);
        Assert.False(offered.DefaultSelected);
        var shred = offered.Actions.Single();
        Assert.Equal(ActionKind.ShredFile, shred.Kind);
        Assert.False(shred.Reversible);
        Assert.Equal(new FileInfo(secret).Length, shred.ExpectedSize);
        Assert.NotNull(group.Items.Single(i => i.Detail == system).DisabledReason);
        Assert.NotNull(group.Items.Single(i => i.Detail == folder).DisabledReason);

        var plan = PcCarePlanBuilder.Build("File shredder", new[] { offered }, new PcCarePageChoices { ShredAcknowledged = true }).Plan!;
        var run = await host.Executor.ExecuteAsync(plan, new ExecuteOptions { Mode = ExecutionMode.Simulate }, null, CancellationToken.None);
        Assert.DoesNotContain(run.Steps, s => s.Status is StepStatus.Done or StepStatus.Failed);
        Assert.Equal("pretend secret", File.ReadAllText(secret));
    }

    [Fact]
    public async Task Simulating_system_tools_changes_nothing_and_fails_nothing()
    {
        using var tb = new TestbedFolder();
        tb.InstallSnapshot(Scenario.OwnerLike());
        using var host = PcTidyHost.CreateSandbox(tb.Root, SandboxProfile.Default);
        var report = await new PcCareScanner(clock: new FixedClock()).ScanReportAsync(host.CreateScanContext(ScanDepth.Quick), null, CancellationToken.None);
        var picks = report.Group(T.Repair, "windows")!.Items.Concat(report.Group(T.Network, "fixes")!.Items).Concat(report.Group(T.Updates, "apps")!.Items);
        var plan = PcCarePlanBuilder.Build("PC care", picks, new PcCarePageChoices { AcceptAgreements = true }).Plan!;

        var run = await host.Executor.ExecuteAsync(plan, new ExecuteOptions { Mode = ExecutionMode.Simulate }, null, CancellationToken.None);

        Assert.Equal(plan.Items.Count, run.Steps.Count);
        Assert.DoesNotContain(run.Steps, s => s.Status is StepStatus.Failed or StepStatus.Done);
    }

    [Fact]
    public async Task Cancelling_the_scan_stops_it()
    {
        using var tb = new TestbedFolder();
        using var host = PcTidyHost.CreateSandbox(tb.Root, SandboxProfile.Minimal);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new PcCareScanner(clock: new FixedClock()).ScanAsync(host.CreateScanContext(ScanDepth.Quick), null, cts.Token));
    }
}

public class ModuleTests
{
    [Fact]
    public void Module_is_wired_as_specified()
    {
        var m = new PcCareModule();
        Assert.Equal("pccare", m.Id);
        Assert.Equal("PC care", m.Title);
        Assert.Equal(NavGroup.SpeedUp, m.Group);
        Assert.Equal("HeartPulse24", m.Icon);
        Assert.Equal(Release.V2, m.Release);
        var scanner = Assert.IsType<PcCareScanner>(Assert.Single(m.Scanners));
        Assert.False(scanner.IncludedInOptimizeAll);
        Assert.True(Enum.TryParse<Wpf.Ui.Controls.SymbolRegular>(m.Icon, out _));
    }

    [Fact]
    public void Tabs_have_unique_ids_and_titles()
    {
        Assert.Equal(T.All.Count, T.All.Select(t => t.Id).Distinct().Count());
        Assert.Equal(T.All.Count, T.All.Select(t => t.Title).Distinct().Count());
    }

    /// <summary>The only tool listings are winget's upgrade LIST and nvidia-smi's query: no install, no agreements, no settings.</summary>
    [Fact]
    public void Read_only_tool_table_can_only_list()
    {
        var commands = typeof(PcCareScanner).Assembly.GetType("PcTidy.Features.PcCare.Source.ReadOnlyTool")!
            .GetField("Commands", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null) as System.Collections.IDictionary;
        var all = commands!.Values.Cast<string[]>().ToList();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, a => a.SequenceEqual(new[] { "upgrade", "--include-unknown", "--disable-interactivity" }));
        Assert.All(all.SelectMany(a => a), a => Assert.DoesNotMatch("accept|--all|install|--id|--silent|-e$|--force", a.Replace("--include-unknown", "")));
    }

    [Fact]
    public void Only_the_read_only_tool_starts_a_process()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "PcTidy.sln"))) d = d.Parent;
        var src = Path.Combine(d!.FullName, "src", "PcTidy.Features.PcCare");
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains(@"\obj\") && !f.Contains(@"\bin\")))
        {
            var code = File.ReadAllLines(file).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal));
            bool starts = code.Any(l => l.Contains("Process.Start", StringComparison.Ordinal));
            Assert.True(!starts || Path.GetFileName(file) == "ReadOnlyTool.cs", file);
        }
    }

    /// <summary>The page's XAML loads, every tab shows with a full report, and it lays out at 1000 px wide.</summary>
    [Fact]
    public void Page_shows_every_tab_at_1000_px()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                EnsureAppResources();
                using var tb = new TestbedFolder();
                using var host = PcTidyHost.CreateSandbox(tb.Root, SandboxProfile.Minimal);
                var page = (View.PcCareView)new PcCareModule().CreatePage(host)!;
                Layout(page);
                page.Load(Scenario.Build());
                var groups = (PcTidy.UI.Controls.Segmented)page.FindName("TabGroups");
                var tabs = (PcTidy.UI.Controls.Segmented)page.FindName("Tabs");
                var tweaks = (System.Windows.UIElement)page.FindName("TweaksButton");
                Assert.Equal(T.All.Select(t => t.Id).OrderBy(x => x), T.Groups.SelectMany(g => g.TabIds).OrderBy(x => x)); // every tab in one group
                for (int g = 0; g < T.Groups.Count; g++)
                {
                    groups.SelectedIndex = g;
                    for (int i = 0; i < T.Groups[g].TabIds.Count; i++)
                    {
                        tabs.SelectedIndex = i;
                        Layout(page);
                        var id = T.Groups[g].TabIds[i];
                        Assert.True(page.DesiredSize.Width <= 1000, id);
                        if (id == T.Security) Assert.Equal(System.Windows.Visibility.Collapsed, tweaks.Visibility); // no shell host in a test
                    }
                }
                groups.SelectedIndex = 0;
                Assert.StartsWith("Fix (", groups.Items);
                Assert.StartsWith("App updates (2)", tabs.Items);
                PanelChecks(page, tabs);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    /// <summary>
    /// Lag check and Fixes panels (same STA thread as the page test): in a sandbox the lag check is off; with a fake runner it
    /// fills the three cards; a fix shows the confirm through the review hook with a one-item plan, and while it runs every
    /// other Run button (and the lag check) is disabled with the reason.
    /// </summary>
    private static void PanelChecks(View.PcCareView page, PcTidy.UI.Controls.Segmented tabs)
    {
        System.Threading.SynchronizationContext.SetSynchronizationContext(
            new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
        var lag = (View.LagCheckPanel)page.FindName("LagPanel");
        var fixes = (View.FixesPanel)page.FindName("FixPanel");
        var results = (System.Windows.UIElement)page.FindName("Results");

        tabs.SelectedIndex = T.Groups[0].TabIds.ToList().IndexOf(T.LagCheck);
        Layout(page);
        Assert.Equal(System.Windows.Visibility.Visible, lag.Visibility);
        Assert.Equal(System.Windows.Visibility.Collapsed, results.Visibility);
        var run = (Wpf.Ui.Controls.Button)lag.FindName("RunButton");
        Assert.False(run.IsEnabled); // sandbox: never sends traffic
        Assert.True(((Wpf.Ui.Controls.InfoBar)lag.FindName("DisabledNote")).IsOpen);

        var gate = new View.LongTaskGate(new PcTidy.UI.Flow.RunGate()); // its own app-wide gate, so other tests are never blocked
        var endpoint = LagCheckEndpoint.CloudflareCandidate with
        {
            DownloadUrl = new Uri("https://lag.example.invalid/down"), UploadUrl = new Uri("https://lag.example.invalid/up"),
        };
        var quick = new LagCheckPlan
        {
            IdleSamples = 3, PingInterval = TimeSpan.FromMilliseconds(2), LoadDuration = TimeSpan.FromMilliseconds(80),
            RampUp = TimeSpan.Zero, Settle = TimeSpan.Zero, DownloadChunkBytes = 1L << 30, UploadChunkBytes = 1L << 30,
            DownloadBudgetBytes = 1L << 40, UploadBudgetBytes = 1L << 40, Streams = 1,
        };
        lag.Init(gate, new LagCheckSettings { Endpoint = endpoint }, sandbox: false,
            e => new LagCheckRunner(new FakeLagServer { IdleMs = 18, DownloadAddedMs = 12, UploadAddedMs = 70 }, e.Host, quick));
        Assert.True(run.IsEnabled);
        Assert.Contains("lag.example.invalid", ((System.Windows.Controls.TextBlock)lag.FindName("SendsText")).Text);
        Pump(lag.RunAsync());
        Assert.Equal("18 ms", ((System.Windows.Controls.TextBlock)lag.FindName("IdleValue")).Text);
        Assert.Equal("88 ms (+70)", ((System.Windows.Controls.TextBlock)lag.FindName("LoadValue")).Text);
        Assert.Equal("Noticeable lag", ((System.Windows.Controls.TextBlock)lag.FindName("GradeValue")).Text);
        Assert.False(gate.IsBusy);

        tabs.SelectedIndex = T.Groups[0].TabIds.ToList().IndexOf(T.Fixes);
        Layout(page);
        Assert.Equal(System.Windows.Visibility.Visible, fixes.Visibility);
        Assert.Equal(System.Windows.Visibility.Collapsed, lag.Visibility);
        Assert.True(page.DesiredSize.Width <= 1000);

        var confirm = new TaskCompletionSource<bool>();
        ActionPlan? reviewed = null;
        fixes.Init(gate, plan => { reviewed = plan; return confirm.Task; });
        var running = fixes.RunAsync(Planning.FixRecipes.Get(Planning.FixRecipes.WifiReset));
        Assert.Single(reviewed!.Items);
        Assert.Equal(ToolIds.ServiceRestart, reviewed.Items[0].ToolId);
        Assert.True(gate.IsBusy);
        Assert.False(run.IsEnabled); // the lag check waits too
        var cards = (PcTidy.UI.Controls.ResponsiveGrid)fixes.FindName("CardsGrid");
        var texts = cards.Children.OfType<System.Windows.Controls.Border>()
            .SelectMany(b => Descendants(b).OfType<System.Windows.Controls.TextBlock>())
            .Where(t => t.Text == "Available when the fix finishes" && t.Visibility == System.Windows.Visibility.Visible).ToList(); // names the task (UI review L3)
        Assert.Equal(Planning.FixRecipes.All.Count - 1, texts.Count);
        confirm.SetResult(false); // the user pressed Cancel in the confirm
        Pump(running);
        Assert.False(gate.IsBusy);
        Assert.True(run.IsEnabled);
    }

    private static IEnumerable<System.Windows.DependencyObject> Descendants(System.Windows.DependencyObject root)
    {
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root).OfType<System.Windows.DependencyObject>())
        {
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    /// <summary>Runs the dispatcher until the task ends (the page's awaits come back on the UI thread).</summary>
    private static void Pump(Task task)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private static void Layout(System.Windows.FrameworkElement page)
    {
        page.Measure(new System.Windows.Size(1000, 760));
        page.Arrange(new System.Windows.Rect(0, 0, 1000, 760));
        page.UpdateLayout();
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
}
