using System.Net.NetworkInformation;
using Microsoft.Win32;
using PcTidy.Catalog.PcCare;
using PcTidy.Features.PcCare.Model;

namespace PcTidy.Features.PcCare.Source;

/// <summary>Network adapters and their class subkeys, for the "Adapter power saving" rows. Read access only.</summary>
public sealed partial class WindowsPcCareSource
{
    private static AdapterPowerFacts? ReadAdapterPower()
    {
        var entries = new List<NetClassEntry>();
        using (var cls = OpenRead(RegistryHive.LocalMachine, AdapterPowerAllowances.NetClassKey))
        {
            if (cls is null) return null;
            foreach (var name in cls.GetSubKeyNames())
            {
                if (name.Length != 4 || !name.All(char.IsAsciiDigit)) continue; // skips "Properties" and friends
                try
                {
                    using var sub = cls.OpenSubKey(name, writable: false);
                    if (sub is null) continue;
                    var kind = sub.GetValueNames().Contains("PnPCapabilities", StringComparer.OrdinalIgnoreCase)
                        ? sub.GetValueKind("PnPCapabilities")
                        : (RegistryValueKind?)null;
                    entries.Add(new NetClassEntry(
                        name,
                        sub.GetValue("NetCfgInstanceId") as string,
                        sub.GetValue("DriverDesc") as string,
                        sub.GetValue("ComponentId") as string,
                        AsInt(sub.GetValue("Characteristics")),
                        AsInt(sub.GetValue("*IfType")),
                        AsInt(sub.GetValue("*PhysicalMediaType")),
                        kind == RegistryValueKind.DWord ? AsInt(sub.GetValue("PnPCapabilities")) : null,
                        PnPCapabilitiesOtherType: kind is not null && kind != RegistryValueKind.DWord));
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                {
                    // One unreadable subkey just isn't offered.
                }
            }
        }

        var physical = ReadWmiPhysical();
        var adapters = new List<NetAdapter>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            var medium = nic.NetworkInterfaceType switch
            {
                NetworkInterfaceType.Wireless80211 => NetAdapterMedium.WiFi,
                NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
                    or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit => NetAdapterMedium.Ethernet,
                _ => NetAdapterMedium.Other,
            };
            bool? isPhysical = physical is null ? null : Guid.TryParse(nic.Id, out var g) && physical.Contains(g);
            adapters.Add(new NetAdapter(nic.Id, nic.Name, nic.Description, medium, isPhysical));
        }
        return new AdapterPowerFacts(entries, adapters);
    }

    /// <summary>GUIDs of the adapters WMI calls physical (Win32_NetworkAdapter.PhysicalAdapter). Null when WMI can't be read.</summary>
    private static HashSet<Guid>? ReadWmiPhysical()
    {
        try
        {
            var ids = Wmi(@"root\cimv2", "SELECT GUID FROM Win32_NetworkAdapter WHERE PhysicalAdapter = TRUE", o => Str(o, "GUID"), timeoutSeconds: 10);
            return ids.Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToHashSet();
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static int? AsInt(object? v) => v switch
    {
        int i => i,
        long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
        string s when int.TryParse(s, out var n) => n,
        _ => null,
    };
}
