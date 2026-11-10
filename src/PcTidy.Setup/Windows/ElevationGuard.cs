using System.Diagnostics;
using System.Runtime.InteropServices;
using PcTidy.Core;
using PcTidy.Platform;

namespace PcTidy.Setup.Windows;

/// <summary>
/// The checks around Setup's one admin prompt (SET-1), the same ones the app makes for its admin helper
/// (<c>InstallIntegrity.PreLaunchCheck</c>):
/// <list type="bullet">
/// <item><see cref="BeforePrompt"/> (normal rights, read-only): no variable for the user that would make the .NET runtime
/// load someone else's code into the admin copy (startup hooks, profilers, extra dependency files). Windows builds an
/// admin process's environment from the user's own registry values, which any of the user's programs can write.</item>
/// <item><see cref="InElevatedPart"/>: the admin copy checks again, and refuses when any module of its process was
/// loaded from the user's temporary folder or from the folder the program file sits in (Downloads, the updates
/// folder): only Windows' own DLLs belong in it. The admin part never touches WPF, so the WPF DLLs a single-file Setup
/// unpacks into %TEMP% for its window are never loaded there.</item>
/// <item><see cref="RestrictDllSearch"/>: DLLs loaded later by name come only from System32.</item>
/// </list>
/// Honest limit: the runtime acts on startup hooks, profilers and the program folder's DLLs before Main, so the admin
/// copy's own check can only refuse to go on; the build (<c>StartupHookSupport=false</c>) and the check before the prompt
/// are what keep them out.
/// </summary>
public static class ElevationGuard
{
    /// <summary>Null when Setup may ask for admin rights now, else the plain-English reason it won't.</summary>
    public static string? BeforePrompt()
        => HelperEnvironment.Refusal(HelperEnvironment.UnsafeForElevation()) is { } refusal ? refusal.Replace("PC Tidy's admin helper", "Setup's admin part", StringComparison.Ordinal) + "." : null;

    /// <summary>Null when the admin copy may go on, else why not (no UI: it only turns into an exit code).</summary>
    public static string? InElevatedPart(string selfPath)
    {
        if (HelperEnvironment.UnsafeVariables(Environment.GetEnvironmentVariables()) is { Count: > 0 } names)
            return "Unsafe environment: " + string.Join(", ", names);
        if (LoadedModules() is not { } modules) return "Couldn't list the modules of the admin copy.";
        // uninstall.exe in Program Files sits next to PC Tidy's own DLLs in an admin-only folder: those may load.
        var ownFolderIsAdminOnly = AclSafety.CheckInstallFolder(selfPath) is null;
        return ForeignModule(selfPath, modules, Path.GetTempPath(), checkOwnFolder: !ownFolderIsAdminOnly);
    }

    /// <summary>
    /// Testable form: the first module loaded from <paramref name="tempFolder"/> or (when <paramref name="checkOwnFolder"/>)
    /// from the program's own folder (other than the program itself), or null.
    /// </summary>
    internal static string? ForeignModule(string selfPath, IEnumerable<string> modules, string tempFolder, bool checkOwnFolder = true)
    {
        var self = Path.GetFullPath(selfPath);
        var ownFolder = Path.GetDirectoryName(self);
        foreach (var module in modules)
        {
            string full;
            try { full = Path.GetFullPath(module); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return "Unreadable module path: " + module; }
            if (string.Equals(full, self, StringComparison.OrdinalIgnoreCase)) continue;
            if (PathUtil.IsUnder(full, tempFolder)) return "Loaded from the temporary folder: " + full;
            if (checkOwnFolder && ownFolder is not null && string.Equals(Path.GetDirectoryName(full), ownFolder, StringComparison.OrdinalIgnoreCase))
                return "Loaded from Setup's own folder: " + full;
        }
        return null;
    }

    /// <summary>Every module's file path, or null when the list can't be read (then the admin copy refuses).</summary>
    private static List<string>? LoadedModules()
    {
        var list = new List<string>();
        try
        {
            using var self = Process.GetCurrentProcess();
            foreach (ProcessModule m in self.Modules)
                using (m)
                    if (m.FileName is { Length: > 0 } f) list.Add(f);
            return list;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>From now on, DLLs loaded by name come only from System32 (not the program's own, user-writable folder).</summary>
    public static void RestrictDllSearch()
    {
        try { SetDefaultDllDirectories(LoadLibrarySearchSystem32); }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { /* every supported Windows has it */ }
    }

    private const uint LoadLibrarySearchSystem32 = 0x00000800;

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool SetDefaultDllDirectories(uint directoryFlags);
}
