using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using static PcTidy.Engine.IO.NativeMethods;

namespace PcTidy.Engine.IO;

/// <summary>What happened to one file the verified delete was asked to remove.</summary>
internal enum DeleteOutcome
{
    Deleted,
    /// <summary>Already gone (nothing to do; the verified copy has it).</summary>
    Missing,
    /// <summary>Size or modified time differ from the verified copy, or it became a link: left in place.</summary>
    Changed,
    /// <summary>Open in another app (sharing violation) or access denied: left in place.</summary>
    Kept,
}

/// <summary>
/// Handle-based file operations. Why: acting by path name has a gap between "checked" and "done" in which a folder on
/// the way can be swapped for a junction, so the change lands somewhere else. Here an item is opened once, WITHOUT
/// following links, its real location is confirmed from the handle, and the change is applied to that very handle.
/// </summary>
internal static class HandleFs
{
    /// <summary>Opens a file or folder itself (never what a link points to). Throws an IOException on failure.</summary>
    public static SafeFileHandle OpenNoFollow(string path, uint access, uint share = FileShareAll)
    {
        var h = CreateFile(LongPath.Io(path), access | Synchronize, share, IntPtr.Zero, OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (h.IsInvalid)
        {
            var err = Marshal.GetLastWin32Error();
            h.Dispose();
            throw err is ErrorFileNotFound or ErrorPathNotFound
                ? new FileNotFoundException("It's no longer there.", LongPath.Display(path))
                : FsOps.Win32Io(err, path);
        }
        return h;
    }

    /// <summary>Attributes from the handle (FILE_ATTRIBUTE_TAG_INFO), so a link is recognised whatever its path says.</summary>
    public static FileAttributes Attributes(SafeFileHandle h)
    {
        var buf = new byte[8];
        if (!GetFileInformationByHandleEx(h, FileAttributeTagInfo, buf, buf.Length)) throw FsOps.Win32Io(Marshal.GetLastWin32Error(), "");
        return (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(buf);
    }

    /// <summary>Logical size and last-write time (UTC ticks) from the handle.</summary>
    public static (long Size, long LastWriteTicks, FileAttributes Attributes) Facts(SafeFileHandle h)
    {
        var basic = new byte[40];
        if (!GetFileInformationByHandleEx(h, FileBasicInfo, basic, basic.Length)) throw FsOps.Win32Io(Marshal.GetLastWin32Error(), "");
        var standard = new byte[24];
        if (!GetFileInformationByHandleEx(h, FileStandardInfo, standard, standard.Length)) throw FsOps.Win32Io(Marshal.GetLastWin32Error(), "");
        var lastWrite = DateTime.FromFileTimeUtc(BinaryPrimitives.ReadInt64LittleEndian(basic.AsSpan(16))).Ticks;
        var attributes = (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(basic.AsSpan(32));
        return (BinaryPrimitives.ReadInt64LittleEndian(standard.AsSpan(8)), lastWrite, attributes);
    }

    /// <summary>How many names (hard links) share this file's data.</summary>
    public static int LinkCount(SafeFileHandle h)
    {
        var standard = new byte[24]; // FILE_STANDARD_INFO: AllocationSize, EndOfFile, NumberOfLinks, DeletePending, Directory
        if (!GetFileInformationByHandleEx(h, FileStandardInfo, standard, standard.Length)) throw FsOps.Win32Io(Marshal.GetLastWin32Error(), "");
        return (int)BinaryPrimitives.ReadUInt32LittleEndian(standard.AsSpan(16));
    }

    /// <summary>The file's named (alternate) data streams, e.g. ":Zone.Identifier:$DATA" (the main "::$DATA" stream excluded).</summary>
    public static IReadOnlyList<(string Name, long Size)> AlternateStreams(SafeFileHandle h)
    {
        var size = 4096;
        while (true)
        {
            var buf = new byte[size];
            if (!GetFileInformationByHandleEx(h, FileStreamInfo, buf, buf.Length))
            {
                var err = Marshal.GetLastWin32Error();
                if (err == ErrorMoreData && size < (1 << 20)) { size *= 4; continue; }
                if (err == ErrorHandleEof) return Array.Empty<(string, long)>(); // no streams at all (not NTFS)
                throw FsOps.Win32Io(err, "");
            }
            var list = new List<(string, long)>();
            var offset = 0;
            while (true)
            {
                // FILE_STREAM_INFO: NextEntryOffset (4), StreamNameLength (4), StreamSize (8), StreamAllocationSize (8), StreamName
                var next = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(offset));
                var nameBytes = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(offset + 4));
                var streamSize = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(offset + 8));
                var name = Encoding.Unicode.GetString(buf, offset + 24, nameBytes);
                if (!name.Equals("::$DATA", StringComparison.OrdinalIgnoreCase)) list.Add((name, streamSize));
                if (next == 0) return list;
                offset += next;
            }
        }
    }

    /// <summary>Deletes the file behind an open handle (it goes when the last handle closes). Read-only files included.</summary>
    public static void DeleteByHandle(SafeFileHandle h)
    {
        var ex = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(ex, FileDispositionFlagDelete | FileDispositionFlagIgnoreReadOnly);
        if (SetFileInformationByHandle(h, FileDispositionInfoEx, ex, ex.Length)) return;
        if (!SetFileInformationByHandle(h, FileDispositionInfo, new byte[] { 1 }, 1)) throw FsOps.Win32Io(Marshal.GetLastWin32Error(), "");
    }

    /// <summary>The handle's real, link-resolved location in extended form (<c>\\?\D:\...</c>).</summary>
    public static string FinalPath(SafeFileHandle h)
    {
        var sb = new StringBuilder(512);
        var n = GetFinalPathNameByHandle(h, sb, sb.Capacity, 0);
        if (n > sb.Capacity)
        {
            sb = new StringBuilder(n + 1);
            n = GetFinalPathNameByHandle(h, sb, sb.Capacity, 0);
        }
        if (n <= 0) throw FsOps.Win32Io(Marshal.GetLastWin32Error(), "");
        return sb.ToString(0, n);
    }

    /// <summary>
    /// Opens <paramref name="path"/> for a change: refuses links (junctions, symlinks) and refuses when the handle's real
    /// location isn't exactly the checked path (something on the way was redirected).
    /// </summary>
    public static SafeFileHandle OpenVerified(string path, uint access, uint share = FileShareAll)
    {
        var h = OpenNoFollow(path, access, share);
        try
        {
            if ((Attributes(h) & FileAttributes.ReparsePoint) != 0)
                throw new SafetyRefusalException("It's a link to somewhere else, so PC Tidy left it alone.");
            var real = FinalPath(h);
            if (!string.Equals(real, LongPath.Io(path), StringComparison.OrdinalIgnoreCase))
                throw new SafetyRefusalException("Its folder now leads somewhere else (a link was put in the way), so PC Tidy left it alone.");
            return h;
        }
        catch
        {
            h.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Moves exactly the item that was checked at <paramref name="source"/> to <paramref name="destination"/>:
    /// both the item and the destination folder are opened by handle and confirmed, then the rename is applied to the
    /// item's handle relative to the folder's handle. Never replaces anything; same volume only.
    /// </summary>
    public static RenameOutcome MoveVerified(string source, string destination)
    {
        var destDisplay = LongPath.Display(LongPath.Io(destination));
        var parent = Path.GetDirectoryName(destDisplay) ?? throw new IOException("The destination has no folder.");
        var name = Path.GetFileName(destDisplay);
        using var item = OpenVerified(source, Delete | FileReadAttributes);
        using var folder = OpenVerified(parent, FileReadAttributes | FileTraverse);
        if ((Attributes(folder) & FileAttributes.Directory) == 0) throw new IOException("The destination folder isn't a folder.");
        return RenameInto(item, folder, name, source);
    }

    /// <summary>
    /// Renames the object behind <paramref name="item"/> to <paramref name="name"/> inside <paramref name="folder"/>
    /// (NtSetInformationFile, FILE_RENAME_INFORMATION with a RootDirectory handle: the target folder is the opened, checked
    /// one, not a path that is looked up again). Never replaces anything.
    /// </summary>
    public static RenameOutcome RenameInto(SafeFileHandle item, SafeFileHandle folder, string name, string forMessages)
    {
        if (name.Length == 0 || name.IndexOfAny(new[] { '\\', '/' }) >= 0) throw new ArgumentException("Bad file name.", nameof(name));
        var nameBytes = Encoding.Unicode.GetBytes(name);
        // x64 layout: Flags (4) + padding (4), RootDirectory (8), FileNameLength (4), FileName (WCHAR[]).
        var buf = new byte[Math.Max(24, 20 + nameBytes.Length + 2)];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, 0); // never replace
        var added = false;
        folder.DangerousAddRef(ref added);
        try
        {
            BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(8), folder.DangerousGetHandle().ToInt64());
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16), (uint)nameBytes.Length);
            nameBytes.CopyTo(buf, 20);
            var status = NtSetInformationFile(item, out _, buf, buf.Length, FileRenameInformation);
            if (status >= 0) return RenameOutcome.Renamed;
            if (status == StatusNotSameDevice) return RenameOutcome.NotSameVolume;
            if (status == StatusObjectNameCollision) throw new IOException($"'{name}' already exists there.");
            var err = RtlNtStatusToDosError(status);
            if (err is ErrorAlreadyExists or ErrorFileExists) throw new IOException($"'{name}' already exists there.");
            throw FsOps.Win32Io(err, forMessages);
        }
        finally
        {
            if (added) folder.DangerousRelease();
        }
    }

    /// <summary>
    /// Deletes one file only if it is still exactly what was verified (size + modified time) and nobody has it open:
    /// it's opened exclusively and deleted through that same handle, so nothing can change or swap it in between.
    /// Read-only files are deleted without touching their attributes.
    /// </summary>
    public static DeleteOutcome DeleteFileIfUnchanged(string path, long expectedSize, long expectedLastWriteTicks)
    {
        SafeFileHandle h;
        try { h = OpenNoFollow(path, Delete | FileReadAttributes | FileWriteAttributes, share: 0); }
        catch (FileNotFoundException) { return DeleteOutcome.Missing; }
        catch (IOException) { return DeleteOutcome.Kept; } // in use (sharing violation) or denied
        using (h)
        {
            try
            {
                var (size, lastWrite, attributes) = Facts(h);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) return DeleteOutcome.Changed;
                if (size != expectedSize || lastWrite != expectedLastWriteTicks) return DeleteOutcome.Changed;

                var ex = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(ex, FileDispositionFlagDelete | FileDispositionFlagIgnoreReadOnly);
                if (SetFileInformationByHandle(h, FileDispositionInfoEx, ex, ex.Length)) return DeleteOutcome.Deleted;

                // Older file systems (FAT/exFAT) don't know the Ex form: clear read-only by handle, then delete.
                var readOnly = (attributes & FileAttributes.ReadOnly) != 0;
                if (readOnly) SetAttributes(h, attributes & ~FileAttributes.ReadOnly);
                if (SetFileInformationByHandle(h, FileDispositionInfo, new byte[] { 1 }, 1)) return DeleteOutcome.Deleted;
                if (readOnly) SetAttributes(h, attributes);
                return DeleteOutcome.Kept;
            }
            catch (IOException) { return DeleteOutcome.Kept; }
        }
    }

    private static void SetAttributes(SafeFileHandle h, FileAttributes attributes)
    {
        var basic = new byte[40]; // zero times = leave them unchanged
        BinaryPrimitives.WriteUInt32LittleEndian(basic.AsSpan(32), (uint)(attributes == 0 ? FileAttributes.Normal : attributes));
        SetFileInformationByHandle(h, FileBasicInfo, basic, basic.Length);
    }
}
