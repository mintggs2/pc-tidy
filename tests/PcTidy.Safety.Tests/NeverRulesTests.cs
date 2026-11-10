using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Safety.Rules;
using Xunit;

namespace PcTidy.Safety.Tests;

/// <summary>Plan §6 "never touched" list against the Default sandbox profile.</summary>
public sealed class NeverRulesTests : IDisposable
{
    private readonly TestSandbox _s = new();
    public void Dispose() => _s.Dispose();

    private SafetyVerdict Q(string path, string feature = "cleanup") => _s.Guard.Check(_s.Quarantine(path, feature));

    private static void Blocked(SafetyVerdict v, string? reasonContains = null)
    {
        Assert.False(v.Allowed, "expected blocked");
        Assert.False(string.IsNullOrWhiteSpace(v.Reason));
        if (reasonContains is not null) Assert.Contains(reasonContains, v.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ordinary_file_in_downloads_is_allowed()
    {
        var f = _s.File(Path.Combine(_s.K.Downloads, "old.zip"));
        var v = Q(f);
        Assert.True(v.Allowed, v.Reason);
        Assert.Equal(PathProtection.None, v.Protection);
    }

    [Theory]
    [InlineData("Windows", "System32", "kernel32.dll")]
    [InlineData("Windows", "Temp", "a.tmp")]
    [InlineData("Windows", "WinSxS", "x.manifest")]
    [InlineData("Windows", "System32", "DriverStore", "FileRepository", "x.inf")]
    [InlineData("Windows", "Installer", "x.msi")]
    [InlineData("Program Files", "Anything", "a.exe")]
    [InlineData("Program Files (x86)", "Other", "a.exe")]
    [InlineData("ProgramData", "Vendor", "data.bin")]
    public void System_and_app_areas_are_never_touched(params string[] under)
    {
        var path = Path.Combine(new[] { _s.K.SystemDrive }.Concat(under).ToArray());
        Blocked(Q(path));
        Assert.Equal(PathProtection.Never, _s.Guard.ClassifyPath(path).Protection);
    }

    [Fact]
    public void Steam_library_and_games_are_never_touched_with_a_specific_reason()
    {
        var game = Path.Combine(_s.K.SteamLibraries[1], "steamapps", "common", "Big Game", "data.pak");
        Blocked(Q(game), "Steam library");
        Blocked(Q(Path.Combine(_s.K.SteamRoot!, "steam.exe")), "Steam");
        Blocked(Q(Path.Combine(_s.K.SteamRoot!, "steamapps", "appmanifest_1.acf")), "Steam library");
        Blocked(Q(Path.Combine(_s.K.ProgramFiles, "Epic Games", "Fortnite", "x.pak")), "game");
    }

    [Fact]
    public void Installed_app_folders_are_never_touched_even_under_the_profile()
    {
        var userApp = _s.K.AppInstallDirs.Single(a => a.Contains("FakeUserApp"));
        Blocked(Q(Path.Combine(userApp, "app.exe")), "installed app");
    }

    [Theory]
    [InlineData("$Recycle.Bin")]
    [InlineData("System Volume Information")]
    [InlineData("pagefile.sys")]
    [InlineData("hiberfil.sys")]
    [InlineData("swapfile.sys")]
    [InlineData("Recovery")]
    [InlineData("Boot")]
    [InlineData("WindowsApps")]
    [InlineData("XboxGames")]
    [InlineData("Windows.old")]
    public void Drive_root_system_entries_are_never_touched_on_every_drive(string name)
    {
        foreach (var drive in _s.K.FixedDrives)
        {
            Blocked(Q(Path.Combine(drive, name)));
            Blocked(Q(Path.Combine(drive, name, "inner.bin")));
        }
    }

    [Fact]
    public void Drive_roots_profile_and_known_folder_roots_themselves_are_never_moved()
    {
        foreach (var p in _s.K.FixedDrives.Concat(new[]
                 {
                     _s.K.UserProfile, _s.K.Desktop, _s.K.Documents, _s.K.Downloads, _s.K.Pictures, _s.K.Videos, _s.K.Music,
                     _s.K.LocalAppData, _s.K.RoamingAppData, _s.K.UserTemp, _s.K.CloudRoots[0],
                 }))
        {
            Blocked(Q(p));
            Blocked(_s.Guard.Check(_s.Move(p, Path.Combine(_s.K.Downloads, "elsewhere"))));
        }
    }

    [Fact]
    public void Contents_of_a_known_folder_can_be_organized()
    {
        var f = _s.File(Path.Combine(_s.K.Downloads, "a.pdf"));
        var v = _s.Guard.Check(_s.Move(f, Path.Combine(_s.K.Downloads, "Documents", "a.pdf")));
        Assert.True(v.Allowed, v.Reason);
    }

    [Fact]
    public void A_folder_that_contains_a_protected_area_cannot_be_moved()
    {
        // LocalAppData\Programs holds an installed app, so the whole Programs folder is off limits.
        var programs = Path.Combine(_s.K.LocalAppData, "Programs");
        var v = Q(programs);
        Blocked(v, "contains");
        Blocked(Q(Path.Combine(_s.Root, "C", "Users")));
        Blocked(Q(Path.Combine(_s.Root, "D", "SteamLibrary")), "Steam library");
    }

    [Fact]
    public void Pc_tidy_data_folder_is_never_touched_including_a_custom_one()
    {
        Blocked(Q(Path.Combine(_s.K.DataRoot, "Quarantine", "run1", "x.txt")), "PC Tidy");
        var custom = _s.Dir(Path.Combine(_s.K.Downloads, "MyTidyData"));
        _s.Settings.DataRoot = custom;
        Blocked(Q(Path.Combine(custom, "settings.json")), "PC Tidy");
        Blocked(_s.Guard.Check(_s.Move(_s.File(Path.Combine(_s.K.Downloads, "f.txt")), Path.Combine(custom, "f.txt"))), "PC Tidy");
    }

    [Fact]
    public void Extra_protected_folders_from_settings_apply_immediately()
    {
        var keep = _s.Dir(Path.Combine(_s.K.Downloads, "Keep"));
        var f = _s.File(Path.Combine(keep, "tax.pdf"));
        Assert.True(Q(f).Allowed);
        _s.Settings.ExtraProtectedFolders.Add(keep);
        Blocked(Q(f), "asked PC Tidy never");
        Blocked(Q(_s.K.Downloads + "\\..\\Downloads\\Keep\\tax.pdf"));
    }

    [Fact]
    public void Claude_and_codex_config_is_protected_but_transcripts_are_not()
    {
        var claude = _s.K.ClaudeDir;
        var codex = _s.K.CodexDir;
        var feature = "aichats";
        Blocked(Q(Path.Combine(claude, "settings.json"), feature), "AI assistant");
        Blocked(Q(Path.Combine(claude, ".credentials.json"), feature));
        Blocked(Q(Path.Combine(claude, "CLAUDE.md"), feature));
        Blocked(Q(Path.Combine(claude, "sessions", "1234.json"), feature));
        Blocked(Q(Path.Combine(claude, "projects", "C--proj"), feature)); // a project folder is not a transcript
        Blocked(Q(claude, feature));
        Blocked(Q(Path.Combine(codex, "config.toml"), feature));
        Blocked(Q(Path.Combine(codex, "auth.json"), feature));

        Assert.True(Q(_s.File(Path.Combine(claude, "projects", "C--proj", "abc.jsonl")), feature).Allowed);
        Assert.True(Q(_s.File(Path.Combine(claude, "projects", "C--proj", "abc", "subagents", "agent-1.jsonl")), feature).Allowed);
        Assert.True(Q(_s.File(Path.Combine(codex, "sessions", "2026", "01", "02", "rollout-x.jsonl")), feature).Allowed);
        Assert.True(Q(_s.File(Path.Combine(codex, "sessions", "2026", "01", "02", "rollout-y.jsonl.zst")), feature).Allowed);
    }

    [Fact]
    public void System_attribute_blocks_the_item()
    {
        var f = _s.File(Path.Combine(_s.K.Downloads, "desktop.ini"));
        File.SetAttributes(f, FileAttributes.Hidden | FileAttributes.System);
        Blocked(Q(f), "system file");
        Blocked(_s.Guard.Check(_s.Move(f, Path.Combine(_s.K.Downloads, "Other", "desktop.ini"))), "system file");
    }

    [Fact]
    public void Anything_reached_through_a_junction_is_blocked()
    {
        var target = _s.Dir(Path.Combine(_s.K.Downloads, "RealFolder"));
        var inner = _s.File(Path.Combine(target, "file.txt"));
        var link = _s.Junction(Path.Combine(_s.K.Downloads, "LinkFolder"), target);

        Assert.True(Q(inner).Allowed); // the real path is fine
        Blocked(Q(link), "link");
        Blocked(Q(Path.Combine(link, "file.txt")), "link");
        Blocked(_s.Guard.Check(_s.Move(_s.File(Path.Combine(_s.K.Downloads, "m.txt")), Path.Combine(link, "m.txt"))), "link");
        Assert.Equal(PathProtection.Never, _s.Guard.ClassifyPath(Path.Combine(link, "deeper", "new.txt")).Protection);
    }

    [Fact]
    public void Junction_pointing_into_windows_cannot_launder_a_path()
    {
        var link = _s.Junction(Path.Combine(_s.K.Downloads, "sneaky"), _s.K.WindowsDir);
        Blocked(Q(Path.Combine(link, "System32", "x.dll")));
    }

    [Fact]
    public void Paths_outside_this_pcs_drives_are_blocked()
    {
        Blocked(Q(@"\\server\share\file.txt"), "network");
        Blocked(Q(Path.Combine(TestSandbox.TestbedRoot, "elsewhere.txt")), "drives");
    }

    [Fact]
    public void Never_rules_report_never_protection()
    {
        var v = _s.Guard.ClassifyPath(Path.Combine(_s.K.WindowsDir, "System32"));
        Assert.False(v.Allowed);
        Assert.Equal(PathProtection.Never, v.Protection);
        Assert.Contains("Windows", v.Reason);
    }
}
