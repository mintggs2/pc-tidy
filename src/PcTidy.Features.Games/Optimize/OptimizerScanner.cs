using System.Diagnostics;
using PcTidy.Catalog.Games;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Games.Cleanup;
using PcTidy.Features.Games.Common;
using PcTidy.Features.Games.Library;

namespace PcTidy.Features.Games.Optimize;

/// <summary>
/// Optimize tab: per detected game, engine-correct launch options, documented config keys, fullscreen mode and known
/// fixes, each with its source and (where the FPS research has one) the measured number. Changes are only ever an edit
/// of Steam's localconfig.vdf (Launch Options) or of the game's own user config file, planned as
/// <see cref="ActionKind.EditTextFile"/> (backed up and undone by the engine, only while the game and Steam are closed),
/// plus, on hybrid-GPU laptops, the per-app GPU preference Windows' Graphics settings write (HKCU, undoable).
/// Games with a kernel or unknown anti-cheat get information only. PLACEBO rows are never applied.
/// Never injects, never patches game files or binaries.
/// </summary>
public sealed class OptimizerScanner : IFeatureScanner
{
    public string FeatureId => GamesContext.FeatureId;
    public string Title => "Game optimizer";
    public Release Release => Release.V2;
    public bool IncludedInOptimizeAll => false;

    /// <summary>The last scan's per-game entries (master/detail view), including catalog games that aren't installed.</summary>
    public IReadOnlyList<GameEntry> LastEntries { get; private set; } = Array.Empty<GameEntry>();

    public Task<ScanResult> ScanAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
        => Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            var g = GamesContext.From(ctx);
            progress?.Report(new ScanProgress("Finding your games…", 0.05));
            var games = GameLibraryDetector.Detect(g, ct);
            var entries = BuildEntries(g, games, progress, ct);
            LastEntries = entries;
            return ResultFrom(entries, games.Count, g) with { Duration = sw.Elapsed, Depth = ctx.Depth };
        }, ct);

    internal static ScanResult Scan(GamesContext g, IReadOnlyList<InstalledGame> games, IProgress<ScanProgress>? progress, CancellationToken ct)
        => ResultFrom(BuildEntries(g, games, progress, ct), games.Count, g);

    /// <summary>One entry per installed game, then one per catalog game that isn't installed (view only).</summary>
    internal static IReadOnlyList<GameEntry> BuildEntries(GamesContext g, IReadOnlyList<InstalledGame> games, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var steamConfig = SteamUserConfig.Load(g.Locations.SteamRoot);
        var compat = g.Environment.CompatibilitySettings();
        var hybrid = GpuKinds.IsHybrid(g.Scan.System?.GpuNames);
        var gpuPrefs = hybrid ? SafeRead(g.Environment.GpuPreferences) : new Dictionary<string, string>();
        var entries = new List<GameEntry>();
        var seenRecipes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < games.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var game = games[i];
            progress?.Report(new ScanProgress($"Looking at {game.Name}…", 0.1 + 0.85 * i / Math.Max(1, games.Count), i));
            var layout = GameInspector.Inspect(g.Scan.Walker, game.InstallDir, ct);
            var profile = GameProfile.For(game, layout);
            if (profile.Recipe is { } r) seenRecipes.Add(r.Id);
            var builder = new RecommendationBuilder(g, game, profile, steamConfig);
            var settings = builder.Build().Concat(GameLeftoverChecks.For(game, layout, profile.Engine, g.Locations, compat)).ToList();
            entries.Add(new GameEntry
            {
                Key = game.Key,
                Name = game.Name,
                Installed = true,
                Game = game,
                Recipe = profile.Recipe,
                AntiCheat = profile.AntiCheat,
                Summary = profile.Summary(game),
                Status = builder.Status(out var why),
                StatusWhy = why,
                Settings = settings,
                Placebo = builder.PlaceboShelf(),
                GpuPreference = hybrid ? GpuPreferencePlanner.Row(game, layout.ExePaths, gpuPrefs, g.Safety) : null,
                ShaderCache = ShaderCacheRow(g, game.Name, profile.Recipe, ct),
                ConfigFiles = builder.ConfigFiles,
                ExePaths = layout.ExePaths,
                ShaderCacheFolders = ShaderCacheFolders(g, game.Name, profile.Recipe),
            });
        }

        foreach (var recipe in GameRecipes.All.Where(r => !seenRecipes.Contains(r.Id)))
            entries.Add(NotInstalled(recipe));
        return entries;
    }

    /// <summary>The flat result (one group per installed game) that the shared results list and Optimize-all understand.</summary>
    private static ScanResult ResultFrom(IReadOnlyList<GameEntry> entries, int installedCount, GamesContext g)
    {
        var groups = new List<ScanGroup>();
        foreach (var e in entries.Where(e => e.Installed))
        {
            var items = e.Settings.Concat(e.Placebo).Concat(e.GpuPreference is { } gpu ? new[] { gpu } : Array.Empty<ScanItem>()).ToList();
            if (items.Count == 0) continue;
            groups.Add(new ScanGroup { Id = "games-optimize-" + e.Key, Title = e.Name, Description = e.Summary, Badge = Badge.Check, Items = items });
        }

        var notes = new List<string>();
        if (installedCount == 0) notes.Add("No installed games were found, so there's nothing to optimize.");
        else if (groups.Count == 0) notes.Add("PC Tidy has no proven tweaks for the games on this PC. That's fine: their own settings menus are the best place.");
        else notes.Add("Changes are only made while the game (and Steam) are closed. Each edit is backed up first and can be undone from History.");
        if (groups.Any(x => x.Items.Any(i => i.DisabledReason == RecommendationBuilder.SteamOpenReason)))
            notes.Add("Steam is open, so launch options can't be changed yet. Exit Steam (Steam menu → Exit), then scan again.");
        if (entries.Any(e => e.GpuPreference is not null))
            notes.Add("This PC has two graphics chips. Each game can be set to use the faster one (Windows' own Graphics setting).");
        return new ScanResult { FeatureId = GamesContext.FeatureId, Title = "Game optimizer", Groups = groups, Notes = notes };
    }

    private static GameEntry NotInstalled(GameRecipe recipe)
    {
        var engine = recipe.Engine ?? GameEngine.Unknown;
        var recs = EngineRecommendations.For(engine).Where(r => !recipe.SteamAppIds.Any(id => r.NotForAppIds.Contains(id)))
            .Concat(recipe.Recommendations).GroupBy(r => r.Id).Select(x => x.First()).ToList();
        var key = "catalog:" + recipe.Id;
        ScanItem Row(GameRecommendation rec, string? disabled) => new()
        {
            Id = $"games.optimize.{key}.{rec.Id}",
            Title = RecommendationBuilder.PlainTitle(rec.Title),
            Reason = rec.Reason,
            Badge = Badge.Check,
            OwnerApp = recipe.Name,
            CurrentState = "Not installed",
            DisabledReason = disabled,
            Details = RecommendationBuilder.RowDetails(rec, engine, null, recipe.AntiCheat),
        };
        var settings = recs.Where(r => !r.IsPlacebo).Select(r => Row(r, null)).ToList();
        var placebo = recs.Where(r => r.IsPlacebo).Select(r => Row(r, RecommendationBuilder.PlaceboReason(r.Measured))).ToList();
        var ac = recipe.AntiCheat == AntiCheat.None ? "no anti-cheat" : recipe.AntiCheat.Label() + (recipe.AntiCheat.IsStrict() ? " (information only)" : "");
        return new GameEntry
        {
            Key = key,
            Name = recipe.Name,
            Installed = false,
            Recipe = recipe,
            AntiCheat = recipe.AntiCheat,
            Summary = $"Not installed · {ac}" + (recipe.WikiUrl is { } w ? " · More on PCGamingWiki: " + w : ""),
            Status = GameTuneStatus.NotInstalled,
            StatusWhy = "Not installed on this PC. You can read the advice now; nothing is applied until the game is installed and scanned.",
            Settings = settings,
            Placebo = placebo,
        };
    }

    /// <summary>
    /// The game's own shader cache folders from the catalog, as one row that is NEVER ticked for you (card C9: games stutter
    /// while shaders rebuild, which can take 1 to 2 hours). Null when PC Tidy knows no shader cache for the game, or it's empty.
    /// </summary>
    internal static ScanItem? ShaderCacheRow(GamesContext g, string gameName, GameRecipe? recipe, CancellationToken ct)
    {
        var targets = ShaderCacheTargets(gameName, recipe);
        var rows = new List<ScanItem>();
        foreach (var t in targets)
            foreach (var folder in GameCleanupScanner.Resolve(g.Locations, t))
            {
                if (!FolderSize.DirectoryExists(folder)) continue;
                if (GameCleanupScanner.TargetItem(g, t, folder, ct) is { } row) rows.Add(row);
            }
        if (rows.Count == 0) return null;
        var actions = rows.SelectMany(r => r.Actions).ToList();
        return new ScanItem
        {
            Id = "games.optimize.shadercache." + gameName.ToLowerInvariant(),
            Title = $"Clear {gameName}'s shader cache",
            Detail = string.Join("; ", rows.Select(r => r.Detail)),
            Reason = $"Only if it's huge or after a graphics glitch. {GameCleanupTargets.ShaderRebuildWarning}",
            Badge = Badge.Check,
            DefaultSelected = false,
            SizeBytes = rows.Sum(r => r.SizeBytes),
            FileCount = rows.Sum(r => r.FileCount),
            OwnerApp = gameName,
            RequiresClosedApps = rows.SelectMany(r => r.RequiresClosedApps ?? Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Details = new Dictionary<string, string>
            {
                ["Source"] = rows[0].Details?.GetValueOrDefault("Source") ?? FpsMeasurements.DigitalFoundryShaders,
                ["Evidence"] = "Community-reported folder, only offered because it exists on this PC.",
                ["Verdict"] = "Situational: clear it only for a reason",
                ["Measured"] = FpsMeasurements.ShaderRebuild.Line,
                ["Measured source"] = FpsMeasurements.ShaderRebuild.SourceUrl,
            },
            Actions = actions,
            DisabledReason = actions.Count == 0 ? rows.Select(r => r.DisabledReason).FirstOrDefault(r => r is not null) : null,
        };
    }

    private static List<GameCleanupTarget> ShaderCacheTargets(string gameName, GameRecipe? recipe)
        => GameCleanupTargets.All.Where(t => t.Kind == CleanupKind.ShaderCache
            && (t.Game.Equals(gameName, StringComparison.OrdinalIgnoreCase) || recipe is not null && t.Game.Equals(recipe.Name, StringComparison.OrdinalIgnoreCase)))
            .ToList();

    /// <summary>Where this game's own shader cache lives (exists or not), so "Reset to my backup" can recognise clearing it.</summary>
    internal static IReadOnlyList<string> ShaderCacheFolders(GamesContext g, string gameName, GameRecipe? recipe)
        => ShaderCacheTargets(gameName, recipe).SelectMany(t => GameCleanupScanner.Resolve(g.Locations, t))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static IReadOnlyDictionary<string, string> SafeRead(Func<IReadOnlyDictionary<string, string>> read)
    {
        try { return read(); } catch (Exception) { return new Dictionary<string, string>(); }
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

    public string EngineName => RecommendationBuilder.EngineLabel(Engine);

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
    private readonly List<ScanItem> _placebo = new();
    private readonly List<string> _configFiles = new();
    private readonly HashSet<string> _auditedTokens = new(StringComparer.OrdinalIgnoreCase);
    private int _outstanding;
    private int _matched;

    public RecommendationBuilder(GamesContext g, InstalledGame game, GameProfile profile, SteamUserConfig? steam)
        => (_g, _game, _p, _steam) = (g, game, profile, steam);

    private bool OnSteam => _game.Launcher == Launcher.Steam && _g.Locations.SteamRoot is not null;

    /// <summary>The user config files this game's rows read (and may edit): where "Reset to my backup" looks.</summary>
    public IReadOnlyList<string> ConfigFiles => _configFiles;

    public IReadOnlyList<ScanItem> Build()
    {
        var recs = EngineRecommendations.For(_p.Engine)
            .Concat(_p.Recipe?.Recommendations ?? Array.Empty<GameRecommendation>())
            .Where(r => !(_game.Launcher == Launcher.Steam && r.NotForAppIds.Contains(_game.Id)))
            .GroupBy(r => r.Id).Select(x => x.First()).ToList();
        foreach (var rec in recs)
        {
            if (rec.IsPlacebo) { _placebo.Add(Row(rec, rec.Reason, null) with { DisabledReason = PlaceboReason(rec.Measured) }); continue; }
            if (rec.AddLaunchOption is { } option) LaunchOption(rec, option);
            else if (rec.Ini is { } ini) Ini(rec, ini);
            else Info(rec, rec.Reason);
        }
        AuditLaunchOptions();
        AddMeasuredLaunchOptionPlacebos(recs);
        return _items;
    }

    /// <summary>PLACEBO rows: greyed under "Doesn't help (measured)" with the number, hardware and source. Never planned.</summary>
    public IReadOnlyList<ScanItem> PlaceboShelf() => _placebo;

    /// <summary>
    /// Tuned / Not tuned from the real config files: does every apply-able row already match? Strict anti-cheat games are
    /// always "Info only"; games where nothing can be set are "Info only" too.
    /// </summary>
    public GameTuneStatus Status(out string why)
    {
        if (_p.Strict)
        {
            why = $"{_game.Name} uses {_p.AntiCheat.Label()}, so PC Tidy only gives advice for it.";
            return GameTuneStatus.InfoOnly;
        }
        if (_outstanding > 0)
        {
            why = $"{_outstanding} recommended setting{(_outstanding == 1 ? " doesn't" : "s don't")} match the game's config files yet.";
            return GameTuneStatus.NotTuned;
        }
        if (_matched > 0)
        {
            why = "The game's config files already match every setting PC Tidy recommends.";
            return GameTuneStatus.Tuned;
        }
        why = "PC Tidy has nothing it can set for this game. The advice below is for you to use in the game.";
        return GameTuneStatus.InfoOnly;
    }

    internal static string PlaceboReason(Measurement? m)
        => "Doesn't help (measured). " + (m is null ? "No test found that shows a gain." : m.Line) + " PC Tidy never applies this.";

    // ---------------- launch options ----------------

    private void LaunchOption(GameRecommendation rec, string option)
    {
        var current = OnSteam ? _steam?.LaunchOptions(_game.Id) : null;
        if (current is not null && LaunchOptionLine.Contains(current, option)) { _matched++; Info(rec, $"Already set: {rec.Reason}", state: "Already set"); return; }
        if (!OnSteam) { Info(rec, $"{rec.Reason} {HowToSetLaunchOptions()}"); return; }
        if (Blocked(rec) is { } why) { Disabled(rec, rec.Reason, why); return; }
        if (_steam is null)
        {
            Info(rec, $"{rec.Reason} Steam hasn't saved settings for your account on this PC yet, so set it in Steam → right-click the game → Properties → Launch Options.");
            return;
        }
        var plan = ConfigEditPlanner.AddLaunchOption(_steam, _g.Locations.SteamRoot!, _game.Id, option, _game.Name, _p.CloseFirst(_game));
        if (plan is null) { Info(rec, $"{rec.Reason} Set it in Steam → right-click the game → Properties → Launch Options."); return; }
        _outstanding++;
        Apply(rec, rec.Reason, plan, current is { Length: > 0 } ? $"Now: {current}" : "Not set");
    }

    /// <summary>Placebo or harmful options already in the game's Steam launch options: offer to take them out.</summary>
    private void AuditLaunchOptions()
    {
        if (!OnSteam || _steam?.LaunchOptions(_game.Id) is not { Length: > 0 } current) return;
        foreach (var word in LaunchOptionLine.Words(current))
        {
            var fact = LaunchOptionFacts.Find(word.Text, _p.Engine);
            if (fact is null || fact.Verdict is not (TweakVerdict.Placebo or TweakVerdict.Harmful) || !_auditedTokens.Add(fact.Token)) continue;
            var rec = new GameRecommendation
            {
                Id = "audit." + fact.Token, Kind = RecommendationKind.LaunchOption,
                Title = $"Remove \"{fact.Token}\" from the launch options",
                Reason = (fact.Verdict == TweakVerdict.Harmful ? "This option slows the game down. " : "This option does nothing useful. ") + fact.Explanation,
                Verdict = fact.Verdict, Evidence = fact.Evidence, SourceUrl = fact.SourceUrl, Measured = fact.Measured,
            };
            // Taking a placebo option out is always safe to offer, but a strict anti-cheat still means "do it yourself".
            if (_p.Strict) { Disabled(rec, rec.Reason, StrictReason("remove it in Steam → Properties → Launch Options")); continue; }
            var plan = ConfigEditPlanner.RemoveLaunchOption(_steam, _g.Locations.SteamRoot!, _game.Id, fact.Token, fact.TakesValue, _game.Name, _p.CloseFirst(_game));
            if (plan is null) continue;
            _outstanding++;
            Apply(rec, rec.Reason, plan, "Now: " + current);
        }
    }

    /// <summary>
    /// Measured placebo launch options for this engine (e.g. "-high": 902.6 vs 900.7 FPS), shown on the shelf so nobody
    /// adds them. Skipped when the recipe already shows the same measurement or the option is set (then it's an audit row).
    /// </summary>
    private void AddMeasuredLaunchOptionPlacebos(IReadOnlyList<GameRecommendation> recs)
    {
        var shown = recs.Where(r => r.IsPlacebo && r.Measured is not null).Select(r => r.Measured!).ToHashSet();
        foreach (var fact in LaunchOptionFacts.All.Where(f => f.Verdict == TweakVerdict.Placebo && f.Measured is not null && f.AppliesTo(_p.Engine)))
        {
            if (shown.Contains(fact.Measured!) || _auditedTokens.Contains(fact.Token)) continue;
            shown.Add(fact.Measured!);
            var rec = new GameRecommendation
            {
                Id = "placebo." + fact.Token, Kind = RecommendationKind.LaunchOption, Title = $"\"{fact.Token}\" launch option",
                Reason = fact.Explanation, Verdict = fact.Verdict, Evidence = fact.Evidence, SourceUrl = fact.SourceUrl, Measured = fact.Measured,
            };
            _placebo.Add(Row(rec, rec.Reason, null) with { DisabledReason = PlaceboReason(fact.Measured) });
        }
    }

    // ---------------- config files ----------------

    private void Ini(GameRecommendation rec, IniSetting ini)
    {
        var file = FindUserConfig(ini.FileName);
        if (file is null) return; // the game hasn't written its settings file yet: nothing to show
        if (!_configFiles.Contains(file, StringComparer.OrdinalIgnoreCase)) _configFiles.Add(file);
        string text;
        DateTime written;
        try { text = File.ReadAllText(file); written = File.GetLastWriteTimeUtc(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        var current = IniText.Get(text, ini.Section, ini.Key);
        if (string.Equals(current, ini.Value, StringComparison.OrdinalIgnoreCase)) { _matched++; Info(rec, $"Already set: {rec.Reason}", state: "Already set"); return; }
        if (Blocked(rec) is { } why) { Disabled(rec, rec.Reason, why); return; }
        _outstanding++;
        if (GameLeftoverChecks.IsReadOnly(file)) { Disabled(rec, rec.Reason, ReadOnlyReason); return; }
        var plan = ConfigEditPlanner.SetIni(file, text, written, ini.Section, ini.Key, ini.Value, _game.InstallDir,
            $"Set {ini.Key}={ini.Value} in {_game.Name}'s {ini.FileName}", _p.CloseFirst(_game));
        if (plan is null) { _outstanding--; Info(rec, rec.Reason); return; }
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

    internal const string ReadOnlyReason = "This settings file is read-only, so PC Tidy leaves it alone. Untick Read-only in the file's Properties first if you want the change.";

    /// <summary>
    /// Steam writes localconfig.vdf when it exits, so a launch-option edit planned while it runs would be skipped as
    /// "changed since the scan" once the user closes Steam to apply it. Such rows wait for a scan with Steam closed.
    /// </summary>
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
        Details = RowDetails(rec, _p.Engine, _p.EngineWhy, _p.AntiCheat),
    };

    /// <summary>Source, evidence tag, verdict and (where the research has one) the measured number with hardware and source.</summary>
    internal static Dictionary<string, string> RowDetails(GameRecommendation rec, GameEngine engine, string? engineWhy, AntiCheat antiCheat)
    {
        var d = new Dictionary<string, string>
        {
            ["Source"] = rec.SourceUrl,
            ["Evidence"] = EvidenceLabel(rec.Evidence),
            ["Verdict"] = VerdictLabel(rec.Verdict),
            ["Engine"] = engineWhy is { } why ? $"{EngineLabel(engine)} ({why})" : EngineLabel(engine),
            ["Anti-cheat"] = antiCheat == AntiCheat.None ? "None found" : antiCheat.Label(),
        };
        if (rec.Measured is { } m)
        {
            d["Measured"] = m.Line;
            d["Measured source"] = m.SourceUrl;
        }
        return d.WithLaunchOption(rec.AddLaunchOption);
    }

    internal static string EvidenceLabel(Evidence e) => e == Evidence.Verified ? "Documented (vendor docs or PCGamingWiki)" : "Community report, not confirmed";

    internal static string VerdictLabel(TweakVerdict v) => v switch
    {
        TweakVerdict.Proven => "Proven",
        TweakVerdict.Situational => "Situational: helps on some PCs",
        TweakVerdict.Placebo => "Placebo: does nothing useful",
        _ => "Harmful",
    };

    internal static string EngineLabel(GameEngine engine) => engine switch
    {
        GameEngine.Unity => "Unity",
        GameEngine.Unreal => "Unreal Engine",
        GameEngine.Source => "Source",
        GameEngine.Source2 => "Source 2",
        GameEngine.IdTech => "id Tech",
        GameEngine.Godot => "Godot",
        _ => "Engine not recognised",
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
