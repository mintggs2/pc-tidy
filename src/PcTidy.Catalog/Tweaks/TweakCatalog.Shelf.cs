using PcTidy.Core.Model;
using static PcTidy.Catalog.Tweaks.TweakSources;

namespace PcTidy.Catalog.Tweaks;

/// <summary>
/// The "Doesn't help (measured)" shelf (tuning-tool comparison §8.2, build list C10) and the measured numbers behind it.
/// Source: "FPS boost tweaks - do they work.md" §4.1-4.7 and the appendix files F1-F7. Every number, hardware and source here
/// is copied from that table; "(snippet)" marks numbers the research only saw in a search snippet. Shelf rows are PLACEBO:
/// greyed, never ticked, never planned, and their registry values are never in the allow-list.
/// </summary>
public static partial class TweakCatalog
{
    private const string Doc = "Microsoft documentation (no FPS test)";
    private const string NoTest = "no test found";
    private const string Undated = "undated";
    private const string Cs2Rig = "9800X3D + RTX 5070 Ti, CS2";
    private const string Cs2Kitchen = "CS2 Kitchen (YouTube)";
    private const string Cs2KitchenDate = "2026-07-12";
    private const string NineErzaRig = "7800X3D + RX 9070 XT, Windows 11 Pro (build 26200), CS2";

    /// <summary>A measured note. <paramref name="strength"/> is the table's cell ("A (doc) / D"); the enum takes its weakest letter.</summary>
    internal static MeasuredNote M(string number, string hardware, string source, string date, string strength, bool snippet = false)
        => new(number, hardware, source, date, Weakest(strength)) { StrengthText = strength, Snippet = snippet };

    /// <summary>"A (doc) / D" → D, "A/B" → B: the evidence for a speed effect is only as good as its weakest part.</summary>
    internal static EvidenceStrength Weakest(string strength)
    {
        var letters = strength.Where(c => c is >= 'A' and <= 'D').Select(c => (EvidenceStrength)(c - 'A')).ToList();
        return letters.Count == 0 ? EvidenceStrength.D : letters.Max();
    }

    private static TweakDefinition WithMeasured(TweakDefinition t)
        => MeasuredById.TryGetValue(t.Id, out var notes) ? t with { Measured = notes } : t;

    /// <summary>Measured notes for rows that live in the other catalog files (offered rows and the older placebo rows).</summary>
    private static readonly Dictionary<string, MeasuredNote[]> MeasuredById = new(StringComparer.OrdinalIgnoreCase)
    {
        // ---------------- offered rows (§4: WORKS / SITUATIONAL) ----------------
        ["gaming.hags-on"] = new[]
        {
            M("+/-2%, RDR2 up to +4%; required for DLSS FG", "i3-10100 / i9-10900K, Windows 10 2004; RTX 2080 Ti", "Gamers Nexus; PC Games Hardware", "2020", "B"),
            M("most of 26 games no change (i9-12900K+RTX 3080); RDR2 +4% (2080 Ti); crashes in some", "i9-12900K + RTX 3080, Windows 11 21H2; RTX 2080 Ti, Windows 10 2004", "BabelTechReviews; PC Games Hardware", Undated, "A/B"),
        },
        ["gaming.background-recording-off"] = new[]
        {
            M("Metro LL 58.3 to 54.3 FPS (-6.9%), Win10 budget rig, PCWorld 2019", "Windows 10 budget PC (CPU and GPU not given)", "PCWorld", "2019-01", "B"),
        },
        ["gaming.capture-off"] = new[]
        {
            M("-6.9% only while recording (Metro, Win10)", "Windows 10 budget PC (CPU and GPU not given)", "PCWorld", "2019-01", "C", snippet: true),
        },
        ["gaming.game-mode"] = new[]
        {
            M("avg under 1% (13700H/4060 laptop, CapFrameX); FH5 lows 48.2 -> 71.5 with background apps", "i7-13700H + RTX 4060 laptop, Windows 11 Pro 25H2", "MakeUseOf", Undated, "C"),
        },
        ["network.delivery-optimization-no-upload"] = new[]
        {
            M("RAM growth to about 20 GB (one user)", "one user's PC (hardware not given)", "Tom's Guide / Neowin", Undated, "C", snippet: true),
            M("none", NoTest, "Microsoft Learn: Delivery Optimization reference", Undated, "D"),
        },

        // ---------------- older placebo rows (kept where they were; numbers added) ----------------
        ["gaming.priority-separation"] = new[] { M("MS blog: 0x2 + default bits 0x24 = 0x26 (XP Pro), same quantum", "Microsoft documentation, Windows XP Pro (no FPS test)", "Microsoft archived blog \"Master Your Quantum\"", "2006", "C / D") },
        ["gaming.system-responsiveness"] = new[] { M("MS doc: values below 10 clamp to 20 (default); 100 disables MMCSS", Doc, "Microsoft Learn: MMCSS", "2025-07", "A (doc) / D") },
        ["gaming.network-throttling"] = new[] { M("Cap ~10 pkt/ms (~120 Mbit/s, the research's own calculation); xperf DPC test favours default (Intel/Realtek, Win10)", "Intel and Realtek network adapters, Windows 10", "djdallmann GamingPCSetup; TIBCO support 90880", Undated, "C") },
        ["gaming.mmcss-games-task"] = new[] { M("MS: GPU Priority and SFIO \"not used\"", Doc, "Microsoft Learn: MMCSS", "2025-07", "A (doc) / D") },
        ["gaming.timer-resolution"] = new[] { M("Dawson: on 2004+ other process at 8 ms gives Sleep(1) about 14.5 ms; no FPS test", "Windows 10 2004 (CPU not stated)", "Bruce Dawson (randomascii)", "2020-10-04", "C / D") },
        // verify: PowerThrottlingOff's value name comes from guides, not Microsoft docs. Data only (never written).
        ["gaming.power-throttling-off"] = new[] { M("MS: in-focus app already High QoS", Doc, "Microsoft Learn: Quality of Service", Undated, "A (doc) / D") },
        ["gaming.standby-list-cleaners"] = new[] { M("No frame-time test found", NoTest, "Wagnardsoft ISLC page (no numbers)", Undated, "D") },
        ["gaming.sysmain-off"] = new[] { M("No FPS test found", NoTest, "Microsoft Learn: IoT Enterprise services (\"don't disable\")", Undated, "D") },
        ["gaming.fso-off-global"] = new[]
        {
            M("fullscreen optimizations 6.1 vs 6.0 ms", Cs2Rig, Cs2Kitchen, Cs2KitchenDate, "B"),
            M("FSO >= FSE, 1% low 52.8 vs 39.5 (V-Sync), Win10 DX11 2020", "Windows 10, DX11 (hardware not captured)", "RodroG, r/allbenchmarks", "2020-05", "C", snippet: true),
        },
        ["gaming.borderless-vs-fullscreen"] = new[]
        {
            M("borderless vs fullscreen 9 vs 9 ms", Cs2Rig, Cs2Kitchen, Cs2KitchenDate, "B"),
            M("about 1 frame extra if composed (Blur Busters staff, no LDAT)", "no test (reasoning only)", "Blur Busters forum", Undated, "C", snippet: true),
        },
        ["gaming.display-scaling"] = new[] { M("display vs GPU scaling within noise (5.9 vs 6.1 ms)", Cs2Rig, Cs2Kitchen, Cs2KitchenDate, "B") },
        ["gaming.raw-input-keyboard"] = new[] { M("no measurable difference (CS2 Kitchen)", Cs2Rig, Cs2Kitchen, Cs2KitchenDate, "B") },
        ["network.nagle"] = new[] { M("No Windows measurement; Cloudflare Linux server case 40-45 ms to <1 ms was an app fix", "Linux servers (not Windows)", "Cloudflare blog", "2025-08-05", "D") },
        ["network.qos-reserve"] = new[] { M("Chen 2006 / KB 316666: cap, not reservation", "Microsoft documentation", "Raymond Chen (The Old New Thing); Microsoft KB 316666", "2006-11-23", "C") },
        ["network.tcp-window-values"] = new[] { M("Documented ignored values", Doc, "Microsoft Learn: network adapter tuning", Undated, "D") },
        ["power.ultimate-performance"] = new[] { M("Fortnite +6.9% avg; 3 other games within noise (unnamed HW, Win 11)", "Windows 11, CPU and GPU not named", "XDA", Undated, "C") },
        ["power.core-parking-off"] = new[] { M("AMD +3% to +21% (Ryzen 1000, Win 10 2017); 7800X3D mis-park -12.1% Cyberpunk 720p", "Ryzen 1000, Windows 10; Ryzen 7 7800X3D", "AMD data via Legit Reviews; PC Games Hardware reporting TechPowerUp", "2017-04", "C / D") },
        ["power.pcie-aspm-off"] = new[] { M("no test", NoTest, "Intel Ethernet tuning documentation (network adapters, not GPUs)", Undated, "D/C") },
        // Not FPS rows: the number is what Microsoft documents (Tweaks.md, not the FPS research).
        ["ui.chat-button"] = new[] { M("Chat was removed from the taskbar in Windows 11 23H2", "Microsoft documentation, Windows 11 23H2 and later", "Microsoft Support: customize the taskbar", Undated, "A") },
        ["privacy.allow-telemetry-0"] = new[] { M("On Home and Pro, 0 is treated as 1 (Required)", "Microsoft documentation, Windows Home and Pro", "Microsoft Learn: configure diagnostic data", Undated, "A") },
        ["privacy.recall"] = new[] { M("Recall only exists on Copilot+ PCs", "Microsoft documentation, Copilot+ PCs only", "Microsoft Learn: WindowsAI policy CSP", Undated, "A") },
        ["privacy.turn-off-copilot-policy"] = new[] { M("Deprecated; it doesn't affect the current Copilot app", "Microsoft documentation", "Microsoft Learn: WindowsAI policy CSP", Undated, "A") },
    };

    /// <summary>New shelf rows from §4 (PLACEBO), plus two display-only SITUATIONAL rows with their numbers.</summary>
    private static IEnumerable<TweakDefinition> Shelf()
    {
        static TweakDefinition P(string id, string title, string why, IReadOnlyList<string> sources, MeasuredNote[] notes, params TweakChange[] changes)
            => Placebo(id, GroupFromId(id), title, why, sources, changes) with { Measured = notes };
        var nvidia = new TweakApplicability { GpuVendors = new[] { GpuVendor.Nvidia } };

        // ---------------- 4.1 scheduler, timers, power plans ----------------
        yield return P("gaming.hpet", "HPET on or off (BIOS or Device Manager)",
            "Claims of 10-20% come from one user, and a forum test in Cyberpunk on Windows 10 found no change. Forcing HPET as the Windows timer (useplatformclock) is harmful; Find bad tweaks puts that back.",
            Src(HpetBlurBusters, HpetTechPowerUp, Bcdedit),
            new[]
            {
                M("claims 10-20% from one user; modern under 2% (secondary)", "one user's PC (hardware not found)", "Blur Busters forum; bottleneckcalculator (secondary)", Undated, "C / D", snippet: true),
                M("TPU forum Win10 Cyberpunk: no change", "Windows 10 (hardware not given)", "TechPowerUp forum", Undated, "C", snippet: true),
            },
            new CommandChange("Device Manager → System devices → High precision event timer → Disable (or the HPET switch in the BIOS)"));
        // verify: the value name and its data come from guides, not Microsoft docs. Data only (never written or read).
        yield return P("gaming.svchost-split", "SvcHostSplitThresholdInKB (fewer service hosts)",
            "It only changes how many svchost.exe processes Windows runs, to save a little memory. It does nothing for games.",
            Src(SvcHostGrouping),
            new[] { M("MS: 17-21 vs 67-74 hosts; memory only", "Microsoft documentation, Windows 10 1703+ with more than 3.5 GB RAM", "Microsoft Learn: Service host grouping", Undated, "A (doc) / D") },
            new CommandChange(@"HKLM\SYSTEM\CurrentControlSet\Control → SvcHostSplitThresholdInKB = your RAM size in KB"));
        yield return P("power.min-max-processor-state", "Minimum and maximum processor state 100%",
            "The High performance and Ultimate plans already set 100%, and no FPS test was found. On Balanced it only raises idle power and heat.",
            Src(ProcessorStateTest, PowerSchemes),
            new[] { M("already 100% in High/Ultimate", "settings comparison on a 7800X3D (no FPS test)", "TechFuelHQ", Undated, "D", snippet: true) },
            new CommandChange("Power Options → Processor power management → Minimum processor state: 100%"));

        // ---------------- 4.2 Windows graphics and OS ----------------
        yield return P("gaming.game-mode-toggle", "Turning Game Mode off (or on) for more FPS",
            "Measured at 82.4 vs 82.9 FPS in Cyberpunk on a laptop: no real difference. Keep Game Mode on: it holds back Windows Update while you play.",
            Src(GameModeOffTest, GameMode),
            new[] { M("82.4 vs 82.9 fps (Cyberpunk, 13700H+4060 laptop)", "i7-13700H + RTX 4060 laptop, Windows 11 25H2", "iGeeksBlog", Undated, "C", snippet: true) },
            new CommandChange("Settings → Gaming → Game Mode: Off"));
        // verify: VisualFXSetting's key comes from guides, not Microsoft docs, so this row only names the switches.
        yield return P("ui.visual-effects-off", "Visual effects and transparency off (for FPS)",
            "No test was found that shows a game running faster. Turn them off only if you prefer the look.",
            Src(PcTuning),
            new[] { M("no test found", NoTest, "none found", Undated, "D") },
            new CommandChange("System Properties → Advanced → Performance → Adjust for best performance, and Settings → Personalization → Colors → Transparency effects: Off"));
        yield return P("gaming.search-indexing-off", "Search indexing off (for FPS)",
            "No FPS test was found, and the indexer already slows itself down while you use the PC. Turning it off breaks Start, Explorer and Outlook search.",
            Src(IndexingAnecdote, SearchCsp),
            new[]
            {
                M("one clock-stability anecdote", "3DMark clock readings (hardware not given)", "GamePP FAQ", Undated, "D", snippet: true),
                M("No FPS test; indexer self-throttles", NoTest, "Microsoft Learn: Windows Search policies", Undated, "D"),
            },
            new CommandChange("Turn off the Windows Search (WSearch) service or untick indexing on a drive"));

        // ---------------- 4.3 GPU driver and control panel ----------------
        yield return P("gaming.reflex-boost", "NVIDIA Reflex \"On + Boost\" instead of \"On\"",
            "Boost added only 0.2 to 1.1 ms over On in an input-lag test, and keeps the card clocked up. Reflex On is the part that helps.",
            Src(Cs2LatencyTests, ReflexLdat),
            new[] { M("0.2 ms (light), 1.1 ms (heavy); same rig; TPU: no noteworthy difference", Cs2Rig, "CS2 Kitchen (YouTube); TechPowerUp LDAT v2", Cs2KitchenDate, "B") },
            new CommandChange("In-game: NVIDIA Reflex Low Latency → On + Boost")) with { Applies = nvidia };
        yield return P("gaming.nvidia-prefer-max-performance", "NVIDIA \"Prefer maximum performance\" power mode",
            "Measured within error: the card ran 1905 vs 1920 MHz and gave no FPS gain, and it keeps the GPU clocked up at idle.",
            Src(NvidiaPowerModes),
            new[] { M("within error; 1905 vs 1920 MHz; RTX 2080 SUPER", "RTX 2080 SUPER Founders Edition (driver and OS not stated)", "TheFPSReview", "2019-12-04", "B") },
            new CommandChange("NVIDIA Control Panel → Manage 3D settings → Power management mode: Prefer maximum performance")) with { Applies = nvidia };
        yield return P("gaming.texture-filtering", "Texture filtering quality: High performance",
            "One forum run measured 54.2 vs 54.4 FPS: no difference, and textures can look worse.",
            Src(TomsHardwareForum),
            new[] { M("54.2 vs 54.4 FPS (forum, 1 run)", "flight simulator (hardware not retrieved)", "Tom's Hardware forum", Undated, "C", snippet: true) },
            new CommandChange("NVIDIA Control Panel → Manage 3D settings → Texture filtering - Quality: High performance")) with { Applies = nvidia };
        yield return P("gaming.nvcp-image-slider", "NVIDIA image settings slider set to \"Performance\"",
            "No test of this slider was found. Lower the graphics settings in the game itself instead.",
            Src("https://www.nvidia.com/en-us/geforce/forums/"),
            new[] { M("no evidence found", NoTest, "none found", Undated, "D") },
            new CommandChange("NVIDIA Control Panel → Adjust image settings with preview → Use my preference emphasizing: Performance")) with { Applies = nvidia };
        yield return P("gaming.ddu-speed-fix", "DDU or a clean driver install as a speed fix",
            "A forum test measured 5447 vs 5438 in 3DMark Speed Way (-0.2%): noise. A clean install fixes a broken driver, not speed, and shaders rebuild afterwards. PC Tidy never automates DDU.",
            Src(DduForum),
            new[] { M("5447 vs 5438 Speed Way (-0.2%); RTX 4070 Ti, forum", "RTX 4070 Ti", "Tom's Hardware forum", Undated, "C", snippet: true) },
            new CommandChange("Display Driver Uninstaller in Safe Mode, then a fresh driver install"));
        yield return P("gaming.debloated-driver", "Debloated GPU drivers (NVCleanstall, NVSlimmer and similar)",
            "A 3-run test found no difference between the driver alone and the driver with the NVIDIA App.",
            Src(NvidiaAppPerformance),
            new[] { M("no difference driver-only vs +App, Forza H6, 3 runs", "hardware not given", "TechSpot: NVIDIA App performance", Undated, "C", snippet: true) },
            new CommandChange("Install a stripped GPU driver package without the vendor app"));

        // ---------------- 4.4 network ----------------
        yield return P("network.rss", "Receive Side Scaling (RSS) changes",
            "RSS spreads network work across CPU cores and is on by default. No test was found that changing it helps games.",
            Src(TcpIpPerformance),
            new[] { M("none", NoTest, "Microsoft Learn: TCP/IP performance guidance", Undated, "D", snippet: true) },
            new CommandChange("netsh int tcp set global rss=disabled"));
        yield return P("network.rsc", "Receive Segment Coalescing (RSC) off",
            "No test was found that turning RSC off helps games.",
            Src(Rsc),
            new[] { M("none", NoTest, "Microsoft Learn: Receive Segment Coalescing overview", Undated, "D") },
            new CommandChange("netsh int tcp set global rsc=disabled"));
        yield return P("network.ecn", "ECN on",
            "No test was found that Explicit Congestion Notification changes ping in games.",
            Src(NextGenTcpIp),
            new[] { M("none", NoTest, "Microsoft Learn: Next Generation TCP/IP (archive)", Undated, "D") },
            new CommandChange("netsh int tcp set global ecncapability=enabled"));
        yield return P("network.timestamps", "TCP timestamps off",
            "Windows already has them off by default, so there's nothing to gain.",
            Src(NetTcpSetting),
            new[] { M("default already off", "Microsoft documentation", "Microsoft docs (Set-NetTCPSetting)", Undated, "D") },
            new CommandChange("netsh int tcp set global timestamps=disabled"));
        yield return P("network.optimize-buttons", "\"Optimize network\" buttons in optimizer apps",
            "These apps don't say what they change, and no test was found.",
            Src(NetworkTuning),
            new[] { M("contents undisclosed", NoTest, "none found", Undated, "D") },
            new CommandChange("A one-click \"optimize network\" button in a third-party optimizer"));
        yield return P("network.mtu", "MTU tuning",
            "No test was found that a hand-set MTU lowers ping, and a wrong value makes some sites load slowly or not at all.",
            Src(NetworkTuning),
            new[] { M("none", NoTest, "none found", Undated, "D") },
            new CommandChange("netsh interface ipv4 set subinterface \"<adapter>\" mtu=<value>"));

        // ---------------- 4.5 hardware, BIOS, peripherals ----------------
        yield return P("power.asus-xmp-tweaked", "ASUS \"XMP Tweaked\" or \"DOCP Tweaked\" memory mode",
            "No test was found, and ASUS itself warns it can make the PC unstable. Plain XMP or EXPO is the tested setting.",
            Src(AsusXmpTweaked),
            new[] { M("none; ASUS stability warning", NoTest, "ASUS FAQ 1042256", Undated, "D", snippet: true) },
            new CommandChange("BIOS → memory profile: XMP Tweaked (PC Tidy never changes BIOS settings)"));
        yield return P("power.c-states-off", "CPU C-states off",
            "No FPS test was found. Intel says it hurts Thread Director on hybrid CPUs, and it costs heat and boost clocks.",
            Src(CStatesIntel, PcTuning),
            new[]
            {
                M("no test; Intel says hurts Thread Director", NoTest, "PC Games Hardware (relaying Intel)", "2026-10-28", "D"),
                M("author warns heat, lost boost; no FPS data", NoTest, "valleyofdoom PC-Tuning", Undated, "C"),
            },
            new CommandChange("BIOS → Global C-state control: Disabled (PC Tidy never changes BIOS settings)"));
        // verify: the queue-size value names come from guides, not Microsoft docs. Data only (never written or read).
        yield return P("gaming.data-queue-size", "Mouse and keyboard data queue size",
            "No test was found that a smaller input queue changes anything you can feel or measure.",
            Src(DjdallmannGamingPcSetup),
            new[] { M("none", NoTest, "none found", Undated, "D") },
            new CommandChange(@"HKLM\SYSTEM\CurrentControlSet\Services\mouclass\Parameters → MouseDataQueueSize (and kbdclass → KeyboardDataQueueSize)"));
        yield return P("gaming.ram-cleaners", "RAM cleaner apps (Mem Reduct and similar)",
            "They only move counters: Windows already uses free memory as a cache, and emptying it makes apps load from disk again. PC Tidy never builds a RAM booster.",
            Src(MemReduct),
            new[]
            {
                M("counters only", "RAMMap counters (hardware not given)", "BetaNews (Mem Reduct)", "2015-04-22", "C", snippet: true),
                M("Russinovich: no effect at best", "no test (expert explanation)", "Mark Russinovich, ITPro Today", Undated, "C"),
            },
            new CommandChange("A third-party app that empties memory on a timer or a button"));
        yield return P("power.readyboost", "ReadyBoost",
            "Windows skips ReadyBoost when the system drive is an SSD, so a USB stick adds nothing.",
            Src(ReadyBoost),
            new[] { M("skipped on SSD", "Windows 10 guide (no FPS test)", "TechRepublic", Undated, "C", snippet: true) },
            new CommandChange("USB stick → Properties → ReadyBoost → Use this device"));

        // ---------------- 4.6 services, debloat, cleanup ----------------
        // verify: EnablePrefetcher's path comes from guides, not Microsoft docs. Data only (never written or read).
        yield return P("gaming.prefetch-delete", "Deleting Prefetch (or EnablePrefetcher = 0)",
            "Microsoft says it isn't needed and makes the next app launches slower. No speed gain was measured.",
            Src(PrefetchMicrosoft),
            new[] { M("Microsoft: not needed, slower next launches; no numbers (Win11 VM 4 GB, daily PC, Windows Latest 2026)", "Windows 11 VM (4 GB, 2 cores) and a daily PC", "Windows Latest (citing Microsoft)", "2026-10-26", "C") },
            new CommandChange(@"Delete C:\Windows\Prefetch, or set …\Memory Management\PrefetchParameters → EnablePrefetcher = 0"));
        yield return P("gaming.xbox-services-off", "Xbox services off",
            "Xbox services set to Manual already sit idle until a game needs them. Turning them off can break Xbox sign-in, Game Pass and Store games, and on dual-CCD Ryzen X3D CPUs Game Bar steers games to the right cores.",
            Src(IotServices),
            new[] { M("Manual services idle; breaks sign-in", Doc, "Microsoft Learn: IoT Enterprise services", Undated, "D") },
            new CommandChange("Set the Xbox services (XblAuthManager, XblGameSave, XboxNetApiSvc, XboxGipSvc) to Disabled"));
        yield return P("gaming.services-presets", "Service presets (Black Viper, \"Manual\" packs)",
            "No boot, memory or FPS numbers were found for any service preset, and turning the wrong service off breaks updates, printing or sign-in.",
            Src(IotServices),
            new[] { M("No boot/RAM/FPS numbers found", NoTest, "none found", Undated, "D", snippet: true) },
            new CommandChange("Apply a list that sets dozens of services to Manual or Disabled"));
        yield return P("gaming.game-boosters", "Game booster apps",
            "A test measured 3072 vs 3074 in 3DMark Fire Strike: no difference.",
            Src(GameBoosters),
            new[] { M("Fire Strike 3072 vs 3074 (i5-2500K, 2015)", "i5-2500K", "Ghacks", "2015-05-23", "C", snippet: true) },
            new CommandChange("A third-party \"game booster\" that closes apps and services before a game"));

        // ---------------- 4.7 per-game launch options ----------------
        yield return P("gaming.launch-high", "-high launch option or High process priority",
            "Measured at 902.6 vs 900.7 FPS in CS2: noise. Windows already favours the game in front.",
            Src(Cs2LaunchOptions),
            new[] { M("902.6 vs 900.7 avg (7800X3D + RX 9070 XT)", NineErzaRig, "9Erza CS2 Optimization Guide (GitHub)", "2026-04", "B") },
            new CommandChange("Steam → game Properties → Launch options: -high"));
        yield return P("gaming.launch-threads", "-threads and other CS2 launch options",
            "-nojoy, thread-pool and similar options all measured within 1% in CS2. -threads itself was never tested.",
            Src(Cs2LaunchOptions),
            new[] { M("all within 1% (9Erza); `-threads` untested", NineErzaRig, "9Erza CS2 Optimization Guide (GitHub)", "2026-04", "B/D") },
            new CommandChange("Steam → game Properties → Launch options: -threads <n> -nojoy"));
        yield return P("gaming.launch-cpucount", "-cpuCount, -maxMem and -USEALLAVAILABLECORES",
            "No evidence was found that these change anything in current games.",
            Src(UnrealCommandLine),
            new[] { M("no evidence found", NoTest, "Unreal Engine command-line reference (research note)", Undated, "D") },
            new CommandChange("Launch options: -USEALLAVAILABLECORES -cpuCount=<n> -maxMem=<n>"));
        yield return P("gaming.roblox-fflags", "Roblox FFlag tweaks",
            "Since 29 September 2025 Roblox only reads 18 allowed flags; every other flag is ignored.",
            Src(RobloxFastFlags),
            new[] { M("18 allowlisted flags, rest ignored (2025-09-29)", "Roblox documentation", "Roblox DevForum allowlist announcement", "2025-09-29", "A") },
            new CommandChange(@"ClientAppSettings.json with performance FFlags in the Roblox folder"));

        // ---------------- display-only SITUATIONAL rows with their numbers ----------------
        // §4.1 "Balanced -> High plan": SITUATIONAL, info card. Not one-click here (Speed owns plan switching); hidden on
        // dual-CCD Ryzen X3D, where High performance stops core parking and games can land on the wrong cores.
        yield return new TweakDefinition
        {
            Id = "power.high-performance-plan", Group = TweakGroup.Power, Title = "High performance power plan (not for dual-CCD Ryzen X3D)",
            Effect = "Switching from Balanced to High performance only helped first-generation Ryzen on Windows 10 in AMD's own numbers. "
                     + "Windows 11 25H2 tests found no significant gain. It uses more power.",
            Verdict = TweakVerdict.Situational, Sources = Src(RyzenBalancedPlan, BeyondBalanced, PowerSchemes),
            Caveat = "Not on dual-CCD Ryzen X3D: it stops core parking, which puts games on the cache cores.",
            Applies = new TweakApplicability { HarmfulOn = CpuKind.DualCcdX3D },
            Changes = Do(new GuidanceChange("Control Panel → Power Options → High performance. PC Tidy's Speed section can switch plans with undo.")),
            Undo = "Nothing to undo: PC Tidy changes nothing here.",
            Measured = new[] { M("same Zen 1 data; Win 11 25H2 VMs: no significant gain", "Ryzen 1000 on Windows 10; 10 Azure VMs on Windows 11 25H2 (office load)", "Legit Reviews (AMD data); GO-EUC", "2017-04", "C") },
        };
        // §4.2 "MPO disable": SITUATIONAL, anecdotes only. verify: OverlayTestMode isn't documented by Microsoft, so it is
        // only described (Expert, display-only), never written.
        yield return new TweakDefinition
        {
            Id = "gaming.mpo-off", Group = TweakGroup.Gaming, Title = "Multiplane overlay (MPO) off for multi-monitor flicker",
            Effect = "Reported to stop flicker and stutter on some multi-monitor setups. Only anecdotes back it, and the switch isn't documented by Microsoft.",
            Verdict = TweakVerdict.Situational, Risk = TweakRisk.Medium, Flags = TweakFlags.ExpertOnly | TweakFlags.Admin | TweakFlags.Restart,
            Sources = Src(MpoReport),
            Caveat = "Only for multi-monitor flicker or stutter. Not checked against Microsoft's documentation, so PC Tidy only describes it. "
                     + "Find bad tweaks lists it once it's set, so you can remove it when you no longer need it.",
            Changes = Do(new GuidanceChange(@"Community guides set HKLM\SOFTWARE\Microsoft\Windows\Dwm → OverlayTestMode = 5 (DWORD). Deleting the value undoes it.")),
            Undo = "Nothing to undo: PC Tidy changes nothing here.",
            Measured = new[] { M("multi-monitor stutter fix, anecdotes only", "R7 5800X + RTX 4090 (one user)", "r/Amd thread", "2022-11", "C", snippet: true) },
        };
    }
}
