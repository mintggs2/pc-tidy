using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using PcTidy.Engine.IO;

namespace PcTidy.Engine;

/// <summary>
/// The elevated helper's own data folder, <c>%ProgramData%\PC Tidy\Admin</c>: admin quarantine, registry backups,
/// tool logs and the helper's ledger of what it did. Why: the normal data folder is writable by the signed-in user,
/// so anything the elevated helper read back from there (where a file came from, a .reg to import) could have been
/// edited to make the helper write somewhere else. This folder is created with access for SYSTEM and Administrators
/// only (owner Administrators), it's re-checked before every use (owner, access list, not a link), and while the helper
/// runs it's held open so it can't be renamed or swapped.
/// </summary>
public sealed class AdminDataFolder : IDisposable
{
    private static readonly SecurityIdentifier[] AdminAccounts = { FolderTrust.LocalSystem, FolderTrust.Administrators };
    private readonly List<SafeFileHandle> _pins = new();

    private AdminDataFolder(string root) => Root = root;

    /// <summary><c>%ProgramData%\PC Tidy\Admin</c> on this PC.</summary>
    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PC Tidy", "Admin");

    public string Root { get; }

    /// <summary>
    /// Creates the folder (and its parent) if missing, with SYSTEM + Administrators only, then verifies and pins it.
    /// Must run elevated. Throws <see cref="UnauthorizedAccessException"/> with a plain reason if it isn't trustworthy.
    /// </summary>
    public static AdminDataFolder OpenOrCreate(string root)
    {
        var display = LongPath.Display(LongPath.Io(root));
        var parent = Path.GetDirectoryName(display) ?? throw new ArgumentException("The admin folder needs a parent folder.", nameof(root));
        CreatePrivate(parent);
        CreatePrivate(display);
        var folder = new AdminDataFolder(display);
        try
        {
            folder.Verify();
            // Held open without delete sharing: nobody can rename or replace these folders while the helper works.
            folder._pins.Add(HandleFs.OpenNoFollow(parent, NativeMethods.FileReadAttributes, NativeMethods.FileShareRead | NativeMethods.FileShareWrite));
            folder._pins.Add(HandleFs.OpenNoFollow(display, NativeMethods.FileReadAttributes, NativeMethods.FileShareRead | NativeMethods.FileShareWrite));
            folder.Verify();
            return folder;
        }
        catch
        {
            folder.Dispose();
            throw;
        }
    }

    /// <summary>Re-checks the folder and its parent. Call before every use; throws if anything is off.</summary>
    public void Verify()
    {
        if (WhyNotTrusted(Root) is { } why)
            throw new UnauthorizedAccessException($"PC Tidy's admin folder isn't safe to use ({why}), so nothing was changed.");
    }

    /// <summary>Null if <paramref name="root"/> and its parent are safe for the helper; otherwise why not.</summary>
    internal static string? WhyNotTrusted(string root)
    {
        var display = LongPath.Display(LongPath.Io(root));
        if (FolderTrust.WhyNotPrivate(display, AdminAccounts, AdminAccounts) is { } self) return self;
        var parent = Path.GetDirectoryName(display);
        return parent is null ? "it has no parent folder" : WhyParentUnsafe(parent);
    }

    /// <summary>
    /// The parent may be readable by others, but nobody except SYSTEM/Administrators may own it, delete or rename
    /// what's inside it, or change its permissions (any of those would let the admin folder be swapped).
    /// </summary>
    private static string? WhyParentUnsafe(string parent)
    {
        var io = LongPath.Io(parent);
        try
        {
            var attributes = File.GetAttributes(io);
            if ((attributes & FileAttributes.ReparsePoint) != 0) return "its parent folder is a link";
            var security = new DirectoryInfo(io).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !AdminAccounts.Contains(owner))
                return "its parent folder belongs to another account";
            const FileSystemRights dangerous = FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
                                               | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow || (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;
                if (rule.IdentityReference is SecurityIdentifier sid && AdminAccounts.Contains(sid)) continue;
                if ((rule.FileSystemRights & dangerous) != 0) return "other accounts can change its parent folder";
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return "its parent folder can't be checked";
        }
    }

    private static void CreatePrivate(string path)
    {
        var io = LongPath.Io(path);
        if (Directory.Exists(io) || File.Exists(io)) return; // existing: Verify decides whether it can be trusted
        new DirectoryInfo(io).Create(FolderTrust.PrivateSecurity(AdminAccounts, FolderTrust.Administrators));
    }

    public void Dispose()
    {
        foreach (var h in _pins) h.Dispose();
        _pins.Clear();
    }
}
