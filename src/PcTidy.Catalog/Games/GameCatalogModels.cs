namespace PcTidy.Catalog.Games;

/// <summary>Game engine, detected from the install folder's file layout (names only, never contents).</summary>
public enum GameEngine { Unknown, Unity, Unreal, Source, Source2, IdTech, Godot }

/// <summary>Anti-cheat a game ships with. Kernel-level ones mean: PC Tidy makes no file edits for that game.</summary>
public enum AntiCheat
{
    None,
    /// <summary>Valve Anti-Cheat: server-side/user-mode, launch options and the game's own config are fine.</summary>
    Vac,
    EasyAntiCheat,
    BattlEye,
    Vanguard,
    Ricochet,
    Faceit,
    /// <summary>Roblox's Hyperion (Byfron).</summary>
    Hyperion,
    /// <summary>nProtect GameGuard.</summary>
    GameGuard,
    /// <summary>EA's Javelin anti-cheat.</summary>
    EaJavelin,
    /// <summary>Denuvo Anti-Cheat or a publisher's own kernel driver.</summary>
    OtherKernel,
}

/// <summary>How well a fact is backed, using the research appendix tags (Community tweaks T9/T10).</summary>
public enum Evidence
{
    /// <summary>Read from the vendor's docs or the live PCGamingWiki page ("Verified" / [V] in the appendix).</summary>
    Verified,
    /// <summary>Third-party guide or memory, marked "(unverified)" in the appendix. Only ever shown, or used for logs/caches that must exist on disk first.</summary>
    Unverified,
}

/// <summary>Research verdict (Tweaks.md wording).</summary>
public enum TweakVerdict { Proven, Situational, Placebo, Harmful }

public enum RecommendationKind { LaunchOption, ConfigSetting, FullscreenMode, KnownFix }

public static class AntiCheatFacts
{
    /// <summary>
    /// Kernel or "unsure" anti-cheats: PC Tidy offers information only for these games (no launch-option or file edits),
    /// because a change an anti-cheat might flag is never worth the risk of a ban.
    /// </summary>
    public static bool IsStrict(this AntiCheat a) => a is not (AntiCheat.None or AntiCheat.Vac);

    public static string Label(this AntiCheat a) => a switch
    {
        AntiCheat.Vac => "Valve Anti-Cheat",
        AntiCheat.EasyAntiCheat => "Easy Anti-Cheat",
        AntiCheat.BattlEye => "BattlEye",
        AntiCheat.Vanguard => "Riot Vanguard",
        AntiCheat.Ricochet => "Ricochet",
        AntiCheat.Faceit => "FACEIT",
        AntiCheat.Hyperion => "Hyperion",
        AntiCheat.GameGuard => "nProtect GameGuard",
        AntiCheat.EaJavelin => "EA Javelin",
        AntiCheat.OtherKernel => "a kernel anti-cheat",
        _ => "none",
    };
}

/// <summary>
/// What PC Tidy knows about one launch-option token. Used to flag placebo/harmful options already set
/// and to never suggest an option for the wrong engine.
/// </summary>
/// <param name="Token">The option as typed, e.g. "-high" or "+fps_max" (matched case-insensitively).</param>
/// <param name="Engines">Engines this fact applies to. Empty = every engine.</param>
/// <param name="TakesValue">The next word (or "=value") belongs to it, e.g. "-threads 8".</param>
public sealed record LaunchOptionFact(
    string Token,
    IReadOnlyList<GameEngine> Engines,
    TweakVerdict Verdict,
    bool TakesValue,
    string Explanation,
    string SourceUrl,
    Evidence Evidence,
    Measurement? Measured = null)
{
    public bool AppliesTo(GameEngine engine) => Engines.Count == 0 || Engines.Contains(engine);
}

/// <summary>
/// One measured number from the FPS research (FPS boost tweaks §4, appendix F1..F8). Always shown with its hardware and
/// its source with date; never turned into a general "+X% FPS" claim.
/// </summary>
/// <param name="Result">What was measured, e.g. "185 vs 135 FPS average, 1% lows 68 vs 41".</param>
/// <param name="Hardware">The test PC, e.g. "i3-12100F + GTX 1660 Super", or "hardware not stated".</param>
/// <param name="Source">Who measured it and when, e.g. "9Erza CS2 guide, April 2026".</param>
/// <param name="Strength">Research evidence strength: A (repeated, published method) to D (no test found).</param>
/// <param name="Caveat">Optional limit of the test ("one rig", "unverified blog").</param>
public sealed record Measurement(string Result, string Hardware, string Source, string SourceUrl, char Strength, string? Caveat = null)
{
    /// <summary>"Measured: 185 vs 135 FPS … · i3-12100F + GTX 1660 Super · nexprotools blog, undated (unverified blog)".</summary>
    public string Line => $"Measured: {Result} · {Hardware} · {Source}" + (Caveat is { Length: > 0 } c ? $" ({c})" : "");
}

/// <summary>
/// A recommendation PC Tidy can make for a game. Apply-able ones change exactly one thing:
/// a Steam launch option (<see cref="AddLaunchOption"/>) or one documented key in the game's own user config file
/// (<see cref="Ini"/>). Everything else is information only.
/// </summary>
public sealed record GameRecommendation
{
    public required string Id { get; init; }
    public required RecommendationKind Kind { get; init; }
    public required string Title { get; init; }
    /// <summary>Plain-English "what it does / when it helps".</summary>
    public required string Reason { get; init; }
    public required TweakVerdict Verdict { get; init; }
    public required Evidence Evidence { get; init; }
    public required string SourceUrl { get; init; }
    /// <summary>Launch option(s) to add, e.g. "-novid" or "+fps_max 0". Null = not a launch option.</summary>
    public string? AddLaunchOption { get; init; }
    /// <summary>Config-file key to set. Null = not a config edit.</summary>
    public IniSetting? Ini { get; init; }
    /// <summary>Steam app ids this recommendation must never be offered for (e.g. CS2 has no intro video).</summary>
    public IReadOnlyList<string> NotForAppIds { get; init; } = Array.Empty<string>();
    /// <summary>The measured number behind this row, with hardware and source (FPS research §4.7). Null = no measurement found.</summary>
    public Measurement? Measured { get; init; }
    /// <summary>PLACEBO rows are never applied: they sit greyed on the "Doesn't help (measured)" shelf.</summary>
    public bool IsPlacebo => Verdict == TweakVerdict.Placebo;
    public bool IsApplicable => !IsPlacebo && (AddLaunchOption is not null || Ini is not null);
}

/// <summary>One documented key in a game's user config file (INI style).</summary>
/// <param name="FileName">File name inside the engine's user config folder, e.g. "GameUserSettings.ini".</param>
public sealed record IniSetting(string FileName, string Section, string Key, string Value);

/// <summary>Per-title knowledge: anti-cheat, engine override and extra advice with sources.</summary>
public sealed record GameRecipe
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public IReadOnlyList<string> SteamAppIds { get; init; } = Array.Empty<string>();
    /// <summary>Epic AppName values (from the .item manifest).</summary>
    public IReadOnlyList<string> EpicAppNames { get; init; } = Array.Empty<string>();
    /// <summary>Install-folder names (any launcher), matched case-insensitively.</summary>
    public IReadOnlyList<string> FolderNames { get; init; } = Array.Empty<string>();
    public AntiCheat AntiCheat { get; init; } = AntiCheat.None;
    public GameEngine? Engine { get; init; }
    public IReadOnlyList<GameRecommendation> Recommendations { get; init; } = Array.Empty<GameRecommendation>();
    /// <summary>The game's PCGamingWiki page (always shown as a link, never fetched).</summary>
    public string? WikiUrl { get; init; }
}

/// <summary>Where a cleanup target lives (resolved against the PC's own known folders at scan time).</summary>
public enum CleanupBase { LocalAppData, RoamingAppData, LocalLow, EachFixedDriveRoot }

public enum CleanupKind { Logs, CrashReports, ShaderCache, DownloadCache, WebCache }

/// <summary>
/// A per-game cache or log folder. Only its CONTENTS are offered, never the folder itself, never saves or settings.
/// Unverified paths are only offered when the folder really exists, and always as "Check".
/// </summary>
/// <param name="RelativePath">Below <see cref="Base"/>, e.g. @"FortniteGame\Saved\Logs".</param>
/// <param name="CloseFirst">Process names (no .exe) that must be closed.</param>
/// <param name="FilePatterns">Only files matching these (e.g. "Player.log"); empty = every entry in the folder.</param>
public sealed record GameCleanupTarget(
    string Id,
    string Game,
    CleanupKind Kind,
    CleanupBase Base,
    string RelativePath,
    IReadOnlyList<string> CloseFirst,
    Evidence Evidence,
    string SourceUrl,
    IReadOnlyList<string>? FilePatterns = null)
{
    public IReadOnlyList<string> Patterns => FilePatterns ?? Array.Empty<string>();
}
