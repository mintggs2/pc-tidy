using System.Text.RegularExpressions;
using PcTidy.Catalog;
using PcTidy.Catalog.Games;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Games.Common;
using PcTidy.Features.Games.Library;
using PcTidy.Features.Games.Optimize;
using Xunit;

namespace PcTidy.Features.Games.Tests;

/// <summary>
/// v3.1 Games → Optimize master/detail: status chips from the real config files, search and filters, the "Doesn't help
/// (measured)" shelf (never planned), measured numbers on rows, guidance cards, per-app GPU preference on hybrid laptops,
/// the shader-cache item (never ticked) and "Reset to my backup". All on invented sandbox PCs.
/// </summary>
[Collection("games-testbed")]
public class OptimizeMasterDetailTests
{
    private const long Updated = 1735732800;

    internal static string LocalConfig(string appsBody) =>
        "\"UserLocalConfigStore\"\n{\n\t\"Software\"\n\t{\n\t\t\"Valve\"\n\t\t{\n\t\t\t\"Steam\"\n\t\t\t{\n\t\t\t\t\"apps\"\n\t\t\t\t{\n" +
        appsBody + "\t\t\t\t}\n\t\t\t}\n\t\t}\n\t}\n}\n";

    internal static string App(string id, string? launchOptions) =>
        $"\t\t\t\t\t\"{id}\"\n\t\t\t\t\t{{\n" + (launchOptions is null ? "\t\t\t\t\t\t\"LastPlayed\"\t\t\"1700000000\"\n" : $"\t\t\t\t\t\t\"LaunchOptions\"\t\t\"{launchOptions}\"\n") + "\t\t\t\t\t}\n";

    private static async Task<IReadOnlyList<GameEntry>> Scan(GamesSandbox s, ISafetyGuard? guard = null, ISystemInfo? system = null)
    {
        var scanner = new OptimizerScanner();
        await scanner.ScanAsync(s.Context(guard: guard ?? new AllowAllGuard(), system: system), null, CancellationToken.None);
        return scanner.LastEntries;
    }

    private static string Cs2(GamesSandbox s) => s.SteamGame(s.SteamRoot, "730", "Counter-Strike 2", "Counter-Strike Global Offensive", null, Updated,
        @"game\bin\win64\engine2.dll", @"game\bin\win64\cs2.exe");

    /// <summary>An installed CS2 with no launch options (Not tuned), for the UI test.</summary>
    internal static GameEntry Cs2Entry()
    {
        using var s = new GamesSandbox("cs2-entry");
        Cs2(s);
        s.Put(s.LocalConfigPath(), LocalConfig(App("730", null)));
        return Scan(s).GetAwaiter().GetResult().Single(e => e.Name == "Counter-Strike 2");
    }

    internal static GameEntry NotInstalledEntry(string recipeId)
    {
        using var s = new GamesSandbox("catalog-entry");
        return Scan(s).GetAwaiter().GetResult().Single(e => e.Key == "catalog:" + recipeId);
    }

    // ---------------- status chips ----------------

    [Fact]
    public async Task Status_chips_come_from_the_real_config_files_not_from_memory()
    {
        using var s = new GamesSandbox("status");
        s.SteamGame(s.SteamRoot, "440", "Team Fortress 2", "Team Fortress 2", null, Updated, "hl2.exe", @"bin\engine.dll");
        s.SteamGame(s.SteamRoot, "1234560", "Some Game", "SomeGame", null, Updated, "SomeGame.exe", "UnityPlayer.dll");
        s.SteamGame(s.SteamRoot, "1172470", "Apex Legends", "Apex Legends", null, Updated, "r5apex.exe", @"bin\x64_retail\engine.dll");
        s.Put(s.LocalConfigPath(), LocalConfig(App("440", "-novid") + App("1234560", null) + App("1172470", null)));

        var entries = await Scan(s);
        GameEntry E(string name) => entries.Single(e => e.Name == name);
        Assert.Equal(GameTuneStatus.Tuned, E("Team Fortress 2").Status);       // -novid already in its launch options
        Assert.Equal(GameTuneStatus.NotTuned, E("Some Game").Status);          // exclusive fullscreen not set yet
        Assert.Equal(GameTuneStatus.InfoOnly, E("Apex Legends").Status);       // Easy Anti-Cheat: advice only
        Assert.Contains("Easy Anti-Cheat", E("Apex Legends").StatusWhy);
        var fortnite = entries.Single(e => e.Key == "catalog:fortnite");        // in the catalog, not installed
        Assert.Equal(GameTuneStatus.NotInstalled, fortnite.Status);
        Assert.False(fortnite.Installed);
        Assert.All(fortnite.Settings.Concat(fortnite.Placebo), i => Assert.Empty(i.Actions)); // viewable, nothing applied
        Assert.Null(fortnite.ShaderCache);
        Assert.DoesNotContain(entries, e => e.Key == "catalog:tf2"); // installed recipes aren't listed twice

        // The user sets the option in Steam themselves: the next scan reads the file and calls it Tuned.
        s.Put(s.LocalConfigPath(), LocalConfig(App("440", "-novid") + App("1234560", "-window-mode exclusive") + App("1172470", null)));
        Assert.Equal(GameTuneStatus.Tuned, (await Scan(s)).Single(e => e.Name == "Some Game").Status);

        // A placebo option already set means the config doesn't match the recipe: Not tuned, with a removal row.
        s.Put(s.LocalConfigPath(), LocalConfig(App("440", "-novid -high") + App("1234560", "-window-mode exclusive") + App("1172470", null)));
        var tf2 = (await Scan(s)).Single(e => e.Name == "Team Fortress 2");
        Assert.Equal(GameTuneStatus.NotTuned, tf2.Status);
        Assert.Contains(tf2.Settings, i => i.Title.Contains("\"-high\"") && i.Actions.Count == 1);
    }

    [Fact]
    public async Task An_unreal_ini_setting_counts_toward_the_status()
    {
        using var s = new GamesSandbox("status-ini");
        s.SteamGame(s.SteamRoot, "555", "Shooter", "Shooter", null, Updated,
            @"Shooter\Binaries\Win64\Shooter-Win64-Shipping.exe", @"Engine\Binaries\Win64\x.dll", @"Shooter\Content\Paks\Shooter-WindowsClient.pak");
        var ini = s.Put(Path.Combine(s.K.LocalAppData, "Shooter", "Saved", "Config", "WindowsClient", "GameUserSettings.ini"),
            "[/Script/Engine.GameUserSettings]\r\nbUseVSync=True\r\n");
        var shooter = (await Scan(s, guard: s.Host.CreateScanContext(ScanDepth.Quick).Safety)).Single(e => e.Name == "Shooter");
        Assert.Equal(GameTuneStatus.NotTuned, shooter.Status);
        Assert.Contains(ini, shooter.ConfigFiles);

        s.Put(ini, "[/Script/Engine.GameUserSettings]\r\nbUseVSync=False\r\n");
        Assert.Equal(GameTuneStatus.Tuned, (await Scan(s)).Single(e => e.Name == "Shooter").Status);
    }

    // ---------------- search and filters ----------------

    private static GameEntry Entry(string name, GameTuneStatus status, bool installed = true) => new()
    {
        Key = name.ToLowerInvariant(), Name = name, Installed = installed, Summary = "", Status = status, StatusWhy = "",
    };

    [Fact]
    public void Search_and_filters_narrow_the_list_and_not_tuned_games_come_first()
    {
        var entries = new[]
        {
            Entry("Valorant", GameTuneStatus.InfoOnly),
            Entry("Counter-Strike 2", GameTuneStatus.Tuned),
            Entry("Some Game", GameTuneStatus.NotTuned),
            Entry("Fortnite", GameTuneStatus.NotInstalled, installed: false),
            Entry("Another Game", GameTuneStatus.NotTuned),
        };
        Assert.Equal(new[] { "Another Game", "Some Game", "Counter-Strike 2", "Valorant", "Fortnite" },
            GameList.Apply(entries, null, GameListFilter.All).Select(e => e.Name));
        Assert.DoesNotContain(GameList.Apply(entries, "", GameListFilter.Installed), e => !e.Installed);
        Assert.Equal(4, GameList.Apply(entries, "", GameListFilter.Installed).Count);
        Assert.Equal(new[] { "Another Game", "Some Game" }, GameList.Apply(entries, "", GameListFilter.NotTuned).Select(e => e.Name));
        Assert.Equal("Counter-Strike 2", Assert.Single(GameList.Apply(entries, "  counter ", GameListFilter.All)).Name);
        Assert.Empty(GameList.Apply(entries, "fort", GameListFilter.Installed)); // search and filter combine
        Assert.Empty(GameList.Apply(entries, "zzz", GameListFilter.All));
    }

    // ---------------- placebo shelf ----------------

    [Fact]
    public async Task Placebo_rows_are_greyed_with_their_number_and_never_planned()
    {
        using var s = new GamesSandbox("placebo");
        Cs2(s);
        s.Put(s.LocalConfigPath(), LocalConfig(App("730", null)));
        var entries = await Scan(s);
        var cs2 = entries.Single(e => e.Name == "Counter-Strike 2");

        var high = cs2.Placebo.Single(i => i.Title.Contains("-high"));
        Assert.Contains("902.6 vs 900.7", high.Details!["Measured"]);
        Assert.Contains("7800X3D", high.Details["Measured"]);
        Assert.Contains("9Erza", high.Details["Measured"]);
        Assert.StartsWith("Doesn't help (measured)", high.DisabledReason);
        Assert.Contains(cs2.Placebo, i => i.Title.Contains("-threads"));
        Assert.Contains(cs2.Placebo, i => i.Title.Contains("Low latency"));

        foreach (var e in entries)
        {
            Assert.All(e.Placebo, i =>
            {
                Assert.Empty(i.Actions);
                Assert.NotNull(i.DisabledReason);
                Assert.False(i.DefaultSelected);
                Assert.StartsWith("Placebo", i.Details!["Verdict"]);
            });
            Assert.Empty(PlanBuilder.FromSelection("games", "t", e.Placebo).Items);
            Assert.DoesNotContain(e.Tickable, i => i.Details?.GetValueOrDefault("Verdict")?.StartsWith("Placebo") == true && !i.Title.StartsWith("Remove"));
        }
        // The catalog itself: no placebo recommendation is ever apply-able.
        Assert.All(GameRecipes.All.SelectMany(r => r.Recommendations).Where(r => r.IsPlacebo), r => Assert.False(r.IsApplicable));
        Assert.Contains(entries.Single(e => e.Key == "catalog:fortnite").Placebo, i => i.Title.Contains("sg."));
    }

    // ---------------- measured numbers ----------------

    [Fact]
    public async Task Recipe_rows_show_the_measured_number_with_hardware_and_source()
    {
        using var s = new GamesSandbox("measured");
        Cs2(s);
        s.Put(s.LocalConfigPath(), LocalConfig(App("730", null)));
        var entries = await Scan(s);
        var cs2 = entries.Single(e => e.Name == "Counter-Strike 2").Settings;
        string M(IEnumerable<ScanItem> rows, string title) => rows.Single(i => i.Title.Contains(title)).Details!["Measured"];

        var cost = M(cs2, "cost the most FPS");
        foreach (var n in new[] { "MSAA 8x −11.6%", "shadows Very High −10.2%", "particles Very High −9.6%", "anisotropic 16x −0.9%", "7800X3D", "April 2026" })
            Assert.Contains(n, cost);
        Assert.Contains("520 average / 180 1% low", M(cs2, "400 FPS cap"));
        Assert.Contains("15% worse 1% lows", M(cs2, "Vulkan"));
        var res = M(cs2, "Lower resolution");
        Assert.Contains("+21.6%", res);
        Assert.Contains("+4.6%", res);

        var fortnite = entries.Single(e => e.Key == "catalog:fortnite").Settings;
        var perf = M(fortnite, "Performance rendering mode");
        foreach (var n in new[] { "185 vs DirectX 12 135", "68 vs 41", "i3-12100F + GTX 1660 Super", "unverified blog" })
            Assert.Contains(n, perf);
        Assert.Contains("10,000+", M(entries.Single(e => e.Key == "catalog:apex").Settings, "DirectX 12"));

        // Unverified recommendations stay display-only.
        Assert.All(entries.SelectMany(e => e.Settings).Where(i => i.Details?.GetValueOrDefault("Evidence")?.StartsWith("Community report") == true
                                                                   && !i.Title.StartsWith("Remove")),
            i => Assert.Empty(i.Actions));
        // Every row names its source.
        Assert.All(entries.SelectMany(e => e.Settings.Concat(e.Placebo)), i => Assert.StartsWith("https://", i.Details!["Source"]));
    }

    // ---------------- guidance cards ----------------

    [Fact]
    public void Guidance_cards_carry_numbers_hardware_and_dated_sources()
    {
        Assert.Equal(new[] { "C1", "C2", "C7", "C9", "C12" }, GuidanceCards.All.Select(c => c.Id));
        foreach (var card in GuidanceCards.All)
        {
            Assert.NotEmpty(card.Points);
            Assert.NotEmpty(card.Measured);
            foreach (var m in card.Measured)
            {
                Assert.Matches(@"\d", m.Result);
                Assert.False(string.IsNullOrWhiteSpace(m.Hardware));
                Assert.True(Regex.IsMatch(m.Source, @"\b(19|20)\d{2}\b") || m.Source.Contains("undated"), m.Source);
                Assert.StartsWith("https://", m.SourceUrl);
                Assert.Contains(m.Hardware, m.Line);
                Assert.Contains(m.Source, m.Line);
            }
        }
        string All(GuidanceCard c) => string.Join(" ", c.Points.Append(c.Summary).Concat(c.Measured.Select(m => m.Line)));

        var c1 = All(GuidanceCards.CapYourFps);
        foreach (var n in new[] { "in-game limiter", "refresh rate minus 3", "11 to 22 ms", "42%", "9 to 10 ms", "about 12 ms", "about 20 ms", "RX 5700", "GTX 1080", "2019", "9800X3D + RTX 5070 Ti" })
            Assert.Contains(n, c1);
        var c2 = All(GuidanceCards.WhyIsMyGameSlow);
        foreach (var n in new[] { "GPU-bound", "CPU-bound", "MSAA 8x −11.6%", "shadows Very High −10.2%", "particles Very High −9.6%", "anisotropic 16x −0.9%" })
            Assert.Contains(n, c2);
        var c7 = All(GuidanceCards.ReflexAntiLag);
        foreach (var n in new[] { "20 → 16.7 ms", "0.2 ms", "1.1 ms", "Boost isn't needed", "Anti-Lag+", "23.10.1", "VAC" })
            Assert.Contains(n, c7);
        var c9 = All(GuidanceCards.ShaderCacheWarning);
        foreach (var n in new[] { "1 to 2 hours", "stutters", "about 1 hour", "16 GB", "Digital Foundry" })
            Assert.Contains(n, c9);
        var c12 = All(GuidanceCards.BackgroundApps);
        foreach (var n in new[] { "423", "403", "about 5%", "Windows 10", "2020", "+2.5%", "9800X3D", "PCWorld", "2025", "never touches services, priority or power" })
            Assert.Contains(n, c12);

        Assert.Equal("C12", GuidanceCards.For(CardPlace.Boost)[0].Id);
        Assert.Contains(GuidanceCards.For(CardPlace.Boost), c => c.Id == "C1");
        Assert.Contains(GuidanceCards.For(CardPlace.Boost), c => c.Id == "C7");
        Assert.DoesNotContain(GuidanceCards.For(CardPlace.GamesTips), c => c.Id == "C12");
        Assert.Contains(GuidanceCards.For(CardPlace.GamesCleanup), c => c.Id == "C9");
        Assert.Contains("1 to 2 hours", Cleanup.GameCleanupScanner.ShaderCacheNote);
    }

    // ---------------- hybrid GPU ----------------

    [Fact]
    public void Hybrid_gpu_needs_an_integrated_and_a_separate_gpu()
    {
        Assert.True(GpuKinds.IsHybrid(new[] { "Intel(R) UHD Graphics", "NVIDIA GeForce RTX 4060 Laptop GPU" }));
        Assert.True(GpuKinds.IsHybrid(new[] { "AMD Radeon(TM) Graphics", "NVIDIA GeForce RTX 3050 Laptop GPU" }));
        Assert.True(GpuKinds.IsHybrid(new[] { "AMD Radeon 780M Graphics", "AMD Radeon RX 7600S" }));
        Assert.True(GpuKinds.IsHybrid(new[] { "Intel(R) Arc(TM) Graphics", "NVIDIA GeForce RTX 4070 Laptop GPU", "Microsoft Basic Display Adapter" }));
        Assert.False(GpuKinds.IsHybrid(new[] { "NVIDIA GeForce RTX 4070" }));
        Assert.False(GpuKinds.IsHybrid(new[] { "NVIDIA GeForce RTX 4070", "Microsoft Basic Display Adapter", "Parsec Virtual Display Adapter" }));
        Assert.False(GpuKinds.IsHybrid(new[] { "Intel(R) Arc(TM) A770 Graphics", "NVIDIA GeForce RTX 4070" })); // two separate GPUs
        Assert.False(GpuKinds.IsHybrid(new[] { "Intel(R) Iris(R) Xe Graphics" }));
        Assert.False(GpuKinds.IsHybrid(Array.Empty<string>()));
        Assert.False(GpuKinds.IsHybrid(null));

        Assert.Equal(GpuKind.Integrated, GpuKinds.Classify("Intel(R) Arc(TM) 140V GPU"));
        Assert.Equal(GpuKind.Integrated, GpuKinds.Classify("AMD Radeon(TM) Vega 8 Graphics"));
        Assert.Equal(GpuKind.Discrete, GpuKinds.Classify("AMD Radeon RX 6800M"));
        Assert.Equal(GpuKind.Discrete, GpuKinds.Classify("Intel(R) Arc(TM) B580 Graphics"));
        Assert.Equal(GpuKind.Virtual, GpuKinds.Classify("Microsoft Remote Display Adapter"));
    }

    [Fact]
    public void Gpu_preference_allowance_is_hkcu_exact_key_exe_path_names_and_one_token_of_data()
    {
        const string key = GpuPreferenceAllowances.Key;
        Assert.Equal(@"Software\Microsoft\DirectX\UserGpuPreferences", key);
        var a = RegistryCatalog.Find(RegistryHive.CurrentUser, key, @"D:\Games\Shooter\Shooter.exe");
        Assert.NotNull(a);
        Assert.Equal(GpuPreferenceAllowances.Source, a!.Source);
        Assert.Equal(RegistryKind.String, a.Kind);
        Assert.Empty(a.AllowedData);
        Assert.Equal(@"^GpuPreference=[0-2];$", a.DataPattern);
        Assert.False(a.NeedsAdmin);
        Assert.True(a.AllowDelete); // undo removes a value that wasn't there before
        Assert.Equal(@"^[A-Za-z]:\\[^<>""|?*\r\n]+\.exe$", a.ValueNamePattern);

        Assert.Null(RegistryCatalog.Find(RegistryHive.LocalMachine, key, @"D:\Games\Shooter\Shooter.exe"));
        Assert.Null(RegistryCatalog.Find(RegistryHive.CurrentUser, key + @"\Sub", @"D:\Games\Shooter\Shooter.exe"));
        foreach (var bad in new[] { "Shooter.exe", @"\\server\share\Shooter.exe", @"D:\Games\Shooter\Shooter.dll", @"D:\Games\a?.exe", "D:\\Games\\a\".exe", "D:\\Games\\a\r\n.exe", @"D:\Games\*.exe" })
            Assert.NotEqual(GpuPreferenceAllowances.Source, RegistryCatalog.Find(RegistryHive.CurrentUser, key, bad)?.Source);
        Assert.NotEqual(GpuPreferenceAllowances.Source, RegistryCatalog.Find(RegistryHive.CurrentUser, key, "DirectXUserGlobalSettings")?.Source);

        foreach (var ok in new[] { "GpuPreference=0;", "GpuPreference=1;", "GpuPreference=2;" }) Assert.Matches(a.DataPattern!, ok);
        foreach (var no in new[] { "GpuPreference=3;", "GpuPreference=2", "GpuPreference=2;SwapEffectUpgradeEnable=1;", " GpuPreference=2;" })
            Assert.DoesNotMatch(a.DataPattern!, no);
        Assert.Equal(2, GpuPreferenceAllowances.ParsePreference("GpuPreference=2;"));
        Assert.Null(GpuPreferenceAllowances.ParsePreference("GpuPreference=1;SwapEffectUpgradeEnable=1;"));
    }

    [Fact]
    public async Task Hybrid_laptops_get_an_unticked_gpu_preference_row_the_real_guard_allows()
    {
        using var s = new GamesSandbox("gpu-pref");
        var install = s.SteamGame(s.SteamRoot, "1234560", "Some Game", "SomeGame", null, Updated, "SomeGame.exe", "UnityPlayer.dll", "UnityCrashHandler64.exe");
        s.Put(s.LocalConfigPath(), LocalConfig(App("1234560", null)));
        var hybrid = new FakeSystemInfo("Intel(R) UHD Graphics", "NVIDIA GeForce RTX 4060 Laptop GPU");
        var guard = s.Host.CreateScanContext(ScanDepth.Quick).Safety;
        var exe = Path.Combine(install, "SomeGame.exe");

        var entry = (await Scan(s, guard, hybrid)).Single(e => e.Name == "Some Game");
        var row = Assert.IsType<ScanItem>(entry.GpuPreference);
        Assert.False(row.DefaultSelected);
        Assert.Null(row.DisabledReason);
        var item = Assert.Single(row.Actions); // the crash handler is not the game
        Assert.Equal(ActionKind.RegistrySetValue, item.Kind);
        Assert.Equal(RegistryHive.CurrentUser, item.Registry!.Hive);
        Assert.Equal(GpuPreferenceAllowances.Key, item.Registry.KeyPath);
        Assert.Equal(exe, item.Registry.ValueName);
        Assert.Equal(RegistryKind.String, item.Registry.Kind);
        Assert.Equal("GpuPreference=2;", item.Registry.Data);
        Assert.False(item.NeedsAdmin);
        Assert.True(item.Reversible);
        Assert.Contains("No FPS data", row.Details!["Evidence"]);
        Assert.Contains("verify", row.Details["Setting"]);
        Assert.Equal(GameTuneStatus.NotTuned, entry.Status); // the GPU row isn't part of the game's config status
        Assert.Contains(entry.Tickable, i => ReferenceEquals(i, row));

        // Undo puts back the exact before-value, or removes the value when there was none: both are allowed, nothing else.
        Assert.True(guard.Check(item with { Kind = ActionKind.RegistryDeleteValue, Registry = item.Registry with { Data = null } }).Allowed);
        Assert.True(guard.Check(item with { Registry = item.Registry with { Data = "GpuPreference=1;" } }).Allowed);
        Assert.False(guard.Check(item with { Registry = item.Registry with { Data = "GpuPreference=2;Other=1;" } }).Allowed);
        Assert.False(guard.Check(item with { Registry = item.Registry with { ValueName = "SomeGame.exe" } }).Allowed);
        Assert.False(guard.Check(item with { Registry = item.Registry with { Hive = RegistryHive.LocalMachine } }).Allowed);

        // Already high performance → nothing to do; other settings in the value → Windows' own page only.
        var env = (FakeEnvironment)s.Environment;
        env.Gpu[exe] = "GpuPreference=2;";
        var already = (await Scan(s, guard, hybrid)).Single(e => e.Name == "Some Game").GpuPreference!;
        Assert.Equal("Already set", already.CurrentState);
        Assert.Empty(already.Actions);
        env.Gpu[exe] = "GpuPreference=1;SwapEffectUpgradeEnable=1;";
        var custom = (await Scan(s, guard, hybrid)).Single(e => e.Name == "Some Game").GpuPreference!;
        Assert.Empty(custom.Actions);
        Assert.Contains("Settings → System → Display → Graphics", custom.DisabledReason);
        env.Gpu[exe] = "GpuPreference=1;";
        Assert.Single((await Scan(s, guard, hybrid)).Single(e => e.Name == "Some Game").GpuPreference!.Actions);

        // One GPU (or none known): no row at all.
        Assert.Null((await Scan(s, guard, new FakeSystemInfo("NVIDIA GeForce RTX 4070"))).Single(e => e.Name == "Some Game").GpuPreference);
        Assert.Null((await Scan(s, guard)).Single(e => e.Name == "Some Game").GpuPreference);
    }

    // ---------------- shader cache ----------------

    [Fact]
    public void The_shader_cache_item_is_never_ticked_and_carries_the_rebuild_warning()
    {
        using var s = new GamesSandbox("shader-detail");
        s.Put(Path.Combine(s.K.LocalAppData, "FortniteGame", "Saved", "D3D12Cache", "cache.bin"), new string('c', 4000));
        var g = GamesContext.From(s.Context());
        var row = OptimizerScanner.ShaderCacheRow(g, "Fortnite", GameRecipes.All.Single(r => r.Id == "fortnite"), CancellationToken.None);
        Assert.NotNull(row);
        Assert.False(row!.DefaultSelected);
        Assert.Contains("1 to 2 hours", row.Reason);
        Assert.Contains("16 GB", row.Details!["Measured"]);
        Assert.NotEmpty(row.Actions);
        Assert.All(row.Actions, a => Assert.Equal(ActionKind.QuarantinePath, a.Kind));
        Assert.Null(OptimizerScanner.ShaderCacheRow(g, "Some Game", null, CancellationToken.None));
    }

    // ---------------- reset to my backup ----------------

    private static RunRecord Run(string id, RunStatus status, params PlanItem[] items) => new()
    {
        RunId = id, FeatureId = "games", Title = "Game settings", Status = status, Mode = ExecutionMode.Real,
        CreatedUtc = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc).AddMinutes(int.Parse(id[^1..])),
        Steps = items.Select((p, i) => new StepRecord { Index = i, Item = p, Status = StepStatus.Done }).ToList(),
    };

    private static PlanItem LaunchEdit(string path, params string[] appIds) => new()
    {
        Kind = ActionKind.EditTextFile, FeatureId = "games", Description = "edit", SourcePath = path,
        Args = appIds.Length == 1
            ? new() { [EditArgs.Kind] = EditArgs.KindLaunchOptions, [EditArgs.AppId] = appIds[0], [EditArgs.Content] = "x" }
            : new() { [EditArgs.Kind] = EditArgs.KindLaunchOptions, [EditArgs.AppIds] = string.Join(",", appIds), [EditArgs.Content] = "x" },
    };

    [Fact]
    public void Reset_finds_this_games_backups_and_never_undoes_a_run_shared_with_other_games()
    {
        const string cfg = @"C:\Steam\userdata\1\config\localconfig.vdf";
        const string exe = @"D:\Games\CS\game\bin\win64\cs2.exe";
        var cs2 = new GameEntry
        {
            Key = "steam:730", Name = "Counter-Strike 2", Installed = true, Summary = "", Status = GameTuneStatus.Tuned, StatusWhy = "",
            Game = new InstalledGame { Launcher = Launcher.Steam, Id = "730", Name = "Counter-Strike 2", InstallDir = @"D:\Games\CS" },
            ExePaths = new[] { exe },
        };
        var own = Run("r1", RunStatus.Completed, LaunchEdit(cfg, "730"));
        var gpu = Run("r2", RunStatus.Completed, GpuPreferencePlanner.Plan("Counter-Strike 2", exe));
        var other = Run("r3", RunStatus.Completed, LaunchEdit(cfg, "440"));
        var undone = Run("r4", RunStatus.Undone, LaunchEdit(cfg, "730"));
        var simulated = Run("r5", RunStatus.Simulated, LaunchEdit(cfg, "730")) with { Mode = ExecutionMode.Simulate };

        var found = BackupFinder.For(cs2, new[] { own, gpu, other, undone, simulated });
        Assert.True(found.CanReset);
        Assert.Equal(new[] { "r2", "r1" }, found.Runs.Select(r => r.RunId)); // newest first, only this game's

        var shared = Run("r6", RunStatus.Completed, LaunchEdit(cfg, "730", "440"));
        var refused = BackupFinder.For(cs2, new[] { own, shared });
        Assert.False(refused.CanReset);
        Assert.Contains("History", refused.Problem);

        var mixed = Run("r7", RunStatus.Completed, LaunchEdit(cfg, "730"), LaunchEdit(cfg, "440"));
        Assert.False(BackupFinder.For(cs2, new[] { mixed }).CanReset);

        Assert.False(BackupFinder.For(cs2, new[] { other }).CanReset);
        Assert.Contains("no backup", BackupFinder.For(cs2, Array.Empty<RunRecord>()).Problem);
        Assert.False(BackupFinder.For(cs2 with { Installed = false }, new[] { own }).CanReset);
    }

    private const string FnIni = @"C:\Users\Player\AppData\Local\FortniteGame\Saved\Config\WindowsClient\GameUserSettings.ini";
    private const string FnCache = @"C:\Users\Player\AppData\Local\FortniteGame\Saved\D3D12Cache";
    private const string FnExe = @"D:\Games\Fortnite\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe";

    private static GameEntry Fortnite() => new()
    {
        Key = "epic:fn", Name = "Fortnite", Installed = true, Summary = "", Status = GameTuneStatus.Tuned, StatusWhy = "",
        Game = new InstalledGame { Launcher = Launcher.Epic, Id = "fn", Name = "Fortnite", InstallDir = @"D:\Games\Fortnite" },
        ConfigFiles = new[] { FnIni },
        ExePaths = new[] { FnExe },
        ShaderCacheFolders = new[] { FnCache },
    };

    private static PlanItem IniEdit(string path) => new()
    {
        Kind = ActionKind.EditTextFile, FeatureId = "games", Description = "Set a value in " + Path.GetFileName(path), SourcePath = path,
        Args = new() { [EditArgs.Content] = "x" },
    };

    private static PlanItem CacheClear(string path) => new()
    {
        Kind = ActionKind.QuarantinePath, FeatureId = "games", Description = "Move shader cache file \"" + Path.GetFileName(path) + "\" to quarantine",
        SourcePath = path,
    };

    [Fact]
    public void Reset_counts_clearing_this_games_own_shader_cache_as_this_game_and_lists_every_step()
    {
        var fn = Fortnite();
        var run = Run("r1", RunStatus.Completed, IniEdit(FnIni), CacheClear(Path.Combine(FnCache, "pipeline.bin")));
        var found = BackupFinder.For(fn, new[] { run });
        Assert.True(found.CanReset);
        Assert.Equal(new[] { "r1" }, found.Runs.Select(r => r.RunId));
        Assert.Equal(2, found.Steps.Count);
        Assert.Contains(found.Steps, s => s.Contains("GameUserSettings.ini"));
        Assert.Contains(found.Steps, s => s.Contains("pipeline.bin"));

        // The note under the button lists the steps before the user clicks.
        var text = UI.GameOptimizeView.ResetText(fn, found);
        Assert.Contains("It undoes:", text);
        Assert.Contains("• Set a value in GameUserSettings.ini", text);
        Assert.Contains("pipeline.bin", text);

        // A run that only cleared the cache isn't a backup of the settings.
        Assert.False(BackupFinder.For(fn, new[] { Run("r2", RunStatus.Completed, CacheClear(Path.Combine(FnCache, "a.bin"))) }).CanReset);
        // The cache folder itself (not an entry inside it) isn't this game's step.
        Assert.False(BackupFinder.For(fn, new[] { Run("r3", RunStatus.Completed, IniEdit(FnIni), CacheClear(FnCache)) }).CanReset);
    }

    [Fact]
    public void Reset_refuses_a_run_that_also_touched_another_game_or_anything_else()
    {
        var fn = Fortnite();
        const string otherIni = @"C:\Users\Player\AppData\Local\OtherGame\Saved\Config\WindowsClient\GameUserSettings.ini";
        const string otherExe = @"D:\Games\Other\Other.exe";
        PlanItem[] others =
        {
            IniEdit(otherIni),                                                            // another game's config
            GpuPreferencePlanner.Plan("Other", otherExe),                                 // another game's GPU preference
            CacheClear(@"C:\Users\Player\AppData\Local\OtherGame\Saved\D3D12Cache\x.bin"), // another game's shader cache
            CacheClear(@"C:\Users\Player\AppData\Local\FortniteGame\Saved\Logs\a.log"),    // a cleanup step, even for this game
            new() { Kind = ActionKind.RegistrySetValue, FeatureId = "tweaks", Description = "another section" },
        };
        foreach (var other in others)
        {
            var lookup = BackupFinder.For(fn, new[] { Run("r1", RunStatus.Completed, IniEdit(FnIni), other) });
            Assert.False(lookup.CanReset);
            Assert.Contains("History", lookup.Problem);
            Assert.Empty(lookup.Steps);
        }

        // Steps that didn't finish aren't undone, so they don't make a run mixed.
        var skipped = Run("r2", RunStatus.CompletedWithSkips, IniEdit(FnIni), IniEdit(otherIni));
        skipped = skipped with { Steps = new[] { skipped.Steps[0], skipped.Steps[1] with { Status = StepStatus.Skipped } } };
        var ok = BackupFinder.For(fn, new[] { skipped });
        Assert.True(ok.CanReset);
        Assert.Single(ok.Steps);
    }

    [Fact]
    public void Reset_refuses_an_old_v30_run_that_covered_several_games_for_every_one_of_them()
    {
        const string cfg = @"C:\Steam\userdata\1\config\localconfig.vdf";
        const string csExe = @"D:\Games\CS\game\bin\win64\cs2.exe";
        var cs2 = new GameEntry
        {
            Key = "steam:730", Name = "Counter-Strike 2", Installed = true, Summary = "", Status = GameTuneStatus.Tuned, StatusWhy = "",
            Game = new InstalledGame { Launcher = Launcher.Steam, Id = "730", Name = "Counter-Strike 2", InstallDir = @"D:\Games\CS" },
            ExePaths = new[] { csExe },
        };
        var fn = Fortnite();
        // v3.0 built one plan for every ticked game: separate per-game steps, not one merged edit.
        var v30 = Run("r1", RunStatus.Completed,
            LaunchEdit(cfg, "730"),
            GpuPreferencePlanner.Plan("Counter-Strike 2", csExe),
            IniEdit(FnIni),
            GpuPreferencePlanner.Plan("Fortnite", FnExe),
            CacheClear(Path.Combine(FnCache, "pipeline.bin")));
        var ownCs = Run("r2", RunStatus.Completed, LaunchEdit(cfg, "730"));

        foreach (var game in new[] { cs2, fn })
        {
            var lookup = BackupFinder.For(game, new[] { v30 });
            Assert.False(lookup.CanReset);
            Assert.Contains("another game", lookup.Problem);
        }
        // A newer run of this game alone is still refused while the shared one is in History: undoing only the newer one
        // would leave the game half put back, so the user is sent to History for the whole picture.
        Assert.False(BackupFinder.For(cs2, new[] { ownCs, v30 }).CanReset);
    }

    [Fact]
    public void A_scanned_game_knows_its_shader_cache_folders_even_when_they_are_empty()
    {
        using var s = new GamesSandbox("shader-folders");
        var g = GamesContext.From(s.Context());
        var folders = OptimizerScanner.ShaderCacheFolders(g, "Fortnite", GameRecipes.All.Single(r => r.Id == "fortnite"));
        Assert.Contains(Path.Combine(s.K.LocalAppData, @"FortniteGame\Saved\D3D12Cache"), folders, StringComparer.OrdinalIgnoreCase);
        Assert.Empty(OptimizerScanner.ShaderCacheFolders(g, "Some Game", null));
    }

    [Fact]
    public void Merged_launch_option_edits_remember_every_game_they_change()
    {
        using var s = new GamesSandbox("merge-ids");
        s.Put(s.LocalConfigPath(), LocalConfig(App("440", "-high") + App("730", null)));
        var cfg = SteamUserConfig.Load(s.SteamRoot)!;
        var a = ConfigEditPlanner.AddLaunchOption(cfg, s.SteamRoot, "440", "-novid", "TF2", Array.Empty<string>())!;
        var b = ConfigEditPlanner.AddLaunchOption(cfg, s.SteamRoot, "730", "+fps_max 0", "CS2", Array.Empty<string>())!;
        var merged = Assert.Single(EditMerger.Merge(new[] { a, b }));
        Assert.Equal(new[] { "440", "730" }, EditArgs.AppIdsOf(merged.Args!));
    }
}
