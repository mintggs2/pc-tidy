using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PcTidy.TestbedGen.Io;

/// <summary>The two NTFS features .NET doesn't expose: sparse files and creating junctions (no admin needed for either).</summary>
internal static class Native
{
    private const uint FsctlSetSparse = 0x000900C4;
    private const uint FsctlSetReparsePoint = 0x000900A4;
    private const uint IoReparseTagMountPoint = 0xA0000003;
    private const uint GenericWrite = 0x40000000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? inBuffer, int inSize,
        IntPtr outBuffer, int outSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, FileShare share, IntPtr security,
        FileMode mode, uint flags, IntPtr template);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHSimpleIDListFromPath(string path);

    [DllImport("shell32.dll")]
    private static extern uint ILGetSize(IntPtr pidl);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    /// <summary>Serialized shell item-ID list for a path, built from the path text only (the target is not opened,
    /// may not exist, and gets no link-tracking id), so the bytes are deterministic. Includes the 2-byte terminator.</summary>
    public static byte[] SimpleIdList(string path)
    {
        var pidl = SHSimpleIDListFromPath(path);
        if (pidl == IntPtr.Zero) throw new InvalidOperationException("SHSimpleIDListFromPath failed for " + path);
        try
        {
            var bytes = new byte[ILGetSize(pidl)];
            Marshal.Copy(pidl, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { ILFree(pidl); }
    }

    /// <summary>Marks an open file sparse so a later SetLength costs no disk space.</summary>
    public static void SetSparse(SafeFileHandle file)
    {
        if (!DeviceIoControl(file, FsctlSetSparse, null, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "FSCTL_SET_SPARSE failed");
    }

    /// <summary>Turns the existing empty folder <paramref name="linkDir"/> into a junction to <paramref name="targetDir"/>
    /// (same result as <c>mklink /J</c>, without spawning cmd or worrying about quoting).</summary>
    public static void MakeJunction(string linkDir, string targetDir)
    {
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + targetDir);
        var print = Encoding.Unicode.GetBytes(targetDir);
        int pathBytes = substitute.Length + 2 + print.Length + 2;
        int dataLength = 8 + pathBytes;
        var buffer = new byte[8 + dataLength];
        BitConverter.TryWriteBytes(buffer.AsSpan(0), IoReparseTagMountPoint);
        BitConverter.TryWriteBytes(buffer.AsSpan(4), (ushort)dataLength);
        BitConverter.TryWriteBytes(buffer.AsSpan(8), (ushort)0);                       // substitute name offset
        BitConverter.TryWriteBytes(buffer.AsSpan(10), (ushort)substitute.Length);
        BitConverter.TryWriteBytes(buffer.AsSpan(12), (ushort)(substitute.Length + 2)); // print name offset
        BitConverter.TryWriteBytes(buffer.AsSpan(14), (ushort)print.Length);
        substitute.CopyTo(buffer, 16);
        print.CopyTo(buffer, 16 + substitute.Length + 2);

        using var handle = CreateFileW(LongPath.Ext(linkDir), GenericWrite, FileShare.None, IntPtr.Zero, FileMode.Open,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot open junction folder " + linkDir);
        if (!DeviceIoControl(handle, FsctlSetReparsePoint, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "FSCTL_SET_REPARSE_POINT failed for " + linkDir);
    }
}

/// <summary>Extended-length paths: keep trailing dots/spaces and reserved names (CON.txt) and allow > 260 chars.</summary>
internal static class LongPath
{
    private const string Prefix = @"\\?\";
    public static string Ext(string path) => path.StartsWith(Prefix, StringComparison.Ordinal) ? path : Prefix + path;
    public static string Strip(string path) => path.StartsWith(Prefix, StringComparison.Ordinal) ? path[Prefix.Length..] : path;
}
