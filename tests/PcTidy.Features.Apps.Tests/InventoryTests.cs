using PcTidy.Core;
using PcTidy.Features.Apps.Inventory;
using Xunit;
using Hive = PcTidy.Core.Model.RegistryHive;

namespace PcTidy.Features.Apps.Tests;

public sealed class InventoryTests : IDisposable
{
    private readonly Sandbox _box = new(SandboxProfile.Minimal);
    private readonly FakeRegistry _reg = new();

    public void Dispose()
    {
        _reg.Dispose();
        _box.Dispose();
    }

    private AppInventory Inventory(FakeStore? store = null) => new(new AppInventorySources
    {
        Locations = _box.Locations,
        Registry = _reg.Reader,
        Store = store ?? new FakeStore(),
    });

    [Theory]
    [InlineData(null, null, null, null, HiddenReason.NoDisplayName)]
    [InlineData("  ", null, null, null, HiddenReason.NoDisplayName)]
    [InlineData("Driver Pack", 1L, null, null, HiddenReason.SystemComponent)]
    [InlineData("Editor Language Pack", null, "{PARENT}", null, HiddenReason.ChildOfAnotherEntry)]
    [InlineData("Editor Fix 3", null, null, "Security Update", HiddenReason.Update)]
    [InlineData("Editor Fix 4", null, null, "Hotfix", HiddenReason.Update)]
    [InlineData("Update for Windows (KB5034441)", null, null, null, HiddenReason.KbPatch)]
    [InlineData("Contoso Editor", 0L, null, "", HiddenReason.None)]
    public void Hide_rules(string? name, long? systemComponent, string? parent, string? releaseType, HiddenReason expected)
        => Assert.Equal(expected, UninstallRegistryReader.HideReason(name, systemComponent, parent, releaseType));

    [Fact]
    public void Reads_all_three_uninstall_lists_and_applies_the_hide_rules()
    {
        var editor = _box.Folder(Path.Combine(_box.Locations.ProgramFiles, "Contoso", "Editor"), "editor.exe", "uninst.exe");
        _reg.UninstallEntry("HKLM64", "ContosoEditor",
            ("DisplayName", "Contoso Editor 2.1"), ("Publisher", "Contoso Software, Inc."), ("DisplayVersion", "2.1.0"),
            ("InstallLocation", editor + "\\"), ("UninstallString", $"\"{editor}\\uninst.exe\" /S"),
            ("EstimatedSize", 2048), ("InstallDate", "20240115"));
        _reg.UninstallEntry("HKLM64", "{11111111-2222-3333-4444-555555555555}",
            ("DisplayName", "Fabrikam Tool"), ("WindowsInstaller", 1),
            ("UninstallString", "MsiExec.exe /X{11111111-2222-3333-4444-555555555555}"));
        _reg.UninstallEntry("HKLM64", "HiddenSys", ("DisplayName", "Hidden Component"), ("SystemComponent", 1));
        _reg.UninstallEntry("HKLM64", "ChildPatch", ("DisplayName", "Editor Patch"), ("ParentKeyName", "ContosoEditor"));
        _reg.UninstallEntry("HKLM64", "Update1", ("DisplayName", "Editor Security Fix"), ("ReleaseType", "Security Update"));
        _reg.UninstallEntry("HKLM64", "KB5034441", ("DisplayName", "Update for Windows (KB5034441)"));
        _reg.UninstallEntry("HKLM64", "NoName", ("UninstallString", "x.exe"));
        _reg.UninstallEntry("HKLM32", "LegacyApp", ("DisplayName", "Legacy App"), ("UninstallString", "MsiExec.exe /I{AAAAAAAA-2222-3333-4444-555555555555}"));
        _reg.UninstallEntry("HKCU", "Contoso.Notes_Microsoft.Winget.Source_8wekyb3d8bbwe", ("DisplayName", "Contoso Notes"));

        var apps = Inventory().Load(CancellationToken.None);
        var names = apps.Select(a => a.Name).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "Contoso Editor 2.1", "Contoso Notes", "Fabrikam Tool", "Legacy App" }, names);

        var e = apps.Single(a => a.Name == "Contoso Editor 2.1");
        Assert.Equal(AppSource.Desktop, e.Source);
        Assert.Equal(AppScope.Machine, e.Scope);
        Assert.Equal(editor, e.InstallLocation);
        Assert.Equal(2048L * 1024, e.ReportedSizeBytes);
        Assert.Equal(new DateTime(2024, 1, 15), e.InstallDate);
        Assert.False(e.InstallDateIsApproximate);
        Assert.Null(e.MsiProductCode);
        Assert.False(e.IsOrphan);
        Assert.Equal(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ContosoEditor", e.RegistryKey);

        var msi = apps.Single(a => a.Name == "Fabrikam Tool");
        Assert.Equal("{11111111-2222-3333-4444-555555555555}", msi.MsiProductCode);

        var legacy = apps.Single(a => a.Name == "Legacy App");
        Assert.False(legacy.RegistryView64);
        Assert.Equal(@"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\LegacyApp", legacy.RegistryKey);
        // ProductCode from the command line when the key isn't the code.
        Assert.Equal("{AAAAAAAA-2222-3333-4444-555555555555}", legacy.MsiProductCode);
        // No InstallDate value: the key's last-change time, marked approximate.
        Assert.True(legacy.InstallDateIsApproximate);
        Assert.InRange(legacy.InstallDate!.Value, DateTime.Now.Date.AddDays(-1), DateTime.Now.Date);

        var notes = apps.Single(a => a.Name == "Contoso Notes");
        Assert.Equal(AppScope.User, notes.Scope);
        Assert.Equal("Contoso.Notes", notes.WingetId);
        Assert.Equal(Hive.CurrentUser, notes.RegistryHive);
    }

    [Fact]
    public void Same_app_in_two_lists_is_shown_once()
    {
        foreach (var list in new[] { "HKLM64", "HKLM32" })
            _reg.UninstallEntry(list, "Dup", ("DisplayName", "Twin App"), ("DisplayVersion", "1.0"), ("UninstallString", "MsiExec.exe /X{BBBBBBBB-2222-3333-4444-555555555555}"));
        Assert.Single(Inventory().Load(CancellationToken.None), a => a.Name == "Twin App");
    }

    [Fact]
    public void Orphaned_entries_are_flagged_when_their_folder_or_uninstaller_is_gone()
    {
        var missingFolder = Path.Combine(_box.Locations.ProgramFiles, "Gone App");
        _reg.UninstallEntry("HKLM64", "GoneFolder", ("DisplayName", "Gone App"), ("InstallLocation", missingFolder),
            ("UninstallString", $"\"{missingFolder}\\unins000.exe\""));
        _reg.UninstallEntry("HKLM64", "GoneUninstaller", ("DisplayName", "Lost Uninstaller"),
            ("UninstallString", Path.Combine(_box.Locations.ProgramFiles, "Lost", "uninstall.exe") + " /quiet"));
        var present = _box.Folder(Path.Combine(_box.Locations.ProgramFiles, "Here"), "uninstall.exe");
        _reg.UninstallEntry("HKLM64", "Present", ("DisplayName", "Present App"), ("InstallLocation", present),
            ("UninstallString", $"\"{present}\\uninstall.exe\""));
        // msiexec always exists, so an MSI entry without a folder isn't called orphaned.
        _reg.UninstallEntry("HKLM64", "MsiNoFolder", ("DisplayName", "Msi App"), ("UninstallString", "MsiExec.exe /X{CCCCCCCC-2222-3333-4444-555555555555}"));

        var apps = Inventory().Load(CancellationToken.None);
        Assert.Equal("The folder it was installed in is gone.", apps.Single(a => a.Name == "Gone App").OrphanReason);
        Assert.Equal("Its uninstaller is gone.", apps.Single(a => a.Name == "Lost Uninstaller").OrphanReason);
        Assert.False(apps.Single(a => a.Name == "Present App").IsOrphan);
        Assert.False(apps.Single(a => a.Name == "Msi App").IsOrphan);
    }

    [Fact]
    public void Store_apps_hide_frameworks_resources_and_system_packages_and_match_the_preinstalled_catalog()
    {
        var store = new FakeStore()
            .Add("Clipchamp.Clipchamp", "Clipchamp")
            .Add("Microsoft.VCLibs.140.00", framework: true)
            .Add("Microsoft.LanguageExperiencePackfr-FR", resource: true)
            .Add("Microsoft.Windows.ShellExperienceHost", system: true)
            .Add("Microsoft.WindowsStore", "Microsoft Store")
            .Add("Contoso.PhotoEditor", "ms-resource:AppName");

        var apps = Inventory(store).Load(CancellationToken.None).Where(a => a.Source == AppSource.Store).ToList();
        Assert.Equal(new[] { "Clipchamp", "Contoso.PhotoEditor", "Microsoft Store" }, apps.Select(a => a.Name).OrderBy(n => n));
        Assert.Equal(Catalog.Apps.PreinstalledTier.Offer, apps.Single(a => a.Name == "Clipchamp").Preinstalled!.Tier);
        Assert.Equal(Catalog.Apps.PreinstalledTier.Never, apps.Single(a => a.Name == "Microsoft Store").Preinstalled!.Tier);
        Assert.Null(apps.Single(a => a.Name == "Contoso.PhotoEditor").Preinstalled);
    }

    [Fact]
    public void Steam_games_come_from_their_manifest_and_replace_steams_own_uninstall_entry()
    {
        using var box = new Sandbox(SandboxProfile.Default with { SecondDrive = false, Epic = false, OneDrive = false });
        var steamapps = Path.Combine(box.Locations.SteamRoot!, "steamapps");
        Directory.CreateDirectory(Path.Combine(steamapps, "common", "Team Fortress 2"));
        var lastPlayed = DateTimeOffset.UtcNow.AddDays(-400).ToUnixTimeSeconds();
        File.WriteAllText(Path.Combine(steamapps, "appmanifest_440.acf"),
            $"\"AppState\"\n{{\n\t\"appid\"\t\t\"440\"\n\t\"name\"\t\t\"Team Fortress 2\"\n\t\"installdir\"\t\t\"Team Fortress 2\"\n\t\"StateFlags\"\t\t\"4\"\n\t\"SizeOnDisk\"\t\t\"30000000000\"\n\t\"LastPlayed\"\t\t\"{lastPlayed}\"\n}}\n");
        _reg.UninstallEntry("HKLM64", "Steam App 440", ("DisplayName", "Team Fortress 2"),
            ("UninstallString", $"\"{box.Locations.SteamRoot}\\steam.exe\" steam://uninstall/440"));

        var apps = new AppInventory(new AppInventorySources { Locations = box.Locations, Registry = _reg.Reader, Store = null }).Load(CancellationToken.None);
        var game = Assert.Single(apps, a => a.Name == "Team Fortress 2");
        Assert.Equal(AppSource.Steam, game.Source);
        Assert.Equal("Managed by Steam", game.ManagedBy);
        Assert.Equal("440", game.LauncherId);
        Assert.Equal(30_000_000_000L, game.ReportedSizeBytes);
        Assert.Equal(LastUsedSource.Steam, game.LastUsedSource);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(lastPlayed).UtcDateTime, game.LastUsedUtc);
    }

    [Fact]
    public void Measured_size_counts_the_install_folder_and_refuses_broad_folders()
    {
        var folder = _box.Folder(Path.Combine(_box.Locations.ProgramFiles, "Sized"), "a.bin", "sub\\b.bin");
        var inventory = Inventory();
        Assert.Equal(2 * 1234L, inventory.Measure(folder, CancellationToken.None));
        Assert.Null(inventory.Measure(_box.Locations.ProgramFiles, CancellationToken.None));
        Assert.Null(inventory.Measure(_box.Locations.SystemDrive, CancellationToken.None));
        Assert.Null(inventory.Measure(Path.Combine(_box.Root, "missing"), CancellationToken.None));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\App\\uninst.exe\" /S", "C:\\Program Files\\App\\uninst.exe")]
    [InlineData("C:\\Program Files\\App\\uninst.exe /S", "C:\\Program Files\\App\\uninst.exe")]
    [InlineData("MsiExec.exe /X{11111111-2222-3333-4444-555555555555}", null)]
    [InlineData("rundll32.exe something", null)]
    [InlineData("uninst.exe", null)]
    public void Uninstall_command_program_path(string command, string? expected)
        => Assert.Equal(expected, UninstallCommand.ProgramPath(command));
}
