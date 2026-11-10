using System.Runtime.InteropServices;
using System.Text;

namespace PcTidy.Engine.IO;

/// <summary>
/// NTFS junctions (mount-point reparse points). Creating one needs no admin rights.
/// Removing one only ever removes the link (RemoveDirectory on the reparse point), never what it points to.
/// </summary>
internal static class Junction
{
    private const string NtPrefix = @"\??\";

    /// <summary>Creates an empty folder at <paramref name="linkPath"/> and turns it into a junction to <paramref name="target"/>.</summary>
    public static void Create(string linkPath, string target)
    {
        var linkIo = LongPath.Io(linkPath);
        var targetFull = LongPath.Display(LongPath.Io(target));
        Directory.CreateDirectory(linkIo);
        try
        {
            var buffer = BuildMountPointBuffer(targetFull);
            using var h = OpenReparse(linkIo, NativeMethods.GenericWrite);
            if (!NativeMethods.DeviceIoControl(h, NativeMethods.FsctlSetReparsePoint, buffer, buffer.Length, null, 0, out _, IntPtr.Zero))
                throw FsOps.Win32Io(Marshal.GetLastWin32Error(), linkPath);
        }
        catch
        {
            try { Directory.Delete(linkIo, recursive: false); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>The junction's target (display form), or null if the path is not a junction.</summary>
    public static string? ReadTarget(string linkPath)
    {
        var info = FsOps.Stat(linkPath);
        if (info is not DirectoryInfo || !FsOps.IsReparsePoint(info)) return null;
        using var h = OpenReparse(LongPath.Io(linkPath), NativeMethods.GenericRead);
        var outBuf = new byte[16 * 1024];
        if (!NativeMethods.DeviceIoControl(h, NativeMethods.FsctlGetReparsePoint, null, 0, outBuf, outBuf.Length, out var got, IntPtr.Zero))
            return null;
        if (got < 16 || BitConverter.ToUInt32(outBuf, 0) != NativeMethods.IoReparseTagMountPoint) return null;
        int subOff = BitConverter.ToUInt16(outBuf, 8), subLen = BitConverter.ToUInt16(outBuf, 10);
        var sub = Encoding.Unicode.GetString(outBuf, 16 + subOff, subLen);
        if (sub.StartsWith(NtPrefix, StringComparison.Ordinal)) sub = sub[NtPrefix.Length..];
        return sub.Length > 3 ? sub.TrimEnd('\\') : sub;
    }

    public static bool PointsTo(string linkPath, string target)
        => ReadTarget(linkPath) is { } t && LongPath.Same(t, target);

    /// <summary>Removes the junction itself. Refuses if the path is not a reparse point (so it can never delete real content).</summary>
    public static void Remove(string linkPath)
    {
        var info = FsOps.Stat(linkPath);
        if (info is null) return;
        if (info is not DirectoryInfo || !FsOps.IsReparsePoint(info))
            throw new IOException($"'{LongPath.Display(linkPath)}' is not a link, so PC Tidy won't remove it.");
        if ((info.Attributes & FileAttributes.ReadOnly) != 0) info.Attributes &= ~FileAttributes.ReadOnly;
        Directory.Delete(LongPath.Io(linkPath), recursive: false); // NEVER recursive through a junction
    }

    private static Microsoft.Win32.SafeHandles.SafeFileHandle OpenReparse(string io, uint access)
    {
        var h = NativeMethods.CreateFile(io, access, 0x7 /* share all */, IntPtr.Zero, NativeMethods.OpenExisting,
            NativeMethods.FileFlagBackupSemantics | NativeMethods.FileFlagOpenReparsePoint, IntPtr.Zero);
        if (h.IsInvalid) throw FsOps.Win32Io(Marshal.GetLastWin32Error(), io);
        return h;
    }

    /// <summary>REPARSE_DATA_BUFFER for IO_REPARSE_TAG_MOUNT_POINT: SubstituteName "\??\target", PrintName "target".</summary>
    private static byte[] BuildMountPointBuffer(string target)
    {
        var substitute = Encoding.Unicode.GetBytes(NtPrefix + target);
        var print = Encoding.Unicode.GetBytes(target);
        var pathBufferLen = substitute.Length + 2 + print.Length + 2;
        var dataLen = 8 + pathBufferLen;
        var buf = new byte[8 + dataLen];
        BitConverter.GetBytes(NativeMethods.IoReparseTagMountPoint).CopyTo(buf, 0);
        BitConverter.GetBytes((ushort)dataLen).CopyTo(buf, 4);
        // Reserved (2 bytes) = 0
        BitConverter.GetBytes((ushort)0).CopyTo(buf, 8);                          // SubstituteNameOffset
        BitConverter.GetBytes((ushort)substitute.Length).CopyTo(buf, 10);         // SubstituteNameLength
        BitConverter.GetBytes((ushort)(substitute.Length + 2)).CopyTo(buf, 12);   // PrintNameOffset
        BitConverter.GetBytes((ushort)print.Length).CopyTo(buf, 14);              // PrintNameLength
        substitute.CopyTo(buf, 16);
        print.CopyTo(buf, 16 + substitute.Length + 2);
        return buf;
    }
}
