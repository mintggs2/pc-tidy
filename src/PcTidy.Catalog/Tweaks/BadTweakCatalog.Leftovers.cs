using PcTidy.Core.Model;
using static PcTidy.Catalog.Tweaks.TweakSources;

namespace PcTidy.Catalog.Tweaks;

/// <summary>
/// Community tweaks §9.7 (the caption-less videos, transcribed): leftovers of "FPS" guides and packs that turn off
/// protection, pause updates for months, leave Safe Mode on, disable devices or import their own power plans.
/// Each value, element and API here was checked against Microsoft's documentation; anything PC Tidy can't set
/// itself (LSA protection, Defender's own switches) is information with a link to Windows Security.
/// </summary>
public static partial class BadTweakCatalog
{
    internal const string Lsa = @"SYSTEM\CurrentControlSet\Control\Lsa";

    /// <summary>BcdOSLoaderInteger_SafeBoot (Microsoft's BCD element list; BitLocker doesn't validate it by default).</summary>
    public const uint SafeBootElement = 0x25000080;

    /// <summary>Windows' own power plans (GUID_TYPICAL_POWER_SAVINGS, GUID_MIN_POWER_SAVINGS, GUID_MAX_POWER_SAVINGS in WinNT.h).</summary>
    public static readonly Guid BalancedPlan = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly IReadOnlySet<Guid> WindowsPowerPlans = new HashSet<Guid>
    {
        BalancedPlan,
        new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"), // High performance
        new("a1841308-3541-4fab-bc81-f71556f20b4a"), // Power saver
    };

    private static IEnumerable<BadTweakCheck> MoreSecurity()
    {
        yield return new BadTweakCheck
        {
            Id = "security.lsa-protection-off", Area = BadTweakArea.Security, Severity = BadTweakSeverity.Medium,
            Title = "LSA protection is turned off",
            WhyBad = "LSA protection stops other programs reading your sign-in secrets from Windows' memory or injecting code into it. "
                     + "Some \"FPS\" guides switch it off next to Memory integrity; no test showed a speed gain from that.",
            WindowsDefault = "Not set, or on (RunAsPPL 1 or 2).",
            Flags = BadTweakFlags.Admin | BadTweakFlags.Restart, Sources = Src(LsaProtection, LsaProtectionInWindowsSecurity),
            Note = "PC Tidy doesn't change this one: Windows Security is the place for it, and PC Tidy opens the page for you. "
                   + "If an old smart-card or password tool you rely on stops working with it on, leave it off and ask that tool's maker.",
            Signals = new[]
            {
                Sig(Lm(Lsa, "RunAsPPL", ValueTest.AnyOf, 0), new GuidanceFix(
                    "Open Windows Security → Device security → Core isolation details, turn on Local Security Authority protection, then restart.",
                    "ms-settings:windowsdefender")),
            },
        };
        yield return new BadTweakCheck
        {
            Id = "security.defender-only-realtime", Area = BadTweakArea.Security, Severity = BadTweakSeverity.High,
            Title = "Defender is on, but most of its protection is switched off",
            WhyBad = "Only real-time scanning is left. Behaviour monitoring, download and script scanning and cloud protection catch what real-time scanning alone misses, "
                     + "and Tamper Protection stops other programs switching things off. Optimizer scripts turn them off to look \"lighter\", for no measured gain.",
            WindowsDefault = "All on.",
            Flags = BadTweakFlags.Admin, Sources = Src(DefenderStatus, DefenderPreferences, DefenderTamperProtection, DefenderCloudProtection),
            Note = "Apps can't switch Defender's protections back on for you, PC Tidy included. If a switch won't stay on, a Defender policy is usually behind it: "
                   + "that shows up as its own finding, which PC Tidy can remove.",
            Signals = new[]
            {
                Custom(CustomProbeKind.DefenderOnlyRealtime, new GuidanceFix(
                    "Open Windows Security → Virus & threat protection → Manage settings and turn on Cloud-delivered protection, Automatic sample submission and Tamper Protection. "
                    + "Behaviour monitoring, download scanning and script scanning have no switch there: in an admin PowerShell run "
                    + "Set-MpPreference -DisableBehaviorMonitoring $false -DisableIOAVProtection $false -DisableScriptScanning $false, then turn on Tamper Protection.",
                    "ms-settings:windowsdefender")),
            },
        };
    }

    private static IEnumerable<BadTweakCheck> UpdatePauses()
    {
        const string wu = WindowsUpdatePolicy;
        var resume = new GuidanceFix("After the reset, open Settings → Windows Update. If it still says updates are paused, choose Resume updates.", "ms-settings:windowsupdate");
        RegistryProbe Text(string name) => new(RegistryHive.LocalMachine, wu, name, ValueTest.Present, Array.Empty<string>(), RegistryKind.String);

        yield return new BadTweakCheck
        {
            Id = "update.paused-by-policy", Area = BadTweakArea.WindowsUpdate, Severity = BadTweakSeverity.High,
            Title = "Windows Update is paused or held back by a policy",
            WhyBad = "Guides sell \"pause updates for 6-12 months\" as an FPS fix. A pause policy keeps security fixes away, and a deferral policy makes Windows wait weeks or months before it takes them. "
                     + "Windows updates have changed game performance far more than any tweak, in both directions.",
            WindowsDefault = "Not set: you can still pause for up to 5 weeks yourself in Settings → Windows Update.",
            Flags = BadTweakFlags.Admin, Sources = Src(UpdateClientPolicies, UpdateCsp),
            Note = "On a work or school PC your IT team may set these on purpose: ask them first. Removing a policy never turns updates off; Settings → Windows Update stays in charge.",
            Signals = new[]
            {
                Sig(Text("PauseQualityUpdatesStartTime"), Remove, resume),
                Sig(Text("PauseFeatureUpdatesStartTime"), Remove, resume),
                Sig(Lm(wu, "PauseQualityUpdates", ValueTest.AnyOf, 1), Remove, resume),
                Sig(Lm(wu, "PauseFeatureUpdates", ValueTest.AnyOf, 1), Remove, resume),
                Sig(Lm(wu, "DeferQualityUpdates", ValueTest.AnyOf, 1), Remove, new RegistryDeleteFix(ValueName: "DeferQualityUpdatesPeriodInDays"), resume),
                Sig(Lm(wu, "DeferFeatureUpdates", ValueTest.AnyOf, 1), Remove, new RegistryDeleteFix(ValueName: "DeferFeatureUpdatesPeriodInDays"), resume),
            },
        };
    }

    private static IEnumerable<BadTweakCheck> SafeBoot()
    {
        yield return new BadTweakCheck
        {
            Id = "boot.safeboot-left-on", Area = BadTweakArea.BootSettings, Severity = BadTweakSeverity.High,
            Title = "Windows is set to start in Safe Mode every time",
            WhyBad = "A Safe Mode flag was left in the boot settings, usually after a driver-cleaning tool or a guide that restarts into Safe Mode. "
                     + "Until it's removed, every restart goes back to Safe Mode: no graphics driver, no sound and most apps don't start.",
            WindowsDefault = "Not set (normal start).",
            Flags = BadTweakFlags.Admin | BadTweakFlags.Restart, Sources = Src(BcdBootOptions, Bcdedit, BitLockerBcd),
            Note = "BitLocker doesn't check this setting, so removing it won't ask for the recovery key. PC Tidy only explains this one: it isn't on the list of boot settings PC Tidy may remove.",
            Signals = new[]
            {
                Sig(new BootValueProbe("safeboot", SafeBootElement, BootTest.Present), new GuidanceFix(
                    "Press Win+R, type msconfig and press Enter. On the Boot tab untick Safe boot, choose OK and restart.\n"
                    + "Or, in an admin terminal: bcdedit /deletevalue {current} safeboot")),
            },
        };
    }

    private static IEnumerable<BadTweakCheck> DevicesAndPower()
    {
        yield return new BadTweakCheck
        {
            Id = "windows.system-devices-disabled", Area = BadTweakArea.Windows, Severity = BadTweakSeverity.Low,
            Title = "System devices are disabled in Device Manager",
            WhyBad = "Latency guides disable \"unneeded\" system devices, such as the High Precision Event Timer. No test found a gain, "
                     + "and Windows or a driver may need the device for sleep, USB, sound or virtual machines.",
            WindowsDefault = "Enabled.",
            Sources = Src(DeviceDisabled),
            Note = "PC Tidy only lists them. If you disabled one on purpose to fix a problem, leave it.",
            Signals = new[]
            {
                Custom(CustomProbeKind.DisabledSystemDevices, new GuidanceFix(
                    "Right-click Start and open Device Manager. Under System devices, right-click each device listed here and choose Enable device.")),
            },
        };
        yield return new BadTweakCheck
        {
            Id = "cpu.stray-power-plan", Area = BadTweakArea.CpuPower, Severity = BadTweakSeverity.Low,
            Title = "The active power plan isn't one of Windows' own",
            WhyBad = "Tuning tools and guides import their own plans (\"Ultimate\", \"Quick CPU\" and similar). Tests found no FPS gain over Balanced, and these plans usually use more power.",
            WindowsDefault = "Balanced.",
            Sources = Src(PowerSchemes, PowerMode),
            Note = "If your PC maker set this plan up (some laptops come with one), keep it. Switching back to Balanced can be undone from History, and the other plan stays on the PC.",
            Signals = new[]
            {
                Custom(CustomProbeKind.StrayPowerPlan, new PowerPlanSetFix(BalancedPlan, "Balanced"), new GuidanceFix(
                    "Or switch it yourself: Control Panel → Power Options → Balanced (PC Tidy's Speed section offers the same switch).")),
            },
        };
    }
}
