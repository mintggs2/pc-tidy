using PcTidy.Catalog.Games;
using PcTidy.Core.Model;
using PcTidy.Features.Games.Cleanup;
using PcTidy.Features.Games.Environment;
using PcTidy.Features.Games.Optimize;
using Xunit;

namespace PcTidy.Features.Games.Tests;

/// <summary>
/// Research additions, Update 3 (Community tweaks §9.7) for Games: the shader-cache rebuild warning, a "Run as administrator"
/// tick on a game and read-only settings files. Fake PCs under testbed\research-additions-3; the compatibility settings
/// come from a fake environment, never from this PC's registry.
/// </summary>
public class ResearchAdditions3Tests
{
    private const string Area = "research-additions-3";
    private static readonly long Updated = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
    private static readonly DateTime Old = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Shader_caches_are_never_ticked_and_warn_that_rebuilding_can_take_an_hour()
    {
        using var s = new GamesSandbox("shader", area: Area);
        s.Put(Path.Combine(s.K.LocalAppData, "FortniteGame", "Saved", "D3D12Cache", "cache.bin"), new string('c', 3000), Old);

        var result = await new GameCleanupScanner().ScanAsync(s.Context(), null, CancellationToken.None);
        var group = Assert.Single(result.Groups, g => g.Id == "games-cleanup-shaders");
        Assert.Contains(GameCleanupTargets.ShaderRebuildWarning, group.Description);
        var shader = Assert.Single(group.Items);
        Assert.False(shader.DefaultSelected);
        Assert.Equal(Badge.Check, shader.Badge);
        Assert.Contains("Games rebuild this on first launch; that can take up to about an hour", shader.Reason);
        Assert.NotEmpty(shader.Actions); // still offered, just never ticked for you
    }

    private static string UnrealGame(GamesSandbox s, string appId = "555", string name = "Shooter")
        => s.SteamGame(s.SteamRoot, appId, name, name, null, Updated,
            $@"{name}\Binaries\Win64\{name}-Win64-Shipping.exe", @"Engine\Binaries\Win64\x.dll", $@"{name}\Content\Paks\{name}-WindowsClient.pak");

    [Fact]
    public async Task Run_as_administrator_on_a_detected_game_is_explained_and_never_changed()
    {
        using var s = new GamesSandbox("runasadmin", area: Area);
        var install = UnrealGame(s);
        var exe = Path.Combine(install, "Shooter", "Binaries", "Win64", "Shooter-Win64-Shipping.exe");
        var env = (FakeEnvironment)s.Environment;
        env.Compatibility.Add(new CompatibilitySetting(exe, "~ RUNASADMIN", AllUsers: false));
        env.Compatibility.Add(new CompatibilitySetting(exe, "~ HIGHDPIAWARE", AllUsers: true));                  // no admin tick
        env.Compatibility.Add(new CompatibilitySetting(@"C:\Tools\NotAGame\tool.exe", "RUNASADMIN", AllUsers: false)); // not a detected game

        var result = await new OptimizerScanner().ScanAsync(s.Context(), null, CancellationToken.None);
        var row = Assert.Single(result.AllItems, i => i.Id.Contains(".runasadmin."));
        Assert.Equal("Shooter-Win64-Shipping.exe is set to run as administrator", row.Title);
        Assert.Empty(row.Actions);
        Assert.False(row.DefaultSelected);
        Assert.Contains("untick \"Run this program as an administrator\"", row.Details!["How to undo"]);
        Assert.Equal(GameLeftoverChecks.RunAsAdminSource, row.Details["Source"]);
        Assert.DoesNotContain(result.AllItems, i => i.Detail?.Contains("NotAGame") == true);
    }

    [Theory]
    [InlineData("~ RUNASADMIN", true)]
    [InlineData("RUNASADMIN", true)]
    [InlineData("~ DISABLEDXMAXIMIZEDWINDOWEDMODE RUNASADMIN HIGHDPIAWARE", true)]
    [InlineData("~ HIGHDPIAWARE", false)]
    [InlineData("~ NOTRUNASADMINREALLY", false)]
    public void Run_as_admin_is_matched_as_a_whole_flag(string flags, bool expected)
        => Assert.Equal(expected, new CompatibilitySetting(@"C:\g.exe", flags, false).RunsAsAdmin);

    [Fact]
    public async Task A_read_only_settings_file_is_explained_and_its_edits_are_held_back()
    {
        using var s = new GamesSandbox("readonly", area: Area);
        UnrealGame(s);
        var userIni = s.Put(Path.Combine(s.K.LocalAppData, "Shooter", "Saved", "Config", "WindowsClient", "GameUserSettings.ini"),
            "[/Script/Engine.GameUserSettings]\r\nbUseVSync=True\r\n");
        var engineIni = s.Put(Path.Combine(s.K.LocalAppData, "Shooter", "Saved", "Config", "WindowsClient", "Engine.ini"), "[Core.System]\r\n");
        File.SetAttributes(userIni, FileAttributes.ReadOnly);

        var result = await new OptimizerScanner().ScanAsync(s.Context(), null, CancellationToken.None);
        var info = Assert.Single(result.AllItems, i => i.Id.Contains(".readonly."));
        Assert.Equal("GameUserSettings.ini is read-only", info.Title);
        Assert.Equal(userIni, info.Detail);
        Assert.Empty(info.Actions);
        Assert.Contains("untick Read-only", info.Details!["How to undo"]);
        Assert.DoesNotContain(result.AllItems, i => i.Detail == engineIni && i.Id.Contains(".readonly."));

        // The VSync edit of that same file isn't offered while it's read-only.
        var vsync = result.AllItems.Single(i => i.Title.Contains("VSync"));
        Assert.Empty(vsync.Actions);
        Assert.Equal(RecommendationBuilder.ReadOnlyReason, vsync.DisabledReason);
        Assert.True((File.GetAttributes(userIni) & FileAttributes.ReadOnly) != 0); // the scan changed nothing
    }

    [Fact]
    public async Task Writable_settings_files_produce_no_read_only_rows()
    {
        using var s = new GamesSandbox("writable", area: Area);
        UnrealGame(s);
        s.Put(Path.Combine(s.K.LocalAppData, "Shooter", "Saved", "Config", "WindowsClient", "GameUserSettings.ini"), "[/Script/Engine.GameUserSettings]\r\nbUseVSync=True\r\n");
        var result = await new OptimizerScanner().ScanAsync(s.Context(), null, CancellationToken.None);
        Assert.DoesNotContain(result.AllItems, i => i.Id.Contains(".readonly."));
        Assert.Empty(GameLeftoverChecks.ReadOnlyFiles(Path.Combine(s.Root, "does-not-exist")));
    }

    [Fact]
    public void The_sandbox_environment_reads_no_compatibility_settings_from_this_pc()
    {
        using var s = new GamesSandbox("sandbox-env", area: Area);
        Assert.Empty(((IGamesEnvironment)new SandboxGamesEnvironment(s.K)).CompatibilitySettings());
    }
}
