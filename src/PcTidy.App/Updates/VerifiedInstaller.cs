using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace PcTidy.App.Updates;

/// <summary>
/// A downloaded update installer held open from its last check until it has been started (UPD-2). The updates folder is
/// PC Tidy's data folder, which every program of the user can write to, so checking by path and then starting by path
/// leaves a gap in which the file could be swapped. Here the file is opened ONCE, denying writes, renames and deletes;
/// its SHA-256 and its Authenticode signature are checked through that handle; the publisher must be the same as the
/// running PC Tidy's (<see cref="UpdateTrust"/>); and the handle stays open while Windows starts it.
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
    /// Opens and checks the installer. Throws <see cref="InvalidOperationException"/> with a plain-English reason when it
    /// may not be started (wrong checksum, unsigned, invalid or another publisher's signature).
    /// </summary>
    /// <param name="own">Who signed the running PC Tidy (<see cref="Authenticode.Own"/>).</param>
    /// <param name="check">Tests: the signature check (real: <see cref="Authenticode.Check(Microsoft.Win32.SafeHandles.SafeFileHandle, string)"/>).</param>
    public static VerifiedInstaller Open(string path, string sha256, SignatureInfo own, Func<FileStream, string, SignatureInfo>? check = null)
    {
        if (own.State != SignatureState.Valid) throw new InvalidOperationException(UpdateTrust.UnsignedSelf);
        FileStream file;
        try { file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); } // no write, rename or delete by anyone meanwhile
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new InvalidOperationException("PC Tidy couldn't open the downloaded installer: " + ex.Message); }
        try
        {
            if (!Convert.ToHexString(SHA256.HashData(file)).Equals(sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The installer changed since it was downloaded, so it wasn't started.");
            file.Position = 0;
            var signature = check is null ? Authenticode.Check(file.SafeFileHandle, path) : check(file, path);
            if (UpdateTrust.Problem(own, signature) is { } problem) throw new InvalidOperationException(problem);
            return new VerifiedInstaller(path, file, signature);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>Starts the checked installer with --silent while the handle still denies changes. The caller closes PC Tidy straight after.</summary>
    public void Start()
    {
        // The handle denies renames, so the name still leads to the checked file; make sure nothing on the way was redirected.
        if (!SamePath(FinalPath(_file), Path)) throw new InvalidOperationException("The installer's folder now leads somewhere else, so it wasn't started.");
        Process.Start(new ProcessStartInfo(Path) { UseShellExecute = false, ArgumentList = { "--silent" }, WorkingDirectory = System.IO.Path.GetDirectoryName(Path)! })?.Dispose();
    }

    public void Dispose() => _file.Dispose();

    private static bool SamePath(string finalPath, string path)
    {
        var a = finalPath.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + finalPath[8..] : finalPath.StartsWith(@"\\?\", StringComparison.Ordinal) ? finalPath[4..] : finalPath;
        return string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
    }

    private static string FinalPath(FileStream file)
    {
        var sb = new StringBuilder(1024);
        var n = GetFinalPathNameByHandleW(file.SafeFileHandle, sb, sb.Capacity, 0);
        if (n > sb.Capacity)
        {
            sb = new StringBuilder(n + 1);
            n = GetFinalPathNameByHandleW(file.SafeFileHandle, sb, sb.Capacity, 0);
        }
        if (n <= 0) throw new InvalidOperationException("PC Tidy couldn't confirm where the installer is, so it wasn't started.");
        return sb.ToString(0, n);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetFinalPathNameByHandleW(Microsoft.Win32.SafeHandles.SafeFileHandle file, StringBuilder path, int size, uint flags);
}
