using PcTidy.Setup.Install;
using PcTidy.Setup.Windows;

namespace PcTidy.Setup.Tests;

[Collection("setup")]
public class InstallUninstallTests
{
    [Fact]
    public async Task Install_is_per_machine_with_one_admin_step_then_the_users_part()
    {
        using var bed = new SetupBed("install");
        var (_, payload, stub) = bed.MakeSetupExe("1.0.0");
        var result = await bed.Install(payload);
        Assert.True(result.Ok, result.Message + string.Join("\n", result.Notes));

        // One admin request, with fixed switches only (no folder: the admin part decides that itself).
        Assert.Equal(new[] { SetupFlow.ElevatedInstallSwitch }, bed.Elevation.Calls.Single());

        var env = bed.Env;
        Assert.Equal(Path.Combine(env.ProgramFilesRoot, "PC Tidy"), env.InstallDir);
        foreach (var f in new[] { Product.AppExe, Product.RescueExe, "README.md", "LICENSE.txt", @"docs\PRIVACY.md" })
            Assert.True(File.Exists(Path.Combine(env.InstallDir, f)), f);
        Assert.False(File.Exists(Path.Combine(env.InstallDir, "payload.json")));
        Assert.Empty(Directory.GetFiles(env.InstallDir, "*.pctidy-new", SearchOption.AllDirectories));
        Assert.Equal(stub, await File.ReadAllBytesAsync(env.UninstallerPath)); // the stub only, no payload

        // The folder was made admin-only before anything was copied, and its contents afterwards.
        Assert.Equal(new[] { env.InstallDir }, bed.Hardener.Hardened);
        Assert.Equal(new[] { env.InstallDir }, bed.Hardener.ContentsHardened);

        var manifest = InstallManifest.Load(env.ManifestPath)!;
        Assert.Equal("1.0.0", manifest.Version);
        Assert.Contains(@"docs\PRIVACY.md", manifest.Files);

        // All-users Start menu.
        Assert.StartsWith(env.StartMenuPrograms, env.AppShortcut);
        var app = ShellLink.Read(env.AppShortcut);
        Assert.Equal(env.AppPath, app.Target, ignoreCase: true);
        Assert.Equal(env.InstallDir, app.WorkingDirectory, ignoreCase: true);
        var rescue = ShellLink.Read(env.RescueShortcut);
        Assert.Equal(env.RescuePath, rescue.Target, ignoreCase: true);
        Assert.Contains("Undo", rescue.Description);
        Assert.False(File.Exists(env.DesktopShortcut)); // off by default
        Assert.False(Directory.Exists(env.UserStartMenuFolder)); // nothing in the user's own Start menu

        // The machine-wide entry (HKLM for real; the test key here).
        using (var key = bed.UninstallKey()!)
        {
            Assert.Equal("PC Tidy", key.GetValue("DisplayName"));
            Assert.Equal("1.0.0", key.GetValue("DisplayVersion"));
            Assert.Equal($"\"{env.AppPath}\",0", key.GetValue("DisplayIcon"));
            Assert.Equal(env.InstallDir, key.GetValue("InstallLocation"));
            Assert.Equal($"\"{env.UninstallerPath}\" --uninstall", key.GetValue("UninstallString"));
            Assert.Equal($"\"{env.UninstallerPath}\" --uninstall --quiet", key.GetValue("QuietUninstallString"));
            Assert.Equal(1, key.GetValue("NoModify"));
        }
        Assert.Equal(new ExistingInstall(env.InstallDir, "1.0.0"), new Installer(env).FindExisting());

        // The user's part: a spare Rescue in the data folder.
        Assert.Equal("fake rescue 1.0.0", await File.ReadAllTextAsync(env.DataRescuePath));
    }

    [Fact]
    public async Task Declined_admin_prompt_installs_nothing_and_touches_nothing_of_the_users()
    {
        using var bed = new SetupBed("declined");
        var (_, payload, _) = bed.MakeSetupExe();
        bed.Elevation.Decline = true;
        var result = await bed.Install(payload, desktop: true);
        Assert.False(result.Ok);
        Assert.Contains("didn't give permission", result.Message);
        Assert.Equal(new[] { SetupFlow.ElevatedInstallSwitch, "--desktop" }, bed.Elevation.Calls.Single());
        Assert.False(Directory.Exists(bed.Env.InstallDir));
        Assert.False(Directory.Exists(bed.Env.DataRoot));
        Assert.Null(bed.UninstallKey());
    }

    [Fact]
    public async Task Already_elevated_setup_does_the_admin_part_in_process()
    {
        using var bed = new SetupBed("elevated");
        var (_, payload, _) = bed.MakeSetupExe();
        bed.Elevation.IsElevated = true;
        Assert.True((await bed.Install(payload)).Ok);
        Assert.Empty(bed.Elevation.Calls);
        Assert.True(File.Exists(bed.Env.AppPath));
    }

    [Fact]
    public async Task The_admin_part_refuses_to_run_without_admin_rights_and_takes_no_folder_from_the_command_line()
    {
        using var bed = new SetupBed("not-elevated");
        var (exe, payload, _) = bed.MakeSetupExe();
        var args = SetupArgs.Parse(new[] { SetupFlow.ElevatedInstallSwitch, "--dir", @"C:\Windows\System32", "--install-dir", @"C:\Windows" });
        Assert.Equal(Program.ExitNotElevated, Program.RunElevatedPart(args, bed.Env, elevated: false, exe, bed.Request(payload)));
        Assert.Equal(Program.ExitOk, Program.RunElevatedPart(args, bed.Env, elevated: true, exe, bed.Request(payload)));
        Assert.True(File.Exists(bed.Env.AppPath)); // the environment's Program Files folder, whatever was passed
    }

    [Fact]
    public async Task A_folder_that_isnt_admin_only_after_install_is_reported()
    {
        using var bed = new SetupBed("weak");
        var (_, payload, _) = bed.MakeSetupExe();
        bed.Hardener.VerifyAnswer = "BUILTIN\\Users may change it.";
        var result = await bed.InstallMachinePart(payload);
        Assert.True(result.Ok);
        Assert.Contains(result.Notes, n => n.Contains("won't run steps that need admin rights"));
    }

    [Fact]
    public async Task A_payload_uninstaller_is_used_instead_of_the_setup_stub()
    {
        using var bed = new SetupBed("payload-uninstaller");
        var (_, payload, _) = bed.MakeSetupExe(extraFiles: new Dictionary<string, string> { [Product.UninstallerExe] = "release uninstaller", ["wpfgfx_cor3.dll"] = "native" });
        Assert.True((await bed.InstallMachinePart(payload)).Ok);
        Assert.Equal("release uninstaller", await File.ReadAllTextAsync(bed.Env.UninstallerPath));
        Assert.Contains("wpfgfx_cor3.dll", InstallManifest.Load(bed.Env.ManifestPath)!.Files);
    }

    [Fact]
    public async Task An_older_per_user_copy_is_removed_after_the_per_machine_install()
    {
        using var bed = new SetupBed("legacy");
        var legacyDir = Path.Combine(bed.Root, @"Local\Programs\PC Tidy");
        Directory.CreateDirectory(legacyDir);
        foreach (var f in new[] { Product.AppExe, Product.RescueExe, Product.UninstallerExe, "README.md" }) await File.WriteAllTextAsync(Path.Combine(legacyDir, f), "old");
        var mine = Path.Combine(legacyDir, "my notes.txt");
        await File.WriteAllTextAsync(mine, "mine");
        var userLink = Path.Combine(bed.Env.UserStartMenuFolder, "PC Tidy.lnk");
        ShellLink.Create(userLink, Path.Combine(legacyDir, Product.AppExe), "old");
        new InstallManifest { Version = "0.9.0", Files = new[] { Product.AppExe, Product.RescueExe, "README.md" }, Shortcuts = new[] { userLink } }
            .Save(Path.Combine(legacyDir, Product.ManifestFile));
        using (var legacy = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(bed.Env.Registry.LegacyUninstallKey))
        {
            legacy.SetValue("InstallLocation", legacyDir);
            legacy.SetValue("DisplayVersion", "0.9.0");
        }

        var (_, payload, _) = bed.MakeSetupExe("1.0.0");
        var result = await bed.Install(payload);
        Assert.True(result.Ok, result.Message + string.Join("\n", result.Notes));
        Assert.Contains(result.Notes, n => n.Contains("older copy"));
        foreach (var f in new[] { Product.AppExe, Product.RescueExe, Product.UninstallerExe, "README.md", Product.ManifestFile })
            Assert.False(File.Exists(Path.Combine(legacyDir, f)), f);
        Assert.Equal("mine", await File.ReadAllTextAsync(mine)); // a user's own file stays
        Assert.False(File.Exists(userLink));
        using (var gone = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(bed.Env.Registry.LegacyUninstallKey)) Assert.Null(gone);
        using (var key = bed.UninstallKey()!) Assert.Equal("1.0.0", key.GetValue("DisplayVersion"));
    }

    [Fact]
    public async Task Uninstall_keeping_data_removes_everything_else()
    {
        using var bed = new SetupBed("uninstall-keep");
        var (_, payload, _) = bed.MakeSetupExe();
        Assert.True((await bed.Install(payload, desktop: true)).Ok);
        var history = Path.Combine(bed.Env.DataRoot, @"runs\20260101-120000-abcdef\run.json");
        Directory.CreateDirectory(Path.GetDirectoryName(history)!);
        await File.WriteAllTextAsync(history, "{}");
        bed.Elevation.Calls.Clear();

        var result = await bed.Uninstall(keepData: true);
        Assert.True(result.Ok, result.Message + string.Join("\n", result.Notes));
        Assert.Equal(new[] { SetupFlow.ElevatedUninstallSwitch }, bed.Elevation.Calls.Single());

        var env = bed.Env;
        Assert.False(Directory.Exists(env.InstallDir));
        Assert.False(Directory.Exists(env.StartMenuFolder));
        Assert.False(File.Exists(env.DesktopShortcut));
        Assert.Null(bed.UninstallKey());
        Assert.True(File.Exists(history));
        Assert.True(File.Exists(env.DataRescuePath)); // Rescue survives the uninstall
        Assert.Contains(result.Notes, n => n.Contains("kept"));
    }

    [Fact]
    public async Task Uninstall_deleting_data_removes_the_data_folder_too()
    {
        using var bed = new SetupBed("uninstall-remove");
        var (_, payload, _) = bed.MakeSetupExe();
        Assert.True((await bed.Install(payload)).Ok);
        var quarantined = Path.Combine(bed.Env.DataRoot, @"Quarantine\run1\entry1\payload\old.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(quarantined)!);
        await File.WriteAllTextAsync(quarantined, "set aside");
        File.SetAttributes(quarantined, FileAttributes.ReadOnly | FileAttributes.Hidden); // quarantine keeps attributes

        var result = await bed.Uninstall(keepData: false);
        Assert.True(result.Ok, result.Message + string.Join("\n", result.Notes));
        Assert.False(Directory.Exists(bed.Env.DataRoot));
        Assert.False(Directory.Exists(bed.Env.InstallDir));
        Assert.Null(bed.UninstallKey());
    }

    [Fact]
    public async Task Declined_uninstall_prompt_removes_nothing_at_all()
    {
        using var bed = new SetupBed("uninstall-declined");
        var (_, payload, _) = bed.MakeSetupExe();
        Assert.True((await bed.Install(payload)).Ok);
        using (var run = bed.TestKey("Run")) run.SetValue("PC Tidy", "\"PcTidy.exe\" --tray");
        bed.Elevation.Decline = true;

        var result = await bed.Uninstall(keepData: false);
        Assert.False(result.Ok);
        Assert.Contains("didn't give permission", result.Message);
        Assert.True(File.Exists(bed.Env.AppPath));
        Assert.True(Directory.Exists(bed.Env.DataRoot));
        using (var run = bed.TestKey("Run")) Assert.NotNull(run.GetValue("PC Tidy"));
        Assert.NotNull(bed.UninstallKey());
    }

    [Fact]
    public async Task The_running_uninstaller_is_left_for_windows_to_delete_at_restart()
    {
        using var bed = new SetupBed("self");
        var (_, payload, _) = bed.MakeSetupExe();
        Assert.True((await bed.Install(payload)).Ok);

        var result = await bed.Uninstall(runningUninstaller: bed.Env.UninstallerPath);
        Assert.True(result.Ok, result.Message + string.Join("\n", result.Notes));
        Assert.Equal(new[] { bed.Env.UninstallerPath, bed.Env.InstallDir }, bed.Reboot.Scheduled);
        Assert.True(File.Exists(bed.Env.UninstallerPath)); // (the fake doesn't restart the PC)
        Assert.False(File.Exists(bed.Env.AppPath));
        Assert.Contains(result.Notes, n => n.Contains("restart"));
    }

    [Fact]
    public async Task Uninstall_removes_what_the_app_added_and_nothing_else()
    {
        using var bed = new SetupBed("footprint");
        var (_, payload, _) = bed.MakeSetupExe();
        Assert.True((await bed.Install(payload)).Ok);

        foreach (var shell in new[] { @"Classes\*\shell", @"Classes\Directory\shell", @"Classes\Directory\Background\shell" })
        {
            using (var verb = bed.TestKey($@"{shell}\PCTidy.WhatIsThis\command")) verb.SetValue("", "\"PcTidy.exe\" --what \"%1\"");
            using (var other = bed.TestKey($@"{shell}\SomeOtherApp.Open")) other.SetValue("", "other");
        }
        using (var run = bed.TestKey("Run")) { run.SetValue("PC Tidy", "\"PcTidy.exe\" --tray"); run.SetValue("Other app", "other.exe"); }
        using (var approved = bed.TestKey(@"StartupApproved\Run")) approved.SetValue("PC Tidy", new byte[12]);
        bed.Tasks.Existing.AddRange(new[] { @"\PC Tidy\Monthly checkup", @"\PC Tidy Weekly scan" });

        var result = await bed.Uninstall();
        Assert.True(result.Ok, result.Message + string.Join("\n", result.Notes));

        foreach (var shell in new[] { @"Classes\*\shell", @"Classes\Directory\shell", @"Classes\Directory\Background\shell" })
        {
            Assert.False(bed.TestKeyExists($@"{shell}\PCTidy.WhatIsThis"), shell);
            Assert.True(bed.TestKeyExists($@"{shell}\SomeOtherApp.Open"), shell);
        }
        using (var run = bed.TestKey("Run"))
        {
            Assert.Null(run.GetValue("PC Tidy"));
            Assert.Equal("other.exe", run.GetValue("Other app"));
        }
        using (var approved = bed.TestKey(@"StartupApproved\Run")) Assert.Null(approved.GetValue("PC Tidy"));
        Assert.Equal(new[] { @"\PC Tidy\Monthly checkup", @"\PC Tidy Weekly scan" }, bed.Tasks.Deleted);
    }

    [Fact]
    public async Task Update_in_place_replaces_files_removes_obsolete_ones_and_follows_the_options()
    {
        using var bed = new SetupBed("update");
        var (_, v1, _) = bed.MakeSetupExe("1.0.0", new Dictionary<string, string> { ["old-only.txt"] = "gone in 1.1" });
        Assert.True((await bed.Install(v1)).Ok);
        var userFile = Path.Combine(bed.Env.InstallDir, "my notes.txt"); // someone's own file in the folder
        await File.WriteAllTextAsync(userFile, "mine");

        var (_, v11, _) = bed.MakeSetupExe("1.1.0");
        var update = await bed.Install(v11, desktop: true);
        Assert.True(update.Ok, update.Message + string.Join("\n", update.Notes));
        Assert.Contains("updated", update.Message);
        Assert.Equal("fake app 1.1.0", await File.ReadAllTextAsync(bed.Env.AppPath));
        Assert.False(File.Exists(Path.Combine(bed.Env.InstallDir, "old-only.txt")));
        Assert.True(File.Exists(bed.Env.DesktopShortcut));
        using (var key = bed.UninstallKey()!) Assert.Equal("1.1.0", key.GetValue("DisplayVersion"));
        Assert.Equal("fake rescue 1.1.0", await File.ReadAllTextAsync(bed.Env.DataRescuePath));
        Assert.Equal(2, bed.Hardener.Hardened.Count); // permissions put right again on every update

        Assert.True((await bed.Install(v11, startMenu: false, desktop: false)).Ok);
        Assert.Contains("--no-start-menu", bed.Elevation.Calls.Last());
        Assert.False(File.Exists(bed.Env.DesktopShortcut));
        Assert.False(Directory.Exists(bed.Env.StartMenuFolder));

        // Uninstall leaves that own file (and so the folder) alone.
        Assert.True((await bed.Uninstall()).Ok);
        Assert.Equal("mine", await File.ReadAllTextAsync(userFile));
        Assert.Single(Directory.GetFileSystemEntries(bed.Env.InstallDir));
    }

    [Fact]
    public async Task Install_refuses_a_folder_with_someone_elses_files()
    {
        using var bed = new SetupBed("foreign");
        Directory.CreateDirectory(bed.Env.InstallDir);
        var theirs = Path.Combine(bed.Env.InstallDir, "important.docx");
        await File.WriteAllTextAsync(theirs, "not ours");
        var (_, payload, _) = bed.MakeSetupExe();

        var result = await bed.InstallMachinePart(payload);
        Assert.False(result.Ok);
        Assert.Contains("other files", result.Message);
        Assert.Single(Directory.GetFileSystemEntries(bed.Env.InstallDir));
        Assert.Null(bed.UninstallKey());
        Assert.Empty(bed.Hardener.Hardened);
    }

    [Fact]
    public async Task Install_and_uninstall_wait_for_a_running_PC_Tidy()
    {
        using var bed = new SetupBed("running");
        var (_, payload, _) = bed.MakeSetupExe();
        bed.Processes.Running.Add(new RunningApp(1234, "PcTidy", bed.Env.AppPath));
        var blocked = await bed.Install(payload);
        Assert.False(blocked.Ok);
        Assert.Contains("open", blocked.Message);
        Assert.False(Directory.Exists(bed.Env.InstallDir));

        bed.Processes.Running.Clear();
        Assert.True((await bed.Install(payload)).Ok);
        bed.Processes.Running.Add(new RunningApp(1234, "PcTidy", bed.Env.AppPath));
        var still = await bed.Uninstall();
        Assert.False(still.Ok);
        Assert.Contains("open", still.Message);
        Assert.True(File.Exists(bed.Env.AppPath));
    }

    [Fact]
    public async Task A_damaged_payload_is_refused_and_a_failed_fresh_install_leaves_nothing()
    {
        using var bed = new SetupBed("zip-slip");
        var (_, payload, _) = bed.MakeSetupExe(badEntry: "../../escaped.txt");
        var result = await bed.InstallMachinePart(payload);
        Assert.False(result.Ok);
        Assert.Contains("damaged", result.Message);
        Assert.False(File.Exists(Path.Combine(bed.Root, "escaped.txt")));
        Assert.False(File.Exists(Path.Combine(bed.Root, @"Program Files\escaped.txt")));
        Assert.False(Directory.Exists(bed.Env.InstallDir));
        Assert.False(Directory.Exists(bed.Env.StartMenuFolder));
        Assert.Null(bed.UninstallKey());
    }

    /// <summary>The installed v1 files and what they say, to prove an update that failed changed none of them.</summary>
    private static Dictionary<string, string> Snapshot(string dir)
        => Directory.GetFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(dir, f), File.ReadAllText, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public async Task An_update_that_fails_half_way_through_the_swap_puts_the_old_version_back()
    {
        using var bed = new SetupBed("update-fail");
        var (_, v1, _) = bed.MakeSetupExe("1.0.0", new Dictionary<string, string> { ["old-only.txt"] = "only in 1.0" });
        Assert.True((await bed.Install(v1)).Ok);
        var before = Snapshot(bed.Env.InstallDir);

        // 1.1 brings a new DLL and changes every file. One of the installed files is held open without delete
        // sharing (a scheduled scan that Task Scheduler started after Setup's first check, an antivirus scan), so the
        // swap fails part-way: files before it in the list were already replaced.
        var (_, v11, _) = bed.MakeSetupExe("1.1.0", new Dictionary<string, string> { ["new-in-1.1.dll"] = "new", ["zz-last.txt"] = "1.1" });
        var held = Path.Combine(bed.Env.InstallDir, "README.md");
        SetupResult update;
        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read))
            update = await bed.Install(v11, desktop: true);

        Assert.False(update.Ok);
        Assert.Contains("1.0.0 was put back", update.Message);
        Assert.DoesNotContain("Nothing", update.Message);
        Assert.Equal(before, Snapshot(bed.Env.InstallDir)); // every file exactly as 1.0 left it, no *.pctidy-* leftovers
        Assert.False(File.Exists(bed.Env.DesktopShortcut)); // the shortcut this attempt added is gone again
        Assert.Equal("1.0.0", InstallManifest.Load(bed.Env.ManifestPath)!.Version);
        using (var key = bed.UninstallKey()!) Assert.Equal("1.0.0", key.GetValue("DisplayVersion"));

        // Nothing is stuck: the same update works once the file is free.
        var retry = await bed.Install(v11);
        Assert.True(retry.Ok, retry.Message + string.Join("\n", retry.Notes));
        Assert.Equal("fake app 1.1.0", await File.ReadAllTextAsync(bed.Env.AppPath));
        Assert.Empty(Directory.GetFiles(bed.Env.InstallDir, "*.pctidy-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task An_update_whose_payload_breaks_part_way_replaces_nothing()
    {
        using var bed = new SetupBed("update-badzip");
        var (_, v1, _) = bed.MakeSetupExe("1.0.0");
        Assert.True((await bed.InstallMachinePart(v1)).Ok);
        var before = Snapshot(bed.Env.InstallDir);

        // The payload throws after a few entries have been read (a damaged download, a disk error).
        var (_, v11, _) = bed.MakeSetupExe("1.1.0", badEntry: "../../escaped.txt");
        var result = await bed.InstallMachinePart(v11);
        Assert.False(result.Ok);
        Assert.Equal(SetupOutcome.RolledBack, result.Outcome);
        Assert.Contains("1.0.0 was kept as it was", result.Message);
        Assert.Equal(before, Snapshot(bed.Env.InstallDir));
        Assert.Equal("fake app 1.0.0", await File.ReadAllTextAsync(bed.Env.AppPath));
    }

    [Fact]
    public async Task A_copy_that_starts_after_the_first_check_stops_the_update_before_any_file_is_replaced()
    {
        using var bed = new SetupBed("update-late-start");
        var (_, v1, _) = bed.MakeSetupExe("1.0.0");
        Assert.True((await bed.InstallMachinePart(v1)).Ok);
        var before = Snapshot(bed.Env.InstallDir);
        // The second FindRunning (right before the swap) sees a scheduled scan that started while the files were unpacked.
        bed.Processes.StartsAfterCalls = 1;
        bed.Processes.LateStarter = new RunningApp(4321, "PcTidy", bed.Env.AppPath);

        var (_, v11, _) = bed.MakeSetupExe("1.1.0");
        var result = await bed.InstallMachinePart(v11);
        Assert.False(result.Ok);
        Assert.Equal(SetupOutcome.AppOpen, result.Outcome);
        Assert.Equal(before, Snapshot(bed.Env.InstallDir));
    }

    [Fact]
    public async Task A_copy_open_in_another_account_is_named_instead_of_asking_to_close_it()
    {
        using var bed = new SetupBed("other-session");
        var (exe, payload, _) = bed.MakeSetupExe("1.0.0");
        Assert.True((await bed.InstallMachinePart(payload)).Ok);
        bed.Processes.Running.Add(new RunningApp(99, "PcTidy", bed.Env.AppPath) { InOtherSession = true, Account = "tester2" });

        var update = await bed.InstallMachinePart(payload);
        Assert.False(update.Ok);
        Assert.Equal(SetupOutcome.AppOpenElsewhere, update.Outcome);
        Assert.Contains("\"tester2\"", update.Message);
        Assert.Contains("sign that account out", update.Message);
        Assert.Equal(Program.ExitAppOpenElsewhere, Program.RunElevatedPart(SetupArgs.Parse(new[] { SetupFlow.ElevatedInstallSwitch }), bed.Env, elevated: true, exe, bed.Request(payload)));
        Assert.Contains("another account", SetupFlow.FromInstallExitCode(Program.ExitAppOpenElsewhere, true).Message);

        var uninstall = new Uninstaller(bed.Env).RemoveProgram(new UninstallRequest());
        Assert.False(uninstall.Ok);
        Assert.Contains("\"tester2\"", uninstall.Message);
        Assert.True(File.Exists(bed.Env.AppPath));
    }

    [Fact]
    public async Task Uninstall_also_clears_other_signed_in_accounts_right_click_entries_and_nothing_else()
    {
        using var bed = new SetupBed("other-hives");
        var users = $@"{RegistryPaths.TestRoot}\{SetupBed.RegistryArea}\{bed.RegistryName}\Users";
        bed.Use(e => e with { TestUsersRoot = users });
        var (_, payload, _) = bed.MakeSetupExe();
        Assert.True((await bed.Install(payload)).Ok);
        const string other = "S-1-5-21-1-2-3-1002";
        using (var verb = bed.TestKey($@"Users\{other}\Software\Classes\Directory\Background\shell\PCTidy.TidyFolder\command")) verb.SetValue("", "\"PcTidy.exe\" --tidy \"%V\"");
        using (var theirs = bed.TestKey($@"Users\{other}\Software\Classes\Directory\Background\shell\SomeOtherApp")) theirs.SetValue("", "other");
        using (var run = bed.TestKey($@"Users\{other}\Software\Microsoft\Windows\CurrentVersion\Run")) { run.SetValue("PC Tidy", "\"PcTidy.exe\" --tray"); run.SetValue("Other", "other.exe"); }
        using (var service = bed.TestKey(@"Users\S-1-5-18\Software\Classes\Directory\shell\PCTidy.WhatIsThis")) service.SetValue("", "not a person's hive");

        var result = await bed.Uninstall();
        Assert.True(result.Ok, result.Message + string.Join("\n", result.Notes));
        Assert.False(bed.TestKeyExists($@"Users\{other}\Software\Classes\Directory\Background\shell\PCTidy.TidyFolder"));
        Assert.True(bed.TestKeyExists($@"Users\{other}\Software\Classes\Directory\Background\shell\SomeOtherApp"));
        using (var run = bed.TestKey($@"Users\{other}\Software\Microsoft\Windows\CurrentVersion\Run"))
        {
            Assert.Null(run.GetValue("PC Tidy"));
            Assert.Equal("other.exe", run.GetValue("Other"));
        }
        Assert.True(bed.TestKeyExists(@"Users\S-1-5-18\Software\Classes\Directory\shell\PCTidy.WhatIsThis"));
        Assert.Contains(Uninstaller.OtherAccountsNote, result.Notes);
    }

    [Fact]
    public void Only_this_accounts_scheduled_tasks_count_as_its_own()
    {
        using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
        Assert.True(WindowsTaskCleaner.IsCurrentUser(me.User!.Value));
        Assert.True(WindowsTaskCleaner.IsCurrentUser(me.Name));
        Assert.True(WindowsTaskCleaner.IsCurrentUser(null));
        Assert.False(WindowsTaskCleaner.IsCurrentUser("S-1-5-21-1-2-3-1002"));
        Assert.False(WindowsTaskCleaner.IsCurrentUser(@"SOMEPC\tester2"));
    }

    [Fact]
    public async Task A_users_own_desktop_shortcut_elsewhere_is_never_removed()
    {
        using var bed = new SetupBed("own-shortcut");
        var (_, payload, _) = bed.MakeSetupExe();
        Assert.True((await bed.Install(payload)).Ok);
        var elsewhere = Path.Combine(bed.Root, @"Portable\PcTidy.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(elsewhere)!);
        await File.WriteAllTextAsync(elsewhere, "portable copy");
        ShellLink.Create(bed.Env.DesktopShortcut, elsewhere, "my portable PC Tidy");

        Assert.True((await bed.Uninstall()).Ok);
        Assert.True(File.Exists(bed.Env.DesktopShortcut));
    }

    [Fact]
    public async Task Delete_data_refuses_a_folder_that_isnt_PC_Tidys()
    {
        using var bed = new SetupBed("not-data");
        var (_, payload, _) = bed.MakeSetupExe();
        Assert.True((await bed.Install(payload)).Ok);
        var notData = Path.Combine(bed.Root, "Somebody's folder");
        Directory.CreateDirectory(notData);
        await File.WriteAllTextAsync(Path.Combine(notData, "photo.jpg"), "precious");
        bed.Use(e => e with { DataRoot = notData });

        var result = await bed.Uninstall(keepData: false);
        Assert.False(result.Ok);
        Assert.True(File.Exists(Path.Combine(notData, "photo.jpg")));
        Assert.Contains(result.Notes, n => n.Contains("doesn't look like PC Tidy's data folder"));
        Assert.Null(bed.UninstallKey()); // everything else was still removed
    }
}

[CollectionDefinition("setup", DisableParallelization = true)]
public class SetupCollection { }
