using PcTidy.Core.Model;

namespace PcTidy.Catalog.PcCare;

/// <summary>
/// The one registry value PC care's "Adapter power saving" rows may write: PnPCapabilities = 24 (0x18) on one network
/// adapter's class subkey, which unticks "Allow the computer to turn off this device to save power" (and also stops the
/// adapter waking the PC). Undo puts the exact old value back, or deletes the value when it wasn't there.
///
/// Source: Microsoft KB 2740020, "Information about power management setting on a network adapter"
/// (https://learn.microsoft.com/en-us/troubleshoot/windows-client/networking/power-management-on-network-adapter):
/// HKLM\SYSTEM\CurrentControlSet\Control\Class\{4D36E972-E325-11CE-BFC1-08002bE10318}\DeviceNumber, REG_DWORD
/// PnPCapabilities, 24 = Windows doesn't turn the adapter off; 0 (or no value) = the default, power management on.
/// VERIFY: the article is written for Windows 7 / Server 2008 R2. Confirm on a clean Windows 10 and 11 VM that the
/// Device Manager box is unticked after a restart before this ships (some 2026 Q&amp;A posts say newer drivers ignore it).
/// </summary>
public sealed class AdapterPowerAllowances : IRegistryAllowanceSource
{
    public const string Source = "pccare.adapter-power";

    /// <summary>The network adapter device class (GUID_DEVCLASS_NET).</summary>
    public const string NetClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";

    /// <summary>
    /// One adapter's subkey: exactly four ASCII digits ("0007"), never deeper (no "\Ndi", no "\Properties"). [0-9], not
    /// \d, which would also match other scripts' digits (safety review L1).
    /// </summary>
    public const string KeyPathPattern = @"^SYSTEM\\CurrentControlSet\\Control\\Class\\\{4d36e972-e325-11ce-bfc1-08002be10318\}\\[0-9]{4}$";

    public const string ValueName = "PnPCapabilities";

    /// <summary>24 (0x18): Windows may not turn this adapter off to save power (KB 2740020). The only value ever written.</summary>
    public const string PowerSavingOff = "24";

    public static string KeyFor(string subkey) => NetClassKey + "\\" + subkey;

    public IEnumerable<RegistryAllowance> Allowances() => new[]
    {
        new RegistryAllowance(Source, RegistryHive.LocalMachine, KeyFor("0001"), ValueName, RegistryKind.DWord,
            // Set-only: nothing in PC Tidy deletes this value as a change, so a crafted delete of a driver's own value is
            // refused. Undo of the set restores the recorded before-value (deleting it if it was absent) as the undo of
            // that same allowed step.
            new[] { PowerSavingOff }, AllowDelete: false, NeedsAdmin: true)
        {
            KeyPathPattern = KeyPathPattern,
        },
    };
}
