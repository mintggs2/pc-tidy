using System.Runtime.InteropServices;
using Microsoft.Win32;
using PcTidy.Features.PcCare.Model;

namespace PcTidy.Features.PcCare.Source;

/// <summary>
/// Saved Wi-Fi profiles via the native WLAN API, read-only (WlanEnumInterfaces, WlanGetProfileList and the current
/// connection). "Last connected" comes from the NetworkList registry key when Windows recorded it.
/// </summary>
public sealed partial class WindowsPcCareSource
{
    private const int InterfaceInfoSize = 16 + 512 + 4;   // GUID + WCHAR[256] + state
    private const int ProfileInfoSize = 512 + 4;          // WCHAR[256] + flags
    private const int OpcodeCurrentConnection = 7;
    private const int StateConnected = 1;

    private static WlanFacts ReadWlan()
    {
        IntPtr handle;
        uint err;
        try { err = Native.WlanOpenHandle(2, IntPtr.Zero, out _, out handle); }
        catch (DllNotFoundException) { return new WlanFacts(false, Array.Empty<WlanProfile>()); }
        if (err != 0) return new WlanFacts(false, Array.Empty<WlanProfile>()); // WLAN service off or no Wi-Fi hardware

        try
        {
            if (Native.WlanEnumInterfaces(handle, IntPtr.Zero, out var list) != 0) return new WlanFacts(false, Array.Empty<WlanProfile>());
            var interfaces = new List<(Guid Id, string Name, int State)>();
            try
            {
                int count = Marshal.ReadInt32(list);
                for (int i = 0; i < count; i++)
                {
                    var item = list + 8 + i * InterfaceInfoSize;
                    var guid = Marshal.PtrToStructure<Guid>(item);
                    var name = Marshal.PtrToStringUni(item + 16, 256).TrimEnd('\0');
                    interfaces.Add((guid, name, Marshal.ReadInt32(item + 16 + 512)));
                }
            }
            finally { Native.WlanFreeMemory(list); }
            if (interfaces.Count == 0) return new WlanFacts(false, Array.Empty<WlanProfile>());

            var lastSeen = LastConnectedByName();
            var profiles = new List<WlanProfile>();
            foreach (var (id, name, state) in interfaces)
            {
                var connected = state == StateConnected ? CurrentProfile(handle, id) : null;
                var guid = id;
                if (Native.WlanGetProfileList(handle, ref guid, IntPtr.Zero, out var pl) != 0) continue;
                try
                {
                    int count = Marshal.ReadInt32(pl);
                    for (int i = 0; i < count; i++)
                    {
                        var profileName = Marshal.PtrToStringUni(pl + 8 + i * ProfileInfoSize, 256).TrimEnd('\0');
                        if (profileName.Length == 0) continue;
                        profiles.Add(new WlanProfile(profileName, id, name, string.Equals(profileName, connected, StringComparison.Ordinal),
                            lastSeen.TryGetValue(profileName, out var t) ? t : null));
                    }
                }
                finally { Native.WlanFreeMemory(pl); }
            }
            return new WlanFacts(true, profiles);
        }
        finally { Native.WlanCloseHandle(handle, IntPtr.Zero); }
    }

    private static string? CurrentProfile(IntPtr handle, Guid id)
    {
        if (Native.WlanQueryInterface(handle, ref id, OpcodeCurrentConnection, IntPtr.Zero, out _, out var data, IntPtr.Zero) != 0) return null;
        try { return Marshal.PtrToStringUni(data + 8, 256).TrimEnd('\0'); } // WLAN_CONNECTION_ATTRIBUTES.strProfileName
        finally { Native.WlanFreeMemory(data); }
    }

    /// <summary>NetworkList\Profiles\{guid}: ProfileName + DateLastConnected (SYSTEMTIME, local time).</summary>
    private static Dictionary<string, DateTime> LastConnectedByName()
    {
        var map = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        using var root = OpenRead(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList\Profiles");
        foreach (var sub in root?.GetSubKeyNames() ?? Array.Empty<string>())
        {
            using var k = root!.OpenSubKey(sub, writable: false);
            if (k?.GetValue("ProfileName") is not string name || k.GetValue("DateLastConnected") is not byte[] { Length: >= 16 } st) continue;
            try
            {
                var local = new DateTime(BitConverter.ToUInt16(st, 0), BitConverter.ToUInt16(st, 2), BitConverter.ToUInt16(st, 6),
                    BitConverter.ToUInt16(st, 8), BitConverter.ToUInt16(st, 10), BitConverter.ToUInt16(st, 12), DateTimeKind.Local);
                var utc = local.ToUniversalTime();
                if (!map.TryGetValue(name, out var known) || utc > known) map[name] = utc;
            }
            catch (ArgumentOutOfRangeException) { }
        }
        return map;
    }

    private static class Native
    {
        [DllImport("wlanapi.dll")] public static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);
        [DllImport("wlanapi.dll")] public static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);
        [DllImport("wlanapi.dll")] public static extern uint WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);
        [DllImport("wlanapi.dll")] public static extern uint WlanGetProfileList(IntPtr clientHandle, ref Guid interfaceGuid, IntPtr reserved, out IntPtr profileList);
        [DllImport("wlanapi.dll")] public static extern uint WlanQueryInterface(IntPtr clientHandle, ref Guid interfaceGuid, int opCode, IntPtr reserved,
            out uint dataSize, out IntPtr data, IntPtr opcodeValueType);
        [DllImport("wlanapi.dll")] public static extern void WlanFreeMemory(IntPtr memory);
    }
}
