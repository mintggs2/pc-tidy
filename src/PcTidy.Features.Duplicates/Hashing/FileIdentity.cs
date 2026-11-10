using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PcTidy.Features.Duplicates.Hashing;

/// <summary>The volume + 128-bit file id that names one file's data, and how many names (hard links) it has.</summary>
public readonly record struct FileId(ulong Volume, ulong IdLow, ulong IdHigh, uint LinkCount);

/// <summary>
/// Reads a file's identity without reading its content: the handle is opened with FILE_READ_ATTRIBUTES only,
/// which never recalls a cloud file and never blocks other apps. Two paths with the same id are hard links.
/// </summary>
public static class FileIdentity
{
    private const uint FileReadAttributes = 0x80;
    private const uint OpenExisting = 3;
    private const uint FlagBackupSemantics = 0x02000000;
    private const uint FlagOpenReparsePoint = 0x00200000;
    private const int FileStandardInfoClass = 1;
    private const int FileIdInfoClass = 18;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        public ulong IdLow;
        public ulong IdHigh;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileStandardInfo
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending;
        public byte Directory;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle OpenHandle(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out FileIdInfo info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out FileStandardInfo info, uint size);

    /// <summary>The file's identity, or null if it can't be read (gone, denied).</summary>
    public static FileId? TryGet(string path)
    {
        // Share everything (read | write | delete = 7): this must never get in another app's way.
        using var handle = OpenHandle(Win32Path.Raw(path), FileReadAttributes, 7, IntPtr.Zero, OpenExisting,
            FlagBackupSemantics | FlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        if (!GetFileInformationByHandleEx(handle, FileIdInfoClass, out FileIdInfo id, (uint)Marshal.SizeOf<FileIdInfo>())) return null;
        uint links = 1;
        if (GetFileInformationByHandleEx(handle, FileStandardInfoClass, out FileStandardInfo std, (uint)Marshal.SizeOf<FileStandardInfo>()))
            links = Math.Max(1, std.NumberOfLinks);
        return new FileId(id.VolumeSerialNumber, id.IdLow, id.IdHigh, links);
    }
}

/// <summary>Path spellings for raw Win32 / FileStream calls.</summary>
public static class Win32Path
{
    /// <summary>
    /// "\\?\" form, so names Windows' normal path rules would change (trailing dots or spaces, reserved names like
    /// CON.txt) and paths over 260 characters still reach the exact file the listing returned.
    /// </summary>
    public static string Raw(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)) return path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + path[2..];
        return @"\\?\" + path;
    }
}
