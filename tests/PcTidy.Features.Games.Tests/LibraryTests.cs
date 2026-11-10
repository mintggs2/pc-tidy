using PcTidy.Catalog.Games;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Games.Common;
using PcTidy.Features.Games.Environment;
using PcTidy.Features.Games.Library;
using Xunit;

namespace PcTidy.Features.Games.Tests;

[Collection("games-testbed")]
public class LibraryTests
{
    private static readonly long LongAgo = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
    private static readonly long Recently = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    [Fact]
    public async Task Generated_default_testbed_lists_its_Steam_and_Epic_games_across_both_drives()
    {
        using var s = new GamesSandbox("lib-default", generate: true);
        var result = await new LibraryScanner().ScanAsync(s.Context(), null, CancellationToken.None);

        var all = result.Groups.Single(g => g.Id == "games-library-all").Items;
        Assert.Contains(all, i => i.Title == "Some Game" && i.OwnerApp == "Steam");
        Assert.Contains(all, i => i.Title == "Another Game" && i.Detail!.Contains(Path.Combine(s.Root, "D", "SteamLibrary")));
        Assert.Contains(all, i => i.Title == "Fake Game" && i.OwnerApp == "Epic Games Launcher");
        Assert.All(all, i => Assert.Empty(i.Actions)); // information only

        // The testbed's manifests say "installed 2025-01-01, never played": suggested via Steam's own uninstall.
        var unplayed = result.Groups.Single(g => g.Id == "games-library-unplayed");
        Assert.Equal(Badge.Check, unplayed.Badge);
        var some = unplayed.Items.Single(i => i.Title == "Some Game");
        Assert.False(some.DefaultSelected);
        Assert.Null(some.DisabledReason);
        var open = Assert.Single(some.Actions);
        Assert.Equal(ActionKind.OpenExternal, open.Kind);
        Assert.Equal("steam://uninstall/1234560", open.Args!["target"]);
        Assert.Equal(0, some.SizeBytes); // the launcher frees the space, not PC Tidy
        Assert.All(result.AllItems, i => Assert.False(string.IsNullOrWhiteSpace(i.Reason)));
        Assert.All(result.Groups, g => Assert.False(string.IsNullOrWhiteSpace(g.Description)));
    }

    [Fact]
    public async Task Minimal_pc_without_launchers_gets_a_friendly_empty_state()
    {
        using var s = new GamesSandbox("lib-minimal", SandboxProfile.Minimal, generate: true);
        Assert.False(GamesAvailability.HasLaunchers(s.K));

        var library = await new LibraryScanner().ScanAsync(s.Context(), null, CancellationToken.None);
        Assert.Empty(library.Groups);
        Assert.Contains(library.Notes, n => n.Contains("No game launchers", StringComparison.Ordinal));

        var optimize = await new Optimize.OptimizerScanner().ScanAsync(s.Context(), null, CancellationToken.None);
        Assert.Empty(optimize.Groups);
        Assert.NotEmpty(optimize.Notes);
    }

    [Fact]
    public async Task Sorting_by_last_played_puts_never_and_long_ago_first_and_recent_games_are_not_suggested()
    {
        using var s = new GamesSandbox("lib-sort");
        s.SteamGame(s.SteamRoot, "10", "Recent Game", "Recent", Recently, LongAgo, "Recent.exe");
        s.SteamGame(s.SteamRoot, "20", "Old Game", "Old", LongAgo, LongAgo, "Old.exe");
        s.SteamGame(s.SteamRoot, "30", "Fresh Install", "Fresh", null, Recently, "Fresh.exe");

        var scanner = new LibraryScanner();
        var result = await scanner.ScanAsync(s.Context(), null, CancellationToken.None);
        var suggested = result.Groups.Single(g => g.Id == "games-library-unplayed").Items.Select(i => i.Title).ToList();
        Assert.Equal(new[] { "Old Game" }, suggested); // recently played and freshly installed games are left alone

        var byPlayed = scanner.Resort(LibrarySort.LastPlayed)!.Groups.Single(g => g.Id == "games-library-all").Items.Select(i => i.Title).ToList();
        Assert.Equal(new[] { "Fresh Install", "Old Game", "Recent Game" }, byPlayed);
    }

    [Fact]
    public async Task Other_launchers_and_xbox_games_are_detected_with_their_own_steps()
    {
        using var s = new GamesSandbox("lib-others", SandboxProfile.Minimal);
        var gog = Path.Combine(s.K.SystemDrive, "GOG Games", "Witcher");
        s.Put(Path.Combine(gog, "witcher.exe"), "x");
        var ubi = Path.Combine(s.K.SystemDrive, "Ubisoft", "SomeUbiGame");
        s.Put(Path.Combine(ubi, "game.exe"), "xyz");
        var xbox = Path.Combine(s.K.SystemDrive, "XboxGames", "Halo", "Content");
        s.Put(Path.Combine(xbox, "MicrosoftGame.config"), "<?xml version=\"1.0\"?><Game configVersion=\"1\"><Identity Name=\"Studio.Halo\" Publisher=\"CN=x\" Version=\"1.0.0.0\"/><ShellVisuals DefaultDisplayName=\"Halo Sim\"/></Game>");
        var env = new FakeEnvironment();
        env.Registry.Add(new RegistryGame(Launcher.Gog, "The Witcher", gog, "1207658924"));
        env.Registry.Add(new RegistryGame(Launcher.Ubisoft, "SomeUbiGame", ubi, "55"));
        s.Environment = env;

        Assert.True(GamesAvailability.HasLaunchers(s.K)); // an XboxGames folder counts
        var result = await new LibraryScanner().ScanAsync(s.Context(), null, CancellationToken.None);
        var all = result.Groups.Single(g => g.Id == "games-library-all").Items;
        Assert.Contains(all, i => i.Title == "The Witcher" && i.OwnerApp == "GOG GALAXY");
        Assert.Contains(all, i => i.Title == "SomeUbiGame" && i.SizeBytes == 3); // measured from the folder
        Assert.Contains(all, i => i.Title == "Halo Sim" && i.OwnerApp == "Xbox app");
        Assert.All(all, i => Assert.Contains("Last played", i.Details!.Keys));
    }

    [Fact]
    public async Task Launcher_links_pass_the_real_safety_guard()
    {
        using var s = new GamesSandbox("lib-guard");
        foreach (var info in Launchers.All.Values)
            foreach (var link in new[] { info.UninstallLink, info.MoveLink }.OfType<string>())
            {
                var item = Plans.OpenExternal(Launchers.Link(link, "123"), "test");
                Assert.True(s.Host.Safety.Check(item).Allowed, link);
            }
        await Task.CompletedTask;
    }
}
