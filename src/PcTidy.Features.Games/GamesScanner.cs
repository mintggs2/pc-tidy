using System.Diagnostics;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Games.Cleanup;
using PcTidy.Features.Games.Clips;
using PcTidy.Features.Games.Common;
using PcTidy.Features.Games.Library;

namespace PcTidy.Features.Games;

/// <summary>
/// The Games section's contribution to "Optimize &amp; Organize everything" (Gamer preset only): unplayed-game
/// suggestions (launcher steps, never ticked), clips (only exact duplicates ticked: an identical copy always stays) and
/// per-game logs/caches (Check). Config edits from the optimizer are never part of it. Other presets get nothing.
/// </summary>
public sealed class GamesScanner : IFeatureScanner
{
    public const string GamerPreset = "Gamer";

    /// <summary>Information-only groups left out of the combined preview (they'd inflate its totals).</summary>
    private static readonly HashSet<string> InfoGroups = new(StringComparer.OrdinalIgnoreCase) { "games-library-all", "clip-sources" };

    public string FeatureId => GamesContext.FeatureId;
    public string Title => "Games";
    public Release Release => Release.V2;
    /// <summary>True, but <see cref="ScanAsync"/> returns nothing unless the preset is Gamer (or the section's own page runs it).</summary>
    public bool IncludedInOptimizeAll => true;

    public Task<ScanResult> ScanAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
        => Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            if (ctx.Preset is not null && !ctx.Preset.Equals(GamerPreset, StringComparison.OrdinalIgnoreCase))
                return new ScanResult
                {
                    FeatureId = FeatureId, Title = Title, Groups = Array.Empty<ScanGroup>(), Depth = ctx.Depth,
                    Notes = new[] { "Games is part of the Gamer preset only." },
                };

            var g = GamesContext.From(ctx);
            progress?.Report(new ScanProgress("Reading your game launchers…", 0.05));
            var library = LibraryScanner.Build(g, GameLibraryDetector.Detect(g, ct), LibrarySort.Size);
            progress?.Report(new ScanProgress("Looking at your clips…", 0.35));
            var clips = ClipsScanner.Scan(g, null, ct);
            progress?.Report(new ScanProgress("Looking for game logs and caches…", 0.75));
            var cleanup = GameCleanupScanner.Scan(g, null, ct);
            progress?.Report(new ScanProgress("Done", 1));

            var parts = new[] { library, clips, cleanup };
            return new ScanResult
            {
                FeatureId = FeatureId,
                Title = Title,
                Depth = ctx.Depth,
                Duration = sw.Elapsed,
                Groups = parts.SelectMany(p => p.Groups).Where(x => !InfoGroups.Contains(x.Id) && x.Items.Count > 0).ToList(),
                Notes = parts.SelectMany(p => p.Notes).Distinct().ToList(),
            };
        }, ct);
}
