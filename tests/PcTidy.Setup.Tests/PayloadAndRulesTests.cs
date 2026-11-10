using PcTidy.Setup.Windows;
using System.Buffers.Binary;
using PcTidy.Setup.Install;
using PcTidy.Setup.Payload;

namespace PcTidy.Setup.Tests;

[Collection("setup")]
public class PayloadAndRulesTests
{
    [Fact]
    public void Payload_round_trip_verify_and_damage_detection()
    {
        using var bed = new SetupBed("payload");
        var (exe, payload, stub) = bed.MakeSetupExe("2.3.4");
        Assert.Equal(stub.Length, payload.StubLength);
        Assert.True(payload.Verify());
        Assert.Equal("2.3.4", Program.ReadPayloadVersion(payload));

        var bytes = File.ReadAllBytes(exe);
        bytes[payload.Offset + 10] ^= 0xFF;
        File.WriteAllBytes(exe, bytes);
        Assert.False(PayloadFile.Find(exe)!.Verify());

        var plain = Path.Combine(bed.Root, "plain.exe");
        File.WriteAllBytes(plain, stub);
        Assert.Null(PayloadFile.Find(plain)); // uninstall.exe / developer build
    }

    [Fact]
    public void Payload_is_still_found_after_signing_adds_a_certificate_table()
    {
        using var bed = new SetupBed("signed");
        // A minimal PE32+ header whose security directory points at a certificate table at the very end,
        // after 3 padding bytes, as signtool lays it out.
        var stub = new byte[0x400];
        stub[0] = (byte)'M'; stub[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(stub.AsSpan(0x3C), 0x80);
        "PE\0\0"u8.CopyTo(stub.AsSpan(0x80));
        BinaryPrimitives.WriteUInt16LittleEndian(stub.AsSpan(0x80 + 24), 0x20B);
        var stubPath = Path.Combine(bed.Root, "pe-stub.exe");
        File.WriteAllBytes(stubPath, stub);
        var (_, original, _) = bed.MakeSetupExe();
        var zip = Path.Combine(bed.Root, "copy.zip");
        using (var s = original.Open()) using (var d = File.Create(zip)) s.CopyTo(d);

        var exe = Path.Combine(bed.Root, "signed.exe");
        PayloadFile.Write(stubPath, zip, exe);
        using (var fs = new FileStream(exe, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Seek(0, SeekOrigin.End);
            fs.Write(new byte[3]);
            var certOffset = fs.Length;
            fs.Write(new byte[16]);
            var entry = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(entry, (uint)certOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(4), 16);
            fs.Position = 0x80 + 24 + 112 + 4 * 8;
            fs.Write(entry);
        }

        var found = PayloadFile.Find(exe);
        Assert.NotNull(found);
        Assert.Equal(stub.Length, found!.StubLength);
        Assert.True(found.Verify());
    }

    [Fact]
    public void Install_folder_rules_only_program_files_pc_tidy()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var data = AppDataLike("PC Tidy");
        Assert.Equal(Path.Combine(programFiles, "PC Tidy"), InstallEnvironment.DefaultInstallDir);
        Assert.Null(FolderSafety.CheckInstallDir(InstallEnvironment.DefaultInstallDir, programFiles, data));
        Assert.Null(FolderSafety.CheckInstallDir(Path.Combine(programFiles, "pc tidy") + "\\", programFiles, data));
        foreach (var bad in new[]
                 {
                     "relative\\path", "C:\\", Environment.GetFolderPath(Environment.SpecialFolder.Windows), programFiles,
                     Path.Combine(programFiles, "PC Tidy", "sub"), Path.Combine(programFiles, "Other"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "PC Tidy"), // the old per-user place
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Tools", "PC Tidy"),
                     @"\\server\share\PC Tidy",
                 })
            Assert.NotNull(FolderSafety.CheckInstallDir(bad, programFiles, data));
        // The data folder may never be inside the program folder (or the other way round).
        Assert.NotNull(FolderSafety.CheckInstallDir(InstallEnvironment.DefaultInstallDir, programFiles, Path.Combine(InstallEnvironment.DefaultInstallDir, "data")));
    }
    [Fact]
    public void Data_folder_cleaning_never_touches_a_drive_root_windows_or_a_user_folder()
    {
        using var bed = new SetupBed("markers");
        var folder = Path.Combine(bed.Root, "some folder");
        Directory.CreateDirectory(folder);
        Assert.True(FolderSafety.IsCleanableFolder(folder));
        Assert.False(FolderSafety.IsCleanableFolder(Path.Combine(bed.Root, "missing")));
        Assert.False(FolderSafety.IsCleanableFolder(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        Assert.False(FolderSafety.IsCleanableFolder(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
        Assert.False(FolderSafety.IsCleanableFolder(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
        Assert.False(FolderSafety.IsCleanableFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp")));
        Assert.False(FolderSafety.IsCleanableFolder("C:\\"));
        Assert.False(FolderSafety.IsCleanableFolder(@"\\server\share\PC Tidy"));
    }

    [Fact]
    public void Test_registry_paths_stay_under_the_test_key_and_never_hklm()
    {
        var t = RegistryPaths.ForTest("abc", @"secfix\setup");
        Assert.True(t.IsTest);
        Assert.Equal(Microsoft.Win32.RegistryHive.CurrentUser, t.MachineHive);
        foreach (var p in new[] { t.UninstallKey, t.LegacyUninstallKey, t.Classes, t.Run, t.StartupApprovedRun })
            Assert.StartsWith(@"Software\PCTidyTest\secfix\setup\abc\", p);
        Assert.NotEqual(t.UninstallKey, t.LegacyUninstallKey);
        Assert.False(RegistryPaths.Real.IsTest);
        Assert.Equal(Microsoft.Win32.RegistryHive.LocalMachine, RegistryPaths.Real.MachineHive);
        Assert.Throws<ArgumentException>(() => RegistryPaths.ForTest(@"..\..\Microsoft"));
        Assert.Throws<ArgumentException>(() => RegistryPaths.ForTest("abc", @"..\Microsoft"));
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\PCTidy", RegistryPaths.Real.UninstallKey);
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\PCTidy", RegistryPaths.Real.LegacyUninstallKey); // in HKCU
    }

    [Fact]
    public void Command_line()
    {
        var u = SetupArgs.Parse(new[] { "--uninstall", "--quiet" });
        Assert.True(u.Uninstall && u.Quiet && !u.RemoveData && !u.IsElevatedPart);
        var s = SetupArgs.Parse(new[] { "--silent", "--desktop", "--whatever" });
        Assert.True(s.Silent && s.Desktop);
        var e = SetupArgs.Parse(new[] { "--install-elevated", "--no-start-menu", "--desktop" });
        Assert.True(e.ElevatedInstall && e.NoStartMenu && e.Desktop && e.IsElevatedPart);
        Assert.True(SetupArgs.Parse(new[] { "--uninstall-elevated" }).ElevatedUninstall);
        Assert.Equal(new[] { "--install-elevated", "--desktop", "--no-start-menu" },
            SetupFlow.InstallArguments(new InstallRequest { OpenPayload = () => Stream.Null, UninstallerSource = "x", DesktopShortcut = true, StartMenuShortcuts = false }));
    }

    [Fact]
    public async Task The_real_elevation_passes_only_plain_switches()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new RunAsSetupElevation().RunElevatedAsync(new[] { "--install-elevated", @"C:\Windows" }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => new RunAsSetupElevation().RunElevatedAsync(new[] { "--x \"y\"" }, default));
    }
    private static string AppDataLike(string name) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), name);
}
