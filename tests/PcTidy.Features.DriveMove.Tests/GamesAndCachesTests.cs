using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.DriveMove.Common;
using PcTidy.Features.DriveMove.PcFacts;
using Xunit;

namespace PcTidy.Features.DriveMove.Tests;

/// <summary>Plan shapes for games (launcher moves only) and app caches (the tool's own setting only).</summary>
[Collection("drivemove-testbed")]
public class GamesAndCachesTests
{
    private static readonly long Old = new DateTimeOffset(DriveMoveSandbox.Now.AddDays(-300)).ToUnixTimeSeconds();
    private static readonly long Recent = new DateTimeOffset(DriveMoveSandbox.Now.AddDays(-2)).ToUnixTimeSeconds();

    [Fact]
    public async Task Games_are_listed_per_drive_and_only_ever_moved_by_their_launcher()
    {
        using var s = new DriveMoveSandbox("games");
        s.SteamGame(s.K.SteamLibraries[0], "100", "Big Old Game", "BigOld", 70L << 30, Old, Old);
        s.SteamGame(s.K.SteamLibraries[0], "200", "Daily Game", "Daily", 20L << 30, Recent, Old);
        s.SteamGame(s.K.SteamLibraries[1], "300", "On D", "OnD", 5L << 30, null, Old);
        var result = await new GameDriveScanner().ScanAsync(s.Context(), null, CancellationToken.None);

        var launchers = result.Groups.Single(g => g.Id == DriveMoveIds.GroupGameLaunchers);
        var steam = launchers.Items.Single(i => i.Title == "Open Steam to move games");
        var open = Assert.Single(steam.Actions);
        Assert.Equal(ActionKind.OpenExternal, open.Kind);
        Assert.Equal("steam://open/settings", open.Args![DriveMoveArgs.Target]);
        Assert.Contains("Storage", steam.Reason);
        Assert.True(s.Host.Safety.Check(open).Allowed);

        var onC = result.Groups.Single(g => g.Id == DriveMoveIds.GroupGamesOnDrive + "c");
        Assert.Equal(new[] { "Big Old Game", "Daily Game" }, onC.Items.Select(i => i.Title));
        Assert.Contains("70 GB", onC.Items[0].CurrentState);
        Assert.Contains("10 months ago", onC.Items[0].CurrentState);
        Assert.StartsWith("Not played in 6 months", onC.Items[0].Reason);
        Assert.Single(result.Groups.Single(g => g.Id == DriveMoveIds.GroupGamesOnDrive + "d").Items);

        // Never a raw copy: no file action anywhere, game rows are information only.
        Assert.DoesNotContain(result.AllItems.SelectMany(i => i.Actions), a => a.Kind != ActionKind.OpenExternal);
        Assert.All(onC.Items, i => Assert.Empty(i.Actions));
    }

    [Fact]
    public async Task No_launchers_means_a_friendly_note()
    {
        using var s = new DriveMoveSandbox("games-none", SandboxProfile.Default with { Steam = false, Epic = false });
        var result = await new GameDriveScanner().ScanAsync(s.Context(), null, CancellationToken.None);
        Assert.Empty(result.Groups);
        Assert.Contains(result.Notes, n => n.Contains("No Steam"));
    }

    [Fact]
    public async Task Pip_cache_gets_a_pip_ini_edit_and_an_unticked_old_cache_quarantine()
    {
        using var s = new DriveMoveSandbox("cache-pip");
        var cache = Path.Combine(s.K.LocalAppData, "pip", "Cache");
        s.Put(Path.Combine(cache, "http", "a.bin"), DriveMoveSandbox.Bytes(4000, 1));
        var result = await new AppCacheScanner().ScanAsync(s.Context(), null, CancellationToken.None);

        var group = result.Groups.Single(g => g.Id == DriveMoveIds.GroupCachesConfig);
        var edit = Assert.Single(group.Items[0].Actions);
        var target = Path.Combine(s.D, DriveMoveIds.MovedFolderName, "pip cache");
        Assert.Equal(ActionKind.EditTextFile, edit.Kind);
        Assert.Equal(Path.Combine(s.K.RoamingAppData, "pip", "pip.ini"), edit.SourcePath);
        Assert.Equal("[global]\r\ncache-dir = " + target + "\r\n", edit.Args![DriveMoveArgs.Content]);
        Assert.Equal("true", edit.Args[DriveMoveArgs.CreateIfMissing]);
        Assert.True(s.Host.Safety.Check(edit).Allowed);
        Assert.False(group.Items[0].DefaultSelected);

        var clear = group.Items[1];
        var q = Assert.Single(clear.Actions);
        Assert.Equal(ActionKind.QuarantinePath, q.Kind);
        Assert.Equal(cache, q.SourcePath);
        Assert.Equal(4000, q.ExpectedSize);
        Assert.False(clear.DefaultSelected);
        Assert.Equal(Badge.Check, clear.Badge);
    }

    [Fact]
    public async Task NuGet_edit_keeps_the_rest_of_an_existing_config()
    {
        using var s = new DriveMoveSandbox("cache-nuget");
        s.Put(Path.Combine(s.K.UserProfile, ".nuget", "packages", "newtonsoft.json", "13.0.1", "lib.dll"), DriveMoveSandbox.Bytes(5000, 2));
        var config = s.Put(Path.Combine(s.K.RoamingAppData, "NuGet", "NuGet.Config"),
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<configuration>\r\n  <packageSources>\r\n    <add key=\"nuget.org\" value=\"https://api.nuget.org/v3/index.json\" />\r\n  </packageSources>\r\n</configuration>\r\n");
        var result = await new AppCacheScanner().ScanAsync(s.Context(), null, CancellationToken.None);
        var edit = result.AllItems.SelectMany(i => i.Actions).Single(a => a.Kind == ActionKind.EditTextFile);
        Assert.Equal(config, edit.SourcePath);
        var content = edit.Args![DriveMoveArgs.Content];
        Assert.Contains("https://api.nuget.org/v3/index.json", content);
        Assert.Contains($"<add key=\"globalPackagesFolder\" value=\"{Path.Combine(s.D, DriveMoveIds.MovedFolderName, "NuGet packages")}\" />", content);
        Assert.False(edit.Args.ContainsKey(DriveMoveArgs.CreateIfMissing));
        Assert.Equal(File.GetLastWriteTimeUtc(config), edit.ExpectedLastWriteUtc);
        Assert.Contains("devenv", edit.RequiresClosedApps!);
    }

    [Fact]
    public async Task Npm_is_planned_through_npmrc_or_shown_as_the_line_to_add()
    {
        using var s = new DriveMoveSandbox("cache-npm");
        s.Put(Path.Combine(s.K.LocalAppData, "npm-cache", "_cacache", "x"), DriveMoveSandbox.Bytes(3000, 3));
        var result = await new AppCacheScanner().ScanAsync(s.Context(), null, CancellationToken.None);
        var line = "cache=" + Path.Combine(s.D, DriveMoveIds.MovedFolderName, "npm cache");
        var row = result.AllItems.First(i => i.Detail?.Contains("npm-cache") == true);
        // Today's guard edits only well-known config extensions, so .npmrc becomes plain steps; once allowed, it's an edit.
        if (row.Actions.Count == 1) Assert.Contains(line, row.Actions[0].Args![DriveMoveArgs.Content]);
        else Assert.Contains(line, row.Reason);
    }

    [Fact]
    public async Task Environment_variable_tools_and_app_settings_are_steps_only()
    {
        using var s = new DriveMoveSandbox("cache-env");
        s.Put(Path.Combine(s.K.UserProfile, ".ollama", "models", "blobs", "sha256-1"), DriveMoveSandbox.Bytes(6000, 4));
        s.Put(Path.Combine(s.K.LocalAppData, "Docker", "wsl", "disk", "docker_data.vhdx"), DriveMoveSandbox.Bytes(7000, 5));
        // Hugging Face already moved by the user's HF_HOME: found there, still offered from where it is.
        var hf = Path.Combine(s.C, "AI", "hf");
        s.Put(Path.Combine(hf, "hub", "model.bin"), DriveMoveSandbox.Bytes(2000, 6));
        s.Pc.Variables["HF_HOME"] = hf;
        var result = await new AppCacheScanner().ScanAsync(s.Context(), null, CancellationToken.None);

        var steps = result.Groups.Single(g => g.Id == DriveMoveIds.GroupCachesSteps);
        var ollama = steps.Items.Single(i => i.Title == "Ollama models");
        Assert.Empty(ollama.Actions);
        Assert.Contains("OLLAMA_MODELS = " + Path.Combine(s.D, DriveMoveIds.MovedFolderName, "Ollama models"), ollama.Reason);
        Assert.Contains("Docker Desktop", steps.Items.Single(i => i.Title == "Docker Desktop disk").Reason);
        var hfRow = steps.Items.Single(i => i.Title == "Hugging Face cache");
        Assert.Equal(PathUtil.Normalize(hf), hfRow.Detail);
        Assert.Contains("set by HF_HOME", hfRow.CurrentState);
        Assert.All(steps.Items, i => Assert.Empty(i.Actions));
    }

    [Fact]
    public async Task WSL_offers_stopping_WSL_and_shows_its_own_move_command()
    {
        using var s = new DriveMoveSandbox("cache-wsl");
        var basePath = Path.Combine(s.K.LocalAppData, "wsl", "{0a1b}");
        s.Put(Path.Combine(basePath, "ext4.vhdx"), DriveMoveSandbox.Bytes(9000, 7));
        s.Pc.Wsl.Add(new WslDistro("Ubuntu", basePath));
        var result = await new AppCacheScanner().ScanAsync(s.Context(), null, CancellationToken.None);
        var row = result.AllItems.Single(i => i.Title == "WSL: move Ubuntu to D:");
        var stop = Assert.Single(row.Actions);
        Assert.Equal(ActionKind.RunSystemTool, stop.Kind);
        Assert.Equal(ToolIds.WslShutdown, stop.ToolId);
        Assert.True(s.Host.Safety.Check(stop).Allowed);
        Assert.Contains($"wsl --manage Ubuntu --move \"{Path.Combine(s.D, DriveMoveIds.MovedFolderName, "WSL", "Ubuntu")}\"", row.Details!["Command"]);
        Assert.False(row.DefaultSelected);
    }
}
