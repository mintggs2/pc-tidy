using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using PcTidy.Platform;
using PcTidy.Setup.Install;

namespace PcTidy.Setup.Windows;

/// <summary>
/// Program Files\PC Tidy, admin-only: owner Administrators and protected permissions (not inherited from above, so a
/// changed parent can't loosen them): SYSTEM and Administrators full control; Users and app containers read and run.
/// Everything inside inherits exactly that and is owned by Administrators too. Needs admin rights.
/// </summary>
public sealed class WindowsFolderHardener : IFolderHardener
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    public void Harden(string installDir)
    {
        var security = new DirectorySecurity();
        security.SetOwner(Administrators);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags all = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        void Allow(SecurityIdentifier sid, FileSystemRights rights) => security.AddAccessRule(new FileSystemAccessRule(sid, rights, all, PropagationFlags.None, AccessControlType.Allow));
        Allow(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl);
        Allow(Administrators, FileSystemRights.FullControl);
        Allow(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute);
        Allow(new SecurityIdentifier("S-1-15-2-1"), FileSystemRights.ReadAndExecute); // ALL APPLICATION PACKAGES
        Allow(new SecurityIdentifier("S-1-15-2-2"), FileSystemRights.ReadAndExecute); // ALL RESTRICTED APPLICATION PACKAGES
        new DirectoryInfo(installDir).SetAccessControl(security);
    }

    public void HardenContents(string installDir)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false };
        foreach (var entry in new DirectoryInfo(installDir).EnumerateFileSystemInfos("*", options))
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException($"{entry.FullName} is a link, which an install folder never has.");
            if (entry is DirectoryInfo d)
            {
                var s = new DirectorySecurity();
                s.SetOwner(Administrators);
                s.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                d.SetAccessControl(s);
            }
            else
            {
                var s = new FileSecurity();
                s.SetOwner(Administrators);
                s.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                ((FileInfo)entry).SetAccessControl(s);
            }
        }
    }

    public string? Verify(string appExe) => AclSafety.CheckInstallFolder(appExe);
}

/// <summary>MoveFileEx(path, NULL, MOVEFILE_DELAY_UNTIL_REBOOT): Windows deletes it when the PC restarts. Needs admin rights.</summary>
public sealed class WindowsRebootCleanup : IRebootCleanup
{
    private const uint DelayUntilReboot = 0x4;

    public bool DeleteAtRestart(string path) => MoveFileExW(path, null, DelayUntilReboot);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExW(string existing, string? replacement, uint flags);
}
