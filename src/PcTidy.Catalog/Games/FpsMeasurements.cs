namespace PcTidy.Catalog.Games;

/// <summary>
/// The measured numbers PC Tidy quotes on game rows and guidance cards, copied from the FPS research verdict tables
/// ("FPS boost tweaks - do they work" §4.3 and §4.7, appendix F4/F6/F7) with each test's hardware and source.
/// Facts and numbers only, in PC Tidy's own words. A number is never generalised beyond its test.
/// </summary>
public static class FpsMeasurements
{
    public const string NineErza = "https://github.com/9Erza/CS2-Optimization-Guide";
    private const string NineErzaSource = "9Erza CS2 guide (CapFrameX, 3 runs), April 2026";
    private const string NineErzaRig = "Ryzen 7 7800X3D + RX 9070 XT, Windows 11";
    public const string SteamForumCs2 = "https://steamcommunity.com/discussions/forum/11/565912032657889064/?l=english";
    public const string NexproFortnite = "https://www.nexprotools.com/blog/fortnite-dx12-vs-performance-mode-fps-input-delay";
    public const string ApexDx12 = "https://forums.ea.com/discussions/apex-legends-general-discussion-de/dev-team-update-directx-12/5632290";
    public const string Cs2Kitchen = "https://www.youtube.com/watch?v=zNHFAAJkIvo";
    private const string Cs2KitchenSource = "CS2 Kitchen latency video (1000 samples per setting), 2026";
    private const string Cs2KitchenRig = "Ryzen 7 9800X3D + RTX 5070 Ti";
    public const string BattleNonSense = "https://www.youtube.com/watch?v=7CKnJ5ujL_Q";
    public const string BlurBustersCap = "https://www.blurbusters.com/gsync/gsync101-input-lag-tests-and-settings/5/";
    public const string AntiLagPlusBans = "https://www.tomshardware.com/news/amd-pulls-drivers-that-cause-counter-strike-2-bans-after-valve-roasted-them";
    public const string LttBloat = "https://rosetta.to/u/linustechtips/bloat-is-killing-your-fps";
    public const string PcWorldReinstall = "https://www.pcworld.com/article/2830514/tested-is-reinstalling-windows-to-boost-performance-a-modern-pc-myth.html";
    /// <summary>Digital Foundry's SCSKiller shader pre-compile video (video id T2AMv9_S_s0).</summary>
    public const string DigitalFoundryShaders = "https://www.youtube.com/watch?v=T2AMv9_S_s0";

    // ---------------- Fortnite ----------------

    public static readonly Measurement FortnitePerformanceMode = new(
        "Performance mode 185 vs DirectX 12 135 FPS average, 1% lows 68 vs 41 (budget PC). On mid and high-end PCs DirectX 12 had the better 1% lows",
        "i3-12100F + GTX 1660 Super", "nexprotools blog, undated", NexproFortnite, 'C', "unverified blog, method not stated");

    // ---------------- CS2 ----------------

    public static readonly Measurement Cs2High = new(
        "-high 902.6 vs 900.7 FPS average (no change)", NineErzaRig, NineErzaSource, NineErza, 'B');

    public static readonly Measurement Cs2OtherOptions = new(
        "-nojoy 897.0, thread_pool_option 899.3, r_dynamic 0 899.5 vs 900.7 FPS without them: all within 1%; -threads was never tested",
        NineErzaRig, NineErzaSource, NineErza, 'B');

    public static readonly Measurement Cs2FpsMax = new(
        "+fps_max 0: 520 average / 180 1% low vs the default 400 cap: 398 / 340. Higher average, much worse lows",
        "Ryzen 7 7800X3D + RTX 4070 Ti Super", "Steam forum post, undated", SteamForumCs2, 'C', "one PC, author-reported");

    public static readonly Measurement Cs2Vulkan = new(
        "about +8% average FPS but 15% worse 1% lows", "Ryzen 7 7800X3D + RTX 4070 Ti Super", "Steam forum post, undated", SteamForumCs2, 'C',
        "one PC, author-reported");

    public static readonly Measurement Cs2SettingCost = new(
        "MSAA 8x −11.6%, shadows Very High −10.2%, particles Very High −9.6%, model/texture High −5.5%, anisotropic 16x −0.9% (vs all-lowest 979.5 FPS)",
        NineErzaRig, NineErzaSource, NineErza, 'B');

    public static readonly Measurement Cs2LatencyCvars = new(
        "engine_low_latency_sleep_after_client_tick, keyboard raw input and Workshop map count: no measurable difference",
        Cs2KitchenRig, Cs2KitchenSource, Cs2Kitchen, 'B');

    public static readonly Measurement Cs2LowerResolution = new(
        "1280x960 900.1 vs 1920x1080 740.5 FPS average (+21.6%), but 1% lows only +4.6% (287.8 vs 275.1)",
        NineErzaRig, NineErzaSource, NineErza, 'B');

    // ---------------- Apex ----------------

    public static readonly Measurement ApexDirectX12 = new(
        "80% of players ran better on DirectX 12 and another 10% within 5%; some GPUs over 30% faster, others up to 15% slower",
        "10,000+ players' PCs (telemetry)", "Respawn dev update, 19 Nov 2024", ApexDx12, 'B', "first-party telemetry, method not fully published");

    // ---------------- latency: caps, Reflex, Anti-Lag (C1, C7) ----------------

    public static readonly Measurement CapBelowGpuLimit = new(
        "a cap just under the GPU limit cut input lag by 11 to 22 ms (42% in Overwatch); driver low-latency modes only about 9 to 10 ms",
        "RX 5700 and GTX 1080", "Battle(non)sense video, Sept 2019", BattleNonSense, 'B', "single runs, older drivers");

    public static readonly Measurement InGameVsExternalCap = new(
        "at a 100 FPS cap: about 12 ms with the in-game limiter vs about 20 ms with RTSS or the NVIDIA driver limiter (400 cap: 6.9 vs 8.3 to 8.6 ms)",
        Cs2KitchenRig, Cs2KitchenSource, Cs2Kitchen, 'B');

    public static readonly Measurement VrrCap = new(
        "2 FPS under the refresh rate was enough to stay out of V-Sync lag; 3 under is the safe rule", "hardware not retrieved",
        "Blur Busters G-SYNC 101 retest, undated", BlurBustersCap, 'B', "ms table not retrieved");

    public static readonly Measurement ReflexGpuBound = new(
        "Reflex On: about 20 → 16.7 ms under heavy GPU load (CS2 at 4K max), but only 6.2 → 6.0 ms (0.2 ms) when the GPU had headroom",
        Cs2KitchenRig, Cs2KitchenSource, Cs2Kitchen, 'B');

    public static readonly Measurement ReflexBoost = new(
        "On + Boost vs On: 0.2 ms (light load) to 1.1 ms (heavy load)", Cs2KitchenRig, Cs2KitchenSource, Cs2Kitchen, 'B');

    public static readonly Measurement AntiLagPlus = new(
        "VAC banned CS2 players for Anti-Lag+ (driver 23.10.1). AMD pulled the driver and Valve reversed the bans", "AMD RDNA 3 cards",
        "Tom's Hardware report, Oct 2023", AntiLagPlusBans, 'A', "a ban incident, not a speed test");

    // ---------------- background apps (C12) ----------------

    public static readonly Measurement BackgroundAppsLtt = new(
        "CS:GO at 1440p 423 FPS on a clean PC vs 403 with many background apps (about 5%)", "two identical PCs, Windows 10",
        "Linus Tech Tips video, 2020", LttBloat, 'B', "run count not stated");

    public static readonly Measurement BackgroundAppsPcWorld = new(
        "clean Windows about +2.5% average FPS in Cyberpunk 2077 vs a year-old install full of apps", "Ryzen 7 9800X3D, Windows 11",
        "PCWorld test, 2025", PcWorldReinstall, 'B', "run count not stated");

    // ---------------- shader caches (C9) ----------------

    public static readonly Measurement ShaderRebuild = new(
        "pre-building all shaders took about 1 hour and about 16 GB of cache in the worst game tested",
        "Ryzen 5 5600 + RTX 4070 Ti Super (as named in the video)", "Digital Foundry SCSKiller video, 3 Nov 2026", DigitalFoundryShaders, 'B',
        "one PC");
}
