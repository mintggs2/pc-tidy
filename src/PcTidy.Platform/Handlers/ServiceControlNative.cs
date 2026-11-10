using System.Runtime.InteropServices;

namespace PcTidy.Platform;

/// <summary>Service recovery (failure actions) P/Invokes, used only by <see cref="WindowsServiceControl"/>.</summary>
internal static partial class NativeMethods
{
    public const uint SERVICE_START = 0x0010;
    public const uint SERVICE_CONFIG_FAILURE_ACTIONS = 2;
    public const uint SERVICE_CONFIG_FAILURE_ACTIONS_FLAG = 4;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;
    /// <summary>SC_ACTION_RESTART: restoring it needs SERVICE_START access on the handle.</summary>
    public const int SC_ACTION_RESTART = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct SERVICE_FAILURE_ACTIONSW
    {
        public uint dwResetPeriod;
        public IntPtr lpRebootMsg;
        public IntPtr lpCommand;
        public uint cActions;
        public IntPtr lpsaActions;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SC_ACTION
    {
        public int Type;
        public uint Delay;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SERVICE_FAILURE_ACTIONS_FLAG
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fFailureActionsOnNonCrashFailures;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "QueryServiceConfig2W")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryServiceConfig2W(IntPtr hService, uint dwInfoLevel, IntPtr lpBuffer, uint cbBufSize, out uint pcbBytesNeeded);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "ChangeServiceConfig2W")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ChangeServiceConfig2Ptr(IntPtr hService, uint dwInfoLevel, IntPtr lpInfo);
}
