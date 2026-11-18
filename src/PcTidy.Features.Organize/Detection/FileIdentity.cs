using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PcTidy.Features.Organize.Detection;

/// <summary>
/// Read-only facts from the file system itself (3.3): a file's identity on its drive, and the real path of a folder
/// (links, subst drives and short 8.3 names resolved). Opens with "read attributes" only: never reads contents, so an
/// online-only OneDrive file is never downloaded.
/// </summary>
public static class FileIdentity
{
    /// <summary>
    /// The drive's serial number plus the file's index on it, or null. A file renamed or moved on the same drive keeps
    /// it, so the same id at the new place proves it is the very same file (not a copy).
    /// </summary>
    public static string? Id(string path)
    {
        using var h = Open(path, folder: false);
        if (h.IsInvalid || !GetFileInformationByHandle(h, out var info)) return null;
        return $"{info.VolumeSerial:X8}-{info.IndexHigh:X8}{info.IndexLow:X8}";
    }

    /// <summary>The folder's real path as Windows resolves it (links, subst, 8.3 names), or null when it can't tell.</summary>
    public static string? FinalPath(string existingFolder)
    {
        using var h = Open(existingFolder, folder: true);
        if (h.IsInvalid) return null;
        var buf = new StringBuilder(1024);
        uint n = GetFinalPathNameByHandleW(h, buf, (uint)buf.Capacity, 0);
        if (n == 0 || n >= buf.Capacity) return null;
        var p = buf.ToString();
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + p[8..];
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) p = p[4..];
        return p.Length >= 2 && p[1] == ':' ? p : null; // a volume without a drive letter: can't compare
    }

    /// <summary>The literal path for Win32 calls, so names ending in a dot or space are looked up as they are.</summary>
    public static string Io(string path) => path.StartsWith(@"\\", StringComparison.Ordinal) ? path : @"\\?\" + path;

    private static SafeFileHandle Open(string path, bool folder)
    {
        try
        {
            return CreateFileW(Io(path), FileReadAttributes, ShareAll, IntPtr.Zero, OpenExisting, folder ? BackupSemantics : 0, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException) { return new SafeFileHandle(IntPtr.Zero, true); }
    }

    private const uint FileReadAttributes = 0x80, ShareAll = 0x7, OpenExisting = 3, BackupSemantics = 0x02000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
}
