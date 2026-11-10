using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using PcTidy.Platform;

namespace PcTidy.Setup.Payload;

/// <summary>Where the admin part copies the payload before installing it (real: an admin-only folder in %WINDIR%\Temp).</summary>
public sealed record StagingOptions(string Parent, bool RestrictAccess, Func<string, string?> CheckFolder)
{
    /// <summary>
    /// A fresh folder in %WINDIR%\Temp with protected permissions (owner Administrators; only SYSTEM and Administrators
    /// may change anything), checked with the same ACL rules the app applies to its own install folder.
    /// </summary>
    public static StagingOptions Real() => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"),
        RestrictAccess: true,
        CheckFolder: zip => AclSafety.CheckInstallFolder(zip));
}

/// <summary>
/// The admin part's own copy of the payload (SET-2). The program file it was started from sits in a folder any program of
/// the user can change (Downloads, or PC Tidy's updates folder), and a running exe can be renamed and replaced. So the
/// admin part never installs from that path directly:
/// <list type="number">
/// <item>it opens the program file once, denying writes and deletes, and copies the payload out of that one handle into
/// a new folder only admins can change, hashing the bytes on the way;</item>
/// <item>the hash must equal the one compiled into the running program (<see cref="PayloadTrust"/>), not the one in the
/// file's own trailer, which a swapped file would carry too;</item>
/// <item>the folder's permissions are checked, the copy is hashed again there, and it stays open (deny writes) until the
/// install is done. Then the folder is deleted.</item>
/// </list>
/// </summary>
public sealed class StagedPayload : IDisposable
{
    public const string ZipName = "payload.zip";

    private readonly FileStream _held;

    private StagedPayload(string folder, string zipPath, FileStream held)
    {
        Folder = folder;
        ZipPath = zipPath;
        _held = held;
    }

    public string Folder { get; }
    public string ZipPath { get; }
    public long Length => _held.Length;

    /// <summary>The verified copy, read through the handle held since it was verified.</summary>
    public Stream Open() => new SubStream(_held, 0, _held.Length, ownsInner: false);

    /// <summary>
    /// Copies and verifies the payload of <paramref name="selfPath"/>. Returns the staged copy, or null with a
    /// <see cref="Program"/> exit code saying why nothing may be installed.
    /// </summary>
    public static (StagedPayload? Staged, int Code) Create(string selfPath, byte[]? expectedSha256, StagingOptions options, CancellationToken ct = default)
    {
        if (expectedSha256 is not { Length: 32 }) return (null, Program.ExitUntrusted);
        string? folder = null;
        try
        {
            string zip;
            // One handle on the program file for the whole copy: nobody can write to it, rename it or delete it meanwhile.
            using (var self = new FileStream(selfPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var payload = PayloadFile.Find(self, selfPath);
                if (payload is null) return (null, Program.ExitNoPayload);
                if (!PayloadTrust.TrailerMatchesEmbedded(payload, expectedSha256)) return (null, Program.ExitDamaged);

                folder = CreateFolder(options);
                zip = Path.Combine(folder, ZipName);
                byte[] copied;
                using (var source = payload.OpenFrom(self))
                using (var target = new FileStream(zip, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    copied = CopyHashing(source, target, ct);
                if (!copied.AsSpan().SequenceEqual(expectedSha256)) return Fail(folder, Program.ExitDamaged);
            }

            if (options.CheckFolder(zip) is not null) return Fail(folder, Program.ExitUnsafe);
            var held = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            byte[] again;
            using (var check = new SubStream(held, 0, held.Length, ownsInner: false)) again = SHA256.HashData(check);
            if (!again.AsSpan().SequenceEqual(expectedSha256))
            {
                held.Dispose();
                return Fail(folder, Program.ExitDamaged);
            }
            return (new StagedPayload(folder, zip, held), Program.ExitOk);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return folder is null ? (null, Program.ExitFailed) : Fail(folder, Program.ExitFailed);
        }
    }

    private static (StagedPayload?, int) Fail(string folder, int code)
    {
        DeleteFolder(folder);
        return (null, code);
    }

    /// <summary>A new, randomly named folder; never one that already exists (someone else could have made it).</summary>
    private static string CreateFolder(StagingOptions options)
    {
        var folder = Path.Combine(options.Parent, "PC Tidy Setup " + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(folder) || File.Exists(folder)) throw new IOException("The staging folder already exists.");
        if (options.RestrictAccess)
        {
            var security = new DirectorySecurity();
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            security.SetOwner(admins);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), admins })
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            security.CreateDirectory(folder);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
            Directory.CreateDirectory(folder);
        }
        if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) throw new IOException("The staging folder is a link.");
        if (Directory.EnumerateFileSystemEntries(folder).Any()) throw new IOException("The staging folder isn't empty.");
        return folder;
    }

    private static byte[] CopyHashing(Stream source, Stream target, CancellationToken ct)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        int n;
        while ((n = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            sha.AppendData(buffer, 0, n);
            target.Write(buffer, 0, n);
        }
        target.Flush();
        return sha.GetHashAndReset();
    }

    private static void DeleteFolder(string folder)
    {
        try
        {
            var zip = Path.Combine(folder, ZipName);
            if (File.Exists(zip)) File.Delete(zip);
            if (Directory.Exists(folder)) Directory.Delete(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* %WINDIR%\Temp is cleaned by Windows */ }
    }

    public void Dispose()
    {
        _held.Dispose();
        DeleteFolder(Folder);
    }
}
