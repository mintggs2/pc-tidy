using System.Security.AccessControl;
using System.Security.Principal;
using PcTidy.Engine.IO;

namespace PcTidy.Engine;

/// <summary>
/// Checks that a folder PC Tidy keeps private stuff in really is private: not a link, owned by the expected account,
/// and nobody else is granted any access. Read-only.
/// </summary>
internal static class FolderTrust
{
    public static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    public static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    /// <summary>
    /// Null if <paramref name="path"/> is a real folder (not a link) owned by one of <paramref name="owners"/>, whose
    /// access list allows only <paramref name="allowed"/>. Otherwise a plain reason.
    /// </summary>
    public static string? WhyNotPrivate(string path, IReadOnlyCollection<SecurityIdentifier> owners, IReadOnlyCollection<SecurityIdentifier> allowed)
    {
        var io = LongPath.Io(path);
        FileAttributes attributes;
        try { attributes = File.GetAttributes(io); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return "it doesn't exist"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "it can't be read"; }
        if ((attributes & FileAttributes.Directory) == 0) return "it isn't a folder";
        if ((attributes & FileAttributes.ReparsePoint) != 0) return "it's a link to somewhere else";

        DirectorySecurity security;
        try { security = new DirectoryInfo(io).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException)
        {
            return "its permissions can't be read";
        }
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !owners.Contains(owner))
            return "it belongs to another account";
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if (rule.IdentityReference is not SecurityIdentifier sid || !allowed.Contains(sid))
                return "other accounts can open it";
        }
        return null;
    }

    /// <summary>The current Windows account.</summary>
    public static SecurityIdentifier CurrentUser()
    {
        using var me = WindowsIdentity.GetCurrent();
        return me.User!;
    }

    /// <summary>A protected access list: full control for exactly these accounts, nothing inherited.</summary>
    public static DirectorySecurity PrivateSecurity(IEnumerable<SecurityIdentifier> accounts, SecurityIdentifier? owner = null)
    {
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in accounts)
            sec.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        if (owner is not null) sec.SetOwner(owner);
        return sec;
    }
}
