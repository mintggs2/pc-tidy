using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PcTidy.Engine.IO;

internal static class NativeMethods
{
    public const int ErrorFileNotFound = 2;
    public const int ErrorPathNotFound = 3;
    public const int ErrorAccessDenied = 5;
    public const int ErrorNotSameDevice = 17;
    public const int ErrorSharingViolation = 32;
    public const int ErrorFileExists = 80;
    public const int ErrorInvalidParameter = 87;
    public const int ErrorNotSupported = 50;
    public const int ErrorDirNotEmpty = 145;
    public const int ErrorAlreadyExists = 183;

    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;
    public const uint Delete = 0x00010000;
    public const uint Synchronize = 0x00100000;
    public const uint FileReadAttributes = 0x0080;
    public const uint FileWriteAttributes = 0x0100;
    public const uint FileTraverse = 0x0020;
    public const uint FileShareRead = 0x1;
    public const uint FileShareWrite = 0x2;
    public const uint FileShareDelete = 0x4;
    public const uint FileShareAll = FileShareRead | FileShareWrite | FileShareDelete;
    public const uint OpenExisting = 3;
    public const uint FileFlagBackupSemantics = 0x02000000;
    public const uint FileFlagOpenReparsePoint = 0x00200000;
    public const uint FsctlSetReparsePoint = 0x000900A4;
    public const uint FsctlGetReparsePoint = 0x000900A8;
    public const uint IoReparseTagMountPoint = 0xA0000003;

    // FILE_INFO_BY_HANDLE_CLASS
    public const int FileBasicInfo = 0;
    public const int FileStandardInfo = 1;
    public const int FileDispositionInfo = 4;
    public const int FileStreamInfo = 7;
    public const int FileAttributeTagInfo = 9;
    public const int ErrorHandleEof = 38;
    public const int ErrorMoreData = 234;

    [DllImport("kernel32.dll", EntryPoint = "SetFileAttributesW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetFileAttributes(string fileName, uint attributes);

    [DllImport("kernel32.dll", EntryPoint = "GetFileAttributesW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint GetFileAttributes(string fileName);
    public const int FileDispositionInfoEx = 21;

    public const uint FileDispositionFlagDelete = 0x1;
    public const uint FileDispositionFlagIgnoreReadOnly = 0x10;

    /// <summary>
    /// Rename only: flags = 0 means "no copy across volumes, never replace an existing file".
    /// (File.Move silently falls back to copy+delete across volumes, without our verification.)
    /// </summary>
    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool MoveFileEx(string existingFileName, string newFileName, uint flags);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, byte[]? inBuffer, int inBufferSize,
        byte[]? outBuffer, int outBufferSize, out int bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, byte[] info, int size);

    public const int FileRenameInformation = 10; // FILE_INFORMATION_CLASS (ntdll)
    public const int StatusNotSameDevice = unchecked((int)0xC00000D4);
    public const int StatusObjectNameCollision = unchecked((int)0xC0000035);

    [StructLayout(LayoutKind.Sequential)]
    public struct IoStatusBlock
    {
        public IntPtr Status;
        public IntPtr Information;
    }

    [DllImport("ntdll.dll")]
    public static extern int NtSetInformationFile(SafeFileHandle file, out IoStatusBlock ioStatus, byte[] info, int length, int infoClass);

    [DllImport("ntdll.dll")]
    public static extern int RtlNtStatusToDosError(int status);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, byte[] info, int size);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, int size, uint flags);

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVolumeInformation(string rootPathName, StringBuilder? volumeName, int volumeNameSize,
        out uint serialNumber, out uint maxComponentLength, out uint fileSystemFlags, StringBuilder fileSystemName, int fileSystemNameSize);

    [DllImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVolumePathName(string fileName, StringBuilder volumePathName, int bufferLength);
}
