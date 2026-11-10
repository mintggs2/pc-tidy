using PcTidy.Core.Model;
using static PcTidy.Catalog.Tweaks.TweakSources;

namespace PcTidy.Catalog.Tweaks;

public static partial class TweakCatalog
{
    private static IEnumerable<TweakDefinition> Gaming()
    {
        const string gameDvr = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
        const string dxPrefs = @"Software\Microsoft\DirectX\UserGpuPreferences";
        const string mouse = @"Control Panel\Mouse";
        const string graphics = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";

        // ---------------- offered ----------------
        yield return new TweakDefinition
        {
            Id = "gaming.game-mode", Group = TweakGroup.Gaming, Title = "Keep Game Mode on",
            Effect = "Game Mode lowers background work and holds Windows Update installs and restart prompts while you play. It's on by default; this puts it back if something turned it off.",
            Verdict = TweakVerdict.Proven, Sources = Src(GameMode),
            Changes = Do(Cu(@"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, 1)),
            Preset = TweakPreset.SafeGaming, PresetDefault = true,
        };
        yield return new TweakDefinition
        {
            Id = "gaming.background-recording-off", Group = TweakGroup.Gaming, Title = "Turn off background recording",
            Effect = "Stops Game Bar from constantly recording the last few minutes of play, which costs a few percent of FPS and video memory while it's on. Manual recording still works.",
            Verdict = TweakVerdict.Proven, Flags = TweakFlags.RestartGame, Sources = Src(GameMode, WinUtilTweaks),
            Changes = Do(Cu(gameDvr, "HistoricalCaptureEnabled", 0, 0)),
            Preset = TweakPreset.SafeGaming, PresetDefault = true,
        };
        yield return new TweakDefinition
        {
            Id = "gaming.windowed-optimizations", Group = TweakGroup.Gaming, Title = "Optimizations for windowed games",
            Effect = "Lets older DirectX 10/11 games running windowed or borderless use the modern flip model: lower latency, variable refresh rate and Auto HDR. Your other graphics settings are kept.",
            Verdict = TweakVerdict.Proven, Flags = TweakFlags.RestartGame, Sources = Src(GraphicsSettings, DirectXBlog),
            Applies = TweakApplicability.Windows11,
            Changes = Do(new RegistryTokenChange(RegistryHive.CurrentUser, dxPrefs, "DirectXUserGlobalSettings", "SwapEffectUpgradeEnable", "1")),
            Undo = "One-click undo puts back the exact settings text PC Tidy found.",
            Preset = TweakPreset.SafeGaming, PresetDefault = true,
        };
        yield return new TweakDefinition
        {
            Id = "gaming.pointer-precision-off", Group = TweakGroup.Gaming, Title = "Turn off Enhance pointer precision",
            Effect = "Removes mouse acceleration on the desktop, so the same hand movement always moves the pointer the same distance. Games that read the mouse directly aren't affected.",
            Verdict = TweakVerdict.Proven, Flags = TweakFlags.SignOut, Sources = Src(DjdallmannGamingPcSetup),
            Caveat = "A preference: some people like acceleration on the desktop.",
            Changes = Do(CuText(mouse, "MouseSpeed", "0", "1"), CuText(mouse, "MouseThreshold1", "0", "6"), CuText(mouse, "MouseThreshold2", "0", "10")),
            Preset = TweakPreset.SafeGaming, PresetDefault = false,
        };
        yield return new TweakDefinition
        {
            Id = "gaming.capture-off", Group = TweakGroup.Gaming, Title = "Turn off Game Bar capture",
            Effect = "Turns off Game Bar's screenshot and clip recording. The Game Bar itself stays, so AMD X3D core steering and the Xbox overlay keep working.",
            Verdict = TweakVerdict.Situational, Sources = Src(GameMode),
            Caveat = "Only worth it if you never record clips with Game Bar.",
            Changes = Do(Cu(Gcs, "GameDVR_Enabled", 0, 1), Cu(gameDvr, "AppCaptureEnabled", 0, 1)),
        };
        yield return new TweakDefinition
        {
            Id = "gaming.gamedvr-policy", Group = TweakGroup.Gaming, Title = "Block Game Bar capture by policy",
            Effect = "A hard block on Game Bar recording for every account on this PC.",
            Verdict = TweakVerdict.Situational, Risk = TweakRisk.Medium, Flags = TweakFlags.Admin | TweakFlags.ManagedPolicy, Sources = Src(WinUtilTweaks),
            Caveat = "Unreliable on Windows 11 and makes Settings say it's managed. The normal switch above is usually enough.",
            Changes = Do(Lm(@"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR", 0, null)),
        };
        yield return new TweakDefinition
        {
            Id = "gaming.vrr-windowed", Group = TweakGroup.Gaming, Title = "Variable refresh rate for windowed games",
            Effect = "Lets DirectX 11 games that don't support G-SYNC/FreeSync on their own use your monitor's variable refresh rate. Only matters on a VRR monitor.",
            Verdict = TweakVerdict.Situational, Flags = TweakFlags.RestartGame, Sources = Src(GraphicsSettings),
            Caveat = "Only for VRR (G-SYNC / FreeSync) monitors. PC Tidy reads the setting back after writing.",
            Applies = new TweakApplicability { MinBuild = 18362 },
            Changes = Do(new RegistryTokenChange(RegistryHive.CurrentUser, dxPrefs, "DirectXUserGlobalSettings", "VRROptimizeEnable", "1")),
            Undo = "One-click undo puts back the exact settings text PC Tidy found.",
        };
        yield return new TweakDefinition
        {
            Id = "gaming.hags-on", Group = TweakGroup.Gaming, Title = "Hardware-accelerated GPU scheduling on",
            Effect = "Needed for DLSS Frame Generation on newer NVIDIA cards; otherwise worth a few percent either way. Windows usually turns it on by itself.",
            Verdict = TweakVerdict.Situational, Risk = TweakRisk.Medium, Flags = TweakFlags.Admin | TweakFlags.Restart, Sources = Src(Hags),
            Caveat = "Leave it as it is unless you need frame generation. Turning it off is only a test for a specific stutter.",
            Applies = new TweakApplicability { MinBuild = 19041, GpuVendors = new[] { GpuVendor.Nvidia, GpuVendor.Amd, GpuVendor.Intel } },
            Changes = Do(Lm(graphics, "HwSchMode", 2, null)),
        };
        yield return new TweakDefinition
        {
            Id = "gaming.keep-gpu-driver", Group = TweakGroup.Gaming, Title = "Don't let Windows Update replace drivers",
            Effect = "Stops Windows Update from installing drivers over the GPU or chipset driver you installed from the vendor. Security and quality updates keep coming.",
            Verdict = TweakVerdict.Situational, Flags = TweakFlags.Admin | TweakFlags.ManagedPolicy, Sources = Src(UpdateCsp),
            Caveat = "You then update drivers yourself (PC Tidy's Drivers section helps). New devices may need a manual driver.",
            Changes = Do(Lm(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "ExcludeWUDriversInQualityUpdate", 1, null)),
        };
        yield return new TweakDefinition
        {
            Id = "gaming.no-auto-driver-search", Group = TweakGroup.Gaming, Title = "Don't download device drivers automatically",
            Effect = "Stops Windows from fetching drivers for devices by itself (Device installation settings), so a vendor GPU driver isn't swapped for an older Windows one.",
            Verdict = TweakVerdict.Situational, Flags = TweakFlags.Admin, Sources = Src(DriverUpdates),
            Caveat = "New devices may then need a driver from the maker's website.",
            Changes = Do(Lm(@"SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching", "SearchOrderConfig", 0, 1)),
        };
        // Community tweaks §9.7: the best evidence in the whole video set. A frame cap below the GPU limit cut input lag the
        // most; the vendor low-latency modes helped a little. Guidance only: the cap lives in each game's own settings.
        yield return new TweakDefinition
        {
            Id = "gaming.fps-cap", Group = TweakGroup.Gaming, Title = "Cap FPS a little below what your GPU can reach (GPU under about 95-97%)",
            Effect = "Set the game's own frame limit a few FPS below what your graphics card usually reaches, so it isn't running flat out. "
                     + "In camera-measured tests this cut input lag more than any setting: 11 to 22 ms in two shooters, and 42% in one game. "
                     + "AMD Anti-Lag and NVIDIA Ultra Low Latency helped a little (about 9 to 10 ms).",
            Verdict = TweakVerdict.Proven, Sources = Src(FrameCapLatencyTest, Cs2LatencyTests),
            Caveat = "Use the game's own limiter first: an outside limiter added about one frame of lag in the same tests.",
            Changes = Do(new GuidanceChange("In the game's graphics settings, set the frame rate limit (Max FPS) a little below the FPS you usually get. "
                                            + "Task Manager → Performance → GPU shows how busy the graphics card is.")),
            Undo = "Nothing to undo: PC Tidy changes nothing here.",
        };
        yield return new TweakDefinition
        {
            Id = "gaming.nvidia-low-latency", Group = TweakGroup.Gaming, Title = "NVIDIA Low Latency / Reflex",
            Effect = "Turn on NVIDIA Reflex inside games that offer it. The driver's Low Latency Mode is the fallback for games without Reflex. "
                     + "Measured gains were small next to a frame cap: a few milliseconds, mostly when the graphics card is maxed out.",
            Verdict = TweakVerdict.Situational, Sources = Src("https://www.nvidia.com/en-us/geforce/technologies/reflex/", Cs2LatencyTests),
            Applies = new TweakApplicability { GpuVendors = new[] { GpuVendor.Nvidia } },
            Changes = Do(new GuidanceChange("In-game: Reflex On. Otherwise NVIDIA Control Panel → Manage 3D settings → Low Latency Mode, per game.")),
            Preset = TweakPreset.SafeGaming,
        };
        yield return new TweakDefinition
        {
            Id = "gaming.gpu-msi-mode", Group = TweakGroup.Gaming, Title = "GPU MSI mode",
            Effect = "Message-signalled interrupts for the graphics card. Current drivers already turn it on; setting it on the wrong device can give a black screen.",
            Verdict = TweakVerdict.Situational, Risk = TweakRisk.High, Flags = TweakFlags.ExpertOnly | TweakFlags.Admin | TweakFlags.Restart, Sources = Src(DjdallmannGamingPcSetup),
            Caveat = "PC Tidy only reports this: the setting lives on your GPU's own device key, which is different on every PC.",
            Changes = Do(new GuidanceChange(@"HKLM\SYSTEM\CurrentControlSet\Enum\PCI\<your GPU>\…\Interrupt Management\MessageSignaledInterruptProperties → MSISupported = 1. A driver reinstall resets it.")),
        };
        yield return new TweakDefinition
        {
            Id = "gaming.nvidia-shader-cache", Group = TweakGroup.Gaming, Title = "NVIDIA shader cache size",
            Effect = "A bigger shader cache (10 GB, 100 GB or Unlimited) means fewer recompiles and less stutter with a big game library.",
            Verdict = TweakVerdict.Situational, Sources = Src("https://www.nvidia.com/en-us/geforce/forums/"),
            Applies = new TweakApplicability { GpuVendors = new[] { GpuVendor.Nvidia } },
            Changes = Do(new GuidanceChange("NVIDIA Control Panel → Manage 3D settings → Shader Cache Size.")),
            Preset = TweakPreset.SafeGaming,
        };
        yield return new TweakDefinition
        {
            Id = "gaming.memory-integrity", Group = TweakGroup.Gaming, Title = "Memory integrity: keep it on",
            Effect = "Protects the core of Windows from bad drivers and malware, and anti-cheat like Riot Vanguard and FACEIT may need it. "
                     + "PC Tidy never changes it. Windows Security shows it under Device security, Core isolation.",
            Verdict = TweakVerdict.Situational, Risk = TweakRisk.High, Flags = TweakFlags.KeepOn, Sources = Src(MemoryIntegrity, RiotVanguard),
            Changes = Do(new GuidanceChange("Windows Security → Device security → Core isolation details.", "ms-settings:windowsdefender")),
            Undo = "Nothing to undo: PC Tidy only opens the page.",
        };
        // Community tweaks §9.6: pushed as "FPS" settings, but they trade safety for nothing measurable. PC Tidy can't read
        // either setting reliably (no documented value), so these are information rows, not finder checks.
        yield return new TweakDefinition
        {
            Id = "gaming.write-cache-flushing", Group = TweakGroup.Gaming, Title = "Disk write-cache flushing: keep it on",
            Effect = "Windows regularly tells each drive to save what it holds in its fast cache. Guides tick \"Turn off Windows write-cache buffer flushing\" for speed, "
                     + "but a power cut or crash can then lose or corrupt files. Only drives with their own battery-backed cache should ever have it ticked.",
            Verdict = TweakVerdict.Situational, Risk = TweakRisk.High, Flags = TweakFlags.KeepOn, Sources = Src(WriteCacheFlushing),
            Changes = Do(new GuidanceChange("Device Manager → Disk drives → your drive → Properties → Policies: \"Turn off Windows write-cache buffer flushing on the device\" should be unticked.")),
            Undo = "Nothing to undo: PC Tidy changes nothing here.",
        };
        yield return new TweakDefinition
        {
            Id = "gaming.exploit-protection", Group = TweakGroup.Gaming, Title = "Exploit protection: keep the system settings on",
            Effect = "Windows' exploit protections (DEP, ASLR, Control Flow Guard, SEHOP...) stop common attacks on the programs you run. Guides switch them off system-wide for speed; no test showed a gain.",
            Verdict = TweakVerdict.Situational, Risk = TweakRisk.High, Flags = TweakFlags.KeepOn, Sources = Src(Sehop),
            Changes = Do(new GuidanceChange("Windows Security → App & browser control → Exploit protection settings → System settings: each should say \"Use default\".","ms-settings:windowsdefender")),
            Undo = "Nothing to undo: PC Tidy only opens the page.",
        };

        // ---------------- placebo (shown greyed) ----------------
        yield return Placebo("gaming.priority-separation", TweakGroup.Gaming, "Win32PrioritySeparation \"26 hex\"",
            "Changes how long foreground threads run. Three channels recommend three different values and no reproducible FPS gain was found.",
            Src(DjdallmannGamingPcSetup, MelodysTimerTests), Lm(@"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", 0x26, 2));
        yield return Placebo("gaming.system-responsiveness", TweakGroup.Gaming, "MMCSS SystemResponsiveness 0 or 10",
            "Only matters while audio-style MMCSS threads run, and Microsoft says values below 10 are treated as 20 anyway. 100 turns MMCSS off (the bad-tweak finder resets that).",
            Src(Mmcss), Lm(Mm, "SystemResponsiveness", 10, 20));
        yield return Placebo("gaming.network-throttling", TweakGroup.Gaming, "NetworkThrottlingIndex off",
            "A Vista-era audio fix that does nothing for ping; turning it off measurably raised network driver latency in djdallmann's tests.",
            Src(Mmcss, DjdallmannGamingPcSetup), Lm(Mm, "NetworkThrottlingIndex", -1, 10));
        yield return Placebo("gaming.mmcss-games-task", TweakGroup.Gaming, "MMCSS \"Games\" task priority",
            "Only threads that register as \"Games\" use it and few do; Microsoft says GPU Priority and SFIO Priority aren't used.",
            Src(Mmcss), Lm(Mm + @"\Tasks\Games", "Priority", 6, 2));
        yield return Placebo("gaming.timer-resolution", TweakGroup.Gaming, "Timer resolution tools",
            "Since Windows 10 2004 the timer is per-process and games already ask for 1 ms themselves; the global switch only works on Windows 11 and costs power.",
            Src(RandomAsciiTimers, TimeBeginPeriod), Lm(@"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "GlobalTimerResolutionRequests", 1, null));
        yield return Placebo("gaming.power-throttling-off", TweakGroup.Gaming, "Power throttling off",
            "Rarely affects games on a plugged-in desktop; Windows already gives the game in front full speed.",
            Src(PcTuning), Lm(@"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", 1, null));
        yield return Placebo("gaming.standby-list-cleaners", TweakGroup.Gaming, "Standby-list cleaners (ISLC and similar)",
            "Fixed a 2019 Windows bug; today emptying the standby list just makes Windows read the same files again.",
            Src(PcTuning), new CommandChange("Third-party tool that empties the standby memory list on a timer."));
        yield return Placebo("gaming.sysmain-off", TweakGroup.Gaming, "SysMain (Superfetch) off",
            "Negligible on an SSD and makes app launches slower on a hard drive; Microsoft lists it as \"don't disable\".",
            Src(IotServices), new ServiceStartChange("SysMain", ServiceStart.Manual, ServiceStart.Automatic));

        // Community tweaks §9.7: measured end-to-end input lag in CS2 (one PC, a Ryzen X3D). All about 0 ms apart.
        yield return Placebo("gaming.fso-off-global", TweakGroup.Gaming, "Fullscreen optimizations off (all games)",
            "Measured at about 0 ms: 6.1 ms with it off and 6.0 ms with it on in an input-lag test. Turning it off everywhere also breaks fast Alt+Tab and overlays. "
            + "If one old game misbehaves, tick the box in that game's own Properties → Compatibility tab instead.",
            Src(Cs2LatencyTests, DjdallmannGamingPcSetup),
            Cu(Gcs, "GameDVR_FSEBehaviorMode", 2, 0), Cu(Gcs, "GameDVR_HonorUserFSEBehaviorMode", 1, 0), Cu(Gcs, "GameDVR_DXGIHonorFSEWindowsCompatible", 1, 0));
        yield return Placebo("gaming.borderless-vs-fullscreen", TweakGroup.Gaming, "Exclusive fullscreen instead of borderless",
            "Measured at about 0 ms: 9 ms in both modes in an input-lag test. Pick whichever you like.",
            Src(Cs2LatencyTests), new GuidanceChange("Game settings → Display mode → Fullscreen or Borderless."));
        yield return Placebo("gaming.display-scaling", TweakGroup.Gaming, "GPU scaling instead of display scaling",
            "Measured at about 0 ms: 5.9 ms and 6.1 ms in an input-lag test. Choose by how the picture looks, not for speed.",
            Src(Cs2LatencyTests), new GuidanceChange("Graphics driver settings → Scaling → perform scaling on the GPU or on the display."));
        yield return Placebo("gaming.raw-input-keyboard", TweakGroup.Gaming, "Raw keyboard input",
            "Measured at about 0 ms: no difference in an input-lag test.",
            Src(Cs2LatencyTests), new CommandChange("A game console setting that switches the keyboard to raw input."));

        // ---------------- harmful (finder data only, never shown) ----------------
        yield return Harmful("gaming.hpet-forced", "Force HPET (useplatformclock)", "A slower timer that can hurt frame times. Microsoft: debugging only.",
            Src(Bcdedit, MelodysTimerTests), new CommandChange("bcdedit /set useplatformclock true"));
        yield return Harmful("gaming.platform-tick", "useplatformtick", "Reports of higher DPC latency and input lag; debugging only.",
            Src(Bcdedit, BlurBustersTimers), new CommandChange("bcdedit /set useplatformtick yes"));
        yield return Harmful("gaming.dynamic-tick-off", "disabledynamictick", "Costs idle power (bad on laptops) for anecdotal gains.",
            Src(Bcdedit), new CommandChange("bcdedit /set disabledynamictick yes"));
        yield return Harmful("gaming.tsc-sync-policy", "tscsyncpolicy enhanced", "No measured benefit; debugging only.",
            Src(Bcdedit, MelodysTimerTests), new CommandChange("bcdedit /set tscsyncpolicy enhanced"));
        yield return Harmful("gaming.large-system-cache", "LargeSystemCache = 1", "A server setting with a history of driver trouble on desktops.",
            Src(PcTuning), Lm(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "LargeSystemCache", 1, 0));
        yield return Harmful("gaming.pagefile-off", "Paging file off", "Apps run out of memory and crash, and Windows can't save crash dumps.",
            Src(Pagefile), new CommandChange("System Properties → Virtual memory → No paging file"));
        yield return Harmful("gaming.search-indexing-disabled", "Search indexing disabled", "Breaks or slows Start, Explorer and Outlook search.",
            Src(IotServices), new ServiceStartChange("WSearch", ServiceStart.Disabled, ServiceStart.AutomaticDelayed));
        yield return Harmful("gaming.spectre-off", "Spectre/Meltdown mitigations off", "Removes CPU security mitigations; FACEIT and others check them.",
            Src(SpectreGuidance), Lm(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "FeatureSettingsOverride", 3, null));
        yield return Harmful("gaming.anti-cheat-blockers", "testsigning / nointegritychecks / debug", "Easy Anti-Cheat, BattlEye and Vanguard refuse to run with these on.",
            Src(Bcdedit, BitLockerBcd), new CommandChange("bcdedit /set testsigning on"));
    }

    // ------------------------------------------------------------------ shared builders

    internal static TweakDefinition Placebo(string id, TweakGroup group, string title, string why, IReadOnlyList<string> sources, params TweakChange[] changes)
        => new()
        {
            Id = id, Group = group, Title = title, Effect = why, Caveat = why,
            Verdict = TweakVerdict.Placebo, Sources = sources, Changes = changes,
            Undo = "Not offered, so there's nothing to undo.",
        };

    internal static TweakDefinition Harmful(string id, string title, string why, IReadOnlyList<string> sources, params TweakChange[] changes)
        => new()
        {
            Id = id, Group = GroupFromId(id), Title = title, Effect = why, Caveat = why,
            Verdict = TweakVerdict.Harmful, Risk = TweakRisk.High, Sources = sources, Changes = changes,
            Undo = "Never applied. If it's already on this PC, Find bad tweaks offers to reset it.",
        };

    /// <summary>"privacy.x" → Privacy. Ids always start with their group.</summary>
    public static TweakGroup GroupFromId(string id) => id.Split('.')[0] switch
    {
        "privacy" => TweakGroup.Privacy,
        "debloat" => TweakGroup.Debloat,
        "ui" => TweakGroup.UiExplorer,
        "network" => TweakGroup.Network,
        "power" => TweakGroup.Power,
        _ => TweakGroup.Gaming,
    };
}
