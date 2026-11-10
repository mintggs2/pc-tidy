using System.Runtime.InteropServices;
using System.Text;
using ComFileTime = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace PcTidy.Platform;

/// <summary>All Win32 P/Invoke declarations used by PcTidy.Platform, in one place.</summary>
internal static partial class NativeMethods
{
    public const int ERROR_SUCCESS = 0;
    public const int ERROR_MORE_DATA = 234;
    public const int ERROR_CANCELLED = 1223;
    public const int ERROR_ACCESS_DENIED = 5;

    // ---------------- Restart Manager ----------------
    public const int CCH_RM_SESSION_KEY = 32;
    private const int CCH_RM_MAX_APP_NAME = 255;
    private const int CCH_RM_MAX_SVC_NAME = 63;

    [StructLayout(LayoutKind.Sequential)]
    public struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public ComFileTime ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_APP_NAME + 1)] public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_SVC_NAME + 1)] public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    public static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, StringBuilder strSessionKey);

    [DllImport("rstrtmgr.dll")]
    public static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    public static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames,
        uint nApplications, RM_UNIQUE_PROCESS[]? rgApplications, uint nServices, string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    public static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[]? rgAffectedApps, ref uint lpdwRebootReasons);

    // ---------------- Recycle Bin ----------------
    public const uint SHERB_NOCONFIRMATION = 0x1;
    public const uint SHERB_NOPROGRESSUI = 0x2;
    public const uint SHERB_NOSOUND = 0x4;
    /// <summary>SHEmptyRecycleBin returns this when the bin is already empty.</summary>
    public const int E_UNEXPECTED = unchecked((int)0x8000FFFF);

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHQueryRecycleBinW(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHEmptyRecycleBinW(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    // ---------------- Services ----------------
    public const uint SC_MANAGER_CONNECT = 0x0001;
    public const uint SERVICE_QUERY_CONFIG = 0x0001;
    public const uint SERVICE_CHANGE_CONFIG = 0x0002;
    public const uint SERVICE_NO_CHANGE = 0xFFFFFFFF;
    public const uint SERVICE_CONFIG_DELAYED_AUTO_START_INFO = 3;

    [StructLayout(LayoutKind.Sequential)]
    public struct SERVICE_DELAYED_AUTO_START_INFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fDelayedAutostart;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint dwAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr OpenServiceW(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ChangeServiceConfigW(IntPtr hService, uint dwServiceType, uint dwStartType, uint dwErrorControl,
        string? lpBinaryPathName, string? lpLoadOrderGroup, IntPtr lpdwTagId, string? lpDependencies,
        string? lpServiceStartName, string? lpPassword, string? lpDisplayName);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ChangeServiceConfig2W(IntPtr hService, uint dwInfoLevel, ref SERVICE_DELAYED_AUTO_START_INFO lpInfo);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseServiceHandle(IntPtr hSCObject);

    // ---------------- Named pipes / processes / tokens ----------------
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint TOKEN_QUERY = 0x0008;
    public const int TokenElevationType = 18;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder exeName, ref int size);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, out int tokenInformation,
        int tokenInformationLength, out int returnLength);

    // ---------------- Power ----------------
    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
}
