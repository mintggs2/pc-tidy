using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PcTidy.Platform;

/// <summary>
/// Writes new files into a folder the user controls, from the admin helper, without ever following a link.
/// The target folder is opened one part at a time from the drive root, each part relative to the handle of the one
/// above (NtCreateFile with a root directory) and with "open the link itself", and refused if it is a junction or
/// symbolic link. Files are created relative to the held folder handle and only if they don't exist yet (never
/// overwritten), so swapping a folder for a link after the check can't redirect an admin write anywhere else.
/// </summary>
internal sealed class NoFollowWriter : IDisposable
{
    private readonly SafeFileHandle _folder;

    private NoFollowWriter(SafeFileHandle folder) => _folder = folder;

    /// <summary>Opens (creating parts that are missing) a plain local folder. Throws <see cref="IOException"/> on a link.</summary>
    public static NoFollowWriter Open(string folder)
    {
        if (!PathArgs.IsPlainLocal(folder, allowDriveRoot: true)) throw new IOException("The folder isn't a plain folder on this PC.");
        var root = Path.GetPathRoot(folder)!;
        var current = CreateFileW(@"\\?\" + root, FileListDirectory | FileTraverse | Synchronize | ReadAttributes, ShareRead | ShareWrite, IntPtr.Zero, OpenExisting,
            FlagBackupSemantics | FlagOpenReparsePoint, IntPtr.Zero);
        if (current.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            foreach (var part in folder[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                var next = OpenRelative(current, part, directory: true, create: OpenIf);
                current.Dispose();
                current = next;
            }
            return new NoFollowWriter(current);
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    /// <summary>A subfolder of this folder (created if missing), opened the same way.</summary>
    public NoFollowWriter Sub(string name) => new(OpenRelative(_folder, CheckName(name), directory: true, create: OpenIf));

    /// <summary>Creates <paramref name="name"/> with the bytes of <paramref name="source"/>. False (nothing written) if it already exists.</summary>
    public bool CreateFile(string name, Stream source)
    {
        SafeFileHandle file;
        try { file = OpenRelative(_folder, CheckName(name), directory: false, create: CreateNew); }
        catch (NtException ex) when (ex.Status == StatusObjectNameCollision) { return false; }
        using var stream = new FileStream(file, FileAccess.Write);
        source.CopyTo(stream);
        return true;
    }

    public void Dispose() => _folder.Dispose();

    private static string CheckName(string name)
        => name.Length == 0 || name is "." or ".." || name.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) >= 0 || name.Any(char.IsControl)
            ? throw new IOException("Unexpected file name: " + name) : name;

    private static SafeFileHandle OpenRelative(SafeFileHandle parent, string name, bool directory, uint create)
    {
        var buffer = Marshal.StringToHGlobalUni(name);
        try
        {
            var unicode = new UnicodeString { Length = (ushort)(name.Length * 2), MaximumLength = (ushort)(name.Length * 2), Buffer = buffer };
            var unicodePtr = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            try
            {
                Marshal.StructureToPtr(unicode, unicodePtr, false);
                var oa = new ObjectAttributes
                {
                    Length = Marshal.SizeOf<ObjectAttributes>(),
                    RootDirectory = parent.DangerousGetHandle(),
                    ObjectName = unicodePtr,
                    Attributes = ObjCaseInsensitive,
                };
                var access = directory ? FileListDirectory | FileTraverse | FileAddFile | FileAddSubdirectory | ReadAttributes | Synchronize
                                       : GenericWrite | ReadAttributes | Synchronize;
                // Folders: no delete sharing, so the folder can't be renamed or removed while it is held.
                var share = directory ? ShareRead | ShareWrite : 0u;
                var options = SynchronousIoNonAlert | OpenReparsePointOption | (directory ? DirectoryFile : NonDirectoryFile);
                var status = NtCreateFile(out var handle, access, ref oa, out _, IntPtr.Zero, FileAttributeNormal, share, create, options, IntPtr.Zero, 0);
                if (status != 0) { handle.Dispose(); throw new NtException(status, name); }
                if (IsReparsePoint(handle))
                {
                    handle.Dispose();
                    throw new IOException($"{name} is a link to somewhere else, so PC Tidy didn't write there.");
                }
                return handle;
            }
            finally { Marshal.FreeHGlobal(unicodePtr); }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool IsReparsePoint(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfoClass, out var info, (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return (info.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0;
    }

    internal sealed class NtException(int status, string name) : IOException($"Windows couldn't create {name} (status 0x{status:X8}).")
    {
        public int Status { get; } = status;
    }

    // ---------------------------------------------------------------- interop

    private const uint FileListDirectory = 0x1, FileAddFile = 0x2, FileAddSubdirectory = 0x4, FileTraverse = 0x20, ReadAttributes = 0x80;
    private const uint Synchronize = 0x100000, GenericWrite = 0x40000000;
    private const uint ShareRead = 0x1, ShareWrite = 0x2;
    private const uint OpenExisting = 3, FlagBackupSemantics = 0x02000000, FlagOpenReparsePoint = 0x00200000;
    private const uint CreateNew = 2 /* FILE_CREATE */, OpenIf = 3 /* FILE_OPEN_IF */;
    private const uint DirectoryFile = 0x1, SynchronousIoNonAlert = 0x20, NonDirectoryFile = 0x40, OpenReparsePointOption = 0x00200000;
    private const uint FileAttributeNormal = 0x80, ObjCaseInsensitive = 0x40;
    private const int FileAttributeTagInfoClass = 9;
    internal const int StatusObjectNameCollision = unchecked((int)0xC0000035);

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public IntPtr Information;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(out SafeFileHandle handle, uint access, ref ObjectAttributes objectAttributes, out IoStatusBlock ioStatus,
        IntPtr allocationSize, uint fileAttributes, uint shareAccess, uint createDisposition, uint createOptions, IntPtr eaBuffer, uint eaLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out FileAttributeTagInfo info, uint size);
}
