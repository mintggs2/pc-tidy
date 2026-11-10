using System.Security.AccessControl;
using System.Security.Principal;

namespace PcTidy.Platform;

/// <summary>
/// Answers "could a program running as the signed-in user (no admin rights) change this program or what it loads?"
/// by reading the owner and DACL of the program, the files and folders next to it, and every parent folder up to the
/// drive root. Used before PC Tidy asks for admin rights (its own install folder) and before the admin helper runs
/// another program (an uninstaller) with admin rights.
/// <para>
/// Only SYSTEM, Administrators and TrustedInstaller may own these objects or hold write rights on them. Two levels:
/// <list type="bullet">
/// <item><b>Contents</b> (the program, the files beside it, its folder and the folders inside it): no right that adds,
/// changes, renames or deletes anything. Adding a file next to a program is enough to plant a DLL.</item>
/// <item><b>Parents</b> (every folder above, and the drive root): no right that could rename, delete or swap the
/// folder below (delete, delete-child, change permissions, take ownership). Creating new folders is fine there:
/// <c>C:\</c> lets everyone do that and it can't replace an existing one.</item>
/// </list>
/// Inherit-only entries are ignored (they don't apply to the object itself); CREATOR OWNER / OWNER RIGHTS entries
/// are templates or follow the (trusted) owner. Anything that can't be read counts as unsafe.
/// </para>
/// </summary>
public static class AclSafety
{
    /// <summary>How many files and folders are read at most under the program's folder (PC Tidy's own has a handful).</summary>
    public const int MaxEntries = 5_000;

    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier TrustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");
    private static readonly SecurityIdentifier CreatorOwner = new(WellKnownSidType.CreatorOwnerSid, null);
    private static readonly SecurityIdentifier CreatorGroup = new(WellKnownSidType.CreatorGroupSid, null);
    private static readonly SecurityIdentifier OwnerRights = new("S-1-3-4");

    // Raw access bits (files and folders share them: WriteData = add file, AppendData = add subfolder).
    private const int WriteData = 0x2, AppendData = 0x4, WriteEa = 0x10, DeleteChild = 0x40, WriteAttributes = 0x100;
    private const int Delete = 0x10000, WriteDac = 0x40000, WriteOwner = 0x80000;
    private const int GenericAll = 0x10000000, GenericWrite = 0x40000000;

    /// <summary>Rights that change a program, a file beside it, or its folder's contents.</summary>
    internal const int ContentsWriteRights = WriteData | AppendData | WriteEa | DeleteChild | WriteAttributes | Delete | WriteDac | WriteOwner | GenericAll | GenericWrite;

    /// <summary>Rights on a parent folder that let someone rename, delete or swap what's below it.</summary>
    internal const int ParentWriteRights = DeleteChild | Delete | WriteDac | WriteOwner | GenericAll;

    /// <summary>Same as <see cref="ParentWriteRights"/> for a drive root (which can't be deleted or renamed).</summary>
    internal const int RootWriteRights = DeleteChild | WriteDac | WriteOwner | GenericAll;

    public enum Level { Contents, Parent, Root }

    /// <summary>One object to judge: its path (for the message), level and security descriptor (owner + DACL).</summary>
    public sealed record Node(string Path, Level Level, RawSecurityDescriptor? Descriptor);

    /// <summary>
    /// Null if no non-admin account can change any of the nodes, otherwise a reason naming the first unsafe one.
    /// <paramref name="alsoTrusted"/> is for tests only (a testbed folder can't be owned by Administrators without admin rights).
    /// </summary>
    public static string? Evaluate(IEnumerable<Node> nodes, IReadOnlyCollection<SecurityIdentifier>? alsoTrusted = null)
    {
        bool Trusted(SecurityIdentifier sid) => sid == System || sid == Administrators || sid == TrustedInstaller || (alsoTrusted?.Contains(sid) ?? false);

        foreach (var node in nodes)
        {
            if (node.Descriptor is not { } sd) return $"PC Tidy couldn't read who may change {node.Path}.";
            if (sd.Owner is null || !Trusted(sd.Owner)) return $"{node.Path} is owned by an account without admin rights.";
            if ((sd.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0 || sd.DiscretionaryAcl is null)
                return $"Everyone may change {node.Path} (it has no permissions list).";

            var dangerous = node.Level switch { Level.Contents => ContentsWriteRights, Level.Parent => ParentWriteRights, _ => RootWriteRights };
            foreach (var ace in sd.DiscretionaryAcl)
            {
                if (ace is not KnownAce known) continue;
                if (ace.AceType is not (AceType.AccessAllowed or AceType.AccessAllowedCallback or AceType.AccessAllowedObject or AceType.AccessAllowedCallbackObject)) continue;
                if ((ace.AceFlags & AceFlags.InheritOnly) != 0) continue;
                var sid = known.SecurityIdentifier;
                if (Trusted(sid) || sid == CreatorOwner || sid == CreatorGroup || sid == OwnerRights) continue;
                if ((known.AccessMask & dangerous) != 0) return $"{Name(sid)} may change {node.Path}.";
            }
        }
        return null;
    }

    /// <summary>
    /// PC Tidy's own install: the program, every file and folder in its folder (recursively, links refused),
    /// and every parent folder up to the drive root.
    /// </summary>
    public static string? CheckInstallFolder(string exePath, IReadOnlyCollection<SecurityIdentifier>? alsoTrusted = null)
    {
        if (!Path.IsPathFullyQualified(exePath) || exePath.StartsWith(@"\\", StringComparison.Ordinal)) return "PC Tidy isn't running from a folder on this PC.";
        var folder = Path.GetDirectoryName(exePath);
        if (folder is null || !File.Exists(exePath)) return "PC Tidy couldn't find its own program file.";
        var nodes = new List<Node> { Read(exePath, Level.Contents, isDirectory: false) };
        if (ReadTree(folder, nodes) is { } problem) return problem;
        nodes.AddRange(Parents(folder));
        return Evaluate(nodes, alsoTrusted);
    }

    /// <summary>
    /// Another program about to run with admin rights (an uninstaller): the program, the files directly beside it
    /// (DLLs it may load), its folder, and every parent folder up to the drive root.
    /// </summary>
    public static string? CheckProgram(string exePath, IReadOnlyCollection<SecurityIdentifier>? alsoTrusted = null)
    {
        if (!PathArgs.IsPlainLocal(exePath) || !File.Exists(exePath)) return "The program isn't on this PC.";
        var folder = Path.GetDirectoryName(exePath)!;
        var nodes = new List<Node>();
        try
        {
            if ((File.GetAttributes(exePath) & FileAttributes.ReparsePoint) != 0) return $"{exePath} is a link to somewhere else.";
            nodes.Add(Read(folder, Level.Contents, isDirectory: true));
            var count = 0;
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false }))
            {
                if (++count > MaxEntries) return $"{folder} has too many files to check.";
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) return $"{entry.FullName} is a link to somewhere else.";
                if (entry is FileInfo) nodes.Add(Read(entry.FullName, Level.Contents, isDirectory: false));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return $"PC Tidy couldn't read {folder}."; }
        nodes.AddRange(Parents(folder));
        return Evaluate(nodes, alsoTrusted);
    }

    /// <summary>A folder and everything in it (links refused).</summary>
    private static string? ReadTree(string folder, List<Node> nodes)
    {
        try
        {
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) return $"{folder} is a link to somewhere else.";
            nodes.Add(Read(folder, Level.Contents, isDirectory: true));
            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false };
            var count = 0;
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", options))
            {
                if (++count > MaxEntries) return $"{folder} has too many files to check.";
                // A link inside could point anywhere (and recursion never follows it): refuse rather than guess.
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) return $"{entry.FullName} is a link to somewhere else.";
                nodes.Add(Read(entry.FullName, Level.Contents, entry is DirectoryInfo));
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"PC Tidy couldn't read {folder}.";
        }
    }

    /// <summary>Every folder above <paramref name="folder"/>, up to and including the drive root.</summary>
    internal static IEnumerable<Node> Parents(string folder)
    {
        var root = Path.GetPathRoot(folder);
        for (var dir = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(folder)); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            var isRoot = root is not null && string.Equals(Path.TrimEndingDirectorySeparator(dir), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase);
            yield return Read(dir, isRoot ? Level.Root : Level.Parent, isDirectory: true);
            if (isRoot) yield break;
        }
    }

    /// <summary>Owner + DACL of one file or folder (null descriptor when it can't be read).</summary>
    internal static Node Read(string path, Level level, bool isDirectory)
    {
        try
        {
            const AccessControlSections sections = AccessControlSections.Owner | AccessControlSections.Access;
            FileSystemSecurity security = isDirectory ? new DirectoryInfo(path).GetAccessControl(sections) : new FileInfo(path).GetAccessControl(sections);
            return new Node(path, level, new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or PlatformNotSupportedException)
        {
            return new Node(path, level, null);
        }
    }

    private static string Name(SecurityIdentifier sid)
    {
        try { return sid.Translate(typeof(NTAccount)).Value; }
        catch (Exception ex) when (ex is IdentityNotMappedException or SystemException) { return sid.Value; }
    }
}
