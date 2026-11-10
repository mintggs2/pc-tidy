using System.Diagnostics;
using PcTidy.Catalog.Games;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Games.Common;
using PcTidy.Features.Games.Library;

namespace PcTidy.Features.Games.Optimize;

/// <summary>
/// Optimize tab: per detected game, engine-correct launch options, documented config keys, fullscreen mode and known
/// fixes, each with its source. Changes are only ever an edit of Steam's localconfig.vdf (Launch Options) or of the
/// game's own user config file, planned as <see cref="ActionKind.EditTextFile"/> (backed up and undone by the engine,
/// only while the game and Steam are closed). Games with a kernel or unknown anti-cheat get information only.
/// Never injects, never patches game files or binaries.
/// </summary>
public sealed class OptimizerScanner : IFeatureScanner
{
    public string FeatureId => GamesContext.FeatureId;
    public string Title => "Game optimizer";
    public Release Release => Release.V2;
    public bool IncludedInOptimizeAll => false;

    public Task<ScanResult> ScanAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
        => Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            var g = GamesContext.From(ctx);
            progress?.Report(new ScanProgress("Finding your games…", 0.05));
            var games = GameLibraryDetector.Detect(g, ct);
            return Scan(g, games, progress, ct) with { Duration = sw.Elapsed, Depth = ctx.Depth };
        }, ct);

    internal static ScanResult Scan(GamesContext g, IReadOnlyList<InstalledGame> games, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var steamConfig = SteamUserConfig.Load(g.Locations.SteamRoot);
        var compat = g.Environment.CompatibilitySettings();
        var groups = new List<ScanGroup>();
        for (int i = 0; i < games.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var game = games[i];
            progress?.Report(new ScanProgress($"Looking at {game.Name}…", 0.1 + 0.85 * i / Math.Max(1, games.Count), i));
            var layout = GameInspector.Inspect(g.Scan.Walker, game.InstallDir, ct);
            var profile = GameProfile.For(game, layout);
            var items = new RecommendationBuilder(g, game, profile, steamConfig).Build()
                .Concat(GameLeftoverChecks.For(game, layout, profile.Engine, g.Locations, compat)).ToList();
            if (items.Count == 0) continue;
            groups.Add(new ScanGroup
            {
                Id = "games-optimize-" + game.Key,
                Title = game.Name,
                Description = profile.Summary(game),
                Badge = Badge.Check,
                Items = items,
            });
        }

        var notes = new List<string>();
        if (games.Count == 0) notes.Add("No installed games were found, so there's nothing to optimize.");
        else if (groups.Count == 0) notes.Add("PC Tidy has no proven tweaks for the games on this PC. That's fine: their own settings menus are the best place.");
        else notes.Add("Changes are only made while the game (and Steam) are closed. Each edit is backed up first and can be undone from History.");
        if (groups.Any(x => x.Items.Any(i => i.DisabledReason == RecommendationBuilder.SteamOpenReason)))
            notes.Add("Steam is open, so launch options can't be changed yet. Exit Steam (Steam menu → Exit), then scan again.");
        progress?.Report(new ScanProgress("Done", 1, games.Count));
        return new ScanResult { FeatureId = GamesContext.FeatureId, Title = "Game optimizer", Groups = groups, Notes = notes };
    }
}

/// <summary>Engine, anti-cheat and recipe for one game: the catalog wins over the folder layout, and the strictest anti-cheat wins.</summary>
internal sealed record GameProfile(GameEngine Engine, string? EngineWhy, AntiCheat AntiCheat, GameRecipe? Recipe, GameLayout Layout)
{
    public static GameProfile For(InstalledGame game, GameLayout layout)
    {
        var recipe = game.Launcher switch
        {
            Launcher.Steam => GameRecipes.ForSteam(game.Id),
            Launcher.Epic => GameRecipes.ForEpic(game.Id),
            _ => null,
        } ?? GameRecipes.ForFolder(game.InstallDir);
        var engine = recipe?.Engine ?? layout.Engine;
        var why = recipe?.Engine is not null ? "PC Tidy's game list" : layout.EngineWhy;
        // A recipe can't make a game less strict than what's actually installed in its folder.
        var antiCheat = recipe?.AntiCheat is { } r && r.IsStrict() ? r
            : layout.AntiCheat.IsStrict() ? layout.AntiCheat
            : recipe?.AntiCheat ?? layout.AntiCheat;
        return new GameProfile(engine, why, antiCheat, recipe, layout);
    }

    public bool Strict => AntiCheat.IsStrict();

    public IReadOnlyList<string> CloseFirst(InstalledGame game)
        => Layout.ProcessNames.Concat(game.ProcessNames).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public string EngineName => Engine switch
    {
        GameEngine.Unity => "Unity",
        GameEngine.Unreal => "Unreal Engine",
        GameEngine.Source => "Source",
        GameEngine.Source2 => "Source 2",
        GameEngine.IdTech => "id Tech",
        GameEngine.Godot => "Godot",
        _ => "Engine not recognised",
    };

    public string Summary(InstalledGame game)
    {
        var ac = AntiCheat == AntiCheat.None ? "no anti-cheat found" : AntiCheat.Label() + (Strict ? " (information only)" : "");
        var wiki = Recipe?.WikiUrl is { } w ? " · More on PCGamingWiki: " + w : "";
        return $"{EngineName} · {ac} · {Launchers.All[game.Launcher].Name}{wiki}";
    }
}

/// <summary>Turns a game's profile into rows: apply-able edits (Check, never pre-ticked) or information.</summary>
internal sealed class RecommendationBuilder
{
    private readonly GamesContext _g;
    private readonly InstalledGame _game;
    private readonly GameProfile _p;
    private readonly SteamUserConfig? _steam;
    private readonly List<ScanItem> _items = new();

    public RecommendationBuilder(GamesContext g, InstalledGame game, GameProfile profile, SteamUserConfig? steam)
        => (_g, _game, _p, _steam) = (g, game, profile, steam);

    private bool OnSteam => _game.Launcher == Launcher.Steam && _g.Locations.SteamRoot is not null;

    public IReadOnlyList<ScanItem> Build()
    {
        var recs = EngineRecommendations.For(_p.Engine)
            .Concat(_p.Recipe?.Recommendations ?? Array.Empty<GameRecommendation>())
            .Where(r => !(_game.Launcher == Launcher.Steam && r.NotForAppIds.Contains(_game.Id)))
            .GroupBy(r => r.Id).Select(x => x.First());
        foreach (var rec in recs)
        {
            if (rec.AddLaunchOption is { } option) LaunchOption(rec, option);
            else if (rec.Ini is { } ini) Ini(rec, ini);
            else Info(rec, rec.Reason);
        }
        AuditLaunchOptions();
        return _items;
    }

    // ---------------- launch options ----------------

    private void LaunchOption(GameRecommendation rec, string option)
    {
        var current = OnSteam ? _steam?.LaunchOptions(_game.Id) : null;
        if (current is not null && LaunchOptionLine.Contains(current, option)) { Info(rec, $"Already set: {rec.Reason}", state: "Already set"); return; }
        if (!OnSteam) { Info(rec, $"{rec.Reason} {HowToSetLaunchOptions()}"); return; }
        if (Blocked(rec) is { } why) { Disabled(rec, rec.Reason, why); return; }
        if (_steam is null)
        {
            Info(rec, $"{rec.Reason} Steam hasn't saved settings for your account on this PC yet, so set it in Steam → right-click the game → Properties → Launch Options.");
            return;
        }
        var plan = ConfigEditPlanner.AddLaunchOption(_steam, _g.Locations.SteamRoot!, _game.Id, option, _game.Name, _p.CloseFirst(_game));
        if (plan is null) { Info(rec, $"{rec.Reason} Set it in Steam → right-click the game → Properties → Launch Options."); return; }
        Apply(rec, rec.Reason, plan, current is { Length: > 0 } ? $"Now: {current}" : "Not set");
    }

    /// <summary>Placebo or harmful options already in the game's Steam launch options: offer to take them out.</summary>
    private void AuditLaunchOptions()
    {
        if (!OnSteam || _steam?.LaunchOptions(_game.Id) is not { Length: > 0 } current) return;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var word in LaunchOptionLine.Words(current))
        {
            var fact = Catalog.Games.LaunchOptionFacts.Find(word.Text, _p.Engine);
            if (fact is null || fact.Verdict is not (TweakVerdict.Placebo or TweakVerdict.Harmful) || !seen.Add(fact.Token)) continue;
            var rec = new GameRecommendation
            {
                Id = "audit." + fact.Token, Kind = RecommendationKind.LaunchOption,
                Title = $"Remove \"{fact.Token}\" from the launch options",
                Reason = (fact.Verdict == TweakVerdict.Harmful ? "This option slows the game down. " : "This option does nothing useful. ") + fact.Explanation,
                Verdict = fact.Verdict, Evidence = fact.Evidence, SourceUrl = fact.SourceUrl,
            };
            // Taking a placebo option out is always safe to offer, but a strict anti-cheat still means "do it yourself".
            if (_p.Strict) { Disabled(rec, rec.Reason, StrictReason("remove it in Steam → Properties → Launch Options")); continue; }
            var plan = ConfigEditPlanner.RemoveLaunchOption(_steam, _g.Locations.SteamRoot!, _game.Id, fact.Token, fact.TakesValue, _game.Name, _p.CloseFirst(_game));
            if (plan is not null) Apply(rec, rec.Reason, plan, "Now: " + current);
        }
    }

    // ---------------- config files ----------------

    private void Ini(GameRecommendation rec, IniSetting ini)
    {
        var file = FindUserConfig(ini.FileName);
        if (file is null) return; // the game hasn't written its settings file yet: nothing to show
        string text;
        DateTime written;
        try { text = File.ReadAllText(file); written = File.GetLastWriteTimeUtc(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        var current = IniText.Get(text, ini.Section, ini.Key);
        if (string.Equals(current, ini.Value, StringComparison.OrdinalIgnoreCase)) { Info(rec, $"Already set: {rec.Reason}", state: "Already set"); return; }
        if (Blocked(rec) is { } why) { Disabled(rec, rec.Reason, why); return; }
        if (GameLeftoverChecks.IsReadOnly(file)) { Disabled(rec, rec.Reason, ReadOnlyReason); return; }
        var plan = ConfigEditPlanner.SetIni(file, text, written, ini.Section, ini.Key, ini.Value, _game.InstallDir,
            $"Set {ini.Key}={ini.Value} in {_game.Name}'s {ini.FileName}", _p.CloseFirst(_game));
        if (plan is null) { Info(rec, rec.Reason); return; }
        Apply(rec, rec.Reason, plan, current is null ? "Not set" : $"Now: {ini.Key}={current}");
    }

    /// <summary>Unreal keeps user settings in %LOCALAPPDATA%\&lt;Project&gt;\Saved\Config\Windows[Client|NoEditor]\.</summary>
    private string? FindUserConfig(string fileName)
    {
        if (_p.Engine != GameEngine.Unreal) return null;
        foreach (var project in _p.Layout.UnrealProjects)
            foreach (var platform in new[] { "WindowsClient", "Windows", "WindowsNoEditor" })
            {
                var path = Path.Combine(_g.Locations.LocalAppData, project, "Saved", "Config", platform, fileName);
                if (FolderSize.FileExists(path)) return path;
            }
        return null;
    }

    // ---------------- rows ----------------

    /// <summary>Why an otherwise apply-able recommendation is shown only, or null.</summary>
    private string? Blocked(GameRecommendation rec)
    {
        if (_p.Strict) return StrictReason(rec.AddLaunchOption is not null ? "set it yourself in Steam → Properties → Launch Options" : "change it in the game's own settings menu");
        if (rec.Evidence != Evidence.Verified) return "This one isn't confirmed by the game's or engine's documentation, so PC Tidy only shows it.";
        return null;
    }

    private string StrictReason(string how)
        => $"{_game.Name} uses {_p.AntiCheat.Label()}. PC Tidy never changes games protected by an anti-cheat like this; {how} if you want it.";

    /// <summary>
    /// Steam writes localconfig.vdf when it exits, so a launch-option edit planned while it runs would be skipped as
    /// "changed since the scan" once the user closes Steam to apply it. Such rows wait for a scan with Steam closed.
    /// </summary>
    internal const string ReadOnlyReason = "This settings file is read-only, so PC Tidy leaves it alone. Untick Read-only in the file's Properties first if you want the change.";

    internal const string SteamOpenReason = "Steam is open. Exit Steam (Steam menu → Exit), then scan again: Steam saves its settings when it closes, so a change planned now would be skipped.";

    private void Apply(GameRecommendation rec, string reason, PlanItem plan, string state)
    {
        if (_g.IsRunning("steam") && plan.RequiresClosedApps?.Contains("steam", StringComparer.OrdinalIgnoreCase) == true)
        {
            _items.Add(Row(rec, reason, state) with { DisabledReason = SteamOpenReason, Detail = plan.SourcePath });
            return;
        }
        var (allowed, refusal) = Plans.Vet(_g.Safety, new[] { plan });
        _items.Add(Row(rec, reason, state) with
        {
            Actions = allowed,
            DisabledReason = refusal,
            RequiresClosedApps = plan.RequiresClosedApps,
            Detail = plan.SourcePath,
        });
    }

    private void Info(GameRecommendation rec, string reason, string? state = null) => _items.Add(Row(rec, reason, state));

    private void Disabled(GameRecommendation rec, string reason, string why) => _items.Add(Row(rec, reason, null) with { DisabledReason = why });

    private ScanItem Row(GameRecommendation rec, string reason, string? state) => new()
    {
        Id = $"games.optimize.{_game.Key}.{rec.Id}",
        Title = PlainTitle(rec.Title),
        Reason = reason,
        Badge = Badge.Check,
        OwnerApp = _game.Name,
        CurrentState = state,
        Details = new Dictionary<string, string>
        {
            ["Source"] = rec.SourceUrl,
            ["Evidence"] = rec.Evidence == Evidence.Verified ? "Documented (vendor docs or PCGamingWiki)" : "Community report, not confirmed",
            ["Verdict"] = rec.Verdict switch
            {
                TweakVerdict.Proven => "Proven",
                TweakVerdict.Situational => "Situational: helps on some PCs",
                TweakVerdict.Placebo => "Placebo: does nothing useful",
                _ => "Harmful",
            },
            ["Engine"] = _p.EngineWhy is { } why ? $"{_p.EngineName} ({why})" : _p.EngineName,
            ["Anti-cheat"] = _p.AntiCheat == AntiCheat.None ? "None found" : _p.AntiCheat.Label(),
        }.WithLaunchOption(rec.AddLaunchOption),
    };

    /// <summary>
    /// "Use exclusive fullscreen (-window-mode exclusive)" → "Use exclusive fullscreen": the launch option itself is a
    /// command-line detail, shown in the row's details instead of its title.
    /// </summary>
    internal static string PlainTitle(string title)
        => System.Text.RegularExpressions.Regex.Replace(title, @"\s*\((?:-|\+)[^)]*\)\s*$", "").Trim();

    private string HowToSetLaunchOptions() => _game.Launcher switch
    {
        Launcher.Epic => "Set it in the Epic Games Launcher: Settings → scroll to the game → tick \"Additional Command Line Arguments\".",
        Launcher.Gog => "Set it in GOG GALAXY: Manage installation → Configure → Custom executables / arguments.",
        Launcher.Ubisoft => "Set it in Ubisoft Connect: the game → Properties → Add launch arguments.",
        Launcher.BattleNet => "Set it in Battle.net: the game → cog next to Play → Game Settings → Additional command line arguments.",
        _ => "This launcher has no launch options box, so there's nothing to set.",
    };
}

internal static class GameDetailsExtensions
{
    /// <summary>Adds the exact launch option to a row's details (it's kept out of the title).</summary>
    public static Dictionary<string, string> WithLaunchOption(this Dictionary<string, string> details, string? option)
    {
        if (!string.IsNullOrWhiteSpace(option)) details["Launch option"] = option;
        return details;
    }
}
