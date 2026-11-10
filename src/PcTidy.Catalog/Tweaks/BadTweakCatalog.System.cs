using PcTidy.Core.Model;
using static PcTidy.Catalog.Tweaks.TweakSources;

namespace PcTidy.Catalog.Tweaks;

public static partial class BadTweakCatalog
{
    private const string Mm = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";

    private static IEnumerable<BadTweakCheck> Scheduler()
    {
        yield return new BadTweakCheck
        {
            Id = "scheduler.system-responsiveness", Area = BadTweakArea.Scheduler, Severity = BadTweakSeverity.Low,
            Title = "MMCSS SystemResponsiveness is changed",
            WhyBad = "Microsoft treats values below 10 as 20, and 100 turns the multimedia scheduler off (audio crackles). No FPS gain was ever measured.",
            WindowsDefault = "20.", Flags = BadTweakFlags.Admin | BadTweakFlags.Restart, Sources = Src(Mmcss, DjdallmannGamingPcSetup),
            Signals = new[] { Sig(Lm(Mm, "SystemResponsiveness", ValueTest.NoneOf, 20), SetTo(20)) },
        };
        yield return new BadTweakCheck
        {
            Id = "scheduler.network-throttling", Area = BadTweakArea.Scheduler, Severity = BadTweakSeverity.Low,
            Title = "NetworkThrottlingIndex is changed",
            WhyBad = "Does nothing for ping; turning it off raised network driver latency in measurements.",
            WindowsDefault = "10.", Flags = BadTweakFlags.Admin | BadTweakFlags.Restart, Sources = Src(Mmcss, DjdallmannGamingPcSetup),
            Signals = new[] { Sig(Lm(Mm, "NetworkThrottlingIndex", ValueTest.NoneOf, 10), SetTo(10)) },
        };
        yield return new BadTweakCheck
        {
            Id = "scheduler.mmcss-hidden-values", Area = BadTweakArea.Scheduler, Severity = BadTweakSeverity.Low,
            Title = "Hidden MMCSS values are set",
            WhyBad = "Undocumented values from tweak packs; no measured benefit.",
            WindowsDefault = "Not set.", Flags = BadTweakFlags.Admin | BadTweakFlags.Restart, Sources = Src(DjdallmannGamingPcSetup),
            Signals = new[] { Sig(Lm(Mm, "NoLazyMode", ValueTest.Present), Remove), Sig(Lm(Mm, "LazyModeTimeout", ValueTest.Present), Remove) },
        };
        yield return new BadTweakCheck
        {
            Id = "scheduler.games-task", Area = BadTweakArea.Scheduler, Severity = BadTweakSeverity.Low,
            Title = "MMCSS \"Games\" task is changed",
            WhyBad = "Only threads that register as \"Games\" use it; Microsoft says GPU Priority and SFIO Priority aren't read.",
            WindowsDefault = "Priority 2, Scheduling Category Medium, SFIO Priority Normal, GPU Priority 8.",
            Flags = BadTweakFlags.Admin | BadTweakFlags.Restart, Sources = Src(Mmcss),
            Signals = new[]
            {
                Sig(Lm(Mm + @"\Tasks\Games", "Priority", ValueTest.NoneOf, 2), SetTo(2)),
                Sig(Lm(Mm + @"\Tasks\Games", "GPU Priority", ValueTest.NoneOf, 8), SetTo(8)),
                Sig(LmText(Mm + @"\Tasks\Games", "Scheduling Category", ValueTest.NoneOf, "Medium"), new RegistrySetFix(RegistryKind.String, "Medium")),
                Sig(LmText(Mm + @"\Tasks\Games", "SFIO Priority", ValueTest.NoneOf, "Normal"), new RegistrySetFix(RegistryKind.String, "Normal")),
            },
        };
        yield return new BadTweakCheck
        {
            Id = "scheduler.global-timer", Area = BadTweakArea.Scheduler, Severity = BadTweakSeverity.Low,
            Title = "Global timer resolution is switched on",
            WhyBad = "Makes one app's 0.5 ms timer apply to the whole PC: more power and heat. Games already ask for the timer they need.",
            WindowsDefault = "Not set.", Flags = BadTweakFlags.Admin | BadTweakFlags.Restart, Sources = Src(RandomAsciiTimers, TimeBeginPeriod),
            Signals = new[] { Sig(Lm(@"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "GlobalTimerResolutionRequests", ValueTest.AnyOf, 1), Remove) },
        };
        yield return new BadTweakCheck
        {
            Id = "scheduler.power-throttling-off", Area = BadTweakArea.Scheduler, Severity = BadTweakSeverity.Low,
            Title = "Power throttling is switched off",
            WhyBad = "Background apps can't be slowed down to save power. No gain in games; costs battery on laptops.",
            WindowsDefault = "Not set.", Flags = BadTweakFlags.Admin | BadTweakFlags.Restart, Sources = Src(PcTuning),
            Signals = new[] { Sig(Lm(@"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", ValueTest.AnyOf, 1), Remove) },
        };
        yield return new BadTweakCheck
        {
            Id = "scheduler.realtime-priority", Area = BadTweakArea.Scheduler, Severity = BadTweakSeverity.Low,
            Title = "A program is forced to Realtime priority",
            WhyBad = "Realtime priority can starve Windows itself (mouse, audio, disk) and freeze the PC.",
            WindowsDefault = "Not set.", Flags = BadTweakFlags.Admin, Sources = Src(PcTuning),
            // Microsoft doesn't document CpuPriorityClass, so PC Tidy only explains this one (no allowance, no removal).
            Note = "Microsoft doesn't document this value (4 is what tuning tools write for Realtime), so PC Tidy only points to it and doesn't remove it for you.",
            Signals = new[] { Custom(CustomProbeKind.RealtimePriority, new GuidanceFix("In Registry Editor (as admin), open the program's key under HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Image File Execution Options, then delete the CpuPriorityClass value inside PerfOptions. Or reinstall the tool that set it.")) },
        };
    }

    private static IEnumerable<BadTweakCheck> Network()
    {
        yield return new BadTweakCheck
        {
            Id = "network.autotuning-off", Area = BadTweakArea.Network, Severity = BadTweakSeverity.High,
            Title = "TCP receive autotuning is off or restricted",
            WhyBad = "Caps how much data Windows accepts at once, so big downloads (games, updates) get much slower.",
            WindowsDefault = "Normal.", Flags = BadTweakFlags.Admin, Sources = Src(NetworkTuning),
            Signals = new[] { Custom(CustomProbeKind.TcpAutotuning, new ToolFix(ToolIds.NetshAutotuningNormal, "Set TCP autotuning back to Normal")) },
        };
        yield return new BadTweakCheck
        {
            Id = "network.pmtud-off", Area = BadTweakArea.Network, Severity = BadTweakSeverity.Medium,
            Title = "Path MTU discovery is off",
            WhyBad = "Some sites and VPNs then hang or load half a page.",
            WindowsDefault = "Not set (on).", Flags = BadTweakFlags.Admin | BadTweakFlags.Restart, Sources = Src(NetworkTuning),
            Signals = new[] { Sig(Lm(Tcpip, "EnablePMTUDiscovery", ValueTest.AnyOf, 0), Remove) },
        };
        yield return new BadTweakCheck
        {
            Id = "network.sack-off", Area = BadTweakArea.Network, Severity = BadTweakSeverity.Medium,
            Title = "TCP selective acknowledgements are off",
            WhyBad = "Lost packets are re-sent less efficiently, which slows connections on Wi-Fi and busy networks.",
            WindowsDefault = "Not set (on).", Flags = BadTweakFlags.Admin | BadTweakFlags.Restart, Sources = Src(NetworkTuning),
            Signals = new[] { Sig(Lm(Tcpip, "SackOpts", ValueTest.AnyOf, 0), Remove) },
        };
        yield return new BadTweakCheck
        {
            Id = "network.offload-off", Area = BadTweakArea.Network, Severity = BadTweakSeverity.Low,
            Title = "Network task offload is turned off",
            WhyBad = "Moves work from the network card to the CPU for no gain.",
            WindowsDefault = "Not set.", Flags = BadTweakFlags.Admin | BadTweakFlags.Restart, Sources = Src(NetworkTuning),
            Signals = new[] { Sig(Lm(Tcpip, "DisableTaskOffload", ValueTest.AnyOf, 1), Remove), Sig(Lm(Tcpip, "EnableTCPChimney", ValueTest.Present), Remove) },
        };
        yield return new BadTweakCheck
        {
            Id = "network.legacy-tcp-values", Area = BadTweakArea.Network, Severity = BadTweakSeverity.Low,
            Title = "Old TCP registry values are set",
            WhyBad = "Window-size and table values from Windows XP guides. Microsoft says modern Windows ignores them; they only clutter the registry.",
            WindowsDefault = "Not set.", Flags = BadTweakFlags.Admin | BadTweakFlags.Restart, Sources = Src(NetworkTuning),
            Signals = new[] { "TcpWindowSize", "GlobalMaxTcpWindowSize", "NumTcbTablePartitions", "MaxHashTableSize", "Tcp1323Opts", "DefaultTTL" }
                .Select(v => Sig(Lm(Tcpip, v, ValueTest.Present), Remove)).ToList(),
        };
        yield return new BadTweakCheck
        {
            Id = "network.nagle-leftovers", Area = BadTweakArea.Network, Severity = BadTweakSeverity.Low,
            Title = "\"Nagle\" values are set on a network adapter",
            WhyBad = "TcpAckFrequency / TCPNoDelay only affect TCP, most games use UDP, and Windows no longer reads TCPNoDelay. Harmless clutter.",
            WindowsDefault = "Not set.", Flags = BadTweakFlags.Admin, Sources = Src(NetworkTuning),
            Signals = new[] { Custom(CustomProbeKind.NagleLeftovers, new GuidanceFix("Remove TcpAckFrequency, TCPNoDelay and TcpDelAckTicks from your adapter's key under Tcpip\\Parameters\\Interfaces (Registry Editor, as admin). Leaving them is harmless.")) },
        };
        yield return new BadTweakCheck
        {
            Id = "network.qos-limit", Area = BadTweakArea.Network, Severity = BadTweakSeverity.Low,
            Title = "The \"20% reserved bandwidth\" policy is set",
            WhyBad = "A myth: nothing is reserved unless an app asks for it, so this policy does nothing.",
            WindowsDefault = "Not set.", Flags = BadTweakFlags.Admin, Sources = Src(NetworkTuning),
            Signals = new[] { Sig(Lm(@"SOFTWARE\Policies\Microsoft\Windows\Psched", "NonBestEffortLimit", ValueTest.Present), Remove) },
        };
        yield return new BadTweakCheck
        {
            Id = "network.ipv6-disabled", Area = BadTweakArea.Network, Severity = BadTweakSeverity.Medium,
            Title = "IPv6 is switched off",
            WhyBad = "Microsoft says not to: it breaks Xbox party chat, Teredo and some VPNs and slows startup. \"Prefer IPv4\" (0x20) is the supported option and isn't flagged.",
            WindowsDefault = "Not set.", Flags = BadTweakFlags.Admin | BadTweakFlags.Restart, Sources = Src(Ipv6),
            Signals = new[] { Sig(Lm(@"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters", "DisabledComponents", ValueTest.NoneOf, 0, 0x20), Remove) },
        };
        yield return new BadTweakCheck
        {
            Id = "network.active-probing-off", Area = BadTweakArea.Network, Severity = BadTweakSeverity.Low,
            Title = "Internet connection check is off",
            WhyBad = "Windows can't tell whether you're online: you get a false \"No internet\" icon and hotel/café Wi-Fi sign-in pages don't open.",
            WindowsDefault = "1 (on).", Flags = BadTweakFlags.Admin, Sources = Src(Endpoints),
            Signals = new[] { Sig(Lm(@"SYSTEM\CurrentControlSet\Services\NlaSvc\Parameters\Internet", "EnableActiveProbing", ValueTest.AnyOf, 0), SetTo(1)) },
        };
        yield return new BadTweakCheck
        {
            Id = "network.adapter-protocols-unbound", Area = BadTweakArea.Network, Severity = BadTweakSeverity.Medium,
            Title = "Network protocols are unticked on an adapter",
            WhyBad = "Some \"latency\" guides untick everything in the adapter's properties except IPv4. Without IPv6 or Client for Microsoft Networks, Xbox party chat, some VPNs, shared folders and printers can stop working, for no gain in ping.",
            WindowsDefault = "Internet Protocol Version 4 and 6 and Client for Microsoft Networks are ticked.",
            Flags = BadTweakFlags.Admin, Sources = Src(Ipv6, NetAdapterBindings),
            Note = "If a VPN or your IT team told you to untick one on purpose, leave it.",
            Signals = new[]
            {
                Custom(CustomProbeKind.UnboundAdapterProtocols, new GuidanceFix(
                    "Open Settings → Network & internet → Advanced network settings, open the adapter listed, choose \"Edit\" next to More adapter options and tick the items shown as unticked.",
                    "ms-settings:network-advancedsettings")),
            },
        };
        yield return new BadTweakCheck
        {
            Id = "network.hosts-blocks-microsoft", Area = BadTweakArea.Network, Severity = BadTweakSeverity.High,
            Title = "The hosts file blocks Windows Update, Store or activation",
            WhyBad = "\"Telemetry blocker\" lists often include the servers Windows needs for updates, Store downloads, activation and Microsoft-account sign-in.",
            WindowsDefault = "Only comments.", Flags = BadTweakFlags.Admin, Sources = Src(Endpoints),
            Note = "PC Tidy backs up the hosts file and only comments out the lines listed; other lines stay.",
            Signals = new[] { Custom(CustomProbeKind.HostsFile, new HostsCommentOutFix()) },
        };
        yield return new BadTweakCheck
        {
            Id = "network.firewall-blocks-microsoft", Area = BadTweakArea.Network, Severity = BadTweakSeverity.Medium,
            Title = "Firewall rules block Windows Update or the Store",
            WhyBad = "Rules added by \"privacy\" tools stop Windows Update, Delivery Optimization or the Store from connecting.",
            WindowsDefault = "No such rules.", Flags = BadTweakFlags.Admin, Sources = Src(Firewall),
            Signals = new[] { Custom(CustomProbeKind.FirewallBlocksMicrosoft, new GuidanceFix("Windows Security → Firewall & network protection → Advanced settings → Outbound Rules: delete the rules listed here.", "ms-settings:windowsdefender")) },
        };
    }

    private static IEnumerable<BadTweakCheck> Services()
    {
        yield return new BadTweakCheck
        {
            Id = "services.core-disabled", Area = BadTweakArea.Services, Severity = BadTweakSeverity.High,
            Title = "Core Windows services are disabled",
            WhyBad = "\"Disable these services\" lists switch off parts Windows needs: event logs, scheduled tasks, time sync (breaks sign-ins and anti-cheat), name lookup, typing in Start, sound.",
            WindowsDefault = "Automatic or Manual, as listed for each service.",
            Flags = BadTweakFlags.Admin | BadTweakFlags.Restart, Sources = Src(IotServices, PerUserServices),
            Note = "Windows protects some core services; if it refuses, PC Tidy says so.",
            Signals = new[]
            {
                SvcOff("EventLog", ServiceStart.Automatic), SvcOff("Schedule", ServiceStart.Automatic), SvcOff("Winmgmt", ServiceStart.Automatic),
                SvcOff("Dnscache", ServiceStart.Automatic), SvcOff("NlaSvc", ServiceStart.Manual), SvcOff("W32Time", ServiceStart.Manual),
                SvcOff("TokenBroker", ServiceStart.Manual), SvcOff("wlidsvc", ServiceStart.Manual), SvcOff("sppsvc", ServiceStart.AutomaticDelayed),
                SvcOff("TextInputManagementService", ServiceStart.Automatic), SvcOff("iphlpsvc", ServiceStart.Automatic),
                SvcOff("Audiosrv", ServiceStart.Automatic), SvcOff("EventSystem", ServiceStart.Automatic), SvcOff("StorSvc", ServiceStart.AutomaticDelayed),
            },
        };
        yield return new BadTweakCheck
        {
            Id = "services.notifications-disabled", Area = BadTweakArea.Services, Severity = BadTweakSeverity.Medium,
            Title = "Push notifications service is disabled",
            WhyBad = "Toast notifications and Mail/Calendar/Store badges stop working. Microsoft says not to disable it.",
            WindowsDefault = "Automatic.", Flags = BadTweakFlags.Admin | BadTweakFlags.Restart, Sources = Src(IotServices),
            Signals = new[] { SvcOff("WpnService", ServiceStart.Automatic) },
        };
        yield return new BadTweakCheck
        {
            Id = "services.themes-disabled", Area = BadTweakArea.Services, Severity = BadTweakSeverity.Medium,
            Title = "Themes service is disabled",
            WhyBad = "Windows falls back to a broken classic look, and dark mode and high contrast stop rendering properly.",
            WindowsDefault = "Automatic.", Flags = BadTweakFlags.Admin | BadTweakFlags.Restart, Sources = Src(IotServices),
            Signals = new[] { SvcOff("Themes", ServiceStart.Automatic) },
        };
        yield return new BadTweakCheck
        {
            Id = "services.xbox-disabled", Area = BadTweakArea.Services, Severity = BadTweakSeverity.Medium,
            Title = "Xbox services are disabled",
            WhyBad = "Breaks Game Pass, Store games, cloud saves, Xbox sign-in and controller updates. They use nothing when idle anyway.",
            WindowsDefault = "Manual.", Flags = BadTweakFlags.Admin, Sources = Src(IotServices),
            Signals = new[] { SvcOff("XblAuthManager", ServiceStart.Manual), SvcOff("XblGameSave", ServiceStart.Manual), SvcOff("XboxGipSvc", ServiceStart.Manual), SvcOff("XboxNetApiSvc", ServiceStart.Manual) },
        };
        yield return new BadTweakCheck
        {
            Id = "services.search-disabled", Area = BadTweakArea.Services, Severity = BadTweakSeverity.Medium,
            Title = "Windows Search is disabled",
            WhyBad = "Start, Explorer, Settings and Outlook search become slow or stop finding things. The indexer already backs off when you're busy.",
            WindowsDefault = "Automatic (delayed start).", Flags = BadTweakFlags.Admin, Sources = Src(IotServices),
            Signals = new[] { SvcOff("WSearch", ServiceStart.AutomaticDelayed) },
        };
        yield return new BadTweakCheck
        {
            Id = "services.third-party-all-disabled", Area = BadTweakArea.Services, Severity = BadTweakSeverity.High,
            Title = "Most other companies' services are switched off",
            WhyBad = "This is what System Configuration's \"Disable all\" does. It's meant for a one-off clean-boot test, but left like that it switches off antivirus, VPN, anti-cheat, audio and graphics helpers, printer and backup services.",
            WindowsDefault = "Each app's services start the way the app installed them.",
            Flags = BadTweakFlags.Admin | BadTweakFlags.Restart, Sources = Src(CleanBoot),
            Note = "PC Tidy can't tell what each service was set to before, so it doesn't guess. Windows' own services aren't counted here.",
            Signals = new[]
            {
                Custom(CustomProbeKind.ManyServicesDisabled, new GuidanceFix(
                    "If you used System Configuration (msconfig): open it, choose Normal startup on the General tab, then on the Services tab untick \"Hide all Microsoft services\", choose Enable all, and restart. "
                    + "Or switch back on just the services you need in PC Tidy's Services section.")),
            },
        };
        yield return new BadTweakCheck
        {
            Id = "services.hardware-access-drivers", Area = BadTweakArea.Services, Severity = BadTweakSeverity.Medium,
            Title = "A low-level hardware access driver is installed",
            WhyBad = "Tuning tools (RWEverything for USB \"IMOD\" tweaks, older fan-control and monitoring apps with WinRing0) install a driver that lets any program read and write the PC's memory and hardware. "
                     + "Malware uses these drivers too, and Microsoft Defender flags WinRing0. Anti-cheats may refuse to run.",
            WindowsDefault = "Not installed.",
            Flags = BadTweakFlags.Admin, Sources = Src(KnownBadDrivers, VulnerableDriverBlocklist),
            Note = "Newer versions of several fan-control and monitoring apps (Fan Control among them) no longer use WinRing0, so updating is often enough. Don't exclude the driver from your antivirus.",
            Signals = new[]
            {
                Custom(CustomProbeKind.HardwareAccessDrivers, new GuidanceFix(
                    "Update or uninstall the tool that installed it (Settings → Apps → Installed apps). For RWEverything, also remove any startup task or script that runs it.",
                    "ms-settings:appsfeatures")),
            },
        };
        yield return new BadTweakCheck
        {
            Id = "services.broken", Area = BadTweakArea.Services, Severity = BadTweakSeverity.Low,
            Title = "Services point at programs that are gone",
            WhyBad = "Left behind by uninstalled apps. Windows tries to start them and logs an error each time. Harmless but noisy.",
            WindowsDefault = "Removed by the app's uninstaller.", Sources = Src(PerUserServices),
            Signals = new[] { Custom(CustomProbeKind.BrokenServices, new GuidanceFix("Reinstall and properly uninstall the app that added it, or check the Apps section for its leftovers.")) },
        };
    }

    private static IEnumerable<BadTweakCheck> Storage()
    {
        yield return new BadTweakCheck
        {
            Id = "storage.trim-off", Area = BadTweakArea.Storage, Severity = BadTweakSeverity.Medium,
            Title = "TRIM is turned off",
            WhyBad = "Your SSD isn't told which blocks are free, so it slows down and wears faster over time.",
            WindowsDefault = "0 (TRIM on).", Flags = BadTweakFlags.Admin, Sources = Src(FsutilBehavior),
            Signals = new[] { Sig(Lm(@"SYSTEM\CurrentControlSet\Control\FileSystem", "DisableDeleteNotification", ValueTest.AnyOf, 1), SetTo(0)) },
        };
        yield return new BadTweakCheck
        {
            Id = "storage.pagefile-off", Area = BadTweakArea.Storage, Severity = BadTweakSeverity.High,
            Title = "The paging file is turned off",
            WhyBad = "Apps and games crash with \"out of memory\" even with plenty of RAM, and Windows can't save crash dumps.",
            WindowsDefault = "System managed (?:\\pagefile.sys).", Flags = BadTweakFlags.Admin | BadTweakFlags.Restart, Sources = Src(Pagefile),
            Signals = new[] { Sig(new RegistryProbe(RegistryHive.LocalMachine, MemoryManagement, "PagingFiles", ValueTest.Empty, Array.Empty<string>(), RegistryKind.MultiString),
                new RegistrySetFix(RegistryKind.MultiString, @"?:\pagefile.sys")) },
        };
        yield return new BadTweakCheck
        {
            Id = "storage.fixed-pagefile", Area = BadTweakArea.Storage, Severity = BadTweakSeverity.Low,
            Title = "The paging file has a fixed size",
            WhyBad = "Old guides say to fix it at 1.5 or 2 times your RAM. On a PC with lots of memory that wastes many gigabytes of disk, and if an app ever needs more, it crashes instead of Windows growing the file. Letting Windows manage it is right for almost everyone.",
            WindowsDefault = "Let Windows manage the paging file size (?:\\pagefile.sys).",
            Flags = BadTweakFlags.Admin | BadTweakFlags.Restart, Sources = Src(Pagefile),
            Note = "If you set a fixed size on purpose (a small drive, a specific app), keep it.",
            Signals = new[]
            {
                Custom(CustomProbeKind.FixedPagefile, new RegistryWriteFix(RegistryHive.LocalMachine, MemoryManagement, "PagingFiles", RegistryKind.MultiString, @"?:\pagefile.sys")),
            },
        };
        yield return new BadTweakCheck
        {
            Id = "storage.clear-pagefile-at-shutdown", Area = BadTweakArea.Storage, Severity = BadTweakSeverity.Low,
            Title = "The paging file is wiped at every shutdown",
            WhyBad = "Windows overwrites the whole paging file each time you shut down, which can add minutes to every shutdown. It only matters on a PC someone could take the drive out of, and BitLocker protects that far better.",
            WindowsDefault = "0 (off).",
            Flags = BadTweakFlags.Admin | BadTweakFlags.Restart, Sources = Src(ClearPagefileAtShutdown),
            Note = "On a work PC this can be a security rule set on purpose: ask your IT team first.",
            Signals = new[] { Sig(Lm(MemoryManagement, "ClearPageFileAtShutdown", ValueTest.AnyOf, 1), SetTo(0)) },
        };
        yield return new BadTweakCheck
        {
            Id = "storage.memory-compression-off", Area = BadTweakArea.Storage, Severity = BadTweakSeverity.Low,
            Title = "Memory compression looks switched off",
            WhyBad = "Windows squeezes memory that isn't being used instead of writing it to disk. Turning that off makes the PC use the paging file sooner and feel slower when memory gets full, for no gain in games.",
            WindowsDefault = "On.",
            Flags = BadTweakFlags.Admin | BadTweakFlags.Restart, Sources = Src(MemoryCompression),
            Note = "PC Tidy can't read this setting directly: it checks whether Windows' Memory Compression process is running, so this is a best guess.",
            Signals = new[]
            {
                Custom(CustomProbeKind.MemoryCompressionOff, new GuidanceFix("Open PowerShell as administrator, run: Enable-MMAgent -MemoryCompression, then restart.")),
            },
        };
        yield return new BadTweakCheck
        {
            Id = "storage.large-system-cache", Area = BadTweakArea.Storage, Severity = BadTweakSeverity.Medium,
            Title = "LargeSystemCache is on",
            WhyBad = "A file-server setting that takes memory from apps and games and has a history of driver trouble on desktops.",
            WindowsDefault = "0.", Flags = BadTweakFlags.Admin | BadTweakFlags.Restart, Sources = Src(PcTuning),
            Signals = new[] { Sig(Lm(MemoryManagement, "LargeSystemCache", ValueTest.AnyOf, 1), SetTo(0)) },
        };
        yield return new BadTweakCheck
        {
            Id = "storage.paging-executive", Area = BadTweakArea.Storage, Severity = BadTweakSeverity.Low,
            Title = "DisablePagingExecutive is on",
            WhyBad = "An old tweak that keeps kernel code in RAM; no measured benefit on current Windows.",
            WindowsDefault = "Not set.", Flags = BadTweakFlags.Admin | BadTweakFlags.Restart, Sources = Src(PcTuning),
            Signals = new[] { Sig(Lm(MemoryManagement, "DisablePagingExecutive", ValueTest.AnyOf, 1), Remove) },
        };
        yield return new BadTweakCheck
        {
            Id = "storage.prefetch-off", Area = BadTweakArea.Storage, Severity = BadTweakSeverity.Low,
            Title = "Prefetch is turned off",
            WhyBad = "Apps start slower, especially on hard drives. Windows already tunes Prefetch for SSDs.",
            WindowsDefault = "3.", Flags = BadTweakFlags.Admin | BadTweakFlags.Restart, Sources = Src(PcTuning),
            Signals = new[] { Sig(Lm(MemoryManagement + @"\PrefetchParameters", "EnablePrefetcher", ValueTest.AnyOf, 0), SetTo(3)) },
        };
        yield return new BadTweakCheck
        {
            Id = "storage.svchost-split", Area = BadTweakArea.Storage, Severity = BadTweakSeverity.Low,
            Title = "Service grouping was changed to \"reduce processes\"",
            WhyBad = "Makes Task Manager show fewer processes, but one crashing service then takes others down with it. No speed gain was shown.",
            WindowsDefault = "3670016.", Flags = BadTweakFlags.Admin | BadTweakFlags.Restart, Sources = Src(WinUtilTweaks),
            Signals = new[] { Sig(Lm(@"SYSTEM\CurrentControlSet\Control", "SvcHostSplitThresholdInKB", ValueTest.NoneOf, 3670016), SetTo(3670016)) },
        };
        yield return new BadTweakCheck
        {
            Id = "storage.optimize-schedule-off", Area = BadTweakArea.Storage, Severity = BadTweakSeverity.Medium,
            Title = "Optimize Drives no longer runs",
            WhyBad = "SSDs stop getting their weekly TRIM and hard drives stop getting defragmented.",
            WindowsDefault = "Enabled (weekly).", Flags = BadTweakFlags.Admin, Sources = Src(FsutilBehavior),
            Signals = new[] { TaskOff(@"\Microsoft\Windows\Defrag\ScheduledDefrag") },
        };
    }

    private static IEnumerable<BadTweakCheck> CpuAndGraphics()
    {
        yield return new BadTweakCheck
        {
            Id = "cpu.core-parking-off", Area = BadTweakArea.CpuPower, Severity = BadTweakSeverity.Medium,
            Title = "Core parking is forced off",
            WhyBad = "No measured gain on most CPUs, and on Ryzen X3D and Intel hybrid CPUs Windows needs core parking to put games on the right cores.",
            WindowsDefault = "Set by the power plan.", Flags = BadTweakFlags.Admin, Sources = Src(PcTuning),
            Signals = new[] { Custom(CustomProbeKind.CoreParking, new GuidanceFix("Control Panel → Power Options → Change plan settings → Restore default settings for this plan.")) },
        };
        var gpuSignals = new List<BadSignal>();
        for (var i = 0; i < GpuClassInstances; i++)
        {
            var key = $@"{DisplayClass}\{i:0000}";
            gpuSignals.Add(Sig(Lm(key, "PerfLevelSrc", ValueTest.Present), Remove));
            gpuSignals.Add(Sig(Lm(key, "DisableDynamicPstate", ValueTest.AnyOf, 1), Remove));
            gpuSignals.Add(Sig(Lm(key, "RMHdcpKeyglobZero", ValueTest.AnyOf, 1), Remove));
        }
        gpuSignals.Add(Sig(Lm(@"SYSTEM\CurrentControlSet\Services\nvlddmkm", "DisableWriteCombining", ValueTest.AnyOf, 1), Remove));
        yield return new BadTweakCheck
        {
            Id = "graphics.driver-hacks", Area = BadTweakArea.Graphics, Severity = BadTweakSeverity.Medium,
            Title = "Graphics driver values from tweak packs are set",
            WhyBad = "Locks the GPU at full clocks (heat, noise, power), slows memory writes, or zeroes HDCP keys so protected video (Netflix, Blu-ray) stops playing.",
            WindowsDefault = "Not set.", Flags = BadTweakFlags.Admin | BadTweakFlags.Restart, Sources = Src(PcTuning),
            Signals = gpuSignals,
        };
        yield return new BadTweakCheck
        {
            Id = "graphics.preemption-off", Area = BadTweakArea.Graphics, Severity = BadTweakSeverity.Low,
            Title = "GPU preemption is switched off",
            WhyBad = "A long GPU task can then freeze the screen until it finishes; no gain was measured.",
            WindowsDefault = "Not set.", Flags = BadTweakFlags.Admin | BadTweakFlags.Restart, Sources = Src(PcTuning),
            Signals = new[]
            {
                Sig(Lm(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers\Scheduler", "EnablePreemption", ValueTest.AnyOf, 0), Remove),
                Sig(Lm(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers\Scheduler", "DisablePreemption", ValueTest.AnyOf, 1), Remove),
            },
        };
        yield return new BadTweakCheck
        {
            Id = "graphics.mpo-test-mode", Area = BadTweakArea.Graphics, Severity = BadTweakSeverity.Low,
            Title = "Multi-plane overlay is switched off",
            WhyBad = "A flicker fix for some multi-monitor setups that costs a little efficiency. Newer NVIDIA drivers handle this themselves; keep it only if you added it for flicker.",
            WindowsDefault = "Not set.", Flags = BadTweakFlags.Admin | BadTweakFlags.Restart, Sources = Src(DirectXBlog),
            Signals = new[] { Sig(Lm(@"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode", ValueTest.AnyOf, 5), Remove) },
        };
    }

    /// <summary>Display-adapter class instances checked for GPU driver hacks (0000-0003 covers every common PC).</summary>
    internal const int GpuClassInstances = 4;

    private static IEnumerable<BadTweakCheck> GamesAndWindows()
    {
        yield return new BadTweakCheck
        {
            Id = "games.roblox-fastflags", Area = BadTweakArea.Games, Severity = BadTweakSeverity.Low,
            Title = "An old Roblox \"FPS pack\" file is installed",
            WhyBad = "Roblox now ignores flags that aren't on its allow-list, so these files do nothing (and some packs came with malware).",
            WindowsDefault = "No ClientAppSettings.json.", Sources = Src(RobloxFastFlags),
            Signals = new[] { Custom(CustomProbeKind.RobloxFastFlags, new QuarantineFix()) },
        };
        yield return new BadTweakCheck
        {
            Id = "windows.typing-task-disabled", Area = BadTweakArea.Windows, Severity = BadTweakSeverity.Medium,
            Title = "The text input task is disabled",
            WhyBad = "Typing in Start search, the emoji panel and input language switching can stop working.",
            WindowsDefault = "Enabled.", Flags = BadTweakFlags.Admin, Sources = Src(IotServices),
            Signals = new[] { TaskOff(@"\Microsoft\Windows\TextServicesFramework\MsCtfMonitor") },
        };
        yield return new BadTweakCheck
        {
            Id = "windows.time-sync-task-disabled", Area = BadTweakArea.Windows, Severity = BadTweakSeverity.Medium,
            Title = "Clock synchronization task is disabled",
            WhyBad = "The clock drifts; sign-ins, secure websites and anti-cheats start failing when it's off by a few minutes.",
            WindowsDefault = "Enabled.", Flags = BadTweakFlags.Admin, Sources = Src(IotServices),
            Signals = new[] { TaskOff(@"\Microsoft\Windows\Time Synchronization\SynchronizeTime") },
        };
        yield return new BadTweakCheck
        {
            Id = "windows.requirements-bypass", Area = BadTweakArea.Windows, Severity = BadTweakSeverity.Low,
            Title = "Windows 11 requirement checks were bypassed",
            WhyBad = "This PC may not meet Windows 11's requirements (TPM, Secure Boot, CPU). Microsoft doesn't recommend it, and anti-cheats like Vanguard need TPM 2.0 and Secure Boot.",
            WindowsDefault = "Not set.", Sources = Src(SystemRequirements),
            Note = "Information only: removing these keys wouldn't change the Windows already installed.",
            Signals = new[]
            {
                Sig(Lm(@"SYSTEM\Setup\LabConfig", "BypassTPMCheck", ValueTest.AnyOf, 1), new GuidanceFix("Nothing to fix now. Check TPM and Secure Boot in Windows Security → Device security.", "ms-settings:windowsdefender")),
                Sig(Lm(@"SYSTEM\Setup\LabConfig", "BypassSecureBootCheck", ValueTest.AnyOf, 1), new GuidanceFix("Nothing to fix now. Check TPM and Secure Boot in Windows Security → Device security.", "ms-settings:windowsdefender")),
                Sig(Lm(@"SYSTEM\Setup\MoSetup", "AllowUpgradesWithUnsupportedTPMOrCPU", ValueTest.AnyOf, 1), new GuidanceFix("Nothing to fix now. Check TPM and Secure Boot in Windows Security → Device security.", "ms-settings:windowsdefender")),
            },
        };
    }
}
