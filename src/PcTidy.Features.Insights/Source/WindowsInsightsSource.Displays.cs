using System.Runtime.InteropServices;

namespace PcTidy.Features.Insights.Source;

/// <summary>Display modes, read with EnumDisplayDevices / EnumDisplaySettings (read-only: no mode is ever set).</summary>
public sealed partial class WindowsInsightsSource
{
    private const int EnumCurrentSettings = -1;
    private const int AttachedToDesktop = 0x1;

    public IReadOnlyList<DisplayFacts> ReadDisplays()
    {
        var list = new List<DisplayFacts>();
        try
        {
            for (uint i = 0; i < 16; i++)
            {
                var device = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
                if (!EnumDisplayDevices(null, i, ref device, 0)) break;
                if ((device.StateFlags & AttachedToDesktop) == 0) continue;

                var current = NewMode();
                if (!EnumDisplaySettings(device.DeviceName, EnumCurrentSettings, ref current)) continue;
                int max = (int)current.dmDisplayFrequency;
                var mode = NewMode();
                for (int m = 0; m < 4096 && EnumDisplaySettings(device.DeviceName, m, ref mode); m++)
                    if (mode.dmPelsWidth == current.dmPelsWidth && mode.dmPelsHeight == current.dmPelsHeight && mode.dmDisplayFrequency > max)
                        max = (int)mode.dmDisplayFrequency;

                var name = MonitorName(device.DeviceName) ?? device.DeviceString;
                list.Add(new DisplayFacts(name.Trim(), (int)current.dmDisplayFrequency, max, (int)current.dmPelsWidth, (int)current.dmPelsHeight));
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or ExternalException) { }
        return list;
    }

    private static string? MonitorName(string adapter)
    {
        var monitor = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
        return EnumDisplayDevices(adapter, 0, ref monitor, 0) && monitor.DeviceString.Length > 0 ? monitor.DeviceString : null;
    }

    private static DevMode NewMode() => new() { dmSize = (short)Marshal.SizeOf<DevMode>() };

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DisplayDevice lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DevMode lpDevMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public int dmDisplayFlags;
        public uint dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }
}
