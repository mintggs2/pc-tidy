using PcTidy.Core.Model;
using static PcTidy.Catalog.Tweaks.TweakSources;

namespace PcTidy.Catalog.Tweaks;

/// <summary>Sources for the v3.1 finder checks (the research pages' links; "verify" = not re-read on Microsoft's site).</summary>
public static class BadTweakSources
{
    public const string Bbr2Reports = "https://learn.microsoft.com/en-us/answers/questions/3879946/fix-bbr2-bugs-on-windows-11";
    public const string NetTcpSetting = "https://learn.microsoft.com/powershell/module/nettcpip/set-nettcpsetting";
    // verify: Microsoft's "standardized INF keywords for jumbo packet" page (the *JumboPacket keyword, 1514 = off).
    public const string JumboPacketKeyword = "https://learn.microsoft.com/windows-hardware/drivers/network/standardized-inf-keywords-for-jumbo-packet";
    public const string WebView2Distribution = "https://learn.microsoft.com/microsoft-edge/webview2/concepts/distribution";
    public const string WebView2Download = "https://developer.microsoft.com/microsoft-edge/webview2/";
    public const string Cs2AntiLagBan = "https://www.pcgamesn.com/counter-strike-2/amd-vac-ban";
    public const string OptimizerSuiteReview = "https://toptenreviews.com/pc-system-utilities-software-advanced-systemcare-review";
    // verify: the research could only read a snippet of Microsoft's registry-cleaner support policy (the page returned 404).
    public const string RegistryCleanerPolicy = "https://support.microsoft.com/kb/2563254";
    // verify: NVIDIA's nvidia-smi page documents the TCC/WDDM driver models; the research didn't re-read it.
    public const string NvidiaSmi = "https://docs.nvidia.com/deploy/nvidia-smi/index.html";
}

public static partial class BadTweakCatalog
{
    /// <summary>BcdOSLoaderBoolean_SafeBootAlternateShell (Microsoft's BCD element list). verify: 0x26000081.</summary>
    public const uint SafeBootAlternateShellElement = 0x26000081;

    /// <summary>Opens Windows Security's "Virus &amp; threat protection settings" (an OpenExternal target the guard allows).</summary>
    public const string DefenderThreatSettings = "windowsdefender://threat-settings";

    /// <summary>The Xbox Game Bar's Microsoft Store page. verify: product id 9NZKPSTSNW4P.</summary>
    public const string GameBarStorePage = "ms-windows-store://pdp/?productid=9NZKPSTSNW4P";

    /// <summary>First Windows build that has BBR2 (Windows 11 24H2).</summary>
    public const int Bbr2FirstBuild = 26100;

    /// <summary>First Windows 11 build (WebView2 is part of Windows from here on).</summary>
    public const int Windows11FirstBuild = 22000;

    /// <summary>Display adapters' class key and how many of its instances the finder reads (for the feature code's custom checks).</summary>
    public const string DisplayAdapterClassKey = DisplayClass;
    public const int DisplayAdapterInstances = GpuClassInstances;

    private static IEnumerable<BadTweakCheck> V31()
    {
        yield return new BadTweakCheck
        {
            Id = "security.defender-realtime-off", Area = BadTweakArea.Security, Severity = BadTweakSeverity.High,
            Title = "Defender's real-time protection is off",
            WhyBad = "Microsoft Defender is the antivirus in charge, but it no longer checks files when they're downloaded, opened or run. "
                     + "\"FPS\" guides switch it off for a gain no test confirmed; on a current PC Defender's cost is small.",
            WindowsDefault = "On.",
            Sources = Src(DefenderStatus, DefenderTamperProtection),
            Note = "Apps can't switch it back on for you, PC Tidy included: Windows Security is the place, and PC Tidy opens it for you. "
                   + "If it keeps going off, a tool or a Defender policy is behind it; a policy shows up as its own finding, which PC Tidy can remove.",
            Signals = new[]
            {
                Custom(CustomProbeKind.DefenderRealtimeOff, new GuidanceFix(
                    "Open Windows Security → Virus & threat protection → Manage settings and turn on Real-time protection. Then turn on Tamper Protection on the same page.",
                    DefenderThreatSettings)),
            },
        };

        yield return new BadTweakCheck
        {
            Id = "network.bbr2-congestion", Area = BadTweakArea.Network, Severity = BadTweakSeverity.Medium,
            Title = "TCP uses the BBR2 congestion setting",
            WhyBad = "Guides switch Windows 11's TCP congestion setting to BBR2 for \"lower ping\". On Windows 11 24H2 people reported that Steam's web helper "
                     + "stopped starting, download managers stopped working and connections inside the PC (localhost) became slow or broken "
                     + "(Microsoft Q&A, build 26100.3915, 2025). Games send their play traffic over UDP, which this setting doesn't touch, so there's no ping to gain.",
            WindowsDefault = "CUBIC (Windows' standard since Windows 10 version 1709).",
            Flags = BadTweakFlags.Admin, Sources = Src(BadTweakSources.Bbr2Reports, BadTweakSources.NetTcpSetting),
            // A one-click reset needs a new allow-listed tool id (Lead integration); until then this shows the command.
            Note = "PC Tidy only shows the command for this one. It affects new connections, so restart Steam (or the PC) afterwards.",
            Signals = new[]
            {
                Custom(CustomProbeKind.Bbr2CongestionProvider, new GuidanceFix(
                    "Open a terminal as administrator and run the command shown under \"Found\" for each setting listed, then restart Steam or the PC.")),
            },
        };

        yield return new BadTweakCheck
        {
            Id = "network.jumbo-frames", Area = BadTweakArea.Network, Severity = BadTweakSeverity.Medium,
            Title = "Jumbo packets are on for a network adapter",
            WhyBad = "Jumbo packets only work when every device on the way (network card, switch, router, NAS) uses the same size. The internet and game servers "
                     + "use the standard size, so with jumbo packets on some connections drop or slow down. Guides switch it on for \"faster\" networking; no gaming gain was measured.",
            WindowsDefault = "Jumbo Packet: Disabled (1514 bytes).",
            Flags = BadTweakFlags.Admin | BadTweakFlags.Restart, Sources = Src(BadTweakSources.JumboPacketKeyword, NetworkTuning),
            Note = "If you switched it on for a NAS on a network where every device supports jumbo packets, keep it. "
                   + "The change takes effect when the adapter restarts or after a restart; the connection drops for a moment.",
            Signals = new[]
            {
                Custom(CustomProbeKind.JumboFrames,
                    new RegistrySetFoundFix(RegistryKind.String, BadTweakAllowances.JumboDefault),
                    new GuidanceFix("Or in Device Manager: open the adapter under Network adapters → Advanced → Jumbo Packet → Disabled.")),
            },
        };

        yield return new BadTweakCheck
        {
            Id = "windows.bios-tool-leftovers", Area = BadTweakArea.Windows, Severity = BadTweakSeverity.Medium,
            Title = "BIOS tools for Windows are on this PC",
            WhyBad = "\"BIOS tweak\" guides run AMI's setup editor (SCEWIN) or flasher from inside Windows to export and change every BIOS setting at once, hidden ones included. "
                     + "One wrong value can stop the PC starting, and their helper driver gives programs direct access to the firmware. No FPS gain was measured.",
            WindowsDefault = "Not on the PC: BIOS settings are changed in the BIOS setup screen.",
            Flags = BadTweakFlags.Admin, Sources = Src(VulnerableDriverBlocklist, KnownBadDrivers),
            Note = "PC Tidy only lists names and folders. It never opens these files, never changes BIOS settings and never deletes firmware tools for you.",
            Signals = new[]
            {
                Custom(CustomProbeKind.BiosToolLeftovers, new GuidanceFix(
                    "If you don't use them any more, delete the folder yourself. Change BIOS settings in the BIOS setup screen instead (press Del or F2 while the PC starts). "
                    + "A helper driver listed here can be removed with the tool that installed it, or in PC Tidy's Drivers section.")),
            },
        };

        yield return new BadTweakCheck
        {
            Id = "windows.optimizer-suite-installed", Area = BadTweakArea.Windows, Severity = BadTweakSeverity.Medium,
            Title = "A \"PC optimizer\" suite is installed",
            WhyBad = "One-click optimizer suites bundle a registry cleaner, startup and service changes and \"turbo\" modes, and keep running in the background themselves. "
                     + "The only measured result PC Tidy found is negative: a review of Advanced SystemCare Pro 11 found the PC about 7% slower overall and about 73% slower to start "
                     + "(TopTenReviews, version 11 from about 2018, test PC not stated, not a gaming test). No newer independent test was found.",
            WindowsDefault = "Not installed: Windows looks after itself.",
            Sources = Src(BadTweakSources.OptimizerSuiteReview),
            Note = "PC Tidy only reports it. If the suite made a registry backup, keep it until Windows runs well after uninstalling.",
            Signals = new[]
            {
                Custom(CustomProbeKind.OptimizerSuites, new GuidanceFix(
                    "Uninstall it in PC Tidy's Apps section (or Settings → Apps → Installed apps).", "ms-settings:appsfeatures")),
            },
        };

        yield return new BadTweakCheck
        {
            Id = "windows.registry-cleaner-installed", Area = BadTweakArea.Windows, Severity = BadTweakSeverity.Low,
            Title = "A registry cleaner is installed",
            WhyBad = "Windows doesn't need its registry cleaned, and no test found a speed gain worth the risk. Microsoft doesn't support registry cleaners: "
                     + "a bad clean can stop apps or Windows working, sometimes until Windows is reinstalled.",
            WindowsDefault = "Not installed.",
            Sources = Src(BadTweakSources.RegistryCleanerPolicy),
            Note = "If it's part of a cleaner you like (CCleaner, for example), its other parts aren't the problem: just don't use its registry part.",
            Signals = new[]
            {
                Custom(CustomProbeKind.RegistryCleaners, new GuidanceFix(
                    "Uninstall it in PC Tidy's Apps section (or Settings → Apps → Installed apps), or stop using its registry part.", "ms-settings:appsfeatures")),
            },
        };

        yield return new BadTweakCheck
        {
            Id = "cpu.processor-idle-disabled", Area = BadTweakArea.CpuPower, Severity = BadTweakSeverity.Medium,
            Title = "The CPU is never allowed to rest (idle states off)",
            WhyBad = "Some latency guides switch off the processor's idle states in the power plan. The CPU then never rests: it runs hotter, uses more power, "
                     + "has less room to boost, and a laptop's battery drains faster. No gaming gain was measured.",
            WindowsDefault = "Processor idle disable: 0 (idle states on).",
            Flags = BadTweakFlags.Admin, Sources = Src(PowerSchemes),
            Signals = new[]
            {
                Custom(CustomProbeKind.ProcessorIdleDisabled, new GuidanceFix(
                    "Control Panel → Power Options → Change plan settings → Restore default settings for this plan. "
                    + "Or in an admin terminal: powercfg -setacvalueindex scheme_current sub_processor 5d76a2ca-e8c0-402f-a133-2158492d58ad 0 "
                    + "(and the same with -setdcvalueindex on a laptop), then powercfg -setactive scheme_current.")),
            },
        };

        yield return new BadTweakCheck
        {
            Id = "cpu.x3d-game-bar-missing", Area = BadTweakArea.CpuPower, Severity = BadTweakSeverity.Medium,
            Title = "The Xbox Game Bar is removed on a Ryzen X3D processor",
            WhyBad = "On Ryzen 9 X3D processors with two chiplets, Windows and AMD's chipset driver use the Xbox Game Bar to see that a game is running and steer it to "
                     + "the cores with the extra cache. Without it, games can run on the wrong half of the chip. Owners report stutter; no percentage was measured.",
            WindowsDefault = "Xbox Game Bar installed.",
            Sources = Src(X3dParkingBug, GameMode),
            Note = CpuKinds.DualCcdX3DAdvice,
            Signals = new[]
            {
                Custom(CustomProbeKind.X3dGameBarMissing, new GuidanceFix(
                    "Install the Xbox Game Bar again from the Microsoft Store, and keep AMD's chipset driver and the Balanced power plan.", GameBarStorePage)),
            },
        };

        yield return new BadTweakCheck
        {
            Id = "windows.webview2-removed", Area = BadTweakArea.Windows, Severity = BadTweakSeverity.High,
            Title = "The WebView2 runtime is missing",
            WhyBad = "Windows 11 ships with Microsoft Edge WebView2. Widgets, the new Outlook, Teams and many other apps draw their windows with it, "
                     + "so they stop opening or show blank windows without it. Debloat scripts remove it together with Edge.",
            WindowsDefault = "Installed (part of Windows 11).",
            Sources = Src(BadTweakSources.WebView2Distribution),
            Note = "Microsoft Edge itself can be uninstalled in the European Economic Area, and that's fine: Windows keeps WebView2 then. Only WebView2 is flagged.",
            Signals = new[]
            {
                Custom(CustomProbeKind.WebView2Missing, new GuidanceFix(
                    "Install the Evergreen WebView2 Runtime from Microsoft's WebView2 page (the \"Evergreen Bootstrapper\" download).", BadTweakSources.WebView2Download)),
            },
        };

        var tcc = new List<BadSignal>();
        for (var i = 0; i < GpuClassInstances; i++)
            tcc.Add(Sig(Lm($@"{DisplayClass}\{i:0000}", "TCCSupported", ValueTest.AnyOf, 1), new GuidanceFix(
                "In Registry Editor (as admin), delete TCCSupported from the display adapter key listed under \"Found\", then restart. If the screen stays black, reinstall the graphics driver.")));
        yield return new BadTweakCheck
        {
            Id = "graphics.tcc-mode", Area = BadTweakArea.Graphics, Severity = BadTweakSeverity.Medium,
            Title = "A graphics card is pushed into compute-only mode (TCCSupported)",
            WhyBad = "A value from GPU \"latency\" packs that tries to switch a GeForce card into NVIDIA's compute-only driver mode (TCC). "
                     + "In that mode a graphics card shows no picture at all, so it can't help games; at worst the screen stays black.",
            WindowsDefault = "Not set.",
            Flags = BadTweakFlags.Admin | BadTweakFlags.Restart, Sources = Src(BadTweakSources.NvidiaSmi),
            // verify: NVIDIA doesn't document TCCSupported for GeForce cards; the research has it from memory, so PC Tidy only explains it.
            Note = "PC Tidy only points to this one: NVIDIA doesn't document the value for GeForce cards, so PC Tidy doesn't remove it for you.",
            Signals = tcc,
        };

        yield return new BadTweakCheck
        {
            Id = "graphics.antilag-plus-driver", Area = BadTweakArea.Graphics, Severity = BadTweakSeverity.Low,
            Title = "An AMD driver with Anti-Lag+ is installed",
            WhyBad = "AMD's Adrenalin 23.10.1 driver brought Anti-Lag+, which Counter-Strike 2's anti-cheat saw as tampering: in October 2023 Valve warned that using it "
                     + "could get you a VAC ban, and AMD pulled the driver. Newer drivers replaced it with Anti-Lag 2, which games build in.",
            WindowsDefault = "A current AMD driver.",
            Sources = Src(BadTweakSources.Cs2AntiLagBan),
            Note = "Information only: plain Anti-Lag and Anti-Lag 2 have no known bans.",
            Signals = new[]
            {
                Custom(CustomProbeKind.AntiLagPlusDriver, new GuidanceFix(
                    "Update the AMD graphics driver (AMD Software → Driver and Software, or PC Tidy's Drivers section). Until then, don't turn on Anti-Lag+ in Counter-Strike 2.")),
            },
        };
    }
}
