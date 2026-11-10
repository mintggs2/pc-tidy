using PcTidy.Setup.Windows;

namespace PcTidy.Setup.Install;

public sealed record UninstallRequest
{
    /// <summary>Keep the data folder (history + quarantine + a copy of Rescue). The default: quarantined files may still be wanted.</summary>
    public bool KeepData { get; init; } = true;
    /// <summary>
    /// The uninstaller's own program file (it can't delete itself while it runs: Windows removes it at the next restart).
    /// Null = none of the installed files is running.
    /// </summary>
    public string? RunningUninstaller { get; init; }
}

/// <summary>
/// Removes PC Tidy in two parts, like Setup installs it:
/// <list type="bullet">
/// <item><see cref="RemoveProgram"/> (admin rights): the files Setup put in Program Files (from install.json, nothing
/// else, never through a link), the all-users shortcuts and the machine-wide "Installed apps" entry.</item>
/// <item><see cref="RemoveUserParts"/> (the user's normal rights): what the app added for this user (right-click verbs,
/// start-with-Windows, scheduled checkups) and, only if asked, the data folder.</item>
/// </list>
/// <see cref="UninstallAsync"/> does both in one process (already elevated, or tests).
/// </summary>
public sealed class Uninstaller
{
    /// <summary>The per-drive quarantine folder name the engine uses (PcTidy.Engine QuarantineStore.VolumeFolderName).</summary>
    public const string DriveQuarantineName = "PC Tidy Quarantine";

    private readonly InstallEnvironment _env;
    private readonly Registration _registration;

    public Uninstaller(InstallEnvironment env)
    {
        _env = env;
        _registration = new Registration(env.Registry);
    }

    public Task<SetupResult> UninstallAsync(UninstallRequest request, IProgress<SetupProgress>? progress = null, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var program = RemoveProgram(request, progress);
            if (program.Outcome is SetupOutcome.AppOpen or SetupOutcome.AppOpenElsewhere) return program;
            var user = RemoveUserParts(request, progress);
            var notes = program.Notes.Concat(user.Notes).ToList();
            var ok = program.Ok && user.Ok;
            var left = notes.Count(n => n.StartsWith(CouldNotRemove, StringComparison.Ordinal));
            return new SetupResult(ok, ok ? "PC Tidy was removed." : LeftBehindMessage(left), notes);
        }, ct);

    private const string CouldNotRemove = "Couldn't remove: ";

    internal static string LeftBehindMessage(int count)
        => $"PC Tidy was removed, but {count} item(s) couldn't be removed (probably in use). You can delete them yourself.";

    /// <summary>The admin part: program files, all-users shortcuts, the machine-wide entry.</summary>
    public SetupResult RemoveProgram(UninstallRequest request, IProgress<SetupProgress>? progress = null)
    {
        if (Installer.OpenRefusal(_env.Processes.FindRunning(_env.InstallDir)) is { } open) return open;
        var notes = new List<string>();
        var leftBehind = new List<string>();
        var manifest = InstallManifest.Load(_env.ManifestPath);

        progress?.Report(new SetupProgress(0.1, "Removing shortcuts…"));
        RemoveShortcuts(manifest, new[] { _env.AppShortcut, _env.RescueShortcut, _env.DesktopShortcut }, _env.StartMenuFolder, _env.InstallDir, leftBehind);

        progress?.Report(new SetupProgress(0.3, "Removing PC Tidy's files…"));
        RemoveProgramFiles(_env.InstallDir, manifest, request.RunningUninstaller, leftBehind, notes);

        progress?.Report(new SetupProgress(0.6, "Removing PC Tidy from Installed apps…"));
        try { _registration.RemoveUninstallEntry(); }
        catch (Exception ex) { leftBehind.Add("Registry: " + ex.Message); }
        RemoveOtherAccountsFootprint(notes);

        notes.AddRange(leftBehind.Select(l => CouldNotRemove + l));
        return leftBehind.Count == 0 ? new SetupResult(true, "PC Tidy was removed.", notes) : new SetupResult(false, LeftBehindMessage(leftBehind.Count), notes);
    }

    /// <summary>
    /// Other accounts on a per-machine install: their right-click entries and start-with-Windows value would point at a
    /// deleted PcTidy.exe. Their hives are reachable only while loaded; an account that is fully signed out keeps its
    /// entries, which the note explains.
    /// </summary>
    private void RemoveOtherAccountsFootprint(List<string> notes)
    {
        try
        {
            if (_env.TestUsersRoot is { } testRoot)
            {
                if (!testRoot.StartsWith(RegistryPaths.TestRoot + "\\", StringComparison.OrdinalIgnoreCase)) return;
                using var fake = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(testRoot);
                if (fake is not null) notes.AddRange(Registration.RemoveOtherUsersFootprint(fake, "HKU").Select(r => "Removed " + r));
            }
            else if (!_env.Registry.IsTest)
            {
                notes.AddRange(Registration.RemoveOtherUsersFootprint(Microsoft.Win32.Registry.Users, "HKU").Select(r => "Removed " + r));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { /* best effort */ }
    }

    internal const string OtherAccountsNote = "Other accounts on this PC that weren't signed in may still see \"PC Tidy\" in their right-click menu. "
                                              + "It does nothing now; installing and removing PC Tidy again from that account clears it.";

    /// <summary>True when this PC has more than one person's account (the real profile list; tests never look).</summary>
    private bool HasOtherAccounts()
    {
        if (_env.Registry.IsTest) return _env.TestUsersRoot is not null;
        try
        {
            using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
            using var list = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            return list?.GetSubKeyNames().Count(n => n.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase)) > 1;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return false; }
    }

    /// <summary>The user's part: right-click verbs, start-with-Windows, scheduled checkups, and the data folder if asked.</summary>
    public SetupResult RemoveUserParts(UninstallRequest request, IProgress<SetupProgress>? progress = null)
    {
        var notes = new List<string>();
        var leftBehind = new List<string>();

        progress?.Report(new SetupProgress(0.7, "Removing what PC Tidy added to Windows…"));
        try { notes.AddRange(_registration.RemoveAppFootprint().Select(r => "Removed " + r)); }
        catch (Exception ex) { leftBehind.Add("Registry: " + ex.Message); }
        foreach (var task in _env.Tasks.FindPcTidyTasks())
        {
            if (_env.Tasks.Delete(task) is { } error) leftBehind.Add($"Scheduled task {task}: {error}");
            else notes.Add("Removed scheduled task " + task);
        }
        if (HasOtherAccounts()) notes.Add(OtherAccountsNote);

        progress?.Report(new SetupProgress(0.8, request.KeepData ? "Keeping your data folder…" : "Deleting your PC Tidy data…"));
        if (request.KeepData)
        {
            if (Directory.Exists(_env.DataRoot))
                notes.Add($"Your data folder was kept: {_env.DataRoot}. It has your history, the quarantine and a copy of PC Tidy Rescue.");
        }
        else
        {
            RemoveData(leftBehind, notes);
        }

        progress?.Report(new SetupProgress(1, "Done."));
        notes.AddRange(leftBehind.Select(l => CouldNotRemove + l));
        return leftBehind.Count == 0 ? new SetupResult(true, "PC Tidy was removed.", notes) : new SetupResult(false, LeftBehindMessage(leftBehind.Count), notes);
    }

    /// <summary>Only shortcuts we recorded, or that point into the install folder (never a user's own shortcut elsewhere).</summary>
    internal static void RemoveShortcuts(InstallManifest? manifest, IEnumerable<string> known, string startMenuFolder, string installDir, List<string> leftBehind)
    {
        var recorded = new HashSet<string>(manifest?.Shortcuts ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var lnk in recorded.Concat(known).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(lnk)) continue;
            var ours = recorded.Contains(lnk) || (ShellLink.TryReadTarget(lnk) is { } target && FolderSafety.IsSameOrUnder(target, installDir));
            if (!ours) continue;
            try { File.Delete(lnk); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { leftBehind.Add(lnk); }
        }
        Shortcuts.RemoveFolderIfEmpty(startMenuFolder);
    }

    /// <summary>
    /// The files Setup installed (install.json plus the fixed names), only inside the folder and never through a link.
    /// The running uninstaller (and then the folder) are left to Windows to delete at the next restart.
    /// </summary>
    internal void RemoveProgramFiles(string dir, InstallManifest? manifest, string? runningUninstaller, List<string> leftBehind, List<string> notes)
    {
        var files = (manifest?.Files ?? Array.Empty<string>())
            .Concat(new[] { Product.AppExe, Product.RescueExe, Product.UninstallerExe, Product.ManifestFile })
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var scheduled = false;
        foreach (var relative in files)
        {
            var path = Path.GetFullPath(Path.Combine(dir, relative));
            if (!FolderSafety.IsSameOrUnder(path, dir) || !File.Exists(path)) continue;
            if (FolderSafety.GoesThroughLink(path, dir)) { leftBehind.Add($"{path} (it is behind a link, so it was left alone)"); continue; }
            if (runningUninstaller is not null && FolderSafety.Same(path, runningUninstaller))
            {
                if (_env.Reboot.DeleteAtRestart(path)) scheduled = true;
                else leftBehind.Add(path);
                continue;
            }
            try
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { leftBehind.Add(path); }
        }
        Folders.RemoveEmptyTree(dir); // a user's own files in the folder stay, and so does the folder then
        if (scheduled)
        {
            _env.Reboot.DeleteAtRestart(dir); // removed at restart once it is empty
            notes.Add($"The uninstaller itself ({runningUninstaller}) is removed when you restart your PC.");
        }
    }

    /// <summary>What "delete my data too" would delete and keep (the window shows it before asking).</summary>
    public DataRemovalPlan PlanDataRemoval()
    {
        var plan = DataRemovalPlanner.Plan(_env);
        var quarantines = _env.DriveQuarantineFolders.Where(IsDriveQuarantine).ToList();
        return plan with { Delete = plan.Delete.Concat(quarantines).ToList() };
    }

    /// <summary>
    /// Only PC Tidy's own entries in PC Tidy's own data folder(s) (see <see cref="DataRemovalPlanner"/>) and the per-drive
    /// quarantine folders. Anything else stays where it is, and so does a folder that still holds it.
    /// </summary>
    private void RemoveData(List<string> leftBehind, List<string> notes)
    {
        var plan = PlanDataRemoval();
        foreach (var refused in plan.Refused) leftBehind.Add(refused);
        foreach (var path in plan.Delete)
        {
            var failed = File.Exists(path) ? DeleteFile(path) : DeleteTree(path);
            leftBehind.AddRange(failed.Take(20));
        }
        foreach (var root in plan.Roots)
        {
            // The folder itself only when nothing else is left in it (an empty folder of the user's in it stays too).
            try { if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (!Directory.Exists(root)) notes.Add("Deleted " + root);
            else notes.Add($"Deleted PC Tidy's own data in {root}. Other things in it were left alone.");
        }
        if (plan.Keep.Count > 0)
            notes.Add($"Kept {plan.Keep.Count} item(s) PC Tidy didn't make, for example {plan.Keep[0]}.");
        foreach (var q in plan.Delete.Where(p => _env.DriveQuarantineFolders.Any(d => FolderSafety.Same(d, p))))
            if (!Directory.Exists(q)) notes.Add("Deleted " + q);
    }

    private static List<string> DeleteFile(string path)
    {
        try
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            return new List<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new List<string> { path }; }
    }

    private static bool IsDriveQuarantine(string folder)
    {
        try
        {
            var d = new DirectoryInfo(folder);
            return d.Exists && d.Name.Equals(DriveQuarantineName, StringComparison.OrdinalIgnoreCase) && d.Parent is { } p && p.Parent is null;
        }
        catch { return false; }
    }

    /// <summary>
    /// Deletes a folder tree, clearing read-only flags (quarantined files keep their original attributes).
    /// Junctions/symlinks are removed as links: what they point to is never touched. Returns what couldn't be deleted.
    /// </summary>
    internal static List<string> DeleteTree(string root)
    {
        var failed = new List<string>();
        void Visit(DirectoryInfo dir)
        {
            foreach (var entry in SafeEntries(dir, failed))
            {
                try
                {
                    if (entry is DirectoryInfo sub && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        Visit(sub);
                        continue;
                    }
                    entry.Attributes = FileAttributes.Normal;
                    if (entry is DirectoryInfo link) link.Delete(); else entry.Delete();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed.Add(entry.FullName); }
            }
            try
            {
                dir.Attributes = FileAttributes.Directory;
                dir.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed.Add(dir.FullName); }
        }
        var top = new DirectoryInfo(root);
        if (!top.Exists) return failed;
        if (top.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            try { top.Delete(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed.Add(top.FullName); }
            return failed;
        }
        Visit(top);
        return failed;
    }

    private static IEnumerable<FileSystemInfo> SafeEntries(DirectoryInfo dir, List<string> failed)
    {
        try { return dir.GetFileSystemInfos(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failed.Add(dir.FullName);
            return Array.Empty<FileSystemInfo>();
        }
    }
}
