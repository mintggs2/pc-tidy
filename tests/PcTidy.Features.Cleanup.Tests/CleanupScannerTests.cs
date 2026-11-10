using PcTidy.Core;
using PcTidy.Core.Model;
using Xunit;

namespace PcTidy.Features.Cleanup.Tests;

public sealed class CleanupScannerTests
{
    private static IEnumerable<PlanItem> Actions(ScanResult r) => r.AllItems.SelectMany(i => i.Actions);
    private static ScanGroup Group(ScanResult r, CleanupTargetInfo t) => r.Groups.Single(g => g.Id == t.Id);

    private static bool Touches(PlanItem a, string path)
        => a.SourcePath is { } src && (PathUtil.IsUnder(path, src) || PathUtil.IsUnder(src, path));

    [Fact]
    public async Task Every_section7_group_appears_in_order_with_its_badge_and_default()
    {
        using var pc = FakePc.Create();
        StandardPc.Build(pc);
        var r = await pc.CleanupScan();

        Assert.Equal(CleanupTargets.All.Select(t => t.Id), r.Groups.Select(g => g.Id));
        foreach (var target in CleanupTargets.All)
        {
            var g = Group(r, target);
            Assert.Equal(target.Badge, g.Badge);
            Assert.NotEmpty(g.Items);
        }

        // Ticked rows (Safe + default) per §7.
        foreach (var t in new[] { CleanupTargets.UserTemp, CleanupTargets.WindowsTemp, CleanupTargets.BrowserCache, CleanupTargets.FirefoxCache,
                     CleanupTargets.InetCache, CleanupTargets.ErrorReports, CleanupTargets.Discord, CleanupTargets.Steam })
            Assert.All(Group(r, t).Items, i => Assert.True(i.Badge == Badge.Safe && i.DefaultSelected, $"{i.Title} should be ticked"));

        // Check / Permanent rows are never ticked.
        foreach (var t in new[] { CleanupTargets.BrowserPrivacy, CleanupTargets.WindowsLogs, CleanupTargets.UpdateDownloads, CleanupTargets.DeliveryOptimization,
                     CleanupTargets.ShaderCache, CleanupTargets.ThumbnailCache, CleanupTargets.RecycleBin, CleanupTargets.Spotify, CleanupTargets.Telegram,
                     CleanupTargets.OldInstallers, CleanupTargets.WindowsOld, CleanupTargets.ComponentStore })
            Assert.All(Group(r, t).Items, i => Assert.False(i.DefaultSelected, $"{i.Title} must not be ticked"));

        Assert.All(r.AllItems.Where(i => i.Badge != Badge.Safe), i => Assert.False(i.DefaultSelected));
        Assert.All(r.AllItems.Where(i => i.Actions.Any(a => !a.Reversible)), i => Assert.False(i.DefaultSelected));
        Assert.All(r.AllItems, i => Assert.False(string.IsNullOrWhiteSpace(i.Reason)));
        // Every group shows its plain "why it's safe" line, even when the §7 row has no extra hint.
        foreach (var target in CleanupTargets.All)
            Assert.StartsWith(target.Reason, Group(r, target).Description);
        Assert.Equal(CleanupTargets.ErrorReports.Reason + " Older than 30 days.", Group(r, CleanupTargets.ErrorReports).Description);
        Assert.Contains(Group(r, CleanupTargets.UserTemp).Items, i => i.Reason.StartsWith(CleanupTargets.TempReason));
        Assert.Contains(Group(r, CleanupTargets.BrowserCache).Items, i => i.Reason.StartsWith(CleanupTargets.BrowserCacheReason));
    }

    [Theory]
    [InlineData(ScanDepth.Quick)]
    [InlineData(ScanDepth.Deep)]
    public async Task Canary_files_are_never_in_any_action(ScanDepth depth)
    {
        using var pc = FakePc.Create();
        var s = StandardPc.Build(pc);
        var r = await pc.CleanupScan(depth);
        var actions = Actions(r).ToList();

        foreach (var canary in s.Canaries)
            Assert.DoesNotContain(actions, a => Touches(a, canary));

        // History / Cookies only ever appear in the opt-in Privacy extras group, never ticked.
        var privacy = Group(r, CleanupTargets.BrowserPrivacy);
        foreach (var file in s.PrivacyFiles)
        {
            Assert.DoesNotContain(r.Groups.Where(g => g != privacy).SelectMany(g => g.Items).SelectMany(i => i.Actions), a => Touches(a, file));
            Assert.Contains(privacy.Items.SelectMany(i => i.Actions), a => a.SourcePath == file);
        }
        Assert.All(privacy.Items, i => Assert.True(i.Badge == Badge.Check && !i.DefaultSelected));
        Assert.Contains("signs you out of every website", privacy.Description);
        var privacyNames = privacy.Items.SelectMany(i => i.Actions).Select(a => Path.GetFileName(a.SourcePath!)).Distinct();
        Assert.All(privacyNames, n => Assert.Contains(n, new[] { "History", "History-journal", "Cookies", "Cookies-journal" }));
    }

    [Fact]
    public async Task Temp_age_rule_quick_takes_old_files_and_fully_old_folders_only()
    {
        using var pc = FakePc.Create();
        var s = StandardPc.Build(pc);
        var item = Group(await pc.CleanupScan(), CleanupTargets.UserTemp).Items.Single();
        var sources = item.Actions.Select(a => a.SourcePath).ToList();

        Assert.Contains(s.TempOldFile, sources);
        Assert.Contains(s.TempOldFolder, sources);
        Assert.Contains(s.TempEmptyOld, sources);
        Assert.DoesNotContain(s.TempNewFile, sources);
        Assert.DoesNotContain(item.Actions, a => Touches(a, s.TempBusyNew));
        Assert.DoesNotContain(s.TempBusyOld, sources); // Quick leaves busy folders alone
        Assert.Equal(StandardPc.TempQuickBytes, item.SizeBytes);
        Assert.Equal(3, item.FileCount);
        Assert.True(item.Actions.Single(a => a.SourcePath == s.TempOldFolder).IsDirectory);
    }

    [Fact]
    public async Task Deep_finds_more_than_quick_and_says_what_it_added()
    {
        using var pc = FakePc.Create();
        var s = StandardPc.Build(pc);
        var quick = await pc.CleanupScan(ScanDepth.Quick);
        var deep = await pc.CleanupScan(ScanDepth.Deep);

        Assert.True(Actions(deep).Count() > Actions(quick).Count());
        Assert.True(deep.AllItems.Sum(i => i.SizeBytes) > quick.AllItems.Sum(i => i.SizeBytes));
        var deepSources = Actions(deep).Select(a => a.SourcePath).ToList();
        Assert.Contains(s.TempBusyOld, deepSources);
        Assert.DoesNotContain(s.TempBusyNew, deepSources);
        Assert.Contains(s.DeepSetup, deepSources);
        Assert.Contains(s.UpdateMixedOld, deepSources);
        // Web apps' offline data (Service Worker storage) is never offered, not even in Deep mode.
        Assert.DoesNotContain(Actions(deep), a => (a.SourcePath ?? "").Contains(@"\Service Worker", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(deep.Notes, n => n.StartsWith("Deep scan found") && n.Contains("busy temp folders"));
        Assert.DoesNotContain(quick.Notes, n => n.StartsWith("Deep scan"));
    }

    [Fact]
    public async Task Crash_dumps_are_ticked_only_when_older_than_14_days()
    {
        using var pc = FakePc.Create();
        var s = StandardPc.Build(pc);
        var items = Group(await pc.CleanupScan(), CleanupTargets.CrashDumps).Items;

        ScanItem RowWith(string path) => items.Single(i => i.Actions.Any(a => Touches(a, path)));
        Assert.True(RowWith(s.CrashAppOld).DefaultSelected);
        Assert.False(RowWith(s.CrashAppOld).NeedsAdmin);
        Assert.False(RowWith(s.CrashAppNew).DefaultSelected);
        Assert.Equal(Badge.Check, RowWith(s.CrashAppNew).Badge);
        Assert.True(RowWith(s.MinidumpOld).DefaultSelected && RowWith(s.MinidumpOld).NeedsAdmin);
        Assert.False(RowWith(s.MemoryDmp).DefaultSelected);
        Assert.Equal(CarveOuts.CrashDumps, RowWith(s.MemoryDmp).Actions.Single(a => a.SourcePath == s.MemoryDmp).CarveOutId);
    }

    [Fact]
    public async Task Error_reports_and_logs_respect_30_days_and_never_the_live_logs()
    {
        using var pc = FakePc.Create();
        var s = StandardPc.Build(pc);
        var r = await pc.CleanupScan(ScanDepth.Deep);
        var wer = Group(r, CleanupTargets.ErrorReports).Items.SelectMany(i => i.Actions).ToList();
        var logs = Group(r, CleanupTargets.WindowsLogs).Items.SelectMany(i => i.Actions).Select(a => a.SourcePath).ToList();

        Assert.Contains(wer, a => Touches(a, s.WerOld));
        Assert.DoesNotContain(wer, a => Touches(a, s.WerNew));
        Assert.Equal(new[] { s.CbsOld, s.DismOld }.OrderBy(x => x), logs.OrderBy(x => x));
        Assert.All(Group(r, CleanupTargets.WindowsLogs).Items, i => Assert.True(i.NeedsAdmin));
    }

    [Fact]
    public async Task Update_downloads_take_10_day_old_folders_and_skip_everything_when_a_restart_is_pending()
    {
        using var pc = FakePc.Create();
        var s = StandardPc.Build(pc);
        var item = Group(await pc.CleanupScan(), CleanupTargets.UpdateDownloads).Items.Single();
        Assert.Contains(item.Actions, a => a.SourcePath == s.UpdateOld && a.IsDirectory);
        Assert.DoesNotContain(item.Actions, a => Touches(a, s.UpdateRecentFolder));
        Assert.Equal(8000, item.SizeBytes);

        pc.State("RebootPending=1");
        var pending = await pc.CleanupScan(ScanDepth.Deep);
        var skipped = Group(pending, CleanupTargets.UpdateDownloads).Items.Single();
        Assert.Empty(skipped.Actions);
        Assert.NotNull(skipped.DisabledReason);
        Assert.Contains(pending.Notes, n => n.Contains("restart"));
    }

    [Fact]
    public async Task Old_installers_are_listed_one_by_one_by_extension_name_or_version_info()
    {
        using var pc = FakePc.Create();
        var s = StandardPc.Build(pc);
        var items = Group(await pc.CleanupScan(), CleanupTargets.OldInstallers).Items;
        var listed = items.Select(i => i.Actions.Single().SourcePath).ToList();

        Assert.Contains(s.SetupMsi, listed);
        Assert.Contains(s.SetupExe, listed);
        if (s.VersionInfoInstaller.Length > 0) Assert.Contains(s.VersionInfoInstaller, listed);
        Assert.DoesNotContain(s.NewMsi, listed);
        Assert.DoesNotContain(s.PlainExe, listed);
        Assert.DoesNotContain(s.Zip, listed);
        Assert.DoesNotContain(s.CloudMsi, listed);
        Assert.DoesNotContain(s.DeepSetup, listed); // sub-folders are Deep only
        Assert.All(items, i => Assert.True(i.Badge == Badge.Check && !i.DefaultSelected && i.Actions.Count == 1));
    }

    [Fact]
    public async Task Every_action_has_the_target_carve_out_id_and_stays_inside_its_roots()
    {
        using var pc = FakePc.Create();
        StandardPc.Build(pc);
        var r = await pc.CleanupScan(ScanDepth.Deep);
        var known = typeof(CarveOuts).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetValue(null)!).ToHashSet();

        foreach (var g in r.Groups)
        foreach (var a in g.Items.SelectMany(i => i.Actions))
        {
            Assert.Equal(g.Id, a.CarveOutId);
            Assert.Contains(a.CarveOutId!, known);
            Assert.Equal("cleanup", a.FeatureId);
            if (CarveOuts.Find(a.CarveOutId) is { } carve && a.SourcePath is { } src)
            {
                var roots = carve.Roots(pc.K).ToList();
                var single = CarveOuts.AllowedSingleFiles(pc.K).Where(f => f.CarveOutId == carve.Id).Select(f => f.File);
                Assert.True(roots.Any(root => PathUtil.IsUnder(src, root) && !PathUtil.Normalize(src).Equals(PathUtil.Normalize(root), StringComparison.OrdinalIgnoreCase))
                            || single.Contains(src), $"{src} is outside carve-out {carve.Id}");
            }
        }
    }

    [Fact]
    public async Task No_plan_item_has_a_destination_or_leaves_the_fake_pc()
    {
        using var pc = FakePc.Create();
        StandardPc.Build(pc);
        var allowedKinds = new[] { ActionKind.QuarantinePath, ActionKind.RunSystemTool, ActionKind.EmptyRecycleBin };
        var allowedTools = new[] { ToolIds.DeliveryOptimizationClear, ToolIds.CleanmgrHandler, ToolIds.DismStartComponentCleanup };

        foreach (var depth in new[] { ScanDepth.Quick, ScanDepth.Deep })
        foreach (var a in Actions(await pc.CleanupScan(depth)))
        {
            Assert.Null(a.DestinationPath);
            Assert.Contains(a.Kind, allowedKinds);
            if (a.SourcePath is not null) Assert.True(PathUtil.IsUnder(a.SourcePath, pc.Root));
            if (a.Kind == ActionKind.QuarantinePath)
            {
                Assert.True(a.Reversible);
                Assert.NotNull(a.ExpectedLastWriteUtc);
                Assert.NotNull(a.SourcePath);
            }
            else
            {
                Assert.False(a.Reversible);
                if (a.Kind == ActionKind.RunSystemTool) Assert.Contains(a.ToolId, allowedTools);
            }
        }
    }

    [Fact]
    public async Task Sizes_and_file_counts_are_exact()
    {
        using var pc = FakePc.Create();
        StandardPc.Build(pc);
        var r = await pc.CleanupScan();

        foreach (var i in r.AllItems.Where(i => i.Actions.All(a => a.Kind == ActionKind.QuarantinePath) && i.Actions.Count > 0))
            Assert.Equal(i.Actions.Sum(a => a.ExpectedSize), i.SizeBytes);

        var chrome = Group(r, CleanupTargets.BrowserCache).Items.Single(i => i.Title == "Chrome cache (Default)");
        Assert.Equal(StandardPc.ChromeDefaultCacheBytes, chrome.SizeBytes);
        Assert.Equal(4, chrome.FileCount);
        Assert.Equal(128 + 64, Group(r, CleanupTargets.BrowserCache).Items.Single(i => i.Title == "Chrome graphics caches").SizeBytes);
        Assert.Equal(1500 + 100 + 50, Group(r, CleanupTargets.FirefoxCache).Items.Single().SizeBytes);
        Assert.Equal(StandardPc.SpotifyBytes, Group(r, CleanupTargets.Spotify).Items.Single().SizeBytes);
        Assert.Equal(StandardPc.RecycleBytes, Group(r, CleanupTargets.RecycleBin).Items.Single().SizeBytes);
        Assert.Equal(640 + 32, Group(r, CleanupTargets.ThumbnailCache).Items.Single().SizeBytes);
        Assert.Equal(600 + 300 + 200 + 100, Group(r, CleanupTargets.Steam).Items.Single().SizeBytes); // new log excluded
        Assert.Equal(1500, Group(r, CleanupTargets.Telegram).Items.Single().SizeBytes); // second account is Deep only
        Assert.Equal(2500 + 100, Group(r, CleanupTargets.Discord).Items.Single().SizeBytes);
        Assert.Equal(5555, Group(r, CleanupTargets.WindowsOld).Items.Single().SizeBytes);
    }

    [Fact]
    public async Task Running_apps_are_named_their_items_still_listed_and_marked()
    {
        using var pc = FakePc.Create();
        StandardPc.Build(pc);
        var r = await pc.CleanupScan(ScanDepth.Quick, "chrome", "discord");

        Assert.Contains(r.Notes, n => n.Contains("Chrome is open"));
        Assert.Contains(r.Notes, n => n.Contains("Discord is open"));
        var chrome = Group(r, CleanupTargets.BrowserCache).Items.Single(i => i.Title == "Chrome cache (Default)");
        Assert.Contains("Chrome is open", chrome.Reason);
        Assert.All(chrome.Actions, a => Assert.Contains("chrome", a.RequiresClosedApps!));
        var edge = Group(r, CleanupTargets.BrowserCache).Items.Single(i => i.Title == "Edge cache (Default)");
        Assert.DoesNotContain("is open", edge.Reason);
        Assert.Contains("msedge", edge.RequiresClosedApps!);
    }

    [Fact]
    public async Task Windows_tools_and_recycle_bin_are_permanent_admin_and_system_flagged_as_planned()
    {
        using var pc = FakePc.Create();
        StandardPc.Build(pc);
        var r = await pc.CleanupScan();

        var dOpt = Group(r, CleanupTargets.DeliveryOptimization).Items.Single().Actions.Single();
        Assert.Equal(ToolIds.DeliveryOptimizationClear, dOpt.ToolId);
        Assert.True(dOpt.NeedsAdmin && !dOpt.Reversible);
        Assert.Equal(777, dOpt.ExpectedSize);

        var thumb = Group(r, CleanupTargets.ThumbnailCache).Items.Single().Actions.Single();
        Assert.Equal(ToolIds.CleanmgrHandler, thumb.ToolId);
        Assert.Equal("Thumbnail Cache", thumb.Args!["handler"]);
        Assert.False(thumb.Reversible);

        var old = Group(r, CleanupTargets.WindowsOld).Items.Single();
        Assert.Equal(Badge.Permanent, old.Badge);
        Assert.Equal("Previous Installations", old.Actions.Single().Args!["handler"]);
        Assert.True(old.Actions.Single().IsSystemChange && old.NeedsAdmin);

        var dism = Group(r, CleanupTargets.ComponentStore).Items.Single();
        Assert.Equal(ToolIds.DismStartComponentCleanup, dism.Actions.Single().ToolId);
        Assert.True(dism.Actions.Single().IsSystemChange);
        Assert.Equal("Measured when run", dism.CurrentState);

        var bin = Group(r, CleanupTargets.RecycleBin).Items.Single();
        Assert.Equal(ActionKind.EmptyRecycleBin, bin.Actions.Single().Kind);
        Assert.Equal(Badge.Permanent, bin.Badge);
    }

    [Fact]
    public async Task Spotify_is_size_only_and_points_to_its_own_button()
    {
        using var pc = FakePc.Create();
        StandardPc.Build(pc);
        var item = Group(await pc.CleanupScan(), CleanupTargets.Spotify).Items.Single();
        Assert.Empty(item.Actions);
        Assert.Contains("Clear cache", item.Reason);
        Assert.False(item.DefaultSelected);
    }

    [Fact]
    public async Task Junctions_inside_a_cleanup_folder_are_never_followed()
    {
        using var pc = FakePc.Create();
        StandardPc.Build(pc);
        var outside = Path.Combine(pc.Root, "Outside");
        var precious = pc.File(Path.Combine(outside, "precious.txt"), 4321, FakePc.Old);
        var link = Path.Combine(pc.K.UserTemp, "LinkedFolder");
        if (!Junction.TryCreate(link, outside)) return; // junctions unsupported here: nothing to test

        foreach (var depth in new[] { ScanDepth.Quick, ScanDepth.Deep })
        {
            var actions = Actions(await pc.CleanupScan(depth)).ToList();
            Assert.DoesNotContain(actions, a => a.SourcePath is { } p && (PathUtil.IsUnder(p, link) || PathUtil.IsUnder(precious, p)));
        }
    }

    [Fact]
    public async Task Excluded_folders_are_skipped()
    {
        using var pc = FakePc.Create();
        var s = StandardPc.Build(pc);
        var ctx = pc.Context(ScanDepth.Quick);
        ctx.Settings.ScanExclusions.Add(s.TempOldFolder);
        var r = await new CleanupScanner().ScanAsync(ctx, null, CancellationToken.None);
        Assert.DoesNotContain(Actions(r), a => Touches(a, s.TempOldFolder));
        Assert.Contains(Actions(r), a => a.SourcePath == s.TempOldFile);
    }

    [Fact]
    public async Task Minimal_pc_scans_cleanly_and_hides_apps_it_does_not_have()
    {
        using var pc = FakePc.Create(SandboxProfile.Minimal);
        pc.File(Path.Combine(pc.K.UserTemp, "old.tmp"), 123, FakePc.Old);
        pc.File(Path.Combine(pc.K.WindowsDir, "WinSxS", "x"), 1, FakePc.Old);
        pc.Seal();

        foreach (var depth in new[] { ScanDepth.Quick, ScanDepth.Deep })
        {
            var r = await pc.CleanupScan(depth);
            Assert.DoesNotContain(r.Notes, n => n.Contains("couldn't be checked"));
            var ids = r.Groups.Select(g => g.Id).ToList();
            Assert.Contains(CarveOuts.UserTemp, ids);
            Assert.Contains(CarveOuts.ComponentStore, ids);
            foreach (var hidden in new[] { CleanupTargets.BrowserCache, CleanupTargets.FirefoxCache, CleanupTargets.BrowserPrivacy, CleanupTargets.Discord,
                         CleanupTargets.Steam, CleanupTargets.Spotify, CleanupTargets.Telegram, CleanupTargets.ShaderCache, CleanupTargets.WindowsOld,
                         CleanupTargets.ThumbnailCache, CleanupTargets.DeliveryOptimization })
                Assert.DoesNotContain(hidden.Id, ids);
            Assert.Equal(123, Group(r, CleanupTargets.UserTemp).Items.Single().SizeBytes);
        }
    }

    [Fact]
    public async Task Empty_sandbox_scan_does_not_fail()
    {
        using var pc = FakePc.Create(SandboxProfile.Minimal);
        var r = await pc.CleanupScan(ScanDepth.Deep);
        Assert.DoesNotContain(r.Notes, n => n.Contains("couldn't be checked"));
        Assert.Empty(Actions(r));
    }

    [Fact]
    public async Task Cancellation_is_honoured()
    {
        using var pc = FakePc.Create();
        StandardPc.Build(pc);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CleanupScanner().ScanAsync(pc.Context(ScanDepth.Quick), null, cts.Token));
    }
}
