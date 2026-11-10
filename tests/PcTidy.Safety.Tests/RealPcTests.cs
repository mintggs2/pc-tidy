using PcTidy.Core;
using PcTidy.Core.Model;
using Xunit;

namespace PcTidy.Safety.Tests;

/// <summary>
/// Read-only checks against this real PC: detection runs and Check() is called, nothing is ever acted on.
/// Assertions are conditional on what's installed so they pass on any PC (no Steam, no OneDrive, one drive...).
/// </summary>
public sealed class RealPcTests
{
    private static readonly AppSettings Settings = new();
    private static readonly WindowsKnownLocations K = new(Settings);
    private static readonly SafetyGuard Guard = new(K, Settings);

    private static PlanItem Q(string path, string feature = "cleanup", string? carveOut = null)
        => new() { Kind = ActionKind.QuarantinePath, FeatureId = feature, Description = "read-only test", SourcePath = path, CarveOutId = carveOut };

    [Fact]
    public void Detection_finds_the_basics_and_marks_the_real_pc()
    {
        Assert.False(K.IsSandbox);
        Assert.True(Directory.Exists(K.WindowsDir));
        Assert.True(Directory.Exists(K.UserProfile));
        Assert.True(Directory.Exists(K.ProgramFiles));
        Assert.Contains(K.FixedDrives, d => K.WindowsDir.StartsWith(d, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(Path.Combine(K.WindowsDir, "Temp"), K.WindowsTemp);
        Assert.All(new[] { K.Desktop, K.Documents, K.Downloads, K.Pictures, K.Videos, K.Music }, p => Assert.True(Path.IsPathFullyQualified(p)));
        Assert.All(K.AppInstallDirs.Concat(K.GameInstallDirs), p => Assert.True(p.Length > 3, "a drive root must never be an install dir"));
        Assert.DoesNotContain(K.AppInstallDirs, p => p.Equals(K.UserProfile, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(AppSettings.DefaultDataRoot, K.DataRoot, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Refresh_never_throws_and_bumps_the_generation()
    {
        var before = K.Generation;
        K.Refresh();
        Assert.True(K.Generation > before);
        Assert.True(Guard.ClassifyPath(Path.Combine(K.WindowsDir, "System32")).Protection == PathProtection.Never);
    }

    [Fact]
    public void Windows_and_program_files_are_protected()
    {
        foreach (var p in new[]
                 {
                     Path.Combine(K.WindowsDir, "System32", "kernel32.dll"),
                     Path.Combine(K.WindowsDir, "System32", "drivers", "etc", "hosts"),
                     Path.Combine(K.WindowsDir, "WinSxS"),
                     Path.Combine(K.ProgramFiles, "Common Files"),
                     Path.Combine(K.ProgramFilesX86, "Microsoft"),
                     K.ProgramData,
                     K.SystemDrive,
                 })
        {
            var v = Guard.Check(Q(p));
            Assert.False(v.Allowed, p);
            Assert.False(string.IsNullOrWhiteSpace(v.Reason));
        }
    }

    [Fact]
    public void Windows_temp_needs_the_carve_out_and_never_the_folder_itself()
    {
        var child = Path.Combine(K.WindowsTemp, "pctidy-test-never-created.tmp");
        Assert.False(Guard.Check(Q(child)).Allowed);
        var withCarveOut = Guard.Check(Q(child, carveOut: CarveOuts.WindowsTemp));
        Assert.True(withCarveOut.Allowed, withCarveOut.Reason);
        Assert.False(Guard.Check(Q(K.WindowsTemp, carveOut: CarveOuts.WindowsTemp)).Allowed);
        Assert.False(Guard.Check(Q(Path.Combine(K.WindowsTemp, "..", "System32"), carveOut: CarveOuts.WindowsTemp)).Allowed);
    }

    [Fact]
    public void If_steam_is_installed_its_libraries_are_protected()
    {
        if (K.SteamRoot is null) return; // not installed on this PC: nothing to check
        Assert.Contains(K.SteamLibraries, l => l.Equals(K.SteamRoot, StringComparison.OrdinalIgnoreCase));
        foreach (var lib in K.SteamLibraries)
        {
            var v = Guard.Check(Q(Path.Combine(lib, "steamapps", "common")));
            Assert.False(v.Allowed);
            Assert.Contains("Steam", v.Reason);
        }
        foreach (var app in new GameLibraryInfo(K).SteamApps.Take(5))
        {
            Assert.False(string.IsNullOrWhiteSpace(app.Name));
            Assert.False(Guard.Check(Q(app.InstallPath, "organize")).Allowed);
        }
    }

    [Fact]
    public void If_epic_games_are_installed_they_are_protected()
    {
        foreach (var app in new GameLibraryInfo(K).EpicApps.Take(5))
            Assert.False(Guard.Check(Q(Path.Combine(app.InstallLocation, "x.pak"))).Allowed);
    }

    [Fact]
    public void Cloud_folders_if_any_are_ask_first_and_their_roots_never_move()
    {
        foreach (var root in K.CloudRoots)
        {
            Assert.True(Guard.IsInCloudRoot(root));
            Assert.False(Guard.Check(Q(root, "organize")).Allowed);
            var inside = Guard.Check(Q(Path.Combine(root, "pctidy-test-never-created.txt"), "organize"));
            Assert.False(inside.Allowed);
            Assert.Equal(PathProtection.AskFirst, inside.Protection);
        }
        // Wherever the Desktop lives (local or redirected into OneDrive), the folder itself never moves.
        Assert.False(Guard.Check(Q(K.Desktop, "organize")).Allowed);
        if (Guard.IsInCloudRoot(K.Desktop))
            Assert.Equal(PathProtection.AskFirst, Guard.Check(Q(Path.Combine(K.Desktop, "pctidy-test.txt"), "organize")).Protection);
    }

    [Fact]
    public void Installed_apps_from_the_uninstall_registry_are_protected()
    {
        foreach (var dir in K.AppInstallDirs.Where(Directory.Exists).Take(10))
            Assert.False(Guard.Check(Q(Path.Combine(dir, "pctidy-test.dll"))).Allowed, dir);
    }

    [Fact]
    public void Real_junctions_in_the_profile_are_never_followed()
    {
        var legacy = Path.Combine(K.LocalAppData, "Application Data"); // Windows' compatibility junction
        if (!Directory.Exists(legacy)) return;
        Assert.False(Guard.ClassifyPath(Path.Combine(legacy, "x.txt")).Allowed);
    }

    [Fact]
    public void Pc_tidy_data_and_ai_config_are_protected()
    {
        Assert.False(Guard.Check(Q(Path.Combine(K.DataRoot, "settings.json"))).Allowed);
        Assert.False(Guard.Check(Q(Path.Combine(K.ClaudeDir, "settings.json"), "aichats")).Allowed);
        Assert.False(Guard.Check(Q(Path.Combine(K.CodexDir, "auth.json"), "aichats")).Allowed);
    }
}
