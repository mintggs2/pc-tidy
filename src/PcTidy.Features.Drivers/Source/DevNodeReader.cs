using System.Runtime.InteropServices;

namespace PcTidy.Features.Drivers.Source;

/// <summary>
/// Reads device facts straight from the Configuration Manager (cfgmgr32), READ-ONLY and without admin:
/// whether a device is connected now, and its last arrival / removal / install times as typed FILETIME values.
/// This is the non-localized source for the ghost-device 30-day wait (pnputil prints these dates in the regional format).
/// </summary>
internal static class DevNodeReader
{
    private const uint CmLocateDevNodeNormal = 0;   // present devices only
    private const uint CmLocateDevNodePhantom = 1;  // also devices that aren't connected
    private const uint CrSuccess = 0;
    private const uint DevPropTypeFileTime = 0x10;

    // DEVPKEY_Device_InstallDate (100), _LastArrivalDate (102), _LastRemovalDate (103) share this format id (devpkey.h).
    private static readonly Guid DeviceDatesFormat = new("83da6326-97a6-4088-9453-a1923f573b29");

    public static IReadOnlyDictionary<string, DeviceFacts> Read(IEnumerable<string> instanceIds, CancellationToken ct)
    {
        var map = new Dictionary<string, DeviceFacts>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in instanceIds.Where(i => !string.IsNullOrWhiteSpace(i)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (NativeMethods.CM_Locate_DevNodeW(out var node, id, CmLocateDevNodePhantom) != CrSuccess) continue;
                bool present = NativeMethods.CM_Locate_DevNodeW(out _, id, CmLocateDevNodeNormal) == CrSuccess;
                map[id] = new DeviceFacts(present, FileTime(node, 102), FileTime(node, 103), FileTime(node, 100));
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ArgumentException)
            {
                break; // cfgmgr32 not usable here: every date stays unknown (nothing gets offered)
            }
        }
        return map;
    }

    private static DateTime? FileTime(uint node, uint pid)
    {
        var key = new DevPropKey { FormatId = DeviceDatesFormat, PropertyId = pid };
        var buffer = new byte[8];
        uint size = (uint)buffer.Length;
        if (NativeMethods.CM_Get_DevNode_PropertyW(node, ref key, out var type, buffer, ref size, 0) != CrSuccess) return null;
        if (type != DevPropTypeFileTime || size != 8) return null;
        long ticks = BitConverter.ToInt64(buffer, 0);
        if (ticks <= 0) return null;
        try { return DateTime.FromFileTimeUtc(ticks); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    private static class NativeMethods
    {
        /// <summary>Read-only: finds a device node by instance id.</summary>
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern uint CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

        /// <summary>Read-only: reads one typed device property.</summary>
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern uint CM_Get_DevNode_PropertyW(uint devInst, ref DevPropKey propertyKey, out uint propertyType,
            byte[] buffer, ref uint bufferSize, uint flags);
    }
}
