namespace PcTidy.Catalog.Games;

/// <summary>
/// Per-title knowledge (Community tweaks T9/T10). Every recommendation names its source; items whose appendix tag is
/// "(unverified)" are marked <see cref="Evidence.Unverified"/> and are never applied, only shown.
/// Games with a strict anti-cheat get information only (see <see cref="AntiCheatFacts.IsStrict"/>).
/// </summary>
public static class GameRecipes
{
    private static string Wiki(string page) => "https://www.pcgamingwiki.com/wiki/" + page;

    private static GameRecommendation Info(string id, string title, string reason, TweakVerdict verdict, Evidence evidence, string source,
        RecommendationKind kind = RecommendationKind.KnownFix, Measurement? measured = null)
        => new() { Id = id, Kind = kind, Title = title, Reason = reason, Verdict = verdict, Evidence = evidence, SourceUrl = source, Measured = measured };

    /// <summary>A PLACEBO row: shown greyed on the "Doesn't help (measured)" shelf, never applied.</summary>
    private static GameRecommendation Placebo(string id, string title, string reason, Evidence evidence, string source, Measurement? measured = null)
        => Info(id, title, reason, TweakVerdict.Placebo, evidence, source, RecommendationKind.KnownFix, measured);

    public static readonly IReadOnlyList<GameRecipe> All = new GameRecipe[]
    {
        new()
        {
            Id = "cs2", Name = "Counter-Strike 2", SteamAppIds = new[] { "730" }, Engine = GameEngine.Source2, AntiCheat = AntiCheat.Vac,
            WikiUrl = Wiki("Counter-Strike_2"),
            Recommendations = new[]
            {
                new GameRecommendation
                {
                    Id = "cs2.fps-max", Kind = RecommendationKind.LaunchOption, Title = "Lift the 400 FPS cap (+fps_max 0)",
                    Reason = "CS2 caps gameplay at 400 FPS. Uncapping raised the average but made the 1% lows much worse in the one test found, and it makes the GPU run hotter. A cap a little below your refresh rate is the usual low-lag choice with G-Sync/FreeSync.",
                    Verdict = TweakVerdict.Situational, Evidence = Evidence.Verified, SourceUrl = Wiki("Counter-Strike_2"),
                    AddLaunchOption = "+fps_max 0", Measured = FpsMeasurements.Cs2FpsMax,
                },
                Info("cs2.vulkan", "Vulkan (-vulkan) is a per-PC experiment",
                    "CS2 can run on Vulkan instead of DirectX 11. It's faster on some GPUs and slower on others, and can break overlays. Try it yourself; PC Tidy doesn't set it.",
                    TweakVerdict.Situational, Evidence.Verified, Wiki("Counter-Strike_2"), measured: FpsMeasurements.Cs2Vulkan),
                Info("cs2.setting-cost", "The settings that cost the most FPS",
                    "MSAA, shadows and particles cost the most; anisotropic filtering costs almost nothing. Lower the expensive ones in Settings → Video first.",
                    TweakVerdict.Proven, Evidence.Verified, FpsMeasurements.NineErza, RecommendationKind.ConfigSetting, FpsMeasurements.Cs2SettingCost),
                Info("cs2.resolution", "Lower resolution raises the average more than the lows",
                    "A lower or stretched resolution gives a big jump in average FPS, but the 1% lows (the stutters you feel) barely move, because CS2 is usually limited by the CPU.",
                    TweakVerdict.Situational, Evidence.Verified, FpsMeasurements.NineErza, RecommendationKind.ConfigSetting, FpsMeasurements.Cs2LowerResolution),
                Placebo("cs2.high", "-high launch option",
                    "Raises CS2's CPU priority. It made no measurable difference and can make voice chat and capture stutter.",
                    Evidence.Verified, FpsMeasurements.NineErza, FpsMeasurements.Cs2High),
                Placebo("cs2.threads", "-threads, -nojoy and thread or render cvars",
                    "The engine already picks its own thread count. None of these changed FPS beyond normal run-to-run noise.",
                    Evidence.Verified, FpsMeasurements.NineErza, FpsMeasurements.Cs2OtherOptions),
                Placebo("cs2.latency-cvars", "\"Low latency\" console commands",
                    "Latency cvars, the raw-input keyboard setting and removing Workshop maps made no measurable difference to input lag.",
                    Evidence.Verified, FpsMeasurements.Cs2Kitchen, FpsMeasurements.Cs2LatencyCvars),
            },
        },
        new()
        {
            Id = "tf2", Name = "Team Fortress 2", SteamAppIds = new[] { "440" }, Engine = GameEngine.Source, AntiCheat = AntiCheat.Vac,
            WikiUrl = Wiki("Team_Fortress_2"),
            Recommendations = new[]
            {
                Info("tf2.dxlevel", "Crash at the Valve logo: -dxlevel is a fix, not a speed-up",
                    "If TF2 crashes at start, -dxlevel 95 or -nod3d9ex can fix it. Remove -dxlevel after one launch, or it hides graphics options.",
                    TweakVerdict.Situational, Evidence.Verified, Wiki("Team_Fortress_2")),
                Info("tf2.mastercomfig", "mastercomfig (community config)",
                    "A popular, open-source TF2 config pack. It's a third-party mod, so PC Tidy only points you to it.",
                    TweakVerdict.Situational, Evidence.Verified, "https://github.com/mastercomfig/mastercomfig"),
            },
        },
        new()
        {
            Id = "dota2", Name = "Dota 2", SteamAppIds = new[] { "570" }, Engine = GameEngine.Source2, AntiCheat = AntiCheat.Vac,
            WikiUrl = Wiki("Dota_2"),
            Recommendations = new[]
            {
                Info("dota2.shadows", "Pixelated shadows: video.txt fix",
                    "If shadows look blocky, PCGamingWiki's fix is \"setting.cpu_level\" \"2\" in dota\\cfg\\video.txt. That file is inside the game folder, so PC Tidy leaves it to you.",
                    TweakVerdict.Situational, Evidence.Verified, Wiki("Dota_2")),
            },
        },
        new()
        {
            Id = "apex", Name = "Apex Legends", SteamAppIds = new[] { "1172470" }, Engine = GameEngine.Source, AntiCheat = AntiCheat.EasyAntiCheat,
            WikiUrl = Wiki("Apex_Legends"),
            Recommendations = new[]
            {
                Info("apex.mouse", "Mouse stutter: try 500 Hz polling",
                    "PCGamingWiki reports mouse stutter at very high polling rates; 500 Hz or lower fixes it. Change it in your mouse's own app.",
                    TweakVerdict.Situational, Evidence.Verified, Wiki("Apex_Legends")),
                Info("apex.fpsmax", "Microstutter: unlimited FPS plus an external cap",
                    "+fps_max unlimited with a cap from your GPU driver is PCGamingWiki's microstutter fix. Apex uses Easy Anti-Cheat, so PC Tidy only shows this.",
                    TweakVerdict.Situational, Evidence.Verified, Wiki("Apex_Legends")),
                Info("apex.dx12", "DirectX 12 runs better for most players",
                    "Respawn's own numbers say most PCs run Apex better on DirectX 12, but a few GPUs run slower. If yours stutters on DirectX 12, try DirectX 11 and compare.",
                    TweakVerdict.Situational, Evidence.Verified, FpsMeasurements.ApexDx12, RecommendationKind.FullscreenMode, FpsMeasurements.ApexDirectX12),
                Placebo("apex.high", "-high and other speed-up launch options",
                    "There is no Apex test of -high; in CS2 it measured no change. In Apex it can also trigger a false update prompt.",
                    Evidence.Verified, Wiki("Apex_Legends"), FpsMeasurements.Cs2High),
                // "-high" (false update prompt in Apex) is also flagged by the launch-option audit when it's actually set.
            },
        },
        new()
        {
            Id = "fortnite", Name = "Fortnite", EpicAppNames = new[] { "Fortnite" }, FolderNames = new[] { "Fortnite" },
            Engine = GameEngine.Unreal, AntiCheat = AntiCheat.EasyAntiCheat, WikiUrl = Wiki("Fortnite"),
            Recommendations = new[]
            {
                Info("fortnite.performance", "Weak GPU: try Performance rendering mode",
                    "Settings → Video → Rendering Mode → Performance gave a big average-FPS gain on a budget PC. On mid and high-end PCs DirectX 12 had the better 1% lows, so try both. Change it in the game.",
                    TweakVerdict.Situational, Evidence.Verified, Wiki("Fortnite"), RecommendationKind.FullscreenMode, FpsMeasurements.FortnitePerformanceMode),
                Info("fortnite.frame-limit", "Cap the frame rate in the game's own menu",
                    "Settings → Video → Frame Rate Limit. The in-game cap adds less lag than a cap from RTSS or the GPU driver (measured in CS2; Fortnite not tested).",
                    TweakVerdict.Proven, Evidence.Verified, Wiki("Fortnite"), RecommendationKind.ConfigSetting, FpsMeasurements.InGameVsExternalCap),
                Info("fortnite.reflex", "NVIDIA Reflex lowers input lag when the GPU is busy",
                    "On an NVIDIA card, Settings → Video → NVIDIA Reflex Low Latency → On cuts input lag when the GPU is the bottleneck. When it isn't, the gain is tiny, and On + Boost adds little (measured in CS2; Fortnite not tested).",
                    TweakVerdict.Situational, Evidence.Unverified, Wiki("Fortnite"), measured: FpsMeasurements.ReflexGpuBound),
                Placebo("fortnite.sg-ini", "\"Beyond low\" sg.* values in GameUserSettings.ini",
                    "The sg.* lines are the same quality groups the menu already writes. No test of setting them to 0 was found, and making the file read-only stops Fortnite saving settings.",
                    Evidence.Unverified, Wiki("Fortnite")),
            },
        },
        new()
        {
            Id = "valorant", Name = "VALORANT", FolderNames = new[] { "VALORANT" }, Engine = GameEngine.Unreal, AntiCheat = AntiCheat.Vanguard,
            WikiUrl = Wiki("Valorant"),
            Recommendations = new[]
            {
                Info("valorant.security", "Keep TPM 2.0, Secure Boot and Memory Integrity on",
                    "Riot Vanguard may refuse to start without TPM 2.0, Secure Boot, Memory Integrity or IOMMU. PC Tidy never turns these off.",
                    TweakVerdict.Proven, Evidence.Verified, "https://support-valorant.riotgames.com/"),
                Info("valorant.multithreaded", "Multithreaded Rendering: on for CPUs with 8+ threads",
                    "Riot recommends Settings → Video → Graphics Quality → Multithreaded Rendering on CPUs with 8 or more threads. No measured numbers were found.",
                    TweakVerdict.Situational, Evidence.Unverified, Wiki("Valorant"), RecommendationKind.ConfigSetting),
                Placebo("valorant.ini", "Hand-edited sg.* and Engine.ini \"extra low\" values",
                    "Riot publishes no rule on editing GameUserSettings.ini, and no source has numbers for the \"lower than low\" tricks. Use the in-game settings.",
                    Evidence.Verified, Wiki("Valorant")),
            },
        },
        new()
        {
            Id = "league", Name = "League of Legends", FolderNames = new[] { "League of Legends" }, AntiCheat = AntiCheat.Vanguard,
            WikiUrl = Wiki("League_of_Legends"),
            Recommendations = new[]
            {
                Info("league.reset", "Settings broken? Use Riot's reset steps",
                    "Riot's fix is: close the game and Riot Client, empty the Config folder, sign in and play a custom game. Editing game.cfg for speed does nothing useful.",
                    TweakVerdict.Situational, Evidence.Verified, Wiki("League_of_Legends")),
            },
        },
        new()
        {
            Id = "rust", Name = "Rust", SteamAppIds = new[] { "252490" }, Engine = GameEngine.Unity, AntiCheat = AntiCheat.EasyAntiCheat,
            WikiUrl = Wiki("Rust"),
            Recommendations = new[]
            {
                Info("rust.verify", "Problems? Use Steam's Verify integrity",
                    "Easy Anti-Cheat checks Rust's files, so never change anything in the game folder. Steam → Properties → Installed Files → Verify fixes damaged files.",
                    TweakVerdict.Proven, Evidence.Unverified, Wiki("Rust")),
            },
        },
        new()
        {
            Id = "eldenring", Name = "ELDEN RING", SteamAppIds = new[] { "1245620" }, AntiCheat = AntiCheat.EasyAntiCheat,
            WikiUrl = Wiki("Elden_Ring"),
            Recommendations = new[]
            {
                Info("eldenring.eac", "Never play online with mods or EAC turned off",
                    "Mods and FPS unlockers are meant for offline play only. PC Tidy never offers \"EAC off\" tricks.",
                    TweakVerdict.Harmful, Evidence.Unverified, Wiki("Elden_Ring")),
            },
        },
        new()
        {
            Id = "doometernal", Name = "DOOM Eternal", SteamAppIds = new[] { "782330" }, Engine = GameEngine.IdTech, AntiCheat = AntiCheat.OtherKernel,
            WikiUrl = Wiki("Doom_Eternal"),
            Recommendations = new[]
            {
                Info("doometernal.intro", "Skip the intro: +com_skipIntroVideo 1",
                    "This Steam launch option skips the intro videos. Cosmetic only. Battlemode once shipped an anti-cheat, so PC Tidy leaves it to you.",
                    TweakVerdict.Proven, Evidence.Verified, Wiki("Doom_Eternal"), RecommendationKind.LaunchOption),
            },
        },
        new()
        {
            Id = "cod", Name = "Call of Duty", SteamAppIds = new[] { "1938090" }, FolderNames = new[] { "Call of Duty" }, AntiCheat = AntiCheat.Ricochet,
            WikiUrl = Wiki("Call_of_Duty:_Modern_Warfare_II"),
            Recommendations = new[]
            {
                Info("cod.vram", "Video Memory Scale: 80, not 90",
                    "The in-game Video Memory Scale slider at about 80 avoids stutter when VRAM runs out. Change it in the game; hand-editing the players folder is unproven and Ricochet is strict.",
                    TweakVerdict.Situational, Evidence.Unverified, Wiki("Call_of_Duty:_Modern_Warfare_II")),
                Info("cod.mods", "No ReShade, unlockers or \"optimizer\" tools",
                    "Anything that injects into Call of Duty or changes its files risks a permanent ban, with no proven gain.",
                    TweakVerdict.Harmful, Evidence.Verified, Wiki("Call_of_Duty:_Modern_Warfare_II")),
            },
        },
        new()
        {
            Id = "palworld", Name = "Palworld", SteamAppIds = new[] { "1623730" }, Engine = GameEngine.Unreal, AntiCheat = AntiCheat.None,
            WikiUrl = Wiki("Palworld"),
            Recommendations = new[]
            {
                Info("palworld.server-flags", "Server flags don't speed up the game",
                    "-useperfthreads -NoAsyncLoadingThread -UseMultithreadForDS are dedicated-server options. On your own PC they do nothing.",
                    TweakVerdict.Placebo, Evidence.Unverified, Wiki("Palworld")),
            },
        },
        new()
        {
            Id = "cyberpunk", Name = "Cyberpunk 2077", SteamAppIds = new[] { "1091500" }, AntiCheat = AntiCheat.None, WikiUrl = Wiki("Cyberpunk_2077"),
            Recommendations = new[]
            {
                Info("cyberpunk.upscaling", "Upscaling and frame generation give the big gains",
                    "DLSS, FSR or XeSS (and frame generation where your GPU supports it) matter far more than any file tweak. Set them in the game.",
                    TweakVerdict.Situational, Evidence.Unverified, Wiki("Cyberpunk_2077")),
            },
        },
        new() { Id = "bg3", Name = "Baldur's Gate 3", SteamAppIds = new[] { "1086940" }, AntiCheat = AntiCheat.None, WikiUrl = Wiki("Baldur%27s_Gate_3") },
        new() { Id = "overwatch2", Name = "Overwatch 2", SteamAppIds = new[] { "2357570" }, FolderNames = new[] { "Overwatch" }, AntiCheat = AntiCheat.OtherKernel, WikiUrl = Wiki("Overwatch_2") },
        new() { Id = "rocketleague", Name = "Rocket League", SteamAppIds = new[] { "252950" }, EpicAppNames = new[] { "Sugar" }, AntiCheat = AntiCheat.EasyAntiCheat, WikiUrl = Wiki("Rocket_League") },
        new() { Id = "gtav", Name = "Grand Theft Auto V", SteamAppIds = new[] { "271590", "3240220" }, AntiCheat = AntiCheat.BattlEye, WikiUrl = Wiki("Grand_Theft_Auto_V") },
        new() { Id = "helldivers2", Name = "HELLDIVERS 2", SteamAppIds = new[] { "553850" }, AntiCheat = AntiCheat.GameGuard, WikiUrl = Wiki("Helldivers_2") },
        new() { Id = "marvelrivals", Name = "Marvel Rivals", SteamAppIds = new[] { "2767030" }, Engine = GameEngine.Unreal, AntiCheat = AntiCheat.OtherKernel, WikiUrl = Wiki("Marvel_Rivals") },
        new() { Id = "pubg", Name = "PUBG: BATTLEGROUNDS", SteamAppIds = new[] { "578080" }, Engine = GameEngine.Unreal, AntiCheat = AntiCheat.BattlEye, WikiUrl = Wiki("PUBG:_Battlegrounds") },
        new() { Id = "r6siege", Name = "Rainbow Six Siege", SteamAppIds = new[] { "359550" }, AntiCheat = AntiCheat.BattlEye, WikiUrl = Wiki("Tom_Clancy%27s_Rainbow_Six_Siege") },
        new() { Id = "destiny2", Name = "Destiny 2", SteamAppIds = new[] { "1085660" }, AntiCheat = AntiCheat.BattlEye, WikiUrl = Wiki("Destiny_2") },
        new() { Id = "dbd", Name = "Dead by Daylight", SteamAppIds = new[] { "381210" }, Engine = GameEngine.Unreal, AntiCheat = AntiCheat.EasyAntiCheat, WikiUrl = Wiki("Dead_by_Daylight") },
        new()
        {
            Id = "minecraft", Name = "Minecraft", FolderNames = new[] { "Minecraft", "Minecraft Launcher" }, AntiCheat = AntiCheat.None,
            WikiUrl = Wiki("Minecraft:_Java_Edition"),
            Recommendations = new[]
            {
                Info("minecraft.ram", "Give Java Edition 2–4 GB, not more",
                    "Launcher → Installations → Edit → More options → JVM arguments: -Xmx4G is plenty for vanilla; only big modpacks need 6–8 GB. More RAM than that makes pauses worse.",
                    TweakVerdict.Situational, Evidence.Unverified, Wiki("Minecraft:_Java_Edition")),
                Info("minecraft.sodium", "Sodium is the modern FPS mod",
                    "Sodium (with Iris for shaders) on Fabric replaces OptiFine and is a big FPS gain. Aikar's flags are for servers, not your game.",
                    TweakVerdict.Proven, Evidence.Verified, "https://github.com/CaffeineMC/sodium"),
            },
        },
        new()
        {
            Id = "roblox", Name = "Roblox", FolderNames = new[] { "Roblox" }, AntiCheat = AntiCheat.Hyperion, WikiUrl = Wiki("Roblox"),
            Recommendations = new[]
            {
                Info("roblox.fflags", "FPS-unlock flag files are ignored now",
                    "Since 29 Sep 2025 Roblox only reads about 18 allowed flags from ClientAppSettings.json. FPS-cap and thread flags do nothing; executors and injectors can get you banned.",
                    TweakVerdict.Placebo, Evidence.Verified, "https://devforum.roblox.com/"),
            },
        },
    };

    public static GameRecipe? ForSteam(string appId) => All.FirstOrDefault(r => r.SteamAppIds.Contains(appId));

    public static GameRecipe? ForEpic(string appName)
        => All.FirstOrDefault(r => r.EpicAppNames.Any(a => a.Equals(appName, StringComparison.OrdinalIgnoreCase)));

    public static GameRecipe? ForFolder(string? installDir)
    {
        var name = installDir is null ? null : Path.GetFileName(installDir.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(name) ? null
            : All.FirstOrDefault(r => r.FolderNames.Any(f => f.Equals(name, StringComparison.OrdinalIgnoreCase)));
    }
}
