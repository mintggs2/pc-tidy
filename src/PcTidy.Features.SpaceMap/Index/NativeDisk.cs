using System.Runtime.InteropServices;

namespace PcTidy.Features.SpaceMap.Index;

/// <summary>Reads a reparse point's tag (metadata only; the target is never opened or followed).</summary>
public interface IReparseTagReader
{
    /// <summary>The reparse tag, or null if it can't be read (callers then treat the entry as a link and don't follow it).</summary>
    uint? GetTag(string path);
}

/// <summary>Read-only Win32 queries used by the scanner.</summary>
internal sealed class NativeDisk : IReparseTagReader
{
    public static readonly NativeDisk Instance = new();
    public const int DefaultClusterSize = 4096;

    /// <summary>Name-surrogate tags (junctions, symlinks...) point at another place: never followed.
    /// Cloud-file tags (OneDrive) are not name surrogates: those folders hold real content and are walked.</summary>
    public static bool IsNameSurrogate(uint tag) => (tag & 0x20000000) != 0;

    public uint? GetTag(string path)
    {
        var handle = FindFirstFileExW(LongPath(path.TrimEnd('\\')), 0 /* FindExInfoStandard */, out var data, 0, IntPtr.Zero, 0);
        if (handle == InvalidHandle) return null;
        FindClose(handle);
        return (data.dwFileAttributes & (uint)FileAttributes.ReparsePoint) != 0 ? data.dwReserved0 : null;
    }

    /// <summary>Extended-length form so paths over 260 characters work.</summary>
    private static string LongPath(string path)
        => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
         : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..]
         : @"\\?\" + path;

    /// <summary>Allocation unit of the volume holding <paramref name="path"/> (4 KB if unknown).</summary>
    public static int ClusterSize(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (root is not null && GetDiskFreeSpaceW(root, out var spc, out var bps, out _, out _))
            {
                long cluster = (long)spc * bps;
                if (cluster is > 0 and <= 1 << 24) return (int)cluster;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { }
        return DefaultClusterSize;
    }

    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindData
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstFileExW(string lpFileName, int fInfoLevelId, out Win32FindData lpFindFileData, int fSearchOp, IntPtr lpSearchFilter, int dwAdditionalFlags);

    [DllImport("kernel32.dll")]
    private static extern bool FindClose(IntPtr hFindFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceW(string lpRootPathName, out uint lpSectorsPerCluster, out uint lpBytesPerSector, out uint lpNumberOfFreeClusters, out uint lpTotalNumberOfClusters);
}
