using PcTidy.Core.Model;
using static PcTidy.Catalog.Tweaks.TweakSources;

namespace PcTidy.Catalog.Tweaks;

public static partial class TweakCatalog
{
    /// <summary>
    /// Consumer Store apps that are safe to remove per user (Tweaks.md "Store apps, per user: proven for consumer apps").
    /// Never the Store, App Installer, frameworks, Edge/WebView2, Xbox/Game Bar components, Windows Security or shell hosts.
    /// </summary>
    public static readonly IReadOnlyList<(string Package, string Name)> ConsumerApps = new[]
    {
        ("Microsoft.BingNews", "Microsoft News"),
        ("Microsoft.BingWeather", "Weather"),
        ("Microsoft.BingSearch", "Bing Search"),
        ("Microsoft.GetHelp", "Get Help"),
        ("Microsoft.Getstarted", "Tips"),
        ("Microsoft.MicrosoftSolitaireCollection", "Solitaire Collection"),
        ("Microsoft.MicrosoftOfficeHub", "Microsoft 365 (Office) hub"),
        ("Microsoft.People", "People"),
        ("Microsoft.WindowsFeedbackHub", "Feedback Hub"),
        ("Microsoft.WindowsMaps", "Maps"),
        ("Microsoft.Todos", "Microsoft To Do"),
        ("Microsoft.PowerAutomateDesktop", "Power Automate"),
        ("Clipchamp.Clipchamp", "Clipchamp"),
        ("MicrosoftCorporationII.MicrosoftFamily", "Microsoft Family"),
        ("Microsoft.MixedReality.Portal", "Mixed Reality Portal"),
    };

    private static IEnumerable<TweakDefinition> Debloat()
    {
        foreach (var (package, name) in ConsumerApps)
        {
            yield return new TweakDefinition
            {
                Id = "debloat.app." + package.ToLowerInvariant(), Group = TweakGroup.Debloat, Title = $"Remove {name}",
                Effect = $"Uninstalls the {name} app for your account. Windows doesn't need it.",
                Verdict = TweakVerdict.Proven, Sources = Src(RemoveAppx),
                Caveat = "A big Windows feature update can bring it back. Reinstall it from the Microsoft Store if you miss it.",
                Changes = Do(new AppxRemoveChange(package)),
                Undo = "Reinstall it from the Microsoft Store (PC Tidy can't put a removed app back by itself).",
                Reversible = false,
            };
        }
        yield return new TweakDefinition
        {
            Id = "debloat.provisioned-apps", Group = TweakGroup.Debloat, Title = "Remove apps for new accounts too",
            Effect = "Removing an app's provisioned copy stops it being installed for new user accounts and frees its files.",
            Verdict = TweakVerdict.Situational, Risk = TweakRisk.Medium, Flags = TweakFlags.ExpertOnly | TweakFlags.Admin, Sources = Src(RemoveAppx),
            Caveat = "Not available yet: PC Tidy only removes apps for your own account, which is easy to reverse.",
            Changes = Do(new GuidanceChange("Remove-AppxProvisionedPackage -Online (all new accounts). Undo: install the app from the Store.")),
        };
        yield return new TweakDefinition
        {
            Id = "debloat.onedrive", Group = TweakGroup.Debloat, Title = "Uninstall OneDrive",
            Effect = "Only after checking that no folders are backed up to OneDrive and nothing is online-only, or files can seem to vanish.",
            Verdict = TweakVerdict.Situational, Risk = TweakRisk.Medium, Sources = Src(RemoveAppx),
            Changes = Do(new GuidanceChange("Done from the Apps section, which checks folder backup and online-only files first.")),
            Undo = "Reinstall OneDrive from microsoft.com.",
        };

        yield return Harmful("debloat.core-apps", "Remove the Store, winget, frameworks, Windows Security or shell hosts", "Breaks updates, sign-in and Start.",
            Src(RemoveAppx), new AppxRemoveChange("Microsoft.WindowsStore"));
        yield return Harmful("debloat.edge-webview2", "Remove Edge or WebView2", "Breaks Widgets, the new Outlook, Teams and many other apps.",
            Src(ManageConnections), new AppxRemoveChange("Microsoft.MicrosoftEdge"));
        yield return Harmful("debloat.xbox", "Remove Xbox services and apps", "Breaks Game Pass, Store games, cloud saves, Xbox sign-in and controller firmware, and Game Bar steers games on Ryzen X3D CPUs.",
            Src(IotServices), new ServiceStartChange("XblAuthManager", ServiceStart.Disabled, ServiceStart.Manual));
    }

    private static IEnumerable<TweakDefinition> UiExplorer()
    {
        const string desktop = @"Control Panel\Desktop";
        var win11 = TweakApplicability.Windows11;

        yield return new TweakDefinition
        {
            Id = "ui.file-extensions", Group = TweakGroup.UiExplorer, Title = "Show file extensions",
            Effect = "Shows \".pdf\", \".exe\" and so on after file names, which also unmasks tricks like \"invoice.pdf.exe\".",
            Verdict = TweakVerdict.Proven, Flags = TweakFlags.RestartExplorer, Sources = Src(FileExplorerHelp),
            Changes = Do(Cu(Adv, "HideFileExt", 0, 1)),
            Preset = TweakPreset.CleanExplorer, PresetDefault = true,
        };
        yield return new TweakDefinition
        {
            Id = "ui.hidden-files", Group = TweakGroup.UiExplorer, Title = "Show hidden files",
            Effect = "Shows hidden files and folders in Explorer. Protected Windows system files stay hidden.",
            Verdict = TweakVerdict.Proven, Flags = TweakFlags.RestartExplorer, Sources = Src(FileExplorerHelp),
            Changes = Do(Cu(Adv, "Hidden", 1, 2)),
            Preset = TweakPreset.CleanExplorer, PresetDefault = true,
        };
        yield return new TweakDefinition
        {
            Id = "ui.open-this-pc", Group = TweakGroup.UiExplorer, Title = "Open Explorer to This PC",
            Effect = "File Explorer opens on This PC (your drives) instead of Home.",
            Verdict = TweakVerdict.Proven, Sources = Src(FileExplorerHelp),
            Changes = Do(Cu(Adv, "LaunchTo", 1, null)),
            Preset = TweakPreset.CleanExplorer, PresetDefault = true,
        };
        yield return new TweakDefinition
        {
            Id = "ui.end-task", Group = TweakGroup.UiExplorer, Title = "End task from the taskbar",
            Effect = "Adds \"End task\" to a taskbar button's right-click menu, so a frozen app closes without Task Manager.",
            Verdict = TweakVerdict.Proven, Sources = Src(TaskbarHelp),
            Applies = new TweakApplicability { MinBuild = 22631 },
            Changes = Do(Cu(Adv + @"\TaskbarDeveloperSettings", "TaskbarEndTask", 1, 0)),
            Preset = TweakPreset.CleanExplorer, PresetDefault = true,
        };
        yield return new TweakDefinition
        {
            Id = "ui.classic-context-menu", Group = TweakGroup.UiExplorer, Title = "Classic right-click menu",
            Effect = "Brings back the full right-click menu without \"Show more options\". Shift+right-click does the same without any change.",
            Verdict = TweakVerdict.Proven, Flags = TweakFlags.RestartExplorer, Sources = Src(FileExplorerHelp),
            Caveat = "Unsupported by Microsoft and may stop working in a future Windows build.",
            Applies = win11,
            Changes = Do(CuText(@"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32", "", "", null)),
            Undo = "One-click undo removes the key PC Tidy created.",
            Preset = TweakPreset.CleanExplorer, PresetDefault = false,
        };
        yield return new TweakDefinition
        {
            Id = "ui.taskbar-left", Group = TweakGroup.UiExplorer, Title = "Taskbar on the left",
            Effect = "Moves the Start button and taskbar icons to the left, like Windows 10.",
            Verdict = TweakVerdict.Proven, Sources = Src(TaskbarHelp), Caveat = "A preference.", Applies = win11,
            Changes = Do(Cu(Adv, "TaskbarAl", 0, 1)),
            Preset = TweakPreset.CleanExplorer, PresetDefault = false,
        };
        yield return new TweakDefinition
        {
            Id = "ui.widgets-button-off", Group = TweakGroup.UiExplorer, Title = "Hide the Widgets button",
            Effect = "Removes the weather/news Widgets button from the taskbar. Widgets still opens with Win+W.",
            Verdict = TweakVerdict.Proven, Sources = Src(TaskbarHelp),
            Caveat = "On some builds Windows protects this switch; if it doesn't stick, PC Tidy says so and you can use Settings → Personalization → Taskbar.",
            Applies = win11,
            Changes = Do(Cu(Adv, "TaskbarDa", 0, 1)),
            Preset = TweakPreset.CleanExplorer, PresetDefault = false,
        };
        yield return new TweakDefinition
        {
            Id = "ui.search-icon-only", Group = TweakGroup.UiExplorer, Title = "Search as an icon only",
            Effect = "Shows a small search icon on the taskbar instead of the wide search box.",
            Verdict = TweakVerdict.Proven, Sources = Src(TaskbarHelp), Caveat = "A preference.",
            Changes = Do(Cu(@"Software\Microsoft\Windows\CurrentVersion\Search", "SearchboxTaskbarMode", 1, 2)),
            Preset = TweakPreset.CleanExplorer, PresetDefault = false,
        };
        yield return new TweakDefinition
        {
            Id = "ui.compact-mode", Group = TweakGroup.UiExplorer, Title = "Compact Explorer view",
            Effect = "Less space between items in File Explorer, so more files fit on screen.",
            Verdict = TweakVerdict.Proven, Flags = TweakFlags.RestartExplorer, Sources = Src(FileExplorerHelp), Caveat = "A preference.", Applies = win11,
            Changes = Do(Cu(Adv, "UseCompactMode", 1, 0)),
            Preset = TweakPreset.CleanExplorer, PresetDefault = false,
        };
        yield return new TweakDefinition
        {
            Id = "ui.menu-show-delay", Group = TweakGroup.UiExplorer, Title = "Faster classic menus",
            Effect = "Classic submenus open after 0.1 s instead of 0.4 s. A small, real difference in old-style menus only.",
            Verdict = TweakVerdict.Proven, Flags = TweakFlags.SignOut, Sources = Src(FileExplorerHelp),
            Changes = Do(CuText(desktop, "MenuShowDelay", "100", "400")),
        };
        yield return new TweakDefinition
        {
            Id = "ui.snap-flyouts-off", Group = TweakGroup.UiExplorer, Title = "Turn off Snap suggestions",
            Effect = "Stops the snap layout pop-up on the maximize button and the \"what to snap next\" suggestions. Win+arrow snapping still works.",
            Verdict = TweakVerdict.Proven, Sources = Src(TaskbarHelp), Caveat = "A preference.", Applies = win11,
            Changes = Do(Cu(Adv, "EnableSnapAssistFlyout", 0, 1), Cu(Adv, "SnapAssist", 0, 1), Cu(Adv, "EnableSnapBar", 0, 1)),
        };

        yield return Placebo("ui.chat-button", TweakGroup.UiExplorer, "Hide the Chat button",
            "Chat was removed from the taskbar in Windows 11 23H2, so this value no longer does anything.",
            Src(TaskbarHelp), Cu(Adv, "TaskbarMn", 0, null));
    }

    private static IEnumerable<TweakDefinition> Network()
    {
        yield return new TweakDefinition
        {
            Id = "network.delivery-optimization-no-upload", Group = TweakGroup.Network, Title = "Don't upload updates to other PCs",
            Effect = "Windows Update stops sharing downloaded update pieces with other PCs, saving upload bandwidth. Downloads keep working. (Turning the Delivery Optimization service off would break updates; this doesn't.)",
            Verdict = TweakVerdict.Proven, Flags = TweakFlags.Admin | TweakFlags.ManagedPolicy, Sources = Src(DeliveryOptimization),
            Changes = Do(Lm(@"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 0, null)),
        };
        yield return new TweakDefinition
        {
            Id = "network.smb1-off", Group = TweakGroup.Network, Title = "Turn off SMB1",
            Effect = "Removes the 1980s file-sharing protocol that WannaCry used. Only very old NAS boxes and printers still need it.",
            Verdict = TweakVerdict.Proven, Flags = TweakFlags.Admin | TweakFlags.Restart, Sources = Src(Smb1),
            Caveat = "If an old network drive stops showing up, turn it back on with undo.",
            Changes = Do(new OptionalFeatureChange("SMB1Protocol")),
            Undo = "One-click undo turns the SMB1 feature back on.",
        };
        yield return new TweakDefinition
        {
            Id = "network.prefer-ipv4", Group = TweakGroup.Network, Title = "Prefer IPv4 over IPv6",
            Effect = "Keeps IPv6 working but tries IPv4 first. Microsoft's supported alternative to switching IPv6 off.",
            Verdict = TweakVerdict.Situational, Risk = TweakRisk.Medium, Flags = TweakFlags.ExpertOnly | TweakFlags.Admin | TweakFlags.Restart, Sources = Src(Ipv6),
            Caveat = "Only helps with a broken IPv6 network. Turning IPv6 fully off (0xFF) breaks Xbox party chat and some VPNs, so PC Tidy never does that.",
            Changes = Do(Lm(@"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters", "DisabledComponents", 0x20, null)),
        };
        yield return new TweakDefinition
        {
            Id = "network.adapter-power-saving", Group = TweakGroup.Network, Title = "Network adapter power saving off",
            Effect = "Fixes Wi-Fi or Ethernet links that drop after idle: untick \"Allow the computer to turn off this device to save power\" and set Wi-Fi power saving to Maximum Performance.",
            Verdict = TweakVerdict.Situational, Sources = Src(NetworkTuning),
            Caveat = "Only if your connection drops. These settings live on your adapter's own device page.",
            Changes = Do(new GuidanceChange("Device Manager → Network adapters → your adapter → Power Management.")),
            Undo = "Nothing to undo: PC Tidy changes nothing here.",
        };
        yield return new TweakDefinition
        {
            Id = "network.dns", Group = TweakGroup.Network, Title = "Change DNS server",
            Effect = "A faster DNS server can speed up the first visit to a site (and DNS over HTTPS adds privacy), but it does nothing for ping and can break work networks.",
            Verdict = TweakVerdict.Situational, Sources = Src(NetworkTuning),
            Changes = Do(new GuidanceChange("Settings → Network & internet → your connection → DNS server assignment.", "ms-settings:network-advancedsettings")),
            Undo = "Set DNS back to Automatic (DHCP) in Settings.",
        };

        yield return Placebo("network.nagle", TweakGroup.Network, "Nagle off (TcpAckFrequency / TCPNoDelay)",
            "Only affects TCP and most games use UDP; Windows no longer reads TCPNoDelay.",
            Src(NetworkTuning), Lm(Tcpip + @"\Interfaces\{adapter}", "TcpAckFrequency", 1, null));
        yield return Placebo("network.qos-reserve", TweakGroup.Network, "QoS \"20% reserved bandwidth\"",
            "A myth: Windows reserves nothing unless an app asks for it.",
            Src(NetworkTuning), Lm(@"SOFTWARE\Policies\Microsoft\Windows\Psched", "NonBestEffortLimit", 0, null));
        yield return Placebo("network.tcp-window-values", TweakGroup.Network, "TCP window registry values",
            "TcpWindowSize, NumTcbTablePartitions and MaxHashTableSize are ignored by modern Windows, which tunes the window itself.",
            Src(NetworkTuning), Lm(Tcpip, "TcpWindowSize", 65535, null));

        yield return Harmful("network.autotuning-off", "TCP receive autotuning off", "Caps the receive window, so big downloads get slow.",
            Src(NetworkTuning), new CommandChange("netsh int tcp set global autotuninglevel=disabled"));
        yield return Harmful("network.ipv6-off", "Disable IPv6 completely", "Microsoft says don't: breaks Xbox party chat, Teredo and some VPNs.",
            Src(Ipv6), Lm(@"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters", "DisabledComponents", 0xFF, null));
    }

    private static IEnumerable<TweakDefinition> Power()
    {
        yield return new TweakDefinition
        {
            Id = "power.fast-startup-off", Group = TweakGroup.Power, Title = "Turn off Fast Startup",
            Effect = "Shut down becomes a full shutdown: fixes dual-boot drive locking, drivers that get stuck and Wake-on-LAN, at the cost of a slower boot.",
            Verdict = TweakVerdict.Situational, Flags = TweakFlags.Admin, Sources = Src(FastStartup),
            Caveat = "Only worth it if you have one of those problems.",
            Changes = Do(Lm(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", 0, 1)),
        };
        yield return new TweakDefinition
        {
            Id = "power.hibernation", Group = TweakGroup.Power, Title = "Hibernation file size",
            Effect = "On a desktop, \"Reduced\" shrinks hiberfil.sys by about half and keeps Fast Startup.",
            Verdict = TweakVerdict.Situational, Sources = Src(Hibernation),
            Changes = Do(new GuidanceChange("Done in Clean → Big hidden Windows files, which shows the file's size.")),
            Undo = "Undo it from History after changing it in Clean.",
        };
        yield return new TweakDefinition
        {
            Id = "power.power-mode", Group = TweakGroup.Power, Title = "Windows power mode",
            Effect = "Settings → Power → Power mode \"Best performance\" is Windows' own supported way to favour speed over battery.",
            Verdict = TweakVerdict.Situational, Sources = Src(PowerMode),
            Changes = Do(new GuidanceChange("Settings → System → Power & battery → Power mode.", "ms-settings:powersleep")),
            Undo = "Switch the mode back in Settings.",
        };
        yield return new TweakDefinition
        {
            Id = "power.usb-selective-suspend-off", Group = TweakGroup.Power, Title = "USB selective suspend off",
            Effect = "Fixes USB audio interfaces and other devices that drop out after a while.",
            Verdict = TweakVerdict.Situational, Flags = TweakFlags.ExpertOnly | TweakFlags.Admin, Sources = Src(PcTuning),
            Caveat = "Only for dropouts. More power use on laptops.",
            Changes = Do(new GuidanceChange("Control Panel → Power Options → Change plan settings → Advanced → USB settings → USB selective suspend: Disabled.")),
            Undo = "Set it back to Enabled in the same place.",
        };
        // FPS research §4.5: PCIe ASPM off is PLACEBO (no test found). It used to be an Expert guidance row; it is now a shelf row.
        yield return Placebo("power.pcie-aspm-off", TweakGroup.Power, "PCI Express link power saving off",
            "No test found that turning link power saving off helps games, and it uses more power. Keep it only if a card has wake problems.",
            Src(PcTuning), new GuidanceChange("Control Panel → Power Options → Advanced → PCI Express → Link State Power Management: Off."));

        yield return Placebo("power.ultimate-performance", TweakGroup.Power, "Ultimate Performance power plan",
            "Uses more power. In one test 3 of 4 games stayed within noise; Fortnite's +6.9% on unnamed hardware was not repeated.",
            Src(PcTuning), new PowerPlanChange(new Guid("e9a42b02-d5df-448d-aa00-03f14749eb61"), "Ultimate Performance"));
        yield return new TweakDefinition
        {
            Id = "power.core-parking-off", Group = TweakGroup.Power, Title = "Core parking off",
            Effect = "No measured gain on most CPUs.", Caveat = "The only gains were AMD's own 2017 numbers for first-generation Ryzen on Windows 10. On Ryzen X3D and Intel hybrid CPUs Windows needs core parking to put games on the right cores.",
            Verdict = TweakVerdict.Placebo, Sources = Src(PcTuning),
            Applies = new TweakApplicability { HarmfulOn = CpuKind.DualCcdX3D | CpuKind.Hybrid },
            Changes = Do(new CommandChange("powercfg -setacvalueindex scheme_current sub_processor CPMINCORES 100")),
            Undo = "Not offered, so there's nothing to undo.",
        };
    }
}
