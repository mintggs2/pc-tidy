using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PcTidy.Features.Duplicates.Scanning;

/// <summary>
/// Where a folder really is, after every junction, symbolic link and mapped path on the way (GetFinalPathNameByHandle).
/// Read-only: the folder is opened for attributes only. Used so one folder reached two ways (for example a root that
/// is, or goes through, a junction to another root) is scanned once and never offered as a duplicate of itself.
/// </summary>
internal static class FinalPath
{
    private const uint FileReadAttributes = 0x80;
    private const uint ShareAll = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000; // needed to open a folder

    /// <summary>The folder's real, normalised path, or null when it can't be opened (then callers keep the path as given).</summary>
    public static string? Of(string folder)
    {
        try
        {
            using var handle = CreateFileW(folder, FileReadAttributes, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
            if (handle.IsInvalid) return null;
            var buffer = new StringBuilder(512);
            int n = GetFinalPathNameByHandleW(handle, buffer, buffer.Capacity, 0);
            if (n > buffer.Capacity)
            {
                buffer = new StringBuilder(n + 1);
                n = GetFinalPathNameByHandleW(handle, buffer, buffer.Capacity, 0);
            }
            if (n <= 0 || n > buffer.Capacity) return null;
            return PathUtil.Normalize(StripPrefix(buffer.ToString(0, n)));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>"\\?\C:\x" → "C:\x", "\\?\UNC\server\share" → "\\server\share".</summary>
    internal static string StripPrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path[8..];
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path[4..];
        return path;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern int GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, int size, uint flags);
}
