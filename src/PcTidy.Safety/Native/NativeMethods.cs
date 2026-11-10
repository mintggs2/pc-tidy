using System.Runtime.InteropServices;

namespace PcTidy.Safety.Native;

/// <summary>Read-only Win32 calls used for path normalisation and detection. Nothing here changes the system.</summary>
internal static partial class NativeMethods
{
    public static readonly IntPtr InvalidHandle = new(-1);

    public const uint ReparseTagNameSurrogateBit = 0x20000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct Win32FindData
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        /// <summary>Reparse tag when the entry is a reparse point.</summary>
        public uint dwReserved0;
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
    }

    public const int FindExInfoBasic = 1;
    public const int FindExSearchNameMatch = 0;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "FindFirstFileExW")]
    public static extern IntPtr FindFirstFileEx(string lpFileName, int fInfoLevelId, out Win32FindData lpFindFileData,
        int fSearchOp, IntPtr lpSearchFilter, int dwAdditionalFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FindClose(IntPtr hFindFile);

    [StructLayout(LayoutKind.Sequential)]
    public struct Win32FileAttributeData
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
    }

    public const int GetFileExInfoStandard = 0;

    /// <summary>Attributes by name lookup. Works where the folder can't be listed (Windows\Temp for a standard user).</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFileAttributesExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetFileAttributesEx(string lpFileName, int fInfoLevelId, out Win32FileAttributeData lpFileInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetLongPathNameW")]
    public static extern uint GetLongPathName(string lpszShortPath, [Out] char[] lpszLongPath, uint cchBuffer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "QueryDosDeviceW")]
    public static extern uint QueryDosDevice(string lpDeviceName, [Out] char[] lpTargetPath, uint ucchMax);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetVolumePathNamesForVolumeNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVolumePathNamesForVolumeName(string lpszVolumeName, [Out] char[] lpszVolumePathNames,
        uint cchBufferLength, out uint lpcchReturnLength);

    /// <summary>Do not verify the folder exists (avoids creating/touching anything).</summary>
    public const uint KfFlagDontVerify = 0x00004000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);
}
