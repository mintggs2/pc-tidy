using PcTidy.Catalog.Games;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Games.Common;
using PcTidy.Features.Games.Library;

namespace PcTidy.Features.Games.Optimize;

/// <summary>The status chip on a game in the Optimize list.</summary>
public enum GameTuneStatus
{
    /// <summary>At least one recommended setting doesn't match the game's real config files yet.</summary>
    NotTuned,
    /// <summary>The game's real config files already match every setting PC Tidy can set.</summary>
    Tuned,
    /// <summary>Strict anti-cheat, or nothing PC Tidy can set: advice only.</summary>
    InfoOnly,
    /// <summary>A catalog game that isn't installed: the advice can be read, nothing is applied.</summary>
    NotInstalled,
}

/// <summary>
/// One game in the Optimize master/detail view. <see cref="Status"/> comes from the game's real config files at scan
/// time (Steam launch options, the game's user ini), never from what PC Tidy remembers doing.
/// </summary>
public sealed record GameEntry
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required bool Installed { get; init; }
    public InstalledGame? Game { get; init; }
    public GameRecipe? Recipe { get; init; }
    public AntiCheat AntiCheat { get; init; }
    public required string Summary { get; init; }
    public required GameTuneStatus Status { get; init; }
    public required string StatusWhy { get; init; }
    /// <summary>Recipe rows: apply-able settings (never pre-ticked) and information.</summary>
    public IReadOnlyList<ScanItem> Settings { get; init; } = Array.Empty<ScanItem>();
    /// <summary>"Doesn't help (measured)": PLACEBO rows, greyed, never planned.</summary>
    public IReadOnlyList<ScanItem> Placebo { get; init; } = Array.Empty<ScanItem>();
    /// <summary>Per-app GPU preference on hybrid-GPU PCs (null elsewhere).</summary>
    public ScanItem? GpuPreference { get; init; }
    /// <summary>The game's own shader cache, never ticked for you (card C9). Null when PC Tidy knows none for this game.</summary>
    public ScanItem? ShaderCache { get; init; }
    /// <summary>User config files the rows read, for "Reset to my backup".</summary>
    public IReadOnlyList<string> ConfigFiles { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExePaths { get; init; } = Array.Empty<string>();
    /// <summary>This game's own shader cache folders from the catalog (whether or not they exist now), for "Reset to my backup".</summary>
    public IReadOnlyList<string> ShaderCacheFolders { get; init; } = Array.Empty<string>();

    public string StatusLabel => Status switch
    {
        GameTuneStatus.NotTuned => "Not tuned",
        GameTuneStatus.Tuned => "Tuned",
        GameTuneStatus.InfoOnly => "Info only",
        _ => "Not installed",
    };

    /// <summary>Rows the user can tick in the detail pane (settings and GPU preference; the shader cache has its own switch).</summary>
    public IEnumerable<ScanItem> Tickable => Settings.Concat(GpuPreference is { } g ? new[] { g } : Array.Empty<ScanItem>())
        .Where(i => i.DisabledReason is null && i.Actions.Count > 0);
}

public enum GameListFilter { All, Installed, NotTuned }

/// <summary>Search and filter for the Optimize list (pure, so the list can be tested without a window).</summary>
public static class GameList
{
    public static IReadOnlyList<GameEntry> Apply(IEnumerable<GameEntry> entries, string? search, GameListFilter filter)
    {
        var q = (search ?? "").Trim();
        return entries
            .Where(e => filter switch
            {
                GameListFilter.Installed => e.Installed,
                GameListFilter.NotTuned => e.Status == GameTuneStatus.NotTuned,
                _ => true,
            })
            .Where(e => q.Length == 0 || e.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(e => e.Status switch { GameTuneStatus.NotTuned => 0, GameTuneStatus.Tuned => 1, GameTuneStatus.InfoOnly => 2, _ => 3 })
            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}

/// <summary>What "Reset to my backup" can undo for one game.</summary>
/// <param name="Runs">Runs to undo, newest first. Empty when there's nothing (or nothing safe) to reset.</param>
/// <param name="Problem">Why there's nothing to reset, in plain English; null when <paramref name="Runs"/> has something.</param>
public sealed record BackupLookup(IReadOnlyList<RunRecord> Runs, string? Problem)
{
    public bool CanReset => Runs.Count > 0;

    /// <summary>The finished steps Reset will undo, newest run first, one plain line each (shown before the user clicks).</summary>
    public IReadOnlyList<string> Steps { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Finds the runs whose backups put a game's config back: Games-section runs with finished config edits of this game's
/// files (its Steam launch options or its own user ini) or its GPU preference. Reset reuses the normal undo from History,
/// which restores the engine's byte-exact backups.
/// <para>
/// A run counts as this game's only when EVERY finished step in it belongs to this game: its config edits, its GPU
/// preference, or clearing its own shader cache. Any other finished step (another game's config, launch options, GPU
/// preference or shader cache, a cleanup step, anything from another section) makes the run "mixed", and mixed runs are
/// never undone from here: the user is sent to History, where the whole run and its steps are shown. v3.0 runs, which
/// could cover several games, stay in History, so this matters.
/// </para>
/// </summary>
public static class BackupFinder
{
    public static BackupLookup For(GameEntry entry, IEnumerable<RunRecord> runs)
    {
        if (!entry.Installed) return new(Array.Empty<RunRecord>(), "This game isn't installed, so there's no backup.");
        var mine = new List<RunRecord>();
        var mixed = false;
        foreach (var run in runs.Where(Undoable).OrderByDescending(r => r.CreatedUtc))
        {
            var touches = run.Steps.Where(s => s.Status == StepStatus.Done).Select(s => Touch(entry, s.Item)).ToList();
            // Only runs that changed this game's settings are backups of them (a cache-only run isn't).
            if (!touches.Any(t => t is Touched.ThisGameSettings or Touched.AlsoOthers)) continue;
            if (touches.Any(t => t is Touched.Other or Touched.AlsoOthers)) { mixed = true; continue; }
            mine.Add(run);
        }
        if (mixed)
            return new(Array.Empty<RunRecord>(),
                $"A backup of {entry.Name}'s settings was made together with changes to other things (another game, or another kind of change). Undo that change from History, where you can see every step, so nothing else is put back by surprise.");
        if (mine.Count == 0)
            return new(Array.Empty<RunRecord>(), $"PC Tidy hasn't changed {entry.Name}'s settings, so there's no backup to go back to.");
        var steps = mine.SelectMany(r => r.Steps.Where(s => s.Status == StepStatus.Done && s.Item.Reversible && s.Item.Kind != ActionKind.OpenExternal))
            .Select(s => s.Item.Description).ToList();
        return new(mine, null) { Steps = steps };
    }

    private static bool Undoable(RunRecord r)
        => r.Mode == ExecutionMode.Real && r.FeatureId == GamesContext.FeatureId
           && r.Status is RunStatus.Completed or RunStatus.CompletedWithSkips or RunStatus.PartiallyUndone or RunStatus.Failed;

    internal enum Touched
    {
        /// <summary>Nothing to put back (opening a launcher screen).</summary>
        Nothing,
        /// <summary>This game's config file, Steam launch options or GPU preference.</summary>
        ThisGameSettings,
        /// <summary>Clearing this game's own shader cache: part of the same game, so Reset may undo it too.</summary>
        ThisGameCache,
        /// <summary>One Steam localconfig.vdf edit that changed this game's and other games' launch options.</summary>
        AlsoOthers,
        /// <summary>Anything else: another game's config, launch options, GPU preference or cache, a cleanup step, another section.</summary>
        Other,
    }

    internal static Touched Touch(GameEntry e, PlanItem item)
    {
        if (item.Kind == ActionKind.OpenExternal) return Touched.Nothing;
        if (item.FeatureId != GamesContext.FeatureId) return Touched.Other;
        if (item.Kind == ActionKind.EditTextFile && item.SourcePath is { } path)
        {
            if (e.ConfigFiles.Any(f => SamePath(f, path)))
                return Touched.ThisGameSettings;
            if (e.Game is { Launcher: Launcher.Steam } g && item.Args is { } a && a.GetValueOrDefault(EditArgs.Kind) == EditArgs.KindLaunchOptions)
            {
                var ids = EditArgs.AppIdsOf(a);
                // One localconfig.vdf edit that changed several games' launch options: undoing it would reset them all.
                return !ids.Contains(g.Id) ? Touched.Other : ids.Count == 1 ? Touched.ThisGameSettings : Touched.AlsoOthers;
            }
            return Touched.Other;
        }
        if (item.Kind is ActionKind.RegistrySetValue or ActionKind.RegistryDeleteValue && item.Registry is { } reg
            && string.Equals(reg.KeyPath, GpuPreferenceAllowances.Key, StringComparison.OrdinalIgnoreCase))
            return e.ExePaths.Contains(reg.ValueName ?? "", StringComparer.OrdinalIgnoreCase) ? Touched.ThisGameSettings : Touched.Other;
        if (item.Kind == ActionKind.QuarantinePath && item.SourcePath is { } cached
            && e.ShaderCacheFolders.Any(f => IsInside(cached, f)))
            return Touched.ThisGameCache;
        return Touched.Other;
    }

    private static bool SamePath(string a, string b)
    {
        try { return string.Equals(PathUtil.Normalize(a), PathUtil.Normalize(b), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// <summary>Strictly inside the folder (the cache step quarantines the folder's entries, never the folder itself).</summary>
    private static bool IsInside(string path, string folder)
    {
        try { return PathUtil.IsUnder(path, folder) && !SamePath(path, folder); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}
