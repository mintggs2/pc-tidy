using System.Diagnostics;

namespace PcTidy.Platform;

/// <summary>
/// The checks made before PC Tidy asks Windows for admin rights, and again by the admin helper when it starts.
/// The admin helper is PC Tidy's own program file run with admin rights, so it is only as safe as the folder it comes
/// from: if a program without admin rights could swap a file there (a portable copy in a user folder, a developer
/// build), approving the next admin prompt would run that program as admin. Such copies never ask for admin rights.
/// </summary>
public static class InstallIntegrity
{
    public const string PortableRefusal = "Admin steps need PC Tidy to be installed (Program Files). Portable copies can't run admin steps safely.";

    /// <summary>Null when PC Tidy may ask for admin rights now, otherwise the plain-English reason it won't.</summary>
    public static string? PreLaunchCheck()
    {
        if (OwnFolderProblem() is not null) return PortableRefusal;
        if (HelperEnvironment.Refusal(HelperEnvironment.UnsafeForElevation()) is { } env) return env + ".";
        return null;
    }

    /// <summary>
    /// Why this process's own program folder isn't safe for admin use (null = it is): a non-admin account could change
    /// the program, a file or folder beside it, or a parent folder; or parts of it were unpacked into the temporary
    /// folder (a single-file build that extracts its native files there would load them as admin too).
    /// </summary>
    public static string? OwnFolderProblem()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return "PC Tidy couldn't find its own program file.";
        if (AclSafety.CheckInstallFolder(exe) is { } acl) return acl;
        return LoadedFromTemp() is { } module ? $"PC Tidy loaded {module} from the temporary folder." : null;
    }

    /// <summary>The first module of this process that was loaded from the user's temporary folder, if any.</summary>
    internal static string? LoadedFromTemp()
    {
        var temp = Path.GetTempPath();
        try
        {
            using var self = Process.GetCurrentProcess();
            foreach (ProcessModule m in self.Modules)
            {
                using (m)
                {
                    if (m.FileName is { Length: > 0 } f && PathUtil.IsUnder(f, temp)) return f;
                }
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
        return null;
    }
}
