using System.Runtime.InteropServices;
using System.Text;

namespace PcTidy.Features.Tweaks.Reading;

/// <summary>
/// Lists System-class devices that Device Manager shows as disabled, read-only through SetupAPI (enumerate the class)
/// and cfgmgr32 (CM_Get_DevNode_Status: DN_HAS_PROBLEM with CM_PROB_DISABLED, code 22). Nothing is opened for writing,
/// no device is touched, and the device's language-independent instance id is kept next to its display name.
/// </summary>
internal static class DeviceReader
{
    /// <summary>GUID_DEVCLASS_SYSTEM ("System devices": HALs, buses, bridges, ACPI, timers).</summary>
    private static readonly Guid SystemClass = new("4d36e97d-e325-11ce-bfc1-08002be10318");

    private const uint DigcfPresent = 0x2;
    private const uint DnHasProblem = 0x400;
    private const uint CmProbDisabled = 22;
    private const uint CrSuccess = 0;
    private const uint SpdrpDeviceDesc = 0x0;
    private const uint SpdrpFriendlyName = 0xC;
    private const int MaxDevices = 2_000;

    public static IReadOnlyList<DisabledDevice>? DisabledSystemDevices()
    {
        try
        {
            var classGuid = SystemClass;
            var set = SetupDiGetClassDevsW(ref classGuid, IntPtr.Zero, IntPtr.Zero, DigcfPresent);
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return null;
            try
            {
                var list = new List<DisabledDevice>();
                var data = new SpDevinfoData { CbSize = (uint)Marshal.SizeOf<SpDevinfoData>() };
                for (uint i = 0; i < MaxDevices && SetupDiEnumDeviceInfo(set, i, ref data); i++)
                {
                    if (CM_Get_DevNode_Status(out var status, out var problem, data.DevInst, 0) != CrSuccess) continue;
                    if ((status & DnHasProblem) == 0 || problem != CmProbDisabled) continue;
                    var id = InstanceId(set, ref data);
                    if (id is null) continue;
                    var name = Property(set, ref data, SpdrpFriendlyName) ?? Property(set, ref data, SpdrpDeviceDesc) ?? id;
                    list.Add(new DisabledDevice(name, id) { ClassGuid = data.ClassGuid == Guid.Empty ? null : data.ClassGuid });
                }
                return list;
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static string? InstanceId(IntPtr set, ref SpDevinfoData data)
    {
        var buffer = new char[512];
        return SetupDiGetDeviceInstanceIdW(set, ref data, buffer, buffer.Length, out var needed) && needed > 0
            ? new string(buffer, 0, Math.Min(needed, buffer.Length)).TrimEnd('\0')
            : null;
    }

    private static string? Property(IntPtr set, ref SpDevinfoData data, uint property)
    {
        var buffer = new byte[1024];
        if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, buffer, (uint)buffer.Length, out var needed) || needed == 0) return null;
        var text = Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(needed, (uint)buffer.Length)).TrimEnd('\0').Trim();
        return text.Length == 0 ? null : text;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevinfoData
    {
        public uint CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SpDevinfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr deviceInfoSet, ref SpDevinfoData deviceInfoData, char[] deviceInstanceId, int deviceInstanceIdSize, out int requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr deviceInfoSet, ref SpDevinfoData deviceInfoData, uint property,
        out uint propertyRegDataType, byte[] propertyBuffer, uint propertyBufferSize, out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_DevNode_Status(out uint status, out uint problemNumber, uint devInst, uint flags);
}

/// <summary>The active power plan and every plan on this PC, read-only through the power API (PowerGetActiveScheme, PowerEnumerate).</summary>
internal static class PowerPlanReader
{
    private const uint AccessScheme = 16;
    private const int MaxPlans = 64;

    public static PowerPlanInfo? Read()
    {
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out var ptr) != 0 || ptr == IntPtr.Zero) return null;
            Guid active;
            try { active = Marshal.PtrToStructure<Guid>(ptr); }
            finally { LocalFree(ptr); }

            var installed = new List<Guid>();
            for (uint i = 0; i < MaxPlans; i++)
            {
                var guid = Guid.Empty;
                uint size = (uint)Marshal.SizeOf<Guid>();
                if (PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, AccessScheme, i, ref guid, ref size) != 0) break;
                installed.Add(guid);
            }
            return new PowerPlanInfo(active, FriendlyName(active), installed);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Display only: the plan's name in the user's language, never compared with anything.</summary>
    private static string? FriendlyName(Guid scheme)
    {
        uint size = 0;
        if (PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, null, ref size) != 0 || size == 0 || size > 4096) return null;
        var buffer = new byte[size];
        if (PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size) != 0) return null;
        var name = Encoding.Unicode.GetString(buffer).TrimEnd('\0').Trim();
        return name.Length == 0 ? null : name;
    }

    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);
    [DllImport("powrprof.dll")]
    private static extern uint PowerEnumerate(IntPtr rootPowerKey, IntPtr schemeGuid, IntPtr subGroupOfPowerSettingsGuid, uint accessFlags, uint index, ref Guid buffer, ref uint bufferSize);
    [DllImport("powrprof.dll")]
    private static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid, IntPtr subGroupOfPowerSettingsGuid, IntPtr powerSettingGuid, byte[]? buffer, ref uint bufferSize);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr hMem);
}
