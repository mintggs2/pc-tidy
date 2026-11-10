using Microsoft.Win32;
using PcTidy.Core;
using PcTidy.Setup.Windows;

namespace PcTidy.Setup.Install;

/// <summary>Names that never change between installs.</summary>
public static class Product
{
    public const string Name = "PC Tidy";
    public const string Publisher = "PC Tidy";
    public const string AppExe = "PcTidy.exe";
    public const string RescueExe = "PC Tidy Rescue.exe";
    public const string RescueName = "PC Tidy Rescue";
    public const string UninstallerExe = "uninstall.exe";
    public const string ManifestFile = "install.json";
    /// <summary>Key name under ...\CurrentVersion\Uninstall.</summary>
    public const string UninstallKeyName = "PCTidy";
    /// <summary>Start-menu folder holding both shortcuts.</summary>
    public const string StartMenuFolder = "PC Tidy";
    /// <summary>HKCU Run value the app's "Start with Windows" switch writes.</summary>
    public const string RunValueName = "PC Tidy";
    /// <summary>Prefix of every right-click menu verb the app adds (e.g. PCTidy.WhatIsThis).</summary>
    public const string ShellVerbPrefix = "PCTidy.";
    /// <summary>Scheduled tasks: anything in the "\PC Tidy\" folder, or named "PC Tidy ..." in the root folder.</summary>
    public const string TaskFolder = "PC Tidy";
    public const string TaskNamePrefix = "PC Tidy";
    /// <summary>Where a copy of Rescue goes inside the data folder, so it survives an uninstall.</summary>
    public const string DataRescueFolder = "Rescue";
}

/// <summary>
/// Every registry key Setup reads or writes. The "Installed apps" entry is machine-wide (HKLM, <see cref="MachineHive"/>);
/// what the app adds for one user (right-click verbs, Run value) and an older per-user install are in HKCU.
/// Tests use <see cref="ForTest"/>: everything (even the "machine" entry) under <c>HKCU\Software\PCTidyTest\...</c>,
/// so no test ever touches HKLM or the real Uninstall, Classes or Run keys.
/// </summary>
public sealed record RegistryPaths(RegistryHive MachineHive, string UninstallKey, string LegacyUninstallKey, string Classes, string Run, string StartupApprovedRun)
{
    public const string TestRoot = @"Software\PCTidyTest";
    private const string UninstallPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + Product.UninstallKeyName;

    public static RegistryPaths Real { get; } = new(
        RegistryHive.LocalMachine,
        UninstallPath,
        UninstallPath,
        @"Software\Classes",
        @"Software\Microsoft\Windows\CurrentVersion\Run",
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");

    /// <summary>The same layout under HKCU\Software\PCTidyTest\&lt;area&gt;\&lt;name&gt; (area e.g. "secfix\setup").</summary>
    public static RegistryPaths ForTest(string name, string area = "setup")
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('\\') || name.Contains(".."))
            throw new ArgumentException("A single key name, please.", nameof(name));
        if (string.IsNullOrWhiteSpace(area) || area.Split('\\').Any(p => p.Length == 0 || p == ".." || p == "." || !p.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new ArgumentException("A plain test area, please.", nameof(area));
        var root = TestRoot + "\\" + area + "\\" + name;
        return new(RegistryHive.CurrentUser, root + @"\Machine\Uninstall\" + Product.UninstallKeyName, root + @"\Uninstall\" + Product.UninstallKeyName,
            root + @"\Classes", root + @"\Run", root + @"\StartupApproved\Run");
    }

    public bool IsTest => MachineHive == RegistryHive.CurrentUser
                          && new[] { UninstallKey, LegacyUninstallKey, Classes, Run, StartupApprovedRun }.All(k => k.StartsWith(TestRoot + "\\", StringComparison.OrdinalIgnoreCase));

    /// <summary>The hive of the "Installed apps" entry (64-bit view: Setup is x64).</summary>
    public RegistryKey OpenMachineRoot() => RegistryKey.OpenBaseKey(MachineHive, RegistryView.Registry64);
}

/// <summary>Scheduled tasks PC Tidy created (real: Task Scheduler; tests: a fake).</summary>
public interface ITaskCleaner
{
    /// <summary>Full paths (e.g. "\PC Tidy\Monthly checkup") of PC Tidy's tasks that exist.</summary>
    IReadOnlyList<string> FindPcTidyTasks();
    /// <summary>Deletes one task. Returns an error message, or null when it's gone.</summary>
    string? Delete(string taskPath);
}

/// <summary>A running copy of PC Tidy (or Rescue) from the install folder.</summary>
public sealed record RunningApp(int ProcessId, string Name, string Path)
{
    /// <summary>
    /// True when it runs in another Windows session: another account signed in on this PC (fast user switching).
    /// Setup can't ask that copy to close, so it says which account has it open instead.
    /// </summary>
    public bool InOtherSession { get; init; }
    /// <summary>The account that session belongs to ("tester"), when Windows tells; null otherwise.</summary>
    public string? Account { get; init; }

    /// <summary>"PC Tidy is open. …", naming the other account(s) when every copy runs in one.</summary>
    public static string OpenMessage(IReadOnlyList<RunningApp> running)
    {
        if (running.Count == 0 || running.Any(r => !r.InOtherSession)) return Installer.AppOpenMessage;
        var accounts = running.Select(r => r.Account).Where(a => !string.IsNullOrEmpty(a)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var who = accounts.Count == 0 ? "another account on this PC" : (accounts.Count == 1 ? "the account " : "the accounts ") + string.Join(" and ", accounts.Select(a => "\"" + a + "\""));
        return $"PC Tidy is open in {who}. Switch to that account and close PC Tidy (also check the icon near the clock), or sign that account out, then try again.";
    }
}

/// <summary>Finding, closing and starting PC Tidy (real: processes; tests: a fake).</summary>
public interface IAppProcesses
{
    IReadOnlyList<RunningApp> FindRunning(string installDir);
    /// <summary>Asks each to close (like clicking X) and waits. Returns the ones still running. Never kills.</summary>
    Task<IReadOnlyList<RunningApp>> CloseAsync(IReadOnlyList<RunningApp> apps, TimeSpan wait, CancellationToken ct);
    void Launch(string exePath);
    /// <summary>Runs <paramref name="exePath"/> with one switch, no window, with the user's own rights; waits up to <paramref name="wait"/>. False if it couldn't.</summary>
    bool RunQuiet(string exePath, string argument, TimeSpan wait);
}

/// <summary>
/// Makes the install folder admin-only (real: owner Administrators, protected permissions: SYSTEM and Administrators
/// full control, everyone else read and run) and checks the result the same way the app does before an admin prompt.
/// Tests: a fake (making Administrators the owner needs admin rights).
/// </summary>
public interface IFolderHardener
{
    /// <summary>Before files are copied: the folder's own permissions, inherited by everything put in it.</summary>
    void Harden(string installDir);
    /// <summary>After copying: owner and inherited permissions for every file and folder in it.</summary>
    void HardenContents(string installDir);
    /// <summary>Null when no account without admin rights can change the program or its folders, else why.</summary>
    string? Verify(string appExe);
}

/// <summary>Files Windows deletes at the next restart (real: MoveFileEx DELAY_UNTIL_REBOOT; tests: a fake).</summary>
public interface IRebootCleanup
{
    /// <summary>True when Windows accepted it.</summary>
    bool DeleteAtRestart(string path);
}

/// <summary>
/// Everything Setup touches, in one injectable place: folders, registry keys, tasks, processes, permissions.
/// <see cref="Real"/> is this PC (per-machine install in Program Files, all-users Start menu, HKLM entry, the user's own
/// data folder); tests build one pointing into testbed\secfix\setup\ and HKCU\Software\PCTidyTest\secfix\setup.
/// </summary>
public sealed record InstallEnvironment
{
    public required string InstallDir { get; init; }
    /// <summary>The folder PC Tidy's install folder must sit directly in (%ProgramFiles%).</summary>
    public required string ProgramFilesRoot { get; init; }
    /// <summary>The all-users Start menu "Programs" folder.</summary>
    public required string StartMenuPrograms { get; init; }
    /// <summary>The all-users (Public) desktop.</summary>
    public required string Desktop { get; init; }
    /// <summary>The user's own Start menu "Programs" folder and desktop (an older per-user install put shortcuts there).</summary>
    public required string UserStartMenuPrograms { get; init; }
    public required string UserDesktop { get; init; }
    /// <summary>PC Tidy's data folder (settings, history, quarantine, logs).</summary>
    public required string DataRoot { get; init; }
    /// <summary>%LOCALAPPDATA%\PC Tidy: holds the pointer file when the data folder was moved elsewhere.</summary>
    public required string DefaultDataRoot { get; init; }
    /// <summary>Per-drive quarantine folders the app may have made (&lt;drive&gt;\PC Tidy Quarantine).</summary>
    public IReadOnlyList<string> DriveQuarantineFolders { get; init; } = Array.Empty<string>();
    public required RegistryPaths Registry { get; init; }
    public required ITaskCleaner Tasks { get; init; }
    public required IAppProcesses Processes { get; init; }
    public required IFolderHardener Hardener { get; init; }
    public required IRebootCleanup Reboot { get; init; }
    /// <summary>
    /// Tests: a key under HKCU\Software\PCTidyTest that stands in for HKEY_USERS (other accounts' hives). A test
    /// environment without it never touches other accounts; the real one uses HKEY_USERS.
    /// </summary>
    public string? TestUsersRoot { get; init; }

    public string StartMenuFolder => Path.Combine(StartMenuPrograms, Product.StartMenuFolder);
    public string AppShortcut => Path.Combine(StartMenuFolder, Product.Name + ".lnk");
    public string RescueShortcut => Path.Combine(StartMenuFolder, Product.RescueName + ".lnk");
    public string DesktopShortcut => Path.Combine(Desktop, Product.Name + ".lnk");
    public string UserStartMenuFolder => Path.Combine(UserStartMenuPrograms, Product.StartMenuFolder);
    public string AppPath => Path.Combine(InstallDir, Product.AppExe);
    public string RescuePath => Path.Combine(InstallDir, Product.RescueExe);
    public string UninstallerPath => Path.Combine(InstallDir, Product.UninstallerExe);
    public string ManifestPath => Path.Combine(InstallDir, Product.ManifestFile);
    public string DataRescuePath => Path.Combine(DataRoot, Product.DataRescueFolder, Product.RescueExe);

    /// <summary>%ProgramFiles%\PC Tidy (the 64-bit Program Files: Setup is x64).</summary>
    public static string DefaultInstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Product.Name);

    /// <summary>
    /// This PC. The install folder is always %ProgramFiles%\PC Tidy: it is the one place an account without admin
    /// rights can't change, which is what makes the admin helper safe.
    /// </summary>
    public static InstallEnvironment Real() => new()
    {
        InstallDir = DefaultInstallDir,
        ProgramFilesRoot = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        StartMenuPrograms = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
        Desktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        UserStartMenuPrograms = Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        UserDesktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        DataRoot = AppSettings.ResolveDataRoot(),
        DefaultDataRoot = AppSettings.DefaultDataRoot,
        DriveQuarantineFolders = DriveInfo.GetDrives()
            .Where(d => { try { return d.DriveType == DriveType.Fixed && d.IsReady; } catch { return false; } })
            .Select(d => Path.Combine(d.RootDirectory.FullName, "PC Tidy Quarantine"))
            .ToList(),
        Registry = RegistryPaths.Real,
        Tasks = new WindowsTaskCleaner(),
        Processes = new WindowsAppProcesses(),
        Hardener = new WindowsFolderHardener(),
        Reboot = new WindowsRebootCleanup(),
    };
}
