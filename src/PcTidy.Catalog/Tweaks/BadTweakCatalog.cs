using System.Globalization;
using PcTidy.Core.Model;
using static PcTidy.Catalog.Tweaks.TweakSources;

namespace PcTidy.Catalog.Tweaks;

/// <summary>
/// The bad-tweak finder's checks (Community tweaks §3 table + Tweaks.md rule 5 + Plan §8 "Find bad tweaks").
/// Everything here is harmful or useless and is only ever RESET to the Windows default, never applied.
/// Service defaults were cross-checked read-only against a current Windows 11 build (Community tweaks T6 asked for it).
/// </summary>
public static partial class BadTweakCatalog
{
    private static readonly Lazy<IReadOnlyList<BadTweakCheck>> _all = new(() =>
        Security().Concat(MoreSecurity()).Concat(WindowsUpdate()).Concat(UpdatePauses()).Concat(Boot()).Concat(SafeBoot())
            .Concat(Scheduler()).Concat(Network()).Concat(Services()).Concat(Storage()).Concat(CpuAndGraphics())
            .Concat(GamesAndWindows()).Concat(DevicesAndPower()).Concat(Policies()).ToList());

    public static IReadOnlyList<BadTweakCheck> All => _all.Value;

    public static BadTweakCheck? Find(string id) => All.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Boot values BitLocker watches (Community tweaks §1 item 1). Removing them needs the BitLocker warning.</summary>
    public static readonly IReadOnlySet<string> BitLockerWatchedBootValues =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "nx", "nointegritychecks", "testsigning", "debug", "bootdebug" };

    /// <summary>
    /// Boot values whose removal the platform can't undo: it has no BCD element type it can vouch for to read the old
    /// value back (Platform's SystemToolCatalog.BcdRemovable maps them to null). Their removal is marked permanent, so the
    /// confirm screen says so before the user agrees. A test keeps this list and the platform's in step.
    /// </summary>
    public static readonly IReadOnlySet<string> NotUndoableBootValues =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "x2apicpolicy", "uselegacyapicmode" };

    /// <summary>True when removing this boot value can be undone from History.</summary>
    public static bool IsBootRemovalUndoable(string name) => !NotUndoableBootValues.Contains(name);

    /// <summary>The GUID of the {current} boot entry (a fixed alias in every BCD store).</summary>
    public const string CurrentBootEntry = "{fa926493-6f1c-4193-a414-58f0b2456d1e}";

    internal const string Defender = @"SOFTWARE\Policies\Microsoft\Windows Defender";
    internal const string WindowsUpdatePolicy = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
    internal const string MemoryManagement = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
    internal const string Tcpip = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters";
    internal const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    // ------------------------------------------------------------------ helpers

    internal static string Dec(long v) => unchecked((uint)v).ToString(CultureInfo.InvariantCulture);

    internal static BadSignal Sig(BadProbe probe, params BadFix[] fixes) => new(probe, fixes);

    internal static RegistryProbe Lm(string key, string name, ValueTest test, params long[] values)
        => new(RegistryHive.LocalMachine, key, name, test, values.Select(Dec).ToList());

    internal static RegistryProbe Cu(string key, string name, ValueTest test, params long[] values)
        => new(RegistryHive.CurrentUser, key, name, test, values.Select(Dec).ToList());

    internal static RegistryProbe LmText(string key, string name, ValueTest test, params string[] values)
        => new(RegistryHive.LocalMachine, key, name, test, values, RegistryKind.String);

    internal static RegistryDeleteFix Remove => new();
    internal static RegistrySetFix SetTo(long v) => new(RegistryKind.DWord, Dec(v));

    internal static BadSignal SvcOff(string service, ServiceStart windowsDefault) => Sig(new ServiceDisabledProbe(service), new ServiceStartFix(windowsDefault));
    internal static BadSignal BootOn(string name, uint type) => Sig(new BootValueProbe(name, type, BootTest.IsTrue), new BootDeleteFix());
    internal static BadSignal BootSet(string name, uint type) => Sig(new BootValueProbe(name, type, BootTest.Present), new BootDeleteFix());
    internal static BadSignal TaskOff(string path) => Sig(new TaskDisabledProbe(path), new TaskEnableFix());
    internal static BadSignal Custom(string kind, params BadFix[] fixes) => Sig(new CustomProbe(kind), fixes);
    internal static IReadOnlyList<string> Src(params string[] urls) => urls;

    // ------------------------------------------------------------------ security

    private static IEnumerable<BadTweakCheck> Security()
    {
        yield return new BadTweakCheck
        {
            Id = "security.defender-off", Area = BadTweakArea.Security, Severity = BadTweakSeverity.High,
            Title = "Microsoft Defender is turned off by a policy",
            WhyBad = "Without Defender (or another antivirus) nothing checks the files you download or run. \"FPS boost\" guides and fake optimizers turn it off first, often so their own download isn't caught. "
                     + "Some sell a Defender policy as \"only while you play\", but a policy stays until someone removes it.",
            WindowsDefault = "No Defender policies; the Microsoft Defender Antivirus service (WinDefend) starts automatically.",
            Flags = BadTweakFlags.Admin | BadTweakFlags.Restart, Sources = Src(DefenderDisableAntiSpyware, DefenderTamperProtection),
            Note = "If Windows refuses to change the service (Tamper Protection guards it), PC Tidy says so: after the restart, open Windows Security and turn on Real-time protection.",
            Signals = new[]
            {
                Sig(Lm(Defender, "DisableAntiSpyware", ValueTest.AnyOf, 1), Remove),
                Sig(Lm(Defender, "DisableAntiVirus", ValueTest.AnyOf, 1), Remove),
                Sig(Lm(Defender, "DisableRoutinelyTakingAction", ValueTest.AnyOf, 1), Remove),
                Sig(Lm(Defender + @"\Real-Time Protection", "DisableRealtimeMonitoring", ValueTest.AnyOf, 1), Remove),
                Sig(Lm(Defender + @"\Real-Time Protection", "DisableBehaviorMonitoring", ValueTest.AnyOf, 1), Remove),
                Sig(Lm(Defender + @"\Real-Time Protection", "DisableOnAccessProtection", ValueTest.AnyOf, 1), Remove),
                Sig(Lm(Defender + @"\Real-Time Protection", "DisableScanOnRealtimeEnable", ValueTest.AnyOf, 1), Remove),
                Sig(Lm(Defender + @"\Real-Time Protection", "DisableIOAVProtection", ValueTest.AnyOf, 1), Remove),
                Sig(Lm(Defender + @"\Spynet", "SpynetReporting", ValueTest.AnyOf, 0), Remove),
                Sig(Lm(Defender + @"\Spynet", "SubmitSamplesConsent", ValueTest.AnyOf, 2), Remove),
                SvcOff("WinDefend", ServiceStart.Automatic),
                SvcOff("WdNisSvc", ServiceStart.Manual),
            },
        };
        yield return new BadTweakCheck
        {
            // Its own check (DEF-1): an exclusion or a single switched-off feature doesn't mean "Defender is off", and
            // policies that ADD protection are never offered for removal.
            Id = "security.defender-weakened", Area = BadTweakArea.Security, Severity = BadTweakSeverity.Medium,
            Title = "Defender policies switch parts of its protection off",
            WhyBad = "These policies exclude files, folders or programs from Defender's scans, or switch single protections off (script scanning, cloud protection, sending suspicious files). Tweak guides add them so their own tools aren't caught. "
                     + "Policies that add protection, such as attack surface reduction rules, Controlled Folder Access, network protection or blocking unwanted apps, are kept.",
            WindowsDefault = "None of these policies: every Defender protection is on and nothing is excluded.",
            Flags = BadTweakFlags.Admin, Sources = Src(DefenderPolicySettings, DefenderTamperProtection),
            Note = "On a work or school PC your organisation may have set these on purpose: ask IT before removing them.",
            Signals = new[]
            {
                // Values under the Defender policy key the check above doesn't name, sorted by DefenderPolicyEffects.
                Custom(CustomProbeKind.DefenderPolicyLeftovers, new RegistryDeleteFoundFix()),
            },
        };
        yield return new BadTweakCheck
        {
            Id = "security.defender-protection-reduced", Area = BadTweakArea.Security, Severity = BadTweakSeverity.Medium,
            Title = "Tamper Protection or cloud protection is off",
            WhyBad = "Tamper Protection stops other programs switching Defender off behind your back. Cloud protection lets Defender check new files against Microsoft's latest information in seconds. Tweak guides turn both off; neither costs you speed.",
            WindowsDefault = "Both on.",
            Sources = Src(DefenderTamperProtection, DefenderCloudProtection),
            Note = "Tamper Protection can only be switched in Windows Security itself: no app can change it, PC Tidy included. PC Tidy opens the page for you.",
            Signals = new[]
            {
                Custom(CustomProbeKind.DefenderProtectionReduced, new GuidanceFix(
                    "Open Windows Security → Virus & threat protection → Manage settings, then turn on Cloud-delivered protection and Tamper Protection.",
                    "ms-settings:windowsdefender")),
            },
        };
        yield return new BadTweakCheck
        {
            Id = "security.security-center-off", Area = BadTweakArea.Security, Severity = BadTweakSeverity.High,
            Title = "Windows Security's status services are disabled",
            WhyBad = "Windows can no longer tell you when protection is off or out of date, and the Windows Security app stops working.",
            WindowsDefault = "Security Center: Automatic (delayed start). Windows Security Service: Manual.",
            Flags = BadTweakFlags.Admin | BadTweakFlags.Restart, Sources = Src(DefenderTamperProtection),
            Signals = new[] { SvcOff("wscsvc", ServiceStart.AutomaticDelayed), SvcOff("SecurityHealthService", ServiceStart.Manual) },
        };
        yield return new BadTweakCheck
        {
            Id = "security.defender-notifications-hidden", Area = BadTweakArea.Security, Severity = BadTweakSeverity.Medium,
            Title = "Windows Security warnings are hidden",
            WhyBad = "You won't see it when Windows finds a threat or when protection is turned off.",
            WindowsDefault = "Not set (warnings shown).",
            Flags = BadTweakFlags.Admin, Sources = Src(DefenderTamperProtection),
            Signals = new[]
            {
                Sig(Lm(@"SOFTWARE\Policies\Microsoft\Windows Defender Security Center\Notifications", "DisableNotifications", ValueTest.AnyOf, 1), Remove),
                Sig(Lm(@"SOFTWARE\Policies\Microsoft\Windows Defender Security Center\Systray", "HideSystray", ValueTest.AnyOf, 1), Remove),
            },
        };
        yield return new BadTweakCheck
        {
            Id = "security.smartscreen-off", Area = BadTweakArea.Security, Severity = BadTweakSeverity.High,
            Title = "SmartScreen is turned off",
            WhyBad = "SmartScreen warns before you run a downloaded program or open a site known to spread malware.",
            WindowsDefault = "Not set (SmartScreen on, \"Warn\").",
            Flags = BadTweakFlags.Admin, Sources = Src(SmartScreen),
            Signals = new[]
            {
                Sig(Lm(@"SOFTWARE\Policies\Microsoft\Windows\System", "EnableSmartScreen", ValueTest.AnyOf, 0), Remove),
                Sig(LmText(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer", "SmartScreenEnabled", ValueTest.AnyOf, "Off"), new RegistryDeleteFix(Kind: RegistryKind.String)),
                Sig(Cu(@"Software\Microsoft\Windows\CurrentVersion\AppHost", "EnableWebContentEvaluation", ValueTest.AnyOf, 0), Remove),
                Sig(Lm(@"SOFTWARE\Policies\Microsoft\Edge", "SmartScreenEnabled", ValueTest.AnyOf, 0), Remove),
                Sig(Lm(@"SOFTWARE\Policies\Microsoft\MicrosoftEdge\PhishingFilter", "EnabledV9", ValueTest.AnyOf, 0), Remove),
            },
        };
        yield return new BadTweakCheck
        {
            Id = "security.uac-off", Area = BadTweakArea.Security, Severity = BadTweakSeverity.High,
            Title = "User Account Control is off or never asks",
            WhyBad = "Any program can change Windows silently with full admin rights. With UAC fully off, Store apps also stop working.",
            WindowsDefault = "EnableLUA = 1, ConsentPromptBehaviorAdmin = 5, PromptOnSecureDesktop = 1.",
            Flags = BadTweakFlags.Admin | BadTweakFlags.Restart, Sources = Src(Uac),
            Signals = new[]
            {
                Sig(Lm(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA", ValueTest.AnyOf, 0), SetTo(1)),
                Sig(Lm(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "ConsentPromptBehaviorAdmin", ValueTest.AnyOf, 0), SetTo(5)),
                Sig(Lm(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "PromptOnSecureDesktop", ValueTest.AnyOf, 0), SetTo(1)),
            },
        };
        const string fwPolicy = @"SOFTWARE\Policies\Microsoft\WindowsFirewall";
        const string fwLocal = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy";
        yield return new BadTweakCheck
        {
            Id = "security.firewall-off", Area = BadTweakArea.Security, Severity = BadTweakSeverity.High,
            Title = "Windows Firewall is turned off",
            WhyBad = "Every program and service on this PC can be reached from the network, including on public Wi-Fi.",
            WindowsDefault = "Firewall on for all three network types; Windows Defender Firewall service Automatic.",
            Flags = BadTweakFlags.Admin, Sources = Src(Firewall),
            Signals = new[]
            {
                Sig(Lm(fwPolicy + @"\DomainProfile", "EnableFirewall", ValueTest.AnyOf, 0), Remove),
                Sig(Lm(fwPolicy + @"\StandardProfile", "EnableFirewall", ValueTest.AnyOf, 0), Remove),
                Sig(Lm(fwPolicy + @"\PublicProfile", "EnableFirewall", ValueTest.AnyOf, 0), Remove),
                Sig(Lm(fwLocal + @"\DomainProfile", "EnableFirewall", ValueTest.AnyOf, 0), SetTo(1)),
                Sig(Lm(fwLocal + @"\StandardProfile", "EnableFirewall", ValueTest.AnyOf, 0), SetTo(1)),
                Sig(Lm(fwLocal + @"\PublicProfile", "EnableFirewall", ValueTest.AnyOf, 0), SetTo(1)),
                SvcOff("mpssvc", ServiceStart.Automatic),
                SvcOff("BFE", ServiceStart.Automatic),
            },
        };
        yield return new BadTweakCheck
        {
            Id = "security.spectre-mitigations-off", Area = BadTweakArea.Security, Severity = BadTweakSeverity.High,
            Title = "Spectre/Meltdown protections are switched off",
            WhyBad = "Removes CPU security fixes for a gain no independent test confirmed. Some anti-cheats (FACEIT) check for it.",
            WindowsDefault = "Not set (protections on).",
            Flags = BadTweakFlags.Admin | BadTweakFlags.Restart, Sources = Src(SpectreGuidance),
            Signals = new[]
            {
                // Bits 1 and 2 of FeatureSettingsOverride switch protections OFF; other values (e.g. 72) switch extra protections ON and are left alone.
                Sig(Lm(MemoryManagement, "FeatureSettingsOverride", ValueTest.AnyBits, 3), Remove, new RegistryDeleteFix(ValueName: "FeatureSettingsOverrideMask")),
            },
        };
        yield return new BadTweakCheck
        {
            Id = "security.dep-off", Area = BadTweakArea.Security, Severity = BadTweakSeverity.High,
            Title = "Data Execution Prevention is off (nx AlwaysOff)",
            WhyBad = "A basic exploit protection with no speed cost is switched off for every program.",
            WindowsDefault = "OptIn (not set).",
            Flags = BadTweakFlags.Admin | BadTweakFlags.Restart | BadTweakFlags.BitLocker, Sources = Src(Bcdedit, BitLockerBcd),
            // Guidance, not a removal: the safety rules never let PC Tidy delete "nx" (on a PC set to AlwaysOn that would lower DEP).
            Signals = new[] { Sig(new BootValueProbe("nx", 0x25000020, BootTest.IntegerEquals, 2),
                new GuidanceFix("Have your BitLocker recovery key ready, then in an admin terminal run: bcdedit /set {current} nx OptIn, and restart.")) },
        };
        yield return new BadTweakCheck
        {
            Id = "security.test-signing", Area = BadTweakArea.Security, Severity = BadTweakSeverity.High,
            Title = "Test signing is on",
            WhyBad = "Windows loads test-signed drivers, so anyone with admin rights can load kernel code. Easy Anti-Cheat, BattlEye and Vanguard refuse to run.",
            WindowsDefault = "Off (not set).",
            Flags = BadTweakFlags.Admin | BadTweakFlags.Restart | BadTweakFlags.BitLocker, Sources = Src(Bcdedit, BitLockerBcd),
            Signals = new[] { BootOn("testsigning", 0x16000049) },
        };
        yield return new BadTweakCheck
        {
            Id = "security.no-integrity-checks", Area = BadTweakArea.Security, Severity = BadTweakSeverity.High,
            Title = "Driver integrity checks are off",
            WhyBad = "Unsigned kernel code can load. Anti-cheats refuse to run.",
            WindowsDefault = "Off (not set).",
            Flags = BadTweakFlags.Admin | BadTweakFlags.Restart | BadTweakFlags.BitLocker, Sources = Src(Bcdedit, BitLockerBcd),
            Signals = new[] { BootOn("nointegritychecks", 0x16000048) },
        };
        yield return new BadTweakCheck
        {
            Id = "security.kernel-debugging", Area = BadTweakArea.Security, Severity = BadTweakSeverity.High,
            Title = "Kernel debugging is left on",
            WhyBad = "Leaves a debug channel open into Windows; anti-cheats refuse to run. Usually left behind by a driver tutorial.",
            WindowsDefault = "Off (not set).",
            Flags = BadTweakFlags.Admin | BadTweakFlags.Restart | BadTweakFlags.BitLocker, Sources = Src(Bcdedit, BitLockerBcd),
            Signals = new[] { BootOn("debug", 0x260000A0), BootOn("bootdebug", 0x16000010) },
        };
        yield return new BadTweakCheck
        {
            Id = "security.execution-policy", Area = BadTweakArea.Security, Severity = BadTweakSeverity.Medium,
            Title = "PowerShell runs any script without asking",
            WhyBad = "Unrestricted or Bypass lets any downloaded script run. \"Optimizer\" packs set it so their own scripts run, and so does malware.",
            WindowsDefault = "Not set (Restricted on Windows 10/11 Home and Pro).",
            Flags = BadTweakFlags.Admin, Sources = Src(ExecutionPolicy),
            Note = "If you're a developer who set RemoteSigned on purpose, that isn't flagged.",
            Signals = new[]
            {
                Sig(LmText(@"SOFTWARE\Microsoft\PowerShell\1\ShellIds\Microsoft.PowerShell", "ExecutionPolicy", ValueTest.AnyOf, "Unrestricted", "Bypass"), new RegistryDeleteFix(Kind: RegistryKind.String)),
                Sig(new RegistryProbe(RegistryHive.CurrentUser, @"Software\Microsoft\PowerShell\1\ShellIds\Microsoft.PowerShell", "ExecutionPolicy", ValueTest.AnyOf, new[] { "Unrestricted", "Bypass" }, RegistryKind.String), new RegistryDeleteFix(Kind: RegistryKind.String)),
            },
        };
        yield return new BadTweakCheck
        {
            Id = "security.sehop-off", Area = BadTweakArea.Security, Severity = BadTweakSeverity.Medium,
            Title = "SEHOP exploit protection is off",
            WhyBad = "Turns off a Windows protection against a classic type of exploit, for no measured speed gain.",
            WindowsDefault = "Not set (on).",
            Flags = BadTweakFlags.Admin | BadTweakFlags.Restart, Sources = Src(Sehop),
            Signals = new[]
            {
                Sig(Lm(@"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "DisableExceptionChainValidation", ValueTest.AnyOf, 1), Remove),
                Sig(Lm(@"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "KernelSEHOPEnabled", ValueTest.AnyOf, 0), Remove),
            },
        };
        yield return new BadTweakCheck
        {
            Id = "security.driver-blocklist-off", Area = BadTweakArea.Security, Severity = BadTweakSeverity.Medium,
            Title = "Microsoft's vulnerable driver blocklist is off",
            WhyBad = "Known-dangerous drivers (often shipped with old RGB and fan tools) can load again. Vanguard and FACEIT treat this as a red flag.",
            WindowsDefault = "On (1).",
            Flags = BadTweakFlags.Admin | BadTweakFlags.Restart, Sources = Src(VulnerableDriverBlocklist),
            Signals = new[] { Sig(Lm(@"SYSTEM\CurrentControlSet\Control\CI\Config", "VulnerableDriverBlocklistEnable", ValueTest.AnyOf, 0), SetTo(1)) },
        };
        yield return new BadTweakCheck
        {
            Id = "security.whql-settings", Area = BadTweakArea.Security, Severity = BadTweakSeverity.Medium,
            Title = "Driver signing policy loosened (WHQLSettings)",
            WhyBad = "Left by \"advanced\" tweak packs to relax driver-signature rules.",
            WindowsDefault = "Not set.",
            Flags = BadTweakFlags.Admin | BadTweakFlags.Restart, Sources = Src(PcTuning),
            Signals = new[] { Sig(Lm(@"SYSTEM\CurrentControlSet\Control\CI\Policy", "WHQLSettings", ValueTest.AnyOf, 1), Remove) },
        };
        yield return new BadTweakCheck
        {
            Id = "security.download-warnings-off", Area = BadTweakArea.Security, Severity = BadTweakSeverity.Medium,
            Title = "Download safety warnings are switched off",
            WhyBad = "Windows no longer warns before opening files from the internet, and stops marking downloads as coming from the web.",
            WindowsDefault = "Prompt before launching (1); downloads marked.",
            Sources = Src(SmartScreen),
            Signals = new[]
            {
                Sig(Cu(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings\Zones\3", "1806", ValueTest.AnyOf, 0), SetTo(1)),
                Sig(Cu(@"Software\Microsoft\Windows\CurrentVersion\Policies\Attachments", "SaveZoneInformation", ValueTest.AnyOf, 1), Remove),
                Sig(Lm(@"SOFTWARE\Microsoft\Internet Explorer\Security", "DisableSecuritySettingsCheck", ValueTest.AnyOf, 1), Remove),
            },
        };
        yield return new BadTweakCheck
        {
            Id = "security.builtin-administrator", Area = BadTweakArea.Security, Severity = BadTweakSeverity.Medium,
            Title = "The hidden built-in Administrator account is turned on",
            WhyBad = "That account runs everything with full admin rights and never shows a UAC prompt, so anything you open there, malware included, can change Windows without asking. "
                     + "A video promoted it for a small FPS gain that later Windows versions don't give.",
            WindowsDefault = "Disabled.",
            Flags = BadTweakFlags.Admin, Sources = Src(WindowsBuiltInAdmin),
            Note = "Make sure your own account is an administrator before you turn this one off (Settings → Accounts → Other users).",
            Signals = new[] { Custom(CustomProbeKind.BuiltInAdministrator, new GuidanceFix("Sign in with your own account, open a terminal as administrator and run the command shown under \"Found\". It turns the built-in account off again; nothing in it is deleted.", "ms-settings:otherusers")) },
        };
        yield return new BadTweakCheck
        {
            Id = "security.restore-points-off", Area = BadTweakArea.Security, Severity = BadTweakSeverity.Medium,
            Title = "System Restore is blocked or switched off",
            WhyBad = "No restore points can be made, so there's no safety net before updates or driver changes. PC Tidy also makes a restore point before every system change it makes; without System Protection that extra safety net is gone. "
                     + "Some guides switch it off and delete the restore points to \"free space\".",
            WindowsDefault = "System Protection on for the Windows drive; no policies; Volume Shadow Copy and its provider are Manual.",
            Flags = BadTweakFlags.Admin, Sources = Src(SystemRestore),
            Signals = new[]
            {
                Custom(CustomProbeKind.SystemProtectionOff, new GuidanceFix(
                    "Open Settings → System → About → System protection, pick the Windows drive, choose Configure and turn on system protection (3-5% of the drive is plenty).",
                    "ms-settings:about")),
                Sig(Lm(@"SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore", "DisableSR", ValueTest.AnyOf, 1), Remove),
                Sig(Lm(@"SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore", "DisableConfig", ValueTest.AnyOf, 1), Remove),
                SvcOff("VSS", ServiceStart.Manual),
                SvcOff("swprv", ServiceStart.Manual),
                TaskOff(@"\Microsoft\Windows\SystemRestore\SR"),
            },
        };
    }

    // ------------------------------------------------------------------ Windows Update and Store

    private static IEnumerable<BadTweakCheck> WindowsUpdate()
    {
        yield return new BadTweakCheck
        {
            Id = "update.automatic-updates-off", Area = BadTweakArea.WindowsUpdate, Severity = BadTweakSeverity.High,
            Title = "Automatic updates are turned off by a policy",
            WhyBad = "Security fixes stop arriving. Windows updates have also changed game performance far more than any tweak, in both directions.",
            WindowsDefault = "Not set.",
            Flags = BadTweakFlags.Admin, Sources = Src(UpdateCsp),
            Signals = new[]
            {
                Sig(Lm(WindowsUpdatePolicy + @"\AU", "NoAutoUpdate", ValueTest.AnyOf, 1), Remove),
                Sig(Lm(WindowsUpdatePolicy + @"\AU", "AUOptions", ValueTest.AnyOf, 1), Remove),
            },
        };
        yield return new BadTweakCheck
        {
            Id = "update.access-blocked", Area = BadTweakArea.WindowsUpdate, Severity = BadTweakSeverity.High,
            Title = "Windows Update is blocked",
            WhyBad = "Windows can't reach Windows Update at all, so it never gets security fixes.",
            WindowsDefault = "Not set.",
            Flags = BadTweakFlags.Admin, Sources = Src(UpdateCsp),
            Signals = new[]
            {
                Sig(Lm(WindowsUpdatePolicy, "DisableWindowsUpdateAccess", ValueTest.AnyOf, 1), Remove),
                Sig(Lm(WindowsUpdatePolicy, "DoNotConnectToWindowsUpdateInternetLocations", ValueTest.AnyOf, 1), Remove),
                Sig(Lm(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoWindowsUpdate", ValueTest.AnyOf, 1), Remove),
                Sig(Cu(@"Software\Microsoft\Windows\CurrentVersion\Policies\WindowsUpdate", "DisableWindowsUpdateAccess", ValueTest.AnyOf, 1), Remove),
            },
        };
        yield return new BadTweakCheck
        {
            Id = "update.fake-update-server", Area = BadTweakArea.WindowsUpdate, Severity = BadTweakSeverity.High,
            Title = "Windows Update points to a fake update server",
            WhyBad = "A trick to block updates: Windows is told to use a company update server that is empty or doesn't exist.",
            WindowsDefault = "Not set (Microsoft's servers).",
            Flags = BadTweakFlags.Admin, Sources = Src(UpdateCsp),
            Note = "On a work PC this can be a real company server: PC Tidy only flags it when the server is blank or this PC itself.",
            Signals = new[]
            {
                Custom(CustomProbeKind.FakeUpdateServer,
                    new RegistryDeleteFix(RegistryHive.LocalMachine, WindowsUpdatePolicy + @"\AU", "UseWUServer"),
                    new RegistryDeleteFix(RegistryHive.LocalMachine, WindowsUpdatePolicy, "WUServer", RegistryKind.String),
                    new RegistryDeleteFix(RegistryHive.LocalMachine, WindowsUpdatePolicy, "WUStatusServer", RegistryKind.String)),
            },
        };
        yield return new BadTweakCheck
        {
            Id = "update.pause-hack", Area = BadTweakArea.WindowsUpdate, Severity = BadTweakSeverity.Medium,
            Title = "Updates can be paused for years",
            WhyBad = "A registry hack stretches \"Pause updates\" far past Windows' 5-week limit, so security fixes can stay away for years.",
            WindowsDefault = "Not set.",
            Flags = BadTweakFlags.Admin, Sources = Src(UpdateCsp),
            Signals = new[] { Sig(Lm(@"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings", "FlightSettingsMaxPauseDays", ValueTest.Present), Remove) },
        };
        yield return new BadTweakCheck
        {
            Id = "update.services-disabled", Area = BadTweakArea.WindowsUpdate, Severity = BadTweakSeverity.High,
            Title = "Windows Update or Store services are disabled",
            WhyBad = "Updates, Store app installs and app licences stop working. Windows' own repair service may also fight the change.",
            WindowsDefault = "Windows Update and Store services: Manual (they start when needed); Update Orchestrator and Delivery Optimization: Automatic (delayed).",
            Flags = BadTweakFlags.Admin | BadTweakFlags.Restart, Sources = Src(IotServices),
            Note = "Windows protects some of these services. If it refuses, PC Tidy says so and Windows Update's own troubleshooter can repair them.",
            Signals = new[]
            {
                SvcOff("wuauserv", ServiceStart.Manual), SvcOff("UsoSvc", ServiceStart.AutomaticDelayed), SvcOff("WaaSMedicSvc", ServiceStart.Manual),
                SvcOff("BITS", ServiceStart.Manual), SvcOff("DoSvc", ServiceStart.AutomaticDelayed), SvcOff("CryptSvc", ServiceStart.Automatic),
                SvcOff("TrustedInstaller", ServiceStart.Manual), SvcOff("AppXSvc", ServiceStart.Manual), SvcOff("ClipSVC", ServiceStart.Manual),
                SvcOff("InstallService", ServiceStart.Manual), SvcOff("LicenseManager", ServiceStart.Manual),
            },
        };
        yield return new BadTweakCheck
        {
            Id = "update.tasks-disabled", Area = BadTweakArea.WindowsUpdate, Severity = BadTweakSeverity.Medium,
            Title = "Windows Update's scheduled tasks are disabled",
            WhyBad = "Windows stops checking for updates (and Store app updates) on its own schedule.",
            WindowsDefault = "Enabled.",
            Flags = BadTweakFlags.Admin, Sources = Src(UpdateCsp),
            Signals = new[]
            {
                TaskOff(@"\Microsoft\Windows\WindowsUpdate\Scheduled Start"),
                TaskOff(@"\Microsoft\Windows\UpdateOrchestrator\Schedule Scan"),
                TaskOff(@"\Microsoft\Windows\WaaSMedic\PerformRemediation"),
                TaskOff(@"\Microsoft\Windows\InstallService\ScanForUpdates"),
            },
        };
        yield return new BadTweakCheck
        {
            Id = "update.store-blocked", Area = BadTweakArea.WindowsUpdate, Severity = BadTweakSeverity.Medium,
            Title = "Microsoft Store or its updates are blocked",
            WhyBad = "Store apps (Calculator, Photos, Terminal, Game Pass games) stop getting fixes, or the Store stops opening.",
            WindowsDefault = "Not set.",
            Flags = BadTweakFlags.Admin, Sources = Src(UpdateCsp),
            Signals = new[]
            {
                Sig(Lm(@"SOFTWARE\Policies\Microsoft\WindowsStore", "AutoDownload", ValueTest.AnyOf, 2), Remove),
                Sig(Lm(@"SOFTWARE\Policies\Microsoft\WindowsStore", "RemoveWindowsStore", ValueTest.AnyOf, 1), Remove),
                Sig(Lm(@"SOFTWARE\Policies\Microsoft\WindowsStore", "DisableStoreApps", ValueTest.AnyOf, 1), Remove),
            },
        };
    }

    // ------------------------------------------------------------------ boot settings (BCD)

    private static IEnumerable<BadTweakCheck> Boot()
    {
        BadTweakCheck Timer(string id, string title, string why, BadTweakSeverity severity, BadSignal signal) => new()
        {
            Id = id, Area = BadTweakArea.BootSettings, Severity = severity, Title = title, WhyBad = why,
            WindowsDefault = "Not set.", Flags = BadTweakFlags.Admin | BadTweakFlags.Restart,
            Sources = Src(Bcdedit, BitLockerBcd), Signals = new[] { signal },
            Note = "BitLocker doesn't watch this setting, so removing it won't ask for the recovery key. A restart is needed.",
        };

        yield return Timer("boot.useplatformclock", "HPET is forced as the main timer (useplatformclock)",
            "A slower timer that can hurt frame times. Microsoft says it's for debugging only.", BadTweakSeverity.High, BootOn("useplatformclock", 0x260000A2));
        yield return Timer("boot.useplatformtick", "useplatformtick is on",
            "Debug-only timer setting with reports of higher latency and input lag.", BadTweakSeverity.Medium, BootOn("useplatformtick", 0x260000A4));
        yield return Timer("boot.disabledynamictick", "Dynamic tick is off (disabledynamictick)",
            "Keeps the CPU waking up when idle: more power and heat (bad on laptops) for anecdotal gains.", BadTweakSeverity.Medium, BootOn("disabledynamictick", 0x260000A5));
        yield return Timer("boot.tscsyncpolicy", "tscsyncpolicy is changed",
            "Debug-only timer setting; no measured benefit.", BadTweakSeverity.Low, BootSet("tscsyncpolicy", 0x250000A6));
        const string permanent = " PC Tidy can't read this value back, so removing it can't be undone from History.";
        yield return Timer("boot.x2apicpolicy", "x2APIC policy is changed",
            "No measured gain; changing it can upset interrupt handling on many-core CPUs.", BadTweakSeverity.Low, BootSet("x2apicpolicy", 0x25000055))
            with { Note = "BitLocker doesn't watch this setting. A restart is needed." + permanent };
        yield return Timer("boot.uselegacyapicmode", "Legacy APIC mode is forced",
            "Forces an older interrupt controller mode, a limitation on modern CPUs.", BadTweakSeverity.Low, BootOn("uselegacyapicmode", 0x26000054))
            with { Note = "BitLocker doesn't watch this setting. A restart is needed." + permanent };
        yield return Timer("boot.increaseuserva", "increaseuserva is set",
            "A 32-bit Windows memory setting that does nothing on 64-bit Windows.", BadTweakSeverity.Low, BootSet("increaseuserva", 0x25000032));
        yield return Timer("boot.quietboot", "Boot screen is hidden (quietboot)",
            "Hides the boot graphics and blue-screen details, so crashes can't be read. Saves no measurable time.", BadTweakSeverity.Low, BootOn("quietboot", 0x26000041));
        yield return new BadTweakCheck
        {
            Id = "boot.cpu-memory-limits", Area = BadTweakArea.BootSettings, Severity = BadTweakSeverity.Medium,
            Title = "Windows is limited to fewer CPU cores or less memory",
            WhyBad = "Left over from System Configuration's \"Number of processors\" or a \"free memory\" tweak: Windows quietly uses less of your hardware.",
            WindowsDefault = "Not set (all cores, all memory).",
            Flags = BadTweakFlags.Admin | BadTweakFlags.Restart, Sources = Src(Bcdedit, BitLockerBcd),
            Signals = new[] { BootSet("numproc", 0x25000061), BootOn("onecpu", 0x26000060), BootSet("removememory", 0x25000031) },
        };
        yield return new BadTweakCheck
        {
            Id = "boot.hypervisor-off", Area = BadTweakArea.BootSettings, Severity = BadTweakSeverity.Low,
            Title = "The Windows hypervisor is switched off",
            WhyBad = "Turns off Memory integrity, WSL 2, Windows Sandbox and virtual machines that rely on it. Fine on a pure gaming PC, but it's a security change.",
            WindowsDefault = "Auto when those features are installed.",
            Flags = BadTweakFlags.Admin | BadTweakFlags.Restart, Sources = Src(Bcdedit, MemoryIntegrity),
            Signals = new[] { Sig(new BootValueProbe("hypervisorlaunchtype", 0x250000F0, BootTest.IntegerEquals, 0),
                new GuidanceFix("If you use WSL 2, Windows Sandbox or Memory integrity, switch the Windows hypervisor back on, then restart.\nCommand (admin terminal): bcdedit /set hypervisorlaunchtype auto")) },
        };
    }
}
