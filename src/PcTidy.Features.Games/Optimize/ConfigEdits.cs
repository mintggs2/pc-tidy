using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Games.Common;

namespace PcTidy.Features.Games.Optimize;

/// <summary>
/// Args the Games section puts on its <see cref="ActionKind.EditTextFile"/> items. "content" is the full new file (what
/// the engine's handler writes, after backing up the original). The "games.*" keys describe the single change, so
/// several ticked changes to the same file can be merged into one edit right before running (<see cref="EditMerger"/>).
/// </summary>
public static class EditArgs
{
    public const string Content = "content";
    public const string Kind = "games.editKind";
    public const string KindLaunchOptions = "steam-launch-options";
    public const string KindIni = "ini";
    public const string AppId = "games.appId";
    public const string AddOption = "games.addOption";
    public const string RemoveOption = "games.removeOption";
    public const string RemoveTakesValue = "games.removeTakesValue";
    public const string IniSection = "games.iniSection";
    public const string IniKey = "games.iniKey";
    public const string IniValue = "games.iniValue";
}

/// <summary>
/// Plans config edits: computes the complete new text of the game's own config file or of Steam's localconfig.vdf.
/// Refuses (returns null) for anything that isn't a plain config file outside the game's install folder, so a plan can
/// never touch game binaries or packaged data.
/// </summary>
internal static class ConfigEditPlanner
{
    /// <summary>Text config formats the Games section may edit (a subset of what the safety rules allow).</summary>
    private static readonly HashSet<string> ConfigExtensions = new(StringComparer.OrdinalIgnoreCase) { ".ini", ".vdf", ".cfg", ".txt", ".json", ".xml" };

    public static PlanItem? AddLaunchOption(SteamUserConfig cfg, string steamRoot, string appId, string option, string game, IReadOnlyList<string> closeFirst)
    {
        var current = cfg.LaunchOptions(appId) ?? "";
        if (LaunchOptionLine.Contains(current, option)) return null;
        var updated = LaunchOptionLine.Add(current, option);
        return LaunchOptionsItem(cfg, steamRoot, appId, updated, closeFirst, $"Add \"{option}\" to {game}'s Steam launch options",
            new() { [EditArgs.AddOption] = option });
    }

    public static PlanItem? RemoveLaunchOption(SteamUserConfig cfg, string steamRoot, string appId, string token, bool takesValue, string game, IReadOnlyList<string> closeFirst)
    {
        var current = cfg.LaunchOptions(appId);
        if (current is null) return null;
        var updated = LaunchOptionLine.Remove(current, token, takesValue);
        if (updated == current) return null;
        return LaunchOptionsItem(cfg, steamRoot, appId, updated, closeFirst, $"Remove \"{token}\" from {game}'s Steam launch options",
            new() { [EditArgs.RemoveOption] = token, [EditArgs.RemoveTakesValue] = takesValue ? "true" : "false" });
    }

    private static PlanItem? LaunchOptionsItem(SteamUserConfig cfg, string steamRoot, string appId, string newOptions,
        IReadOnlyList<string> closeFirst, string description, Dictionary<string, string> delta)
    {
        if (!IsSteamLocalConfig(cfg.Path, steamRoot)) return null;
        var content = VdfText.WithValue(cfg.Text, SteamUserConfig.AppPath(appId), "LaunchOptions", newOptions);
        if (content is null) return null;
        delta[EditArgs.Kind] = EditArgs.KindLaunchOptions;
        delta[EditArgs.AppId] = appId;
        delta[EditArgs.Content] = content;
        return Edit(cfg.Path, cfg.LastWriteUtc, description, delta,
            closeFirst.Append("steam").Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    public static PlanItem? SetIni(string file, string text, DateTime lastWriteUtc, string section, string key, string value,
        string installDir, string description, IReadOnlyList<string> closeFirst)
    {
        if (!IsEditableConfig(file) || PathUtil.IsUnder(file, installDir)) return null;
        if (string.Equals(IniText.Get(text, section, key), value, StringComparison.OrdinalIgnoreCase)) return null;
        var content = IniText.Set(text, section, key, value);
        return Edit(file, lastWriteUtc, description, new()
        {
            [EditArgs.Kind] = EditArgs.KindIni,
            [EditArgs.IniSection] = section,
            [EditArgs.IniKey] = key,
            [EditArgs.IniValue] = value,
            [EditArgs.Content] = content,
        }, closeFirst);
    }

    private static PlanItem Edit(string path, DateTime lastWriteUtc, string description, Dictionary<string, string> args, IReadOnlyList<string> closeFirst)
        => new()
        {
            Kind = ActionKind.EditTextFile,
            FeatureId = GamesContext.FeatureId,
            Description = description,
            SourcePath = path,
            // A config edit frees nothing; the timestamp makes the engine skip the step if the game rewrote the file since the scan.
            ExpectedSize = 0,
            ExpectedLastWriteUtc = lastWriteUtc,
            Args = args,
            RequiresClosedApps = closeFirst,
        };

    public static bool IsEditableConfig(string path) => ConfigExtensions.Contains(Path.GetExtension(path));

    /// <summary>Exactly &lt;Steam&gt;\userdata\&lt;account&gt;\config\localconfig.vdf.</summary>
    public static bool IsSteamLocalConfig(string path, string steamRoot)
    {
        var p = PathUtil.Normalize(path);
        var userdata = PathUtil.Normalize(Path.Combine(steamRoot, "userdata"));
        if (!Path.GetFileName(p).Equals("localconfig.vdf", StringComparison.OrdinalIgnoreCase)) return false;
        var config = Path.GetDirectoryName(p);
        var account = config is null ? null : Path.GetDirectoryName(config);
        return config is not null && account is not null
               && Path.GetFileName(config).Equals("config", StringComparison.OrdinalIgnoreCase)
               && string.Equals(Path.GetDirectoryName(account), userdata, StringComparison.OrdinalIgnoreCase)
               && Path.GetFileName(account).All(char.IsAsciiDigit);
    }
}

/// <summary>
/// Several ticked edits of the same file were each planned from the scan-time text, so running them one after another
/// would keep only the last. Right before the plan runs, this folds them into one edit per file computed from the file
/// as it is now, and refuses if the file changed since the scan (the game or Steam rewrote it).
/// </summary>
public static class EditMerger
{
    public sealed class FileChangedException : Exception
    {
        public FileChangedException(string path) : base($"\"{Path.GetFileName(path)}\" changed since the scan (the game or Steam saved its settings). Scan again, then apply.") { }
    }

    public static IReadOnlyList<PlanItem> Merge(IReadOnlyList<PlanItem> items, Func<string, (string Text, DateTime LastWriteUtc)?>? read = null)
    {
        read ??= ReadFile;
        var mergeable = items.Where(IsGamesEdit).GroupBy(i => PathUtil.Normalize(i.SourcePath!), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        if (mergeable.Count == 0) return items;

        var result = new List<PlanItem>();
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (!IsGamesEdit(item) || !mergeable.TryGetValue(PathUtil.Normalize(item.SourcePath!), out var group)) { result.Add(item); continue; }
            var key = PathUtil.Normalize(item.SourcePath!);
            if (!emitted.Add(key)) continue;
            result.Add(Combine(group, read));
        }
        return result;
    }

    private static bool IsGamesEdit(PlanItem i)
        => i.Kind == ActionKind.EditTextFile && i.SourcePath is not null && i.Args?.ContainsKey(EditArgs.Kind) == true;

    private static PlanItem Combine(List<PlanItem> group, Func<string, (string Text, DateTime LastWriteUtc)?> read)
    {
        var first = group[0];
        var now = read(first.SourcePath!) ?? throw new FileChangedException(first.SourcePath!);
        if (group.Any(i => i.ExpectedLastWriteUtc is { } t && t != now.LastWriteUtc)) throw new FileChangedException(first.SourcePath!);

        var text = now.Text;
        foreach (var item in group) text = Apply(text, item.Args!) ?? throw new FileChangedException(first.SourcePath!);

        var args = new Dictionary<string, string> { [EditArgs.Kind] = first.Args![EditArgs.Kind], [EditArgs.Content] = text };
        return first with
        {
            Id = Guid.NewGuid().ToString("N"),
            Description = string.Join("; ", group.Select(i => i.Description)),
            Args = args,
            RequiresClosedApps = group.SelectMany(i => i.RequiresClosedApps ?? Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    /// <summary>Re-applies one recorded change to the current text.</summary>
    internal static string? Apply(string text, IReadOnlyDictionary<string, string> a)
    {
        switch (a.GetValueOrDefault(EditArgs.Kind))
        {
            case EditArgs.KindLaunchOptions:
                var path = SteamUserConfig.AppPath(a[EditArgs.AppId]);
                var current = VdfText.GetValue(text, path, "LaunchOptions") ?? "";
                var updated = a.TryGetValue(EditArgs.AddOption, out var add) ? LaunchOptionLine.Add(current, add)
                    : a.TryGetValue(EditArgs.RemoveOption, out var remove) ? LaunchOptionLine.Remove(current, remove, a.GetValueOrDefault(EditArgs.RemoveTakesValue) == "true")
                    : current;
                return updated == current ? text : VdfText.WithValue(text, path, "LaunchOptions", updated);
            case EditArgs.KindIni:
                return IniText.Set(text, a[EditArgs.IniSection], a[EditArgs.IniKey], a[EditArgs.IniValue]);
            default:
                return null;
        }
    }

    private static (string, DateTime)? ReadFile(string path)
    {
        try { return (File.ReadAllText(path), File.GetLastWriteTimeUtc(path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
