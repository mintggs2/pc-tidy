using PcTidy.Catalog;
using PcTidy.Catalog.Games;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Games.Common;
using PcTidy.Features.Games.Library;

namespace PcTidy.Features.Games.Optimize;

/// <summary>
/// Hybrid-GPU laptops (comparison B7): "use the high-performance GPU" for one game, the same per-app choice Windows'
/// Settings → System → Display → Graphics makes. One HKCU value per game exe (value name = full exe path, REG_SZ
/// "GpuPreference=2;"), checked against <see cref="GpuPreferenceAllowances"/>. The platform's registry handler records
/// the exact before-value, so undo puts it back or removes the value when there was none.
/// VERIFY the key and data format against Microsoft's docs / the Settings → Graphics behaviour on a clean VM.
/// Evidence: situational, no FPS data found.
/// </summary>
internal static class GpuPreferencePlanner
{
    /// <summary>At most this many exes per game (the game, plus e.g. its Unreal shipping exe).</summary>
    private const int MaxExes = 3;

    /// <summary>Microsoft's support page for the Graphics settings (verify the exact article).</summary>
    public const string SourceUrl = "https://support.microsoft.com/windows";

    public const string Evidence = "Situational. No FPS data was found: it only helps when Windows runs the game on the slower built-in GPU.";

    public static ScanItem? Row(InstalledGame game, IReadOnlyList<string> exePaths, IReadOnlyDictionary<string, string> current, ISafetyGuard safety)
    {
        var exes = exePaths.Where(p => RegistryCatalog.Find(RegistryHive.CurrentUser, GpuPreferenceAllowances.Key, p) is not null)
            .Take(MaxExes).ToList();
        if (exes.Count == 0) return null;

        var details = new Dictionary<string, string>
        {
            ["Source"] = SourceUrl,
            ["Evidence"] = Evidence,
            ["Verdict"] = "Situational: helps on hybrid-GPU laptops only",
            ["Setting"] = @"HKCU\" + GpuPreferenceAllowances.Key + " (verify)",
            ["Programs"] = string.Join("\n", exes),
        };
        var states = exes.Select(e => current.TryGetValue(e, out var v) ? v : null).ToList();
        var stateText = string.Join(", ", states.Select(s => s is null ? "Let Windows decide" : GpuPreferenceAllowances.ParsePreference(s) is { } p ? GpuPreferenceAllowances.Describe(p) : "Custom"));
        var row = new ScanItem
        {
            Id = $"games.optimize.{game.Key}.gpu-preference",
            Title = "Use the high-performance GPU",
            Detail = exes[0],
            Reason = $"This PC has a built-in and a separate graphics chip. This tells Windows to run {game.Name} on the faster one, like Settings → System → Display → Graphics does. It doesn't touch the game's files. Undo puts the old choice back.",
            Badge = Badge.Check,
            DefaultSelected = false,
            OwnerApp = game.Name,
            CurrentState = stateText,
            Details = details,
        };

        if (states.All(s => s == GpuPreferenceAllowances.HighPerformance))
            return row with { CurrentState = "Already set", Reason = $"Already set: Windows runs {game.Name} on the high-performance GPU." };
        // Other settings stored in the same value (e.g. by the Settings app): only Windows' own page should change those.
        if (states.Any(s => s is not null && GpuPreferenceAllowances.ParsePreference(s) is null))
            return row with { DisabledReason = "Windows keeps other graphics settings for this game in the same place. Change it in Settings → System → Display → Graphics instead, so they stay as they are." };

        var items = exes.Zip(states)
            .Where(x => x.Second != GpuPreferenceAllowances.HighPerformance)
            .Select(x => Plan(game.Name, x.First))
            .ToList();
        var (allowed, refusal) = Plans.Vet(safety, items);
        return row with { Actions = allowed, DisabledReason = refusal };
    }

    public static PlanItem Plan(string gameName, string exePath) => new()
    {
        Kind = ActionKind.RegistrySetValue,
        FeatureId = GamesContext.FeatureId,
        Description = $"Run {gameName} ({Path.GetFileName(exePath)}) on the high-performance GPU",
        Registry = new RegistryValueSpec
        {
            Hive = RegistryHive.CurrentUser,
            KeyPath = GpuPreferenceAllowances.Key,
            ValueName = exePath,
            Kind = RegistryKind.String,
            Data = GpuPreferenceAllowances.HighPerformance,
        },
        NeedsAdmin = false,
        IsSystemChange = false,
        Reversible = true,
    };
}
