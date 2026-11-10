using PcTidy.Core.Model;

namespace PcTidy.Catalog.Tweaks;

// =====================================================================================
//  "Find bad tweaks" (Plan §8, Community tweaks §3). Each check is a list of signals:
//  a read-only probe (what to look for) plus the fix that puts the Windows default back.
//  The finder only reads; fixes become PlanItems that the user previews and confirms.
// =====================================================================================

public enum BadTweakArea { Security, WindowsUpdate, BootSettings, Scheduler, Network, Services, Storage, CpuPower, Graphics, Games, Policies, Windows }

public enum BadTweakSeverity { High, Medium, Low }

[Flags]
public enum BadTweakFlags
{
    None = 0,
    Admin = 1,
    Restart = 2,
    /// <summary>Changes a boot value BitLocker watches (nx, nointegritychecks, testsigning, debug, bootdebug): have the recovery key ready.</summary>
    BitLocker = 4,
    SignOut = 8,
}

// ------------------------------------------------------------------ probes (read-only)

public abstract record BadProbe
{
    public abstract string Describe();
}

public enum ValueTest
{
    /// <summary>The value exists (any data).</summary>
    Present,
    /// <summary>Exists and equals one of the values.</summary>
    AnyOf,
    /// <summary>Exists and is none of the values.</summary>
    NoneOf,
    /// <summary>DWORD exists and has any of the bits of the first value set.</summary>
    AnyBits,
    /// <summary>Exists and is empty (empty string / empty multi-string).</summary>
    Empty,
}

/// <summary>One registry value condition. <see cref="Kind"/> is the expected type (used for the fix and its allowance).</summary>
public sealed record RegistryProbe(RegistryHive Hive, string KeyPath, string ValueName, ValueTest Test, IReadOnlyList<string> Values, RegistryKind Kind = RegistryKind.DWord)
    : BadProbe
{
    public string FullKey => $@"{RegistryNames.Short(Hive)}\{KeyPath}";
    public string Label => $@"{FullKey} → {ValueName}";
    public override string Describe() => Test switch
    {
        ValueTest.Present => $"{Label} is set",
        ValueTest.AnyOf => $"{Label} = {string.Join(" or ", Values)}",
        ValueTest.NoneOf => $"{Label} isn't {string.Join(" or ", Values)}",
        ValueTest.AnyBits => $"{Label} has bits {Values[0]} set",
        _ => $"{Label} is empty",
    };
}

/// <summary>The service's start type is Disabled.</summary>
public sealed record ServiceDisabledProbe(string ServiceName) : BadProbe
{
    public override string Describe() => $"Service '{ServiceName}' is Disabled";
}

public enum BootTest { IsTrue, Present, IntegerEquals }

/// <summary>
/// A value on the current Windows boot entry, read with the WMI BCD provider (never by parsing bcdedit's localized text).
/// <paramref name="ElementType"/> is the BCD element id (Community tweaks T2 hex values).
/// </summary>
public sealed record BootValueProbe(string Name, uint ElementType, BootTest Test, ulong Value = 0) : BadProbe
{
    public override string Describe() => Test switch
    {
        BootTest.IsTrue => $"Boot setting '{Name}' is on",
        BootTest.IntegerEquals => $"Boot setting '{Name}' = {Value}",
        _ => $"Boot setting '{Name}' is set",
    };
}

/// <summary>A scheduled task that Windows needs is disabled.</summary>
public sealed record TaskDisabledProbe(string TaskPath) : BadProbe
{
    public override string Describe() => $"Scheduled task {TaskPath} is disabled";
}

/// <summary>Checks that need code rather than one value (hosts file, TCP autotuning, broken services...). See <see cref="CustomProbeKind"/>.</summary>
public sealed record CustomProbe(string Kind) : BadProbe
{
    public override string Describe() => Kind;
}

/// <summary>Kinds of <see cref="CustomProbe"/>.</summary>
public static class CustomProbeKind
{
    public const string HostsFile = "hosts";
    public const string TcpAutotuning = "tcp-autotuning";
    public const string BrokenServices = "broken-services";
    public const string BuiltInAdministrator = "builtin-admin";
    public const string FirewallBlocksMicrosoft = "firewall-blocks";
    public const string FakeUpdateServer = "fake-wsus";
    public const string NagleLeftovers = "nagle";
    public const string RealtimePriority = "ifeo-realtime";
    public const string CoreParking = "core-parking";
    public const string RobloxFastFlags = "roblox-fastflags";
    public const string EdgePolicies = "edge-policies";
    public const string OtherPolicies = "other-policies";
    /// <summary>Any Defender policy value the catalog doesn't list by name (each is offered for removal).</summary>
    public const string DefenderPolicyLeftovers = "defender-policy-leftovers";
    /// <summary>Tamper Protection or cloud protection off while Defender is the active antivirus (information only).</summary>
    public const string DefenderProtectionReduced = "defender-protection-reduced";
    /// <summary>System Protection is off for the Windows drive (no restore points can be made).</summary>
    public const string SystemProtectionOff = "system-protection-off";
    /// <summary>The paging file has a fixed size instead of "let Windows manage".</summary>
    public const string FixedPagefile = "fixed-pagefile";
    /// <summary>Windows' memory compression looks switched off.</summary>
    public const string MemoryCompressionOff = "memory-compression-off";
    /// <summary>IPv4, IPv6 or Client for Microsoft Networks is unticked on a physical network adapter.</summary>
    public const string UnboundAdapterProtocols = "unbound-protocols";
    /// <summary>Most third-party services disabled at once (System Configuration's "Disable all").</summary>
    public const string ManyServicesDisabled = "many-services-disabled";
    /// <summary>Low-level hardware access drivers left behind by tuning tools (RWEverything, WinRing0).</summary>
    public const string HardwareAccessDrivers = "hardware-access-drivers";
    /// <summary>Defender is the antivirus and real-time protection is on, but most of its other protections are off (information only).</summary>
    public const string DefenderOnlyRealtime = "defender-only-realtime";
    /// <summary>System-class devices disabled in Device Manager (problem code CM_PROB_DISABLED). Listed only.</summary>
    public const string DisabledSystemDevices = "disabled-system-devices";
    /// <summary>The active power plan isn't one of Windows' standard plans.</summary>
    public const string StrayPowerPlan = "stray-power-plan";
}

// ------------------------------------------------------------------ fixes

public abstract record BadFix
{
    public abstract string Describe();
}

/// <summary>Delete a registry value. Null parts = the probe's own hive/key/value.</summary>
public sealed record RegistryDeleteFix(RegistryHive? Hive = null, string? KeyPath = null, string? ValueName = null, RegistryKind Kind = RegistryKind.DWord) : BadFix
{
    public override string Describe() => "Remove the value (back to Windows' default)";
}

/// <summary>Set the probe's registry value to the Windows default.</summary>
public sealed record RegistrySetFix(RegistryKind Kind, string Data) : BadFix
{
    public override string Describe() => $"Set it back to {(Data.Length == 0 ? "empty" : Data.Replace("\n", ", "))}";
}

/// <summary>Set a named registry value to the Windows default (for custom probes, which have no value of their own).</summary>
public sealed record RegistryWriteFix(RegistryHive Hive, string KeyPath, string ValueName, RegistryKind Kind, string Data) : BadFix
{
    public override string Describe() => $"Set {RegistryNames.Short(Hive)}\\{KeyPath} → {ValueName} back to {(Data.Length == 0 ? "empty" : Data.Replace("\n", ", "))}";
}

/// <summary>
/// Remove each registry value the custom probe listed (values that can't be named in advance, such as leftover
/// Defender policies). Only values a dynamic allowance source allows can be removed; the guard refuses the rest.
/// </summary>
public sealed record RegistryDeleteFoundFix : BadFix
{
    public override string Describe() => "Remove each value listed (back to Windows' default)";
}

public sealed record ServiceStartFix(ServiceStart Start) : BadFix
{
    public override string Describe() => $"Set the service back to {RegistryNames.Friendly(Start)}";
}

/// <summary>Remove the probe's boot value (<see cref="ToolIds.BcdDeleteValue"/>).</summary>
public sealed record BootDeleteFix : BadFix
{
    public override string Describe() => "Remove the boot setting (Windows' default)";
}

/// <summary>Run a whitelisted Windows tool (<see cref="ToolIds"/>).</summary>
public sealed record ToolFix(string ToolId, string What) : BadFix
{
    public override string Describe() => What;
}

public sealed record TaskEnableFix : BadFix
{
    public override string Describe() => "Turn the task back on";
}

/// <summary>Comment out the offending hosts lines (the hosts file is backed up first).</summary>
public sealed record HostsCommentOutFix : BadFix
{
    public override string Describe() => "Comment out these lines in the hosts file (backed up first)";
}

/// <summary>
/// Switch the active power plan back to one of Windows' own plans (<see cref="ActionKind.PowerPlanSet"/>, the same step
/// Speed offers). Only emitted when the custom probe confirmed the plan exists on this PC; undo switches back.
/// </summary>
public sealed record PowerPlanSetFix(Guid SchemeGuid, string PlanName) : BadFix
{
    public override string Describe() => $"Switch the power plan back to {PlanName}";
}

/// <summary>Move a file to quarantine (restorable).</summary>
public sealed record QuarantineFix : BadFix
{
    public override string Describe() => "Move the file to quarantine";
}

/// <summary>No safe one-click fix: explain what to do.</summary>
public sealed record GuidanceFix(string Steps, string? SettingsUri = null) : BadFix
{
    public override string Describe() => Steps;
}

/// <summary>A probe and the fixes to apply when it fires.</summary>
public sealed record BadSignal(BadProbe Probe, IReadOnlyList<BadFix> Fixes);

/// <summary>One finding type in "Find bad tweaks".</summary>
public sealed record BadTweakCheck
{
    public required string Id { get; init; }
    public required BadTweakArea Area { get; init; }
    public required string Title { get; init; }
    /// <summary>Why it's bad, in plain English.</summary>
    public required string WhyBad { get; init; }
    /// <summary>What Windows has by default ("Not set", "Automatic").</summary>
    public required string WindowsDefault { get; init; }
    public BadTweakSeverity Severity { get; init; } = BadTweakSeverity.Medium;
    public BadTweakFlags Flags { get; init; }
    public required IReadOnlyList<string> Sources { get; init; }
    public required IReadOnlyList<BadSignal> Signals { get; init; }
    /// <summary>Extra caution shown with the finding (e.g. Tamper Protection, BitLocker).</summary>
    public string? Note { get; init; }
}
