namespace PcTidy.Catalog.DriveMove;

/// <summary>An app whose updater deletes and recreates its own folder (Squirrel-style), which can replace a junction.</summary>
/// <param name="FolderName">Its folder directly under %LOCALAPPDATA% (or a two-level path such as "Microsoft\Teams").</param>
/// <param name="ProcessNames">Process names (no .exe) to close before a move.</param>
public sealed record JunctionRiskApp(string Name, string FolderName, IReadOnlyList<string> ProcessNames);

/// <summary>
/// Research.md §7 "Things that break with junctions": apps whose updater replaces their folder can swap PC Tidy's link for
/// a fresh real folder (the moved copy stays safe on the other drive, but the app fills C: again). Shown as a warning.
/// Folders with Squirrel's own layout (Update.exe next to app-1.2.3 folders) are spotted even when they aren't listed here.
/// </summary>
public static class JunctionRiskApps
{
    public static readonly IReadOnlyList<JunctionRiskApp> All = new JunctionRiskApp[]
    {
        new("Discord", "Discord", new[] { "Discord" }),
        new("Discord PTB", "DiscordPTB", new[] { "DiscordPTB" }),
        new("Discord Canary", "DiscordCanary", new[] { "DiscordCanary" }),
        new("Slack", "slack", new[] { "slack" }),
        new("Microsoft Teams (classic)", @"Microsoft\Teams", new[] { "Teams" }),
        new("GitHub Desktop", "GitHubDesktop", new[] { "GitHubDesktop" }),
        new("Postman", "Postman", new[] { "Postman" }),
        new("Figma", "Figma", new[] { "Figma" }),
    };

    /// <summary>Squirrel's updater: an "Update.exe" next to versioned "app-x.y.z" folders.</summary>
    public const string SquirrelUpdater = "Update.exe";
    public const string SquirrelVersionPrefix = "app-";
}

/// <summary>
/// Per-user environment variables (HKCU\Environment) that move a tool's data, from <see cref="AppCacheCatalog"/>.
/// Not yet registry allowances: the data is a path chosen at run time, so it can't be pre-listed. They need a
/// "path on one of this PC's fixed drives" rule in the safety guard first; until then these moves are shown as steps.
/// </summary>
public static class LocationVariables
{
    public static IReadOnlyList<string> All { get; } = AppCacheCatalog.All
        .Select(t => t.Variable).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
