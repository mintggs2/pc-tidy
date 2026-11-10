using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PcTidy.Features.Speed.Common;
using PcTidy.Features.Speed.Locking;
using PcTidy.Features.Speed.Sources;
using PcTidy.Features.Speed.Startup;
using PcTidy.UI.Flow;
using Wpf.Ui.Controls;
using Hive = PcTidy.Core.Model.RegistryHive;
using Badge = PcTidy.Core.Model.Badge;

namespace PcTidy.Features.Speed.Tests;

public class LockTests
{
    [Fact]
    public async Task Finds_the_app_holding_a_file_and_never_offers_to_close_PC_Tidy_itself()
    {
        using var sb = new SpeedSandbox();
        var file = sb.MakeFile(@"C\Users\tester\Documents\report.txt", "locked");
        var env = Env.Fake(locks: new RestartManagerLockFinder(), processes: new WindowsProcessSource());
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = await new LockScanner(_ => env).ScanAsync(sb.Context(roots: new[] { file }), null, CancellationToken.None);
            var items = result.AllItems.ToList();
            Assert.NotEmpty(items);
            // This test process holds it; it's never offered "Close" (no window, and it's us).
            var me = items.FirstOrDefault(i => i.Details?.GetValueOrDefault("Process ID") == Environment.ProcessId.ToString());
            if (me is not null)
            {
                Assert.Empty(me.Actions);
                Assert.NotNull(me.DisabledReason);
            }
            Assert.All(items, i => Assert.DoesNotContain(i.Actions, a => a.Args?["pid"] == Environment.ProcessId.ToString()));
        }

        // Once it's closed, nothing holds it.
        var free = await new LockScanner(_ => env).ScanAsync(sb.Context(roots: new[] { file }), null, CancellationToken.None);
        Assert.Empty(free.AllItems);
        Assert.Contains(free.Notes, n => n.Contains("Nothing is using"));
    }

    [Fact]
    public async Task Folders_are_checked_through_their_files_without_following_junctions()
    {
        using var sb = new SpeedSandbox();
        var a = sb.MakeFile(@"C\Users\tester\Projects\site\a.txt");
        sb.MakeFile(@"C\Users\tester\Projects\site\sub\b.txt");
        var folder = Path.GetDirectoryName(a)!;
        Assert.Equal(2, LockScanner.FilesOf(folder, CancellationToken.None).Count());
        Assert.Equal(new[] { a }, LockScanner.FilesOf(a, CancellationToken.None));
        Assert.Empty(LockScanner.FilesOf(Path.Combine(sb.Root, "nope"), CancellationToken.None));

        var holders = new FixedLockFinder(new[]
        {
            new LockHolder(4321, DateTime.UtcNow.AddMinutes(-3), "editor", "Text Editor", LockHolderKind.MainWindow, null, 1),
            new LockHolder(812, null, "svchost", "Windows Search", LockHolderKind.Service, "WSearch", 0),
            new LockHolder(900, null, "explorer", "Windows Explorer", LockHolderKind.Explorer, null, 1),
        });
        using (new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var env = Env.Fake(locks: holders, processes: new FixedProcessSource(Array.Empty<ProcessSnapshot>(), sessionId: 1));
            var result = await new LockScanner(_ => env).ScanAsync(sb.Context(roots: new[] { folder }), null, CancellationToken.None);
            var items = result.AllItems.ToList();
            var editor = items.Single(i => i.Title == "Text Editor");
            var close = Assert.Single(editor.Actions);
            Assert.Equal(ActionKind.CloseApp, close.Kind);
            Assert.Equal("4321", close.Args!["pid"]);
            Assert.Equal("editor", close.Args["processName"]);
            Assert.True(close.Args.ContainsKey("startedUtc"));
            Assert.True(sb.Host.Safety.Check(close).Allowed);
            Assert.Empty(items.Single(i => i.Title == "Windows Search").Actions);
            Assert.Empty(items.Single(i => i.Title == "Windows Explorer").Actions);
        }
    }

    [Fact]
    public async Task Nothing_picked_means_nothing_to_do()
    {
        using var sb = new SpeedSandbox();
        var result = await new LockScanner(_ => Env.Fake()).ScanAsync(sb.Context(), null, CancellationToken.None);
        Assert.Empty(result.Groups);
        Assert.Single(result.Notes);
    }
}

public class ModuleTests
{
    [Fact]
    public void Module_metadata()
    {
        var m = new SpeedModule();
        Assert.Equal("speed", m.Id);
        Assert.Equal("Speed", m.Title);
        Assert.Equal(NavGroup.SpeedUp, m.Group);
        Assert.Equal("TopSpeed24", m.Icon);
        Assert.Equal(Release.V2, m.Release);
        Assert.Equal(ModuleRisk.Check, m.Risk);
        var scanner = Assert.Single(m.Scanners);
        Assert.True(scanner.IncludedInOptimizeAll);
        Assert.Equal("speed", scanner.FeatureId);
        Assert.All(new IFeatureScanner[] { m.Startup, m.Running, m.SettingsCards, m.Drives, m.ContextMenu, m.Locking }, s =>
        {
            Assert.False(s.IncludedInOptimizeAll);
            Assert.StartsWith("speed.", s.FeatureId);
        });
    }

    /// <summary>The rail icon and every tab icon must be real symbols that draw pixels (an unknown glyph renders blank).</summary>
    [Theory]
    [InlineData("TopSpeed24")]
    [InlineData("Rocket24")]
    [InlineData("Options24")]
    [InlineData("HardDrive20")]
    [InlineData("TextBulletListSquare24")]
    [InlineData("LockClosed24")]
    public void Icons_render(string name)
    {
        Assert.True(Enum.TryParse<SymbolRegular>(name, out var symbol), name + " is not a WPF-UI symbol");
        var (drawn, blank) = Sta.Run(() => (InkPixels(symbol), InkPixels(SymbolRegular.Empty)));
        Assert.Equal(0, blank);
        Assert.True(drawn > 40, $"{name} drew {drawn} pixels");
    }

    private static int InkPixels(SymbolRegular symbol)
    {
        var icon = new SymbolIcon
        {
            Symbol = symbol, FontSize = 48, Foreground = Brushes.Black,
            FontFamily = new FontFamily(new Uri("pack://application:,,,/Wpf.Ui;component/Resources/Fonts/"), "./#FluentSystemIcons-Regular"),
        };
        icon.Measure(new System.Windows.Size(64, 64));
        icon.Arrange(new Rect(0, 0, 64, 64));
        icon.UpdateLayout();
        var bmp = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(icon);
        var pixels = new byte[64 * 64 * 4];
        bmp.CopyPixels(pixels, 64 * 4, 0);
        var ink = 0;
        for (var i = 3; i < pixels.Length; i += 4) if (pixels[i] > 0) ink++;
        return ink;
    }

    [Fact]
    public async Task Sandbox_default_environment_reads_nothing_real()
    {
        using var sb = new SpeedSandbox(SandboxProfile.Default);
        var module = new SpeedModule(); // default environment: sandbox → fake registry, empty lists
        var ctx = sb.Context();
        Assert.Empty((await module.Startup.ScanAsync(ctx, null, CancellationToken.None)).AllItems);
        Assert.Empty((await module.Running.ScanAsync(ctx, null, CancellationToken.None)).AllItems);
        Assert.Empty((await module.ContextMenu.ScanAsync(ctx, null, CancellationToken.None)).AllItems);
        var settings = await module.SettingsCards.ScanAsync(ctx, null, CancellationToken.None);
        Assert.NotEmpty(settings.AllItems);
    }

    /// <summary>The Optimize-everything scanner: startup groups the presets recognise, Game Mode as Safe.</summary>
    [Fact]
    public async Task Optimize_all_scan_offers_startup_and_game_mode_only()
    {
        using var sb = new SpeedSandbox();
        using var reg = new FakeRegistry();
        var heavy = sb.MakeFile(@"C\Program Files\Heavy\heavy.exe");
        reg.Set(Hive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "Heavy", $"\"{heavy}\"");
        reg.Set(Hive.CurrentUser, SpeedRegistryAllowances.GameBarKey, SpeedRegistryAllowances.GameModeValue, 0);
        var signIn = DateTime.UtcNow.AddMinutes(-1);
        var env = Env.Fake(registry: reg.View, clock: new FixedSessionClock(DateTime.UtcNow, signIn),
            processes: new FixedProcessSource(new[] { new ProcessSnapshot(77, "heavy", heavy, 1, signIn.AddSeconds(4), TimeSpan.FromSeconds(3), 0, 9L << 20, false, null) }));
        var module = new SpeedModule(_ => env);
        var result = await module.Scanner.ScanAsync(sb.Context(OptimizePresets.Recommended), null, CancellationToken.None);

        Assert.Equal("speed", result.FeatureId);
        var startup = result.Groups.Single(g => g.Id == StartupScanner.AppsGroup);
        Assert.True(OptimizePresets.ShouldTick(OptimizePresets.Recommended, result.FeatureId, startup, startup.Items.Single()));
        Assert.False(OptimizePresets.ShouldTick(OptimizePresets.Safe, result.FeatureId, startup, startup.Items.Single()));
        var gm = result.Groups.Single(g => g.Id == SpeedScanner.GameModeGroup).Items.Single();
        Assert.True(gm.DefaultSelected);
        Assert.Equal(Badge.Safe, gm.Badge);
        Assert.DoesNotContain(result.Groups, g => g.Id == StartupScanner.AdvancedGroup);
    }

    /// <summary>
    /// End to end with the real SafetyGuard, Executor and Undo: every action the Speed scanners emit passes the guard,
    /// and running them in the sandbox changes nothing real (system handlers only simulate there).
    /// </summary>
    [Fact]
    public async Task Plans_pass_the_real_guard_and_run_without_touching_the_PC()
    {
        using var sb = new SpeedSandbox();
        using var reg = new FakeRegistry();
        var app = sb.MakeFile(@"C\Program Files\ToolCo\tool.exe");
        reg.Set(Hive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "ToolCo", $"\"{app}\"");
        reg.Set(Hive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\ToolCoSvc", "Type", 0x10);
        reg.Set(Hive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\ToolCoSvc", "Start", 2);
        reg.Set(Hive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\ToolCoSvc", "ImagePath", $"\"{app}\"");
        reg.Set(Hive.CurrentUser, SpeedRegistryAllowances.GameBarKey, SpeedRegistryAllowances.GameModeValue, 0);
        var tasks = new FixedTaskSource(new[] { new TaskInfo(@"\ToolCo Update", true, true, false, app, null, "ToolCo", null, false) });
        var module = new SpeedModule(_ => Env.Fake(registry: reg.View, tasks: tasks,
            power: new FixedPowerSource(FixedPowerSource.HighPerformance, false)));
        var ctx = sb.Context();

        var picked = new List<ScanItem>();
        picked.AddRange((await module.Startup.ScanAsync(ctx, null, CancellationToken.None)).AllItems.Where(i => i.Actions.Count > 0));
        picked.AddRange((await module.SettingsCards.ScanAsync(ctx, null, CancellationToken.None)).AllItems.Where(i => i.Actions.Count > 0 && i.DisabledReason is null));
        Assert.Contains(picked, i => i.Actions.Any(a => a.Kind == ActionKind.StartupItemSetEnabled));
        Assert.Contains(picked, i => i.Actions.Any(a => a.Kind == ActionKind.ServiceSetStartType));
        Assert.Contains(picked, i => i.Actions.Any(a => a.Kind == ActionKind.ScheduledTaskSetEnabled));
        Assert.Contains(picked, i => i.Actions.Any(a => a.Kind == ActionKind.PowerPlanSet));
        Assert.Contains(picked, i => i.Actions.Any(a => a.Kind == ActionKind.RegistrySetValue));

        var plan = PlanBuilder.FromSelection(SpeedPlanItems.FeatureId, "Speed", picked);
        foreach (var item in plan.Items)
        {
            var verdict = sb.Host.Safety.Check(item);
            Assert.True(verdict.Allowed, $"{item.Kind} '{item.Description}' was refused: {verdict.Reason}");
        }

        var gameModeBefore = reg.View.With(Hive.CurrentUser, SpeedRegistryAllowances.GameBarKey, k => k.Dword(SpeedRegistryAllowances.GameModeValue), null);
        var run = await sb.Host.Executor.ExecuteAsync(plan, new ExecuteOptions { Mode = ExecutionMode.Simulate }, null, CancellationToken.None);
        Assert.Equal(plan.Items.Count, run.Steps.Count);
        Assert.DoesNotContain(run.Steps, s => s.Status == StepStatus.Done);
        // Nothing real changed: the fake tree still says Game Mode is off.
        Assert.Equal(gameModeBefore, reg.View.With(Hive.CurrentUser, SpeedRegistryAllowances.GameBarKey, k => k.Dword(SpeedRegistryAllowances.GameModeValue), null));
    }

    /// <summary>A real run and its undo over a generated testbed leave every file exactly as it was.</summary>
    [Fact]
    public async Task Run_and_undo_over_a_generated_testbed_leave_it_identical()
    {
        using var sb = new SpeedSandbox(SandboxProfile.Default, generate: true);
        using var reg = new FakeRegistry();
        var app = Directory.EnumerateFiles(sb.Locations.ProgramFiles, "*.exe", SearchOption.AllDirectories).FirstOrDefault() ?? sb.MakeFile(@"C\Program Files\ToolCo\tool.exe");
        reg.Set(Hive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "ToolCo", $"\"{app}\"");
        var folder = Path.Combine(sb.Locations.RoamingAppData, "Microsoft", "Windows", "Start Menu", "Programs", "Startup");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "ToolCo.lnk"), LinkBuilder.Build(app, null));
        var before = PcTidy.TestbedGen.TestbedBuilder.Manifest(Path.Combine(sb.Root, "C"));

        var module = new SpeedModule(_ => Env.Fake(registry: reg.View));
        var ctx = sb.Context();
        var items = new List<ScanItem>();
        foreach (var s in new IFeatureScanner[] { module.Startup, module.SettingsCards, module.Drives, module.ContextMenu })
            items.AddRange((await s.ScanAsync(ctx, null, CancellationToken.None)).AllItems.Where(i => i.Actions.Count > 0 && i.DisabledReason is null));
        var plan = PlanBuilder.FromSelection(SpeedPlanItems.FeatureId, "Speed", items);
        Assert.NotEmpty(plan.Items);

        var run = await sb.Host.Executor.ExecuteAsync(plan, new ExecuteOptions { Mode = ExecutionMode.Real }, null, CancellationToken.None);
        await sb.Host.Undo.UndoRunAsync(run.RunId, null, CancellationToken.None);
        var after = PcTidy.TestbedGen.TestbedBuilder.Manifest(Path.Combine(sb.Root, "C"));
        var diff = PcTidy.TestbedGen.TestbedBuilder.Compare(before, after);
        Assert.True(diff.Identical, diff.Format());
        // And the fake registry tree still holds exactly what the test put there (the sandbox never writes the real StartupApproved key).
        Assert.Null(reg.View.With(Hive.CurrentUser, StartupApprovedState.BaseKey + @"\Run", k => k.Bytes("ToolCo"), null));
    }

    [Fact]
    public async Task Every_row_has_a_reason_and_risky_rows_are_never_pre_ticked()
    {
        using var sb = new SpeedSandbox();
        using var reg = new FakeRegistry();
        reg.Set(Hive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "A", @"C:\A\a.exe");
        reg.Set(Hive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\RunOnce", "B", @"C:\B\b.exe");
        var system = new TestSystemInfo { Drives = new[] { new DriveDetails(@"C:\", "Windows", DriveMedia.Ssd, 100L << 30, 3L << 30, true) } };
        var module = new SpeedModule(_ => Env.Fake(registry: reg.View,
            disks: new FixedDiskSource(new[] { new PhysicalDiskInfo("0", "SSD", DriveMedia.Ssd, "NVMe", DiskHealth.Healthy, 100L << 30, new[] { 'C' }, null) })));
        var ctx = sb.Context(system: system);
        foreach (var scanner in new IFeatureScanner[] { module.Startup, module.Running, module.SettingsCards, module.Drives, module.ContextMenu, module.Scanner })
        {
            var result = await scanner.ScanAsync(ctx, null, CancellationToken.None);
            foreach (var g in result.Groups)
            {
                Assert.False(string.IsNullOrWhiteSpace(g.Description), $"{scanner.FeatureId}/{g.Id} has no description");
                foreach (var i in g.Items)
                {
                    Assert.False(string.IsNullOrWhiteSpace(i.Reason), $"{i.Title} has no reason");
                    if (i.Badge is Badge.Check or Badge.Permanent) Assert.False(i.DefaultSelected, i.Title);
                    if (i.Actions.Any(a => !a.Reversible)) Assert.False(i.DefaultSelected, i.Title);
                    Assert.False(i.DisabledReason is not null && i.DefaultSelected, i.Title);
                }
            }
        }
    }
}

/// <summary>Local copy of the boundary rule for quick feedback: the Speed project never changes anything itself.</summary>
public class BoundaryTests
{
    private static readonly Regex[] Forbidden =
    {
        new(@"\bFile\.(Delete|Move|Copy|Replace|WriteAll\w*|AppendAll\w*|Create|CreateText|AppendText|SetAttributes)\s*\("),
        new(@"\bDirectory\.(Delete|Move|CreateDirectory|CreateSymbolicLink)\s*\("),
        new(@"\.MoveTo\s*\(|\.Delete\s*\(\s*(\)|true\s*\)|recursive)"),
        new(@"FileAccess\.(Write|ReadWrite)|FileMode\.(Create|CreateNew|Append|Truncate|OpenOrCreate)"),
        new(@"\.(SetValue|DeleteValue|DeleteSubKey|DeleteSubKeyTree|CreateSubKey)\s*\("),
        new(@"\bProcess\.Start\s*\(|\.Kill\s*\(|CloseMainWindow\s*\(|RmShutdown|TerminateProcess"),
        new(@"ShellExecute|SHFileOperation|IFileOperation|SHEmptyRecycleBin\s*\("),
        new(@"\bwritable:\s*true"),
    };

    [Fact]
    public void Speed_project_only_reads()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "PcTidy.sln"))) d = d.Parent;
        var dir = Path.Combine(d!.FullName, "src", "PcTidy.Features.Speed");
        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains(@"\obj\") && !f.Contains(@"\bin\")))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var t = lines[i].TrimStart();
                if (t.StartsWith("//") || t.StartsWith("*")) continue;
                if (Forbidden.Any(r => r.IsMatch(lines[i]))) violations.Add($"{Path.GetFileName(file)}:{i + 1}: {t}");
            }
        }
        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }
}
