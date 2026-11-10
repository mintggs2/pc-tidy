using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Games.Clips;
using PcTidy.TestbedGen;
using PcTidy.UI.Flow;
using Xunit;
using Xunit.Abstractions;

namespace PcTidy.Features.Games.Tests;

[CollectionDefinition("games-testbed", DisableParallelization = true)]
public class GamesTestbedCollection { }

/// <summary>Real composition (sandbox): scan → plan → real SafetyGuard → Executor → Undo, proved with hash manifests.</summary>
[Collection("games-testbed")]
public class EndToEndTests(ITestOutputHelper log)
{
    private static readonly ExecuteOptions Real = new() { Mode = ExecutionMode.Real, ContinueWithoutRestorePoint = _ => Task.FromResult(true) };

    [Fact]
    public async Task Clip_duplicate_quarantine_then_undo_leaves_the_testbed_identical()
    {
        using var s = new GamesSandbox("e2e-clips", generate: true);
        // The testbed's Game Bar folder, plus an exact copy of one of its clips (as when a clip is saved twice).
        var captures = Path.Combine(s.K.Videos, "Captures");
        var source = Directory.GetFiles(captures, "*.mp4").OrderBy(f => f, StringComparer.Ordinal).First();
        var copy = Path.Combine(captures, Path.GetFileNameWithoutExtension(source) + " (2).mp4");
        s.Put(copy, File.ReadAllBytes(source), GamesSandbox.Now.AddDays(-1));
        // The user allowed PC Tidy to tidy Videos (Settings → folder permissions).
        s.Host.Settings.AllowedAskFirstFolders.Add(s.K.Videos);
        var before = TestbedBuilder.Manifest(s.Root);

        var scan = await new ClipsScanner().ScanAsync(s.Context(), null, CancellationToken.None);
        foreach (var g in scan.Groups) log.WriteLine($"{g.Id}: {g.Items.Count} items, {g.Items.Count(i => i.DefaultSelected)} ticked");
        var ticked = scan.AllItems.Where(OptimizePresets.IsTickableByDefault).ToList();
        var dup = Assert.Single(ticked);
        Assert.Equal(copy, dup.Detail);

        var plan = PlanBuilder.FromSelection("games", "Clips & screenshots", ticked);
        var run = await s.Host.Executor.ExecuteAsync(plan, Real, null, CancellationToken.None);
        foreach (var step in run.Steps) log.WriteLine($"{step.Status}: {step.Item.Description} {step.Message}");
        Assert.Equal(1, run.DoneCount);
        Assert.False(File.Exists(copy));
        Assert.True(File.Exists(source)); // the original always stays

        var undo = await s.Host.Undo.UndoRunAsync(run.RunId, null, CancellationToken.None);
        Assert.Equal(0, undo.Failed);
        var diff = TestbedBuilder.Compare(before, TestbedBuilder.Manifest(s.Root));
        // PC Tidy's own data (journal, quarantine) is the only thing allowed to differ.
        var outside = diff.Differences.Where(d => !d.Path.StartsWith("Data", StringComparison.OrdinalIgnoreCase)
                                                  && !d.Path.Contains("PC Tidy Quarantine", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(outside.Count == 0, "Undo did not restore the tree exactly:\n" + string.Join("\n", outside.Take(20).Select(d => $"{d.Change}: {d.Path}")));
    }

    [Fact]
    public async Task Gamer_preset_ticks_only_exact_clip_duplicates_and_other_presets_get_nothing()
    {
        using var s = new GamesSandbox("e2e-gamer", generate: true);
        var captures = Path.Combine(s.K.Videos, "Captures");
        var source = Directory.GetFiles(captures, "*.mp4").OrderBy(f => f, StringComparer.Ordinal).First();
        s.Put(Path.Combine(captures, "copy.mp4"), File.ReadAllBytes(source), GamesSandbox.Now.AddDays(-1));
        s.Put(Path.Combine(s.K.LocalAppData, "FortniteGame", "Saved", "Logs", "FortniteGame.log"), "log", GamesSandbox.Now.AddDays(-100));
        s.Host.Settings.AllowedAskFirstFolders.Add(s.K.Videos);

        var scanner = new GamesScanner();
        Assert.True(scanner.IncludedInOptimizeAll);
        var gamer = await scanner.ScanAsync(s.Context(preset: OptimizePresets.Gamer), null, CancellationToken.None);
        var ticked = gamer.Groups.SelectMany(g => g.Items.Where(i => OptimizePresets.ShouldTick(OptimizePresets.Gamer, gamer.FeatureId, g, i)).Select(i => (g, i))).ToList();
        var (group, item) = Assert.Single(ticked);
        Assert.Equal("clip-duplicates", group.Id);
        Assert.EndsWith("copy.mp4", item.Detail);
        Assert.Contains(gamer.Groups, g => g.Id == "games-library-unplayed");      // shown, never ticked
        Assert.Contains(gamer.Groups, g => g.Id == "games-cleanup-logs");
        Assert.DoesNotContain(gamer.Groups, g => g.Id.StartsWith("games-optimize")); // config edits never in Optimize-all
        Assert.DoesNotContain(gamer.Groups, g => g.Id is "games-library-all" or "clip-sources");

        foreach (var preset in new[] { OptimizePresets.Safe, OptimizePresets.Recommended, OptimizePresets.Privacy })
            Assert.Empty((await scanner.ScanAsync(s.Context(preset: preset), null, CancellationToken.None)).Groups);
    }

    [Fact]
    public void Module_is_the_v2_games_section()
    {
        var m = new GamesModule();
        Assert.Equal("games", m.Id);
        Assert.Equal("Games", m.Title);
        Assert.Equal(NavGroup.SpeedUp, m.Group);
        Assert.Equal("Games24", m.Icon);
        Assert.Equal(Release.V2, m.Release);
        Assert.IsType<GamesScanner>(Assert.Single(m.Scanners));
        Assert.True(Enum.TryParse<Wpf.Ui.Controls.SymbolRegular>(m.Icon, out _));
    }
}
