using System.Diagnostics;
using PcTidy.Features.Speed.Settings;
using PcTidy.Features.Speed.Startup;

namespace PcTidy.Features.Speed;

/// <summary>
/// The Speed section's part of "Optimize &amp; Organize everything": the startup list (only measured high-impact,
/// non-Microsoft apps are pre-ticked, and only by the Recommended and Gamer presets) and Game Mode if it's off
/// (Safe, pre-ticked). Running apps, drives, the right-click menu and locks are never part of it. Read-only.
/// </summary>
public sealed class SpeedScanner : IFeatureScanner
{
    public const string GameModeGroup = "speed-settings-game-mode";

    private readonly Func<ScanContext, SpeedEnvironment> _environment;
    private readonly StartupScanner _startup;

    public SpeedScanner(Func<ScanContext, SpeedEnvironment>? environment = null)
    {
        _environment = environment ?? SpeedEnvironment.For;
        _startup = new StartupScanner(_environment);
    }

    public string FeatureId => SpeedModule.ModuleId;
    public string Title => "Speed";
    public Release Release => Release.V2;
    public bool IncludedInOptimizeAll => true;

    public Task<ScanResult> ScanAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
        => Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            var groups = _startup.BuildGroups(ctx, progress, ct, out var notes)
                .Where(g => g.Id != StartupScanner.AdvancedGroup) // read-only rows add nothing to a combined run
                .ToList();
            var gameMode = SettingsScanner.GameModeItem(_environment(ctx));
            if (gameMode.Actions.Count > 0)
                groups.Add(new ScanGroup
                {
                    Id = GameModeGroup, Title = "Game Mode", Badge = Badge.Safe, Items = new[] { gameMode },
                    Description = "Gives games priority and holds back Windows Update restarts while you play.",
                });
            return new ScanResult
            {
                FeatureId = FeatureId, Title = Title, Depth = ctx.Depth, Duration = sw.Elapsed, Groups = groups, Notes = notes,
            };
        }, ct);
}
