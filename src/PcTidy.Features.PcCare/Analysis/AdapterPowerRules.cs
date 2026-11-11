using System.Text.RegularExpressions;
using PcTidy.Catalog.PcCare;
using PcTidy.Features.PcCare.Model;

namespace PcTidy.Features.PcCare.Analysis;

/// <summary>What PC care can do for one adapter's power-saving setting.</summary>
public enum AdapterPowerState
{
    /// <summary>Windows may turn the adapter off (no value, or 0): PC Tidy can set 24.</summary>
    CanTurnOff,
    /// <summary>Already 24: Windows won't turn it off.</summary>
    AlreadyOff,
    /// <summary>Some other value or type (set by the driver or another tool): PC Tidy leaves it alone.</summary>
    CustomValue,
}

/// <summary>A physical Ethernet or Wi-Fi adapter whose class subkey PC Tidy matched to it, and its current setting.</summary>
public sealed record AdapterPowerCandidate(NetClassEntry Entry, NetAdapter Adapter, NetAdapterMedium Medium, AdapterPowerState State)
{
    /// <summary>The exact value seen at scan time, as shown and recorded ("absent" or the decimal DWORD).</summary>
    public string BeforeValue => Entry.PnPCapabilitiesOtherType ? "other type" : Entry.PnPCapabilities?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "absent";
}

/// <summary>
/// Picks the adapters the "Adapter power saving" rows may touch. Strict on purpose: only a PHYSICAL Ethernet or Wi-Fi
/// adapter on a real bus (PCI, USB, SDIO) whose class subkey's NetCfgInstanceId matches exactly one adapter Windows
/// lists. Never virtual, VPN, Hyper-V, Bluetooth PAN, loopback, Wi-Fi Direct or packet-capture adapters. Pure: no I/O.
/// </summary>
public static class AdapterPowerRules
{
    public const int NcfVirtual = 0x1;
    public const int NcfPhysical = 0x4;
    public const int NcfHidden = 0x8;

    private static readonly Regex FourDigits = new(@"^\d{4}$", RegexOptions.CultureInvariant);

    /// <summary>Real hardware buses. Virtual miniports use ROOT\, SWD\, BTH\, ms_* or vms_* ids instead.</summary>
    private static readonly string[] HardwareBuses = { @"pci\", @"usb\", @"sd\" };

    /// <summary>Words that mark a virtual or tunnel adapter in a name, description or component id.</summary>
    private static readonly string[] VirtualWords =
    {
        "virtual", "vpn", "hyper-v", "vethernet", "vms_", "bluetooth", "bth", "loopback", "tap-", "tap adapter", "tap0",
        "wireguard", "wintun", "openvpn", "vmware", "virtualbox", "vbox", "npcap", "winpcap", "miniport", "tunnel",
        "teredo", "isatap", "6to4", "kernel debug", "wi-fi direct", "hosted network", "zerotier", "tailscale", "hamachi",
        "fortinet", "cisco anyconnect", "globalprotect", "nordlynx", "proton", "mullvad",
    };

    public static IReadOnlyList<AdapterPowerCandidate> Candidates(AdapterPowerFacts facts)
    {
        var list = new List<AdapterPowerCandidate>();
        foreach (var entry in facts.ClassEntries)
        {
            if (!FourDigits.IsMatch(entry.Subkey)) continue;
            if (!Guid.TryParse(entry.NetCfgInstanceId, out var id)) continue;
            var matches = facts.Adapters.Where(a => Guid.TryParse(a.Id, out var g) && g == id).ToList();
            if (matches.Count != 1) continue;
            var adapter = matches[0];
            var medium = MediumOf(entry, adapter);
            if (medium is null || !IsPhysical(entry, adapter)) continue;
            list.Add(new AdapterPowerCandidate(entry, adapter, medium.Value, StateOf(entry)));
        }
        // Wi-Fi first, then Ethernet; each by name (none ticked by default).
        return list.OrderBy(c => c.Medium == NetAdapterMedium.WiFi ? 0 : 1)
            .ThenBy(c => c.Adapter.Description, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Ethernet or Wi-Fi from the driver's own *IfType / *PhysicalMediaType; it must agree with what Windows lists.</summary>
    internal static NetAdapterMedium? MediumOf(NetClassEntry e, NetAdapter a)
    {
        NetAdapterMedium? fromDriver = e.IfType switch
        {
            6 => NetAdapterMedium.Ethernet,
            71 => NetAdapterMedium.WiFi,
            _ => e.PhysicalMediaType switch { 14 => NetAdapterMedium.Ethernet, 9 => NetAdapterMedium.WiFi, _ => null },
        };
        if (fromDriver is null) return null;
        if (a.Medium != NetAdapterMedium.Other && a.Medium != fromDriver) return null;
        return fromDriver;
    }

    internal static bool IsPhysical(NetClassEntry e, NetAdapter a)
    {
        if (e.Characteristics is not { } flags) return false;
        if ((flags & NcfPhysical) == 0 || (flags & NcfVirtual) != 0 || (flags & NcfHidden) != 0) return false;
        if (a.WmiPhysical == false) return false;
        var component = e.ComponentId ?? "";
        if (!HardwareBuses.Any(b => component.StartsWith(b, StringComparison.OrdinalIgnoreCase))) return false;
        var text = string.Join(" | ", e.DriverDesc, component, a.Name, a.Description);
        return !VirtualWords.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    internal static AdapterPowerState StateOf(NetClassEntry e)
    {
        if (e.PnPCapabilitiesOtherType) return AdapterPowerState.CustomValue;
        return e.PnPCapabilities switch
        {
            null or 0 => AdapterPowerState.CanTurnOff,
            24 => AdapterPowerState.AlreadyOff,
            _ => AdapterPowerState.CustomValue,
        };
    }

    /// <summary>The full key (without hive) for a candidate.</summary>
    public static string KeyPath(AdapterPowerCandidate c) => AdapterPowerAllowances.KeyFor(c.Entry.Subkey);
}
