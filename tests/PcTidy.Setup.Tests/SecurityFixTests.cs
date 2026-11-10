using PcTidy.Setup.Install;
using PcTidy.Setup.Payload;
using PcTidy.Setup.Windows;

namespace PcTidy.Setup.Tests;

/// <summary>
/// v3 safety review fixes: SET-1 (the admin copy's environment checks), SET-2 (the admin part installs only a staged
/// payload that matches the hash built into the running program) and UNI-1 ("delete my data" only takes PC Tidy's own
/// things out of PC Tidy's own folder). All inside testbed\secfix\setup\; no admin prompt, no real ACL change.
/// </summary>
[Collection("setup")]
public class SecurityFixTests
{
    private static readonly Dictionary<string, string> WithUninstaller = new() { [Product.UninstallerExe] = "fake uninstaller" };

    private static StagingOptions Staging(SetupBed bed, Func<string, string?>? check = null)
        => new(Path.Combine(bed.Root, "admin-temp"), RestrictAccess: false, CheckFolder: check ?? (_ => null));

    private static string[] Install => new[] { SetupFlow.ElevatedInstallSwitch };

    // ------------------------------------------------------------------ SET-2

    [Fact]
    public void The_admin_part_installs_a_staged_copy_that_matches_the_built_in_hash_and_cleans_up()
    {
        using var bed = new SetupBed("staged");
        var (exe, payload, _) = bed.MakeSetupExe("3.1.0", WithUninstaller);
        var staging = Staging(bed);

        var code = Program.RunElevatedPart(SetupArgs.Parse(Install), bed.Env, elevated: true, exe, staging: staging, expectedSha256: payload.Sha256);

        Assert.Equal(Program.ExitOk, code);
        Assert.Equal("fake app 3.1.0", File.ReadAllText(bed.Env.AppPath));
        Assert.Equal("fake uninstaller", File.ReadAllText(bed.Env.UninstallerPath)); // from the verified payload, never the user-folder stub
        Assert.Empty(Directory.EnumerateFileSystemEntries(staging.Parent)); // staging folder deleted
    }

    [Fact]
    public void A_setup_file_swapped_after_the_prompt_is_refused_even_though_its_own_trailer_matches()
    {
        using var bed = new SetupBed("swapped");
        var (genuine, approved, _) = bed.MakeSetupExe("3.1.0", WithUninstaller);
        // The attacker's file at the same path: a complete, self-consistent Setup with another payload.
        var (evil, _, _) = bed.MakeSetupExe("6.6.6", new Dictionary<string, string>(WithUninstaller) { ["evil.dll"] = "boom" });
        File.Copy(evil, genuine, overwrite: true);
        Assert.True(PayloadFile.Find(genuine)!.Verify()); // its trailer hash is fine; that proves nothing

        var code = Program.RunElevatedPart(SetupArgs.Parse(Install), bed.Env, elevated: true, genuine, staging: Staging(bed), expectedSha256: approved.Sha256);

        Assert.Equal(Program.ExitDamaged, code);
        Assert.False(Directory.Exists(bed.Env.InstallDir));
        Assert.False(Directory.Exists(Staging(bed).Parent) && Directory.EnumerateFileSystemEntries(Staging(bed).Parent).Any());
    }

    [Fact]
    public void Without_a_built_in_hash_the_admin_part_installs_nothing()
    {
        using var bed = new SetupBed("untrusted");
        var (exe, _, _) = bed.MakeSetupExe("3.1.0", WithUninstaller);
        Assert.Null(PayloadTrust.Embedded); // developer/test build: nothing compiled in
        Assert.Equal(Program.ExitUntrusted, Program.RunElevatedPart(SetupArgs.Parse(Install), bed.Env, elevated: true, exe, staging: Staging(bed)));
        Assert.False(Directory.Exists(bed.Env.InstallDir));
        Assert.Contains("wasn't built as a release", SetupFlow.FromInstallExitCode(Program.ExitUntrusted, false).Message);
    }

    [Fact]
    public void A_staging_folder_that_isnt_admin_only_is_refused_and_removed()
    {
        using var bed = new SetupBed("weak-staging");
        var (exe, payload, _) = bed.MakeSetupExe("3.1.0", WithUninstaller);
        var staging = Staging(bed, _ => "BUILTIN\\Users may change it.");
        Assert.Equal(Program.ExitUnsafe, Program.RunElevatedPart(SetupArgs.Parse(Install), bed.Env, elevated: true, exe, staging: staging, expectedSha256: payload.Sha256));
        Assert.False(Directory.Exists(bed.Env.InstallDir));
        Assert.Empty(Directory.EnumerateFileSystemEntries(staging.Parent));
    }

    [Fact]
    public void The_admin_part_never_copies_the_uninstaller_from_the_user_folder()
    {
        using var bed = new SetupBed("no-uninstaller");
        var (exe, payload, _) = bed.MakeSetupExe("3.1.0"); // payload without uninstall.exe
        Assert.Equal(Program.ExitFailed, Program.RunElevatedPart(SetupArgs.Parse(Install), bed.Env, elevated: true, exe, staging: Staging(bed), expectedSha256: payload.Sha256));
        Assert.False(File.Exists(bed.Env.AppPath));
        Assert.False(File.Exists(bed.Env.UninstallerPath));
    }

    [Fact]
    public void The_staged_copy_stays_locked_against_writes_until_it_is_disposed()
    {
        using var bed = new SetupBed("locked");
        var (exe, payload, _) = bed.MakeSetupExe("3.1.0", WithUninstaller);
        var (staged, code) = StagedPayload.Create(exe, payload.Sha256, Staging(bed));
        Assert.Equal(Program.ExitOk, code);
        using (staged)
        {
            Assert.Throws<IOException>(() => new FileStream(staged!.ZipPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose());
            Assert.Equal(payload.Length, staged!.Length);
            using var zip = new System.IO.Compression.ZipArchive(staged.Open());
            Assert.NotNull(zip.GetEntry(Product.AppExe));
        }
        Assert.False(Directory.Exists(staged!.Folder));
    }

    [Fact]
    public void Embedded_hash_parsing_accepts_only_64_hex_digits()
    {
        Assert.Null(PayloadTrust.Parse(null));
        Assert.Null(PayloadTrust.Parse("abc"));
        Assert.Null(PayloadTrust.Parse(new string('g', 64)));
        Assert.Equal(32, PayloadTrust.Parse(new string('a', 64))!.Length);
    }

    // ------------------------------------------------------------------ SET-1

    [Fact]
    public void The_admin_copy_refuses_modules_from_the_temp_folder_or_its_own_user_folder()
    {
        var self = @"C:\Users\tester\Downloads\PC Tidy Setup 3.1.0.exe";
        var temp = @"C:\Users\tester\AppData\Local\Temp\";
        var windows = new[] { self, @"C:\Windows\System32\kernel32.dll", @"C:\Windows\System32\ntdll.dll" };
        Assert.Null(ElevationGuard.ForeignModule(self, windows, temp));
        Assert.NotNull(ElevationGuard.ForeignModule(self, windows.Append(@"C:\Users\tester\Downloads\version.dll"), temp));
        Assert.NotNull(ElevationGuard.ForeignModule(self, windows.Append(temp + @".net\PC Tidy Setup\abc\wpfgfx_cor3.dll"), temp));
        // uninstall.exe in admin-only Program Files: its own DLLs beside it are fine, %TEMP% never is.
        var installed = @"C:\Program Files\PC Tidy\uninstall.exe";
        Assert.Null(ElevationGuard.ForeignModule(installed, new[] { installed, @"C:\Program Files\PC Tidy\wpfgfx_cor3.dll" }, temp, checkOwnFolder: false));
        Assert.NotNull(ElevationGuard.ForeignModule(installed, new[] { installed, temp + "x.dll" }, temp, checkOwnFolder: false));
    }

    [Fact]
    public void An_unsafe_environment_gets_a_plain_refusal()
    {
        var text = SetupFlow.FromInstallExitCode(Program.ExitUnsafe, isUpdate: false).Message;
        Assert.Contains("Nothing was changed", text);
        Assert.False(SetupFlow.FromUninstallExitCode(Program.ExitUnsafe).Ok);
    }

    // ------------------------------------------------------------------ UNI-1

    private static void Own(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "Quarantine", "run1"));
        File.WriteAllText(Path.Combine(root, "Quarantine", "run1", "old.txt"), "set aside");
        Directory.CreateDirectory(Path.Combine(root, "runs"));
        File.WriteAllText(Path.Combine(root, "settings.json"), "{}");
    }

    [Fact]
    public async Task Delete_my_data_removes_only_PC_Tidys_own_things_and_keeps_the_users_files_and_folder()
    {
        using var bed = new SetupBed("data-mixed");
        var (_, payload, _) = bed.MakeSetupExe();
        Assert.True((await bed.Install(payload)).Ok);
        Own(bed.Env.DataRoot);
        var mine = Path.Combine(bed.Env.DataRoot, "My notes.docx");
        File.WriteAllText(mine, "precious");
        Directory.CreateDirectory(Path.Combine(bed.Env.DataRoot, "Empty folder of mine"));

        var plan = new Uninstaller(bed.Env).PlanDataRemoval();
        Assert.Contains(Path.Combine(bed.Env.DataRoot, "Quarantine"), plan.Delete);
        Assert.Contains(mine, plan.Keep);
        Assert.Contains("My notes.docx", plan.Describe());

        var result = await bed.Uninstall(keepData: false);
        Assert.True(File.Exists(mine));
        Assert.True(Directory.Exists(Path.Combine(bed.Env.DataRoot, "Empty folder of mine")));
        Assert.False(Directory.Exists(Path.Combine(bed.Env.DataRoot, "Quarantine")));
        Assert.False(File.Exists(Path.Combine(bed.Env.DataRoot, "settings.json")));
        Assert.Contains(result.Notes, n => n.Contains("Other things in it were left alone"));
    }

    [Fact]
    public async Task A_moved_data_folder_counts_as_PC_Tidys_only_with_its_marker_and_the_recorded_id()
    {
        using var bed = new SetupBed("data-marker");
        var (_, payload, _) = bed.MakeSetupExe();
        Assert.True((await bed.Install(payload)).Ok);
        var moved = Path.Combine(bed.Root, @"D\PC Tidy Data");
        Own(moved);
        bed.Use(e => e with { DataRoot = moved });
        const string id = "0123456789abcdef0123456789abcdef";
        File.WriteAllText(Path.Combine(moved, DataFolderMarker.FileName), $$"""{"format":"{{DataFolderMarker.Format}}","id":"{{id}}"}""");

        // Marker but no matching record next to the default folder: not ours.
        var refused = DataRemovalPlanner.Plan(bed.Env);
        Assert.DoesNotContain(refused.Delete, p => p.StartsWith(moved, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(refused.Refused, r => r.Contains("doesn't look like PC Tidy's data folder"));

        Directory.CreateDirectory(bed.Env.DefaultDataRoot);
        File.WriteAllText(Path.Combine(bed.Env.DefaultDataRoot, DataFolderMarker.IdFileName), id);
        var result = await bed.Uninstall(keepData: false);
        Assert.True(result.Ok, result.Message + string.Join("\n", result.Notes));
        Assert.False(Directory.Exists(moved)); // only PC Tidy's own things were in it, so the folder went too
        Assert.True(Directory.Exists(Path.Combine(bed.Root, "D")));
    }
}
