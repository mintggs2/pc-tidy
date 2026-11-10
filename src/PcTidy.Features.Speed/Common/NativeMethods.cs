using System.Runtime.InteropServices;
using System.Text;

namespace PcTidy.Features.Speed.Common;

/// <summary>
/// Read-only Win32 calls used by the Speed scanners. Nothing here changes the system: these query trust,
/// processes, power, sessions, GPU capabilities and Restart Manager lock owners.
/// </summary>
internal static class NativeMethods
{
    // ------------------------------------------------------------------ WinVerifyTrust (signature check)

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

    // ------------------------------------------------------------------ processes

    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    internal struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessIoCounters(IntPtr process, out IO_COUNTERS counters);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);

    // ------------------------------------------------------------------ power

    [StructLayout(LayoutKind.Sequential)]
    internal struct SYSTEM_POWER_STATUS
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
    internal static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    internal const uint ACCESS_SCHEME = 16;
    internal static readonly Guid NoSubgroupGuid = new("fea3413e-7e05-4911-9a71-700331f1c294");
    internal static readonly Guid PowerSchemePersonalityGuid = new("245d8541-3943-4422-b025-13a784f679b7");

    [DllImport("powrprof.dll")]
    internal static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    internal static extern uint PowerEnumerate(IntPtr rootPowerKey, IntPtr schemeGuid, IntPtr subGroupOfPowerSettingsGuid, uint accessFlags,
        uint index, ref Guid buffer, ref uint bufferSize);

    [DllImport("powrprof.dll")]
    internal static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid, IntPtr subGroupOfPowerSettingsGuid,
        IntPtr powerSettingGuid, byte[]? buffer, ref uint bufferSize);

    [DllImport("powrprof.dll")]
    internal static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupOfPowerSettingsGuid,
        ref Guid powerSettingGuid, out uint acValueIndex);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr LocalFree(IntPtr mem);

    // ------------------------------------------------------------------ session (sign-in time)

    internal static readonly IntPtr WTS_CURRENT_SERVER_HANDLE = IntPtr.Zero;
    internal const int WTS_CURRENT_SESSION = -1;
    internal const int WTSSessionInfo = 24;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WTSINFOW
    {
        public int State;
        public int SessionId;
        public int IncomingBytes, OutgoingBytes, IncomingFrames, OutgoingFrames, IncomingCompressedBytes, OutgoingCompressedBytes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string WinStationName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 17)] public string Domain;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)] public string UserName;
        public long ConnectTime, DisconnectTime, LastInputTime, LogonTime, CurrentTime;
    }

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "WTSQuerySessionInformationW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out uint bytes);

    [DllImport("wtsapi32.dll")]
    internal static extern void WTSFreeMemory(IntPtr memory);

    // ------------------------------------------------------------------ GPU scheduling capability (D3DKMT, read-only)

    internal const int KMTQAITYPE_WDDM_2_7_CAPS = 70;

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3DKMT_ADAPTERINFO
    {
        public uint hAdapter;
        public long AdapterLuid;
        public uint NumOfSources;
        public int bPrecisePresentRegionsPreferred;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3DKMT_ENUMADAPTERS2
    {
        public uint NumAdapters;
        public IntPtr pAdapters;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3DKMT_QUERYADAPTERINFO
    {
        public uint hAdapter;
        public int Type;
        public IntPtr pPrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3DKMT_CLOSEADAPTER
    {
        public uint hAdapter;
    }

    [DllImport("gdi32.dll")]
    internal static extern int D3DKMTEnumAdapters2(ref D3DKMT_ENUMADAPTERS2 args);

    [DllImport("gdi32.dll")]
    internal static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO args);

    [DllImport("gdi32.dll")]
    internal static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER args);

    // ------------------------------------------------------------------ Restart Manager (who holds a file open; read-only)

    internal const int CCH_RM_SESSION_KEY = 32;
    internal const int CCH_RM_MAX_APP_NAME = 255;
    internal const int CCH_RM_MAX_SVC_NAME = 63;
    internal const int ERROR_SUCCESS = 0;
    internal const int ERROR_MORE_DATA = 234;

    [StructLayout(LayoutKind.Sequential)]
    internal struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct RM_PROCESS_INFO
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
    internal static extern int RmStartSession(out uint sessionHandle, int flags, StringBuilder sessionKey);

    [DllImport("rstrtmgr.dll")]
    internal static extern int RmEndSession(uint sessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    internal static extern int RmRegisterResources(uint sessionHandle, uint nFiles, string[] files, uint nApplications,
        RM_UNIQUE_PROCESS[]? applications, uint nServices, string[]? services);

    [DllImport("rstrtmgr.dll")]
    internal static extern int RmGetList(uint sessionHandle, out uint procInfoNeeded, ref uint procInfo,
        [In, Out] RM_PROCESS_INFO[]? affectedApps, ref uint rebootReasons);

    // ------------------------------------------------------------------ indirect strings ("@shell32.dll,-8506")

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int SHLoadIndirectString(string source, StringBuilder output, uint outputSize, IntPtr reserved);
}
