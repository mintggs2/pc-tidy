using PcTidy.Catalog.Games;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Games.Common;
using PcTidy.Features.Games.Environment;
using PcTidy.Features.Games.Library;

namespace PcTidy.Features.Games.Optimize;

/// <summary>
/// Per-game leftovers of "FPS" guides (Community tweaks §9.7) that PC Tidy explains but never changes:
///  - a "Run this program as an administrator" tick on the game's exe (the game then runs with full admin rights);
///  - a read-only settings file (the game can't save anything you change in its menus).
/// Both are information rows with the steps to undo them yourself: the compatibility flags' storage isn't documented by
/// Microsoft, and clearing a file attribute isn't something a Games plan does.
/// </summary>
internal static class GameLeftoverChecks
{
    /// <summary>Microsoft's own steps for the Compatibility tab's administrator tick.</summary>
    internal const string RunAsAdminSource = "https://learn.microsoft.com/previous-versions/technet-magazine/ff431742(v=msdn.10)";

    /// <summary>The read-only file attribute (attrib reference).</summary>
    internal const string ReadOnlySource = "https://learn.microsoft.com/windows-server/administration/windows-commands/attrib";

    /// <summary>Config formats checked for the read-only attribute (the same text formats the optimizer may edit).</summary>
    private static readonly string[] ConfigPatterns = { "*.ini", "*.cfg", "*.json", "*.xml", "*.txt" };

    private const int MaxFilesPerFolder = 200;

    public static IEnumerable<ScanItem> For(InstalledGame game, GameLayout layout, GameEngine engine, IKnownLocations k, IReadOnlyList<CompatibilitySetting> compat)
    {
        foreach (var item in RunAsAdmin(game, compat)) yield return item;
        foreach (var item in ReadOnlyConfigs(game, layout, engine, k)) yield return item;
    }

    // ---------------- "Run this program as an administrator" ----------------

    internal static IEnumerable<ScanItem> RunAsAdmin(InstalledGame game, IReadOnlyList<CompatibilitySetting> compat)
    {
        foreach (var setting in compat)
        {
            if (!setting.RunsAsAdmin || !IsUnder(setting.ExePath, game.InstallDir)) continue;
            var exe = Path.GetFileName(setting.ExePath);
            yield return new ScanItem
            {
                Id = $"games.optimize.{game.Key}.runasadmin.{setting.ExePath.ToLowerInvariant()}{(setting.AllUsers ? ".allusers" : "")}",
                Title = $"{exe} is set to run as administrator",
                Detail = setting.ExePath,
                Reason = "Guides tick \"Run this program as an administrator\" for FPS. It doesn't make the game faster, but the game (and any mod or overlay it loads) then runs with full admin rights, "
                         + "and some launchers and anti-cheats stop working properly.",
                Badge = Badge.Check,
                OwnerApp = game.Name,
                CurrentState = setting.AllUsers ? "On, for all users" : "On",
                Details = new Dictionary<string, string>
                {
                    ["How to undo"] = $"Right-click {exe} → Properties → Compatibility, untick \"Run this program as an administrator\""
                                      + (setting.AllUsers ? " (it was set under \"Change settings for all users\", so open that button first)." : "."),
                    ["Verdict"] = "Placebo: no speed gain, and a security cost",
                    ["Source"] = RunAsAdminSource,
                    ["Good to know"] = "PC Tidy only explains this one and changes nothing.",
                },
            };
        }
    }

    // ---------------- read-only settings files ----------------

    /// <summary>Unreal games keep user settings in %LOCALAPPDATA%\&lt;Project&gt;\Saved\Config\Windows[Client|NoEditor] (the folder the optimizer edits).</summary>
    internal static IEnumerable<ScanItem> ReadOnlyConfigs(InstalledGame game, GameLayout layout, GameEngine engine, IKnownLocations k)
    {
        if (engine != GameEngine.Unreal) yield break;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in layout.UnrealProjects)
            foreach (var platform in new[] { "WindowsClient", "Windows", "WindowsNoEditor" })
            {
                var folder = Path.Combine(k.LocalAppData, project, "Saved", "Config", platform);
                foreach (var file in ReadOnlyFiles(folder))
                {
                    if (!seen.Add(file)) continue;
                    yield return ReadOnlyRow(game, file);
                }
            }
    }

    internal static ScanItem ReadOnlyRow(InstalledGame game, string file) => new()
    {
        Id = $"games.optimize.{game.Key}.readonly.{file.ToLowerInvariant()}",
        Title = $"{Path.GetFileName(file)} is read-only",
        Detail = file,
        Reason = $"Guides make a settings file read-only so {game.Name} can't change a tweak back. But the game then can't save anything you change in its own menus either.",
        Badge = Badge.Check,
        OwnerApp = game.Name,
        CurrentState = "Read-only",
        Details = new Dictionary<string, string>
        {
            ["How to undo"] = "Right-click the file → Properties, untick Read-only and choose OK. If you made it read-only on purpose, leave it.",
            ["Verdict"] = "Situational: only useful if you know which setting the game keeps resetting",
            ["Source"] = ReadOnlySource,
            ["Good to know"] = "PC Tidy only explains this one and changes nothing. While the file is read-only, PC Tidy doesn't edit it either.",
        },
    };

    /// <summary>Read-only config files directly in <paramref name="folder"/> (no subfolders, never through links). Missing folder = none.</summary>
    internal static IEnumerable<string> ReadOnlyFiles(string folder)
    {
        var found = new List<string>();
        try
        {
            if (!FolderSize.DirectoryExists(folder)) return found;
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) return found;
            foreach (var pattern in ConfigPatterns)
                foreach (var file in Directory.EnumerateFiles(folder, pattern, SearchOption.TopDirectoryOnly).Take(MaxFilesPerFolder))
                    if (IsReadOnly(file)) found.Add(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // An unreadable folder lists nothing.
        }
        return found;
    }

    internal static bool IsReadOnly(string file)
    {
        try
        {
            var a = File.GetAttributes(file);
            return (a & FileAttributes.ReadOnly) != 0 && (a & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static bool IsUnder(string path, string folder)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(folder)) return false;
        try { return PathUtil.IsUnder(path, folder); }
        catch (ArgumentException) { return false; }
    }
}
