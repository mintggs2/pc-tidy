using System.Runtime.InteropServices;

namespace PcTidy.Features.Services.Sources;

/// <summary>
/// Read-only Win32 calls used by the Services scanner: listing services and reading their configuration through the
/// Service Control Manager (never ChangeServiceConfig; handles are opened with query rights only), checking file
/// signatures offline, and reading process CPU time.
/// </summary>
internal static class NativeMethods
{
    // ------------------------------------------------------------------ Service Control Manager (query only)

    internal const uint SC_MANAGER_CONNECT = 0x0001;
    internal const uint SC_MANAGER_ENUMERATE_SERVICE = 0x0004;
    internal const uint SERVICE_QUERY_CONFIG = 0x0001;
    internal const uint SERVICE_WIN32 = 0x30;
    internal const uint SERVICE_DRIVER = 0x0B;
    internal const uint SERVICE_STATE_ALL = 0x3;
    internal const uint SC_ENUM_PROCESS_INFO = 0;
    internal const uint SERVICE_CONFIG_DESCRIPTION = 1;
    internal const uint SERVICE_CONFIG_DELAYED_AUTO_START_INFO = 3;
    internal const uint SERVICE_CONFIG_TRIGGER_INFO = 8;
    internal const int ERROR_MORE_DATA = 234;
    internal const int ERROR_INSUFFICIENT_BUFFER = 122;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SERVICE_STATUS_PROCESS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
        public uint dwProcessId;
        public uint dwServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ENUM_SERVICE_STATUS_PROCESSW
    {
        public IntPtr lpServiceName;
        public IntPtr lpDisplayName;
        public SERVICE_STATUS_PROCESS ServiceStatusProcess;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct QUERY_SERVICE_CONFIGW
    {
        public uint dwServiceType;
        public uint dwStartType;
        public uint dwErrorControl;
        public IntPtr lpBinaryPathName;
        public IntPtr lpLoadOrderGroup;
        public uint dwTagId;
        public IntPtr lpDependencies;
        public IntPtr lpServiceStartName;
        public IntPtr lpDisplayName;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SERVICE_DESCRIPTIONW
    {
        public IntPtr lpDescription;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SERVICE_DELAYED_AUTO_START_INFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fDelayedAutostart;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SERVICE_TRIGGER_INFO
    {
        public uint cTriggers;
        public IntPtr pTriggers;
        public IntPtr pReserved;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "OpenSCManagerW")]
    internal static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "OpenServiceW")]
    internal static extern IntPtr OpenService(IntPtr scm, string serviceName, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "EnumServicesStatusExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumServicesStatusEx(IntPtr scm, uint infoLevel, uint serviceType, uint serviceState, IntPtr services,
        uint bufferSize, out uint bytesNeeded, out uint servicesReturned, ref uint resumeHandle, string? groupName);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "QueryServiceConfigW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryServiceConfig(IntPtr service, IntPtr config, uint bufferSize, out uint bytesNeeded);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "QueryServiceConfig2W")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryServiceConfig2(IntPtr service, uint infoLevel, IntPtr buffer, uint bufferSize, out uint bytesNeeded);

    // ------------------------------------------------------------------ processes (limited query rights)

    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    // ------------------------------------------------------------------ WinVerifyTrust (signature check, offline)

    internal static readonly Guid WintrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    internal const uint WTD_UI_NONE = 2;
    internal const uint WTD_REVOKE_NONE = 0;
    internal const uint WTD_CHOICE_FILE = 1;
    internal const uint WTD_CHOICE_CATALOG = 2;
    internal const uint WTD_STATEACTION_VERIFY = 1;
    internal const uint WTD_STATEACTION_CLOSE = 2;
    internal const uint WTD_REVOCATION_CHECK_NONE = 0x10;
    internal const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000;
    internal const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100);
    internal const int TRUST_E_SUBJECT_FORM_UNKNOWN = unchecked((int)0x800B0003);
    internal const int TRUST_E_PROVIDER_UNKNOWN = unchecked((int)0x800B0001);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WINTRUST_CATALOG_INFO
    {
        public uint cbStruct;
        public uint dwCatalogVersion;
        public string pcwszCatalogFilePath;
        public string pcwszMemberTag;
        public string pcwszMemberFilePath;
        public IntPtr hMemberFile;
        public IntPtr pbCalculatedFileHash;
        public uint cbCalculatedFileHash;
        public IntPtr pcCatalogContext;
        public IntPtr hCatAdmin;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pInfo;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct CATALOG_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string wszCatalogFile;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    internal static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, ref WINTRUST_DATA pWVTData);

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CryptCATAdminAcquireContext2(out IntPtr phCatAdmin, IntPtr pgSubsystem, string pwszHashAlgorithm, IntPtr pStrongHashPolicy, uint dwFlags);

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr hCatAdmin, IntPtr hFile, ref uint pcbHash, byte[]? pbHash, uint dwFlags);

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    internal static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr hCatAdmin, byte[] pbHash, uint cbHash, uint dwFlags, IntPtr phPrevCatInfo);

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CryptCATCatalogInfoFromContext(IntPtr hCatInfo, ref CATALOG_INFO psCatInfo, uint dwFlags);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CryptCATAdminReleaseCatalogContext(IntPtr hCatAdmin, IntPtr hCatInfo, uint dwFlags);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CryptCATAdminReleaseContext(IntPtr hCatAdmin, uint dwFlags);
}
