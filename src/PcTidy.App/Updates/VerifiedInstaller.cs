using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace PcTidy.App.Updates;

/// <summary>
/// A downloaded update file held open from its last check until it has been started (UPD-2). The updates folder is
/// PC Tidy's data folder, which every program of the user can write to, so checking by path and then starting by path
/// leaves a gap in which the file could be swapped. Here the file is opened ONCE with FileShare.Read (no writes,
/// renames or deletes by anyone meanwhile); its SHA-256 and its Authenticode signature are checked through that handle;
/// <see cref="UpdateTrust"/> decides whether it may run; and the handle stays open while Windows starts it.
/// The portable zip is held the same way (<see cref="OpenArchive"/>) while it is unpacked; it is never run.
/// </summary>
public sealed class VerifiedInstaller : IDisposable
{
    private readonly FileStream _file;

    private VerifiedInstaller(string path, FileStream file, SignatureInfo signature)
    {
        Path = path;
        _file = file;
        Signature = signature;
    }

    public string Path { get; }
    public SignatureInfo Signature { get; }

    /// <summary>
    /// Arguments PC Tidy Setup is started with for an update. Empty = Setup's own window: it asks to close a PC Tidy that
    /// is still open (never kills it), shows progress and the result, keeps settings, history and quarantine, and offers
    /// "Start PC Tidy" at the end. "--silent" would show no result and never restart PC Tidy, so it isn't used.
    /// LEAD: switch to a dedicated "--update" once Setup has one (see the updater report).
    /// </summary>
    public static readonly IReadOnlyList<string> SetupArguments = Array.Empty<string>();

    /// <summary>
    /// Opens and checks the installer. Throws <see cref="InvalidOperationException"/> with a plain-English reason when it
    /// may not be started (wrong checksum, an invalid or another publisher's signature, or an unsigned file without
    /// the release proof while this PC Tidy is unsigned).
    /// </summary>
    /// <param name="own">Who signed the running PC Tidy (<see cref="Authenticode.Own"/>).</param>
    /// <param name="release">What the download passed (<see cref="UpdateChecker.DownloadAsync"/>); null = no release proof.</param>
    /// <param name="check">Tests: the signature check (real: <see cref="Authenticode.Check(Microsoft.Win32.SafeHandles.SafeFileHandle, string)"/>).</param>
    public static VerifiedInstaller Open(string path, string sha256, SignatureInfo own, VerifiedRelease? release, Func<FileStream, string, SignatureInfo>? check = null)
    {
        if (release is not null && (!string.Equals(release.Sha256, sha256, StringComparison.OrdinalIgnoreCase)
                                    || !string.Equals(System.IO.Path.GetFileName(path), release.AssetName, StringComparison.Ordinal)
                                    || release.Kind != InstallKind.Installed))
            throw new InvalidOperationException("The installer isn't the file the release checks were made for, so it wasn't started.");
        FileStream file;
        try { file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); } // no write, rename or delete by anyone meanwhile
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new InvalidOperationException("PC Tidy couldn't open the downloaded installer: " + ex.Message); }
        try
        {
            if (!Convert.ToHexString(SHA256.HashData(file)).Equals(sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The installer changed since it was downloaded, so it wasn't started.");
            file.Position = 0;
            var signature = check is null ? Authenticode.Check(file.SafeFileHandle, path) : check(file, path);
            if (UpdateTrust.Problem(own, signature, release) is { } problem) throw new InvalidOperationException(problem);
            file.Position = 0;
            return new VerifiedInstaller(path, file, signature);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens the downloaded portable zip the same way (FileShare.Read, SHA-256 checked through the handle). The caller
    /// unpacks from <see cref="Stream"/> and disposes it afterwards. A zip has no signature; it is never run.
    /// </summary>
    public static VerifiedInstaller OpenArchive(string path, string sha256)
    {
        FileStream file;
        try { file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new InvalidOperationException("PC Tidy couldn't open the downloaded zip: " + ex.Message); }
        try
        {
            if (!Convert.ToHexString(SHA256.HashData(file)).Equals(sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The zip changed since it was downloaded, so it wasn't unpacked.");
            file.Position = 0;
            return new VerifiedInstaller(path, file, SignatureInfo.NotSigned);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>The checked file, read-only, still locked against changes.</summary>
    public Stream Stream => _file;

    /// <summary>
    /// Starts the checked Setup (<see cref="SetupArguments"/>) while the handle still denies changes. The caller closes
    /// PC Tidy straight after, because Setup refuses to replace files of a PC Tidy that is still running.
    /// Before that, the name Windows will start is opened again and must lead to the very file that was checked (same
    /// volume and file id, and the same canonical final path). A subst or mapped drive, a junction or 8.3 short names in
    /// the data folder's path are fine that way (3.1.1 review L7); anything that can't be confirmed refuses, with the
    /// release page to get the update by hand.
    /// </summary>
    public void Start(IProcessStarter? starter = null)
    {
        if (!SameFileAsHeld(Path)) throw new InvalidOperationException(CantConfirmMessage);
        var psi = new ProcessStartInfo(Path) { UseShellExecute = false, WorkingDirectory = System.IO.Path.GetDirectoryName(Path)! };
        foreach (var a in SetupArguments) psi.ArgumentList.Add(a);
        (starter ?? RealProcessStarter.Instance).Start(psi);
    }

    public static string CantConfirmMessage =>
        "PC Tidy couldn't confirm that the installer's name still leads to the file it checked, so it wasn't started. " +
        $"You can get the update from the release page instead: {ReleaseSource.ReleasesPage.AbsoluteUri}";

    public void Dispose() => _file.Dispose();

    /// <summary>
    /// True when <paramref name="path"/>, opened again now, is the file behind the held handle: the same volume serial
    /// and file id (GetFileInformationByHandle), and the same canonical final path (GetFinalPathNameByHandle on both
    /// handles, so subst and mapped drives, junctions and 8.3 names compare by where they really lead). Fail-closed:
    /// anything that can't be read is false.
    /// </summary>
    public bool SameFileAsHeld(string path)
    {
        try
        {
            using var again = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (!FileId(_file.SafeFileHandle, out var a) || !FileId(again.SafeFileHandle, out var b) || a != b) return false;
            var heldPath = FinalPath(_file.SafeFileHandle);
            var againPath = FinalPath(again.SafeFileHandle);
            return heldPath is not null && againPath is not null && string.Equals(heldPath, againPath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool FileId(Microsoft.Win32.SafeHandles.SafeFileHandle handle, out (uint Volume, uint High, uint Low) id)
    {
        id = default;
        if (!GetFileInformationByHandle(handle, out var info)) return false;
        id = (info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow);
        return true;
    }

    /// <summary>The handle's canonical path ("\?\C:\…" or "\?\UNC\…", VOLUME_NAME_DOS), or null.</summary>
    private static string? FinalPath(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        var sb = new StringBuilder(1024);
        var n = GetFinalPathNameByHandleW(handle, sb, sb.Capacity, 0);
        if (n > sb.Capacity)
        {
            sb = new StringBuilder(n + 1);
            n = GetFinalPathNameByHandleW(handle, sb, sb.Capacity, 0);
        }
        return n <= 0 || n > sb.Capacity ? null : sb.ToString(0, n);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetFinalPathNameByHandleW(Microsoft.Win32.SafeHandles.SafeFileHandle file, StringBuilder path, int size, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, out BY_HANDLE_FILE_INFORMATION info);
}

/// <summary>Starts a program. Only <see cref="VerifiedInstaller.Start"/> uses it; tests pass a fake that starts nothing.</summary>
public interface IProcessStarter
{
    void Start(ProcessStartInfo info);
}

/// <summary>Windows' own process start.</summary>
public sealed class RealProcessStarter : IProcessStarter
{
    public static readonly RealProcessStarter Instance = new();
    public void Start(ProcessStartInfo info) => Process.Start(info)?.Dispose();
}
