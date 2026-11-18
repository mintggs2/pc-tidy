using PcTidy.Core;

namespace PcTidy.Features.Organize.Planning;

/// <summary>
/// Folders Organize never sorts or sorts into (3.3, from the v3.3 design review S1/S2/S6/S17). Pure path logic.
///  • Off limits, whatever the user picks: AppData (Roaming, Local, LocalLow), ProgramData, Saved Games, Program Files,
///    Windows, PC Tidy's own data, and game or launcher folders (Steam libraries, known game installs, Epic, Battle.net,
///    Plutonium's storage…).
///  • App-owned folders inside the user's own folders: My Games, Call of Duty, Sims 4 mods, any "mods" folder,
///    PowerShell profiles, Office templates, clip folders of NVIDIA, Game Bar and Medal…
/// </summary>
public static class SafetyFolders
{
    /// <summary>A path part with one of these names marks a game or launcher folder (anywhere in the path).</summary>
    private static readonly string[] LauncherSegments =
    {
        "steamapps", "Steam", "Epic Games", "Battle.net", "Blizzard App", "Riot Games", "XboxGames", "WindowsApps", "Plutonium",
        "GOG Galaxy", "GOG Games", "Ubisoft Game Launcher", "EA Games", "Origin Games", "Rockstar Games Launcher",
    };

    /// <summary>Folders inside Documents / Desktop / Videos… that belong to an app or game (exact names).</summary>
    private static readonly string[] AppOwnedExact =
    {
        "My Games", "Saved Games", "Electronic Arts", "Rockstar Games", "The Sims 4", "Paradox Interactive", "Euro Truck Simulator 2",
        "American Truck Simulator", "Klei", "mods", "Mods", "WindowsPowerShell", "PowerShell", "Custom Office Templates",
        "OneNote Notebooks", "Outlook Files", "My Web Sites", "Zoom", "Captures", "NVIDIA", "Medal", "Overwolf", "iw4x", "t6", "storage",
        "Fiddler2", "Arduino", "Image-Line", "FL Studio", "Ableton", "Native Instruments", "Adobe",
    };

    /// <summary>Folder-name starts that belong to an app or game ("Call of Duty Black Ops", "Visual Studio 2022"…).</summary>
    private static readonly string[] AppOwnedPrefixes =
    {
        "Call of Duty", "Black Ops", "Battlefield", "FarmingSimulator", "Farming Simulator", "Visual Studio", "SQL Server Management Studio",
        "Assassin's Creed", "Grand Theft Auto", "Red Dead Redemption", "Cities Skylines", "Football Manager",
    };

    public static bool IsAppOwnedName(string name)
        => AppOwnedExact.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase))
           || AppOwnedPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>The user's own top folders (Desktop, Documents, Downloads, Pictures, Videos, Music).</summary>
    public static IReadOnlyList<string> UserRoots(IKnownLocations k)
        => new[] { k.Desktop, k.Documents, k.Downloads, k.Pictures, k.Videos, k.Music }.Where(p => !string.IsNullOrEmpty(p)).Select(PathUtil.Normalize).ToList();

    /// <summary>
    /// Why PC Tidy never sorts in or into <paramref name="path"/>, or null. A path through a junction or symbolic link
    /// ("Desktop\Games" pointing into a Steam library) is checked where it really leads too (3.3 code review L1).
    /// </summary>
    public static string? ForbiddenReason(string path, IKnownLocations? k)
        => ForbiddenReasonAt(path, k) ?? (ThroughLinks(path) is { } real ? ForbiddenReasonAt(real, k) : null);

    /// <summary>The first folder in <paramref name="path"/> (itself included) that is a junction or symbolic link, or null.</summary>
    public static string? LinkOnTheWay(string path)
    {
        try
        {
            var p = PathUtil.Normalize(path);
            var chain = new List<string>();
            for (var c = p; !string.IsNullOrEmpty(c); c = Path.GetDirectoryName(c)) chain.Insert(0, c);
            foreach (var c in chain)
            {
                var info = new DirectoryInfo(c);
                if (!info.Exists) return null;
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0 && info.LinkTarget is not null) return c;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return null;
    }

    /// <summary>
    /// Where <paramref name="path"/> really leads, as Windows resolves it: junctions, symbolic links, subst drives, short
    /// 8.3 names and mount points (3.3 code review L1/N12). Null when it's the same path, or when it can't tell.
    /// The part of the path that doesn't exist yet is kept as given.
    /// </summary>
    public static string? ThroughLinks(string path)
    {
        try
        {
            var p = PathUtil.Normalize(path);
            var existing = p;
            var rest = new List<string>();
            while (!string.IsNullOrEmpty(existing) && !Directory.Exists(existing))
            {
                rest.Insert(0, Path.GetFileName(existing));
                existing = Path.GetDirectoryName(existing);
            }
            if (string.IsNullOrEmpty(existing) || PcTidy.Features.Organize.Detection.FileIdentity.FinalPath(existing) is not { } final) return null;
            var real = PathUtil.Normalize(rest.Count == 0 ? final : Path.Combine(rest.Prepend(final).ToArray()));
            return real.Equals(p, StringComparison.OrdinalIgnoreCase) ? null : real;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    /// <summary><see cref="AppOwnedPart"/> of the path as given, or of where it really leads.</summary>
    public static string? AppOwnedPartAnywhere(string path, IKnownLocations? k)
        => AppOwnedPart(path, k) ?? (ThroughLinks(path) is { } real ? AppOwnedPart(real, k) : null);

    private static string? ForbiddenReasonAt(string path, IKnownLocations? k)
    {
        var p = PathUtil.Normalize(path);
        if (k is not null)
        {
            var user = UserRoots(k);
            string? Under(string? dir, string what)
            {
                if (string.IsNullOrEmpty(dir)) return null;
                var d = PathUtil.Normalize(dir);
                // A folder that holds one of the user's own folders (the profile, a drive) is never "an app folder".
                if (user.Any(u => PathUtil.IsUnder(u, d))) return null;
                return PathUtil.IsUnder(p, d) ? what : null;
            }
            var reason =
                Under(k.RoamingAppData, "AppData") ?? Under(k.LocalAppData, "AppData")
                ?? Under(Path.Combine(k.UserProfile, "AppData"), "AppData")
                ?? Under(k.ProgramData, "ProgramData") ?? Under(Path.Combine(k.UserProfile, "Saved Games"), "Saved Games")
                ?? Under(k.ProgramFiles, "Program Files") ?? Under(k.ProgramFilesX86, "Program Files") ?? Under(k.WindowsDir, "Windows")
                ?? Under(k.DataRoot, "PC Tidy's own data folder")
                ?? Under(k.SteamRoot, "Steam") ?? k.SteamLibraries.Select(s => Under(s, "a Steam library")).FirstOrDefault(r => r is not null)
                ?? k.GameInstallDirs.Select(g => Under(g, "a game's install folder")).FirstOrDefault(r => r is not null)
                ?? k.AppInstallDirs.Select(a => Under(a, "an app's install folder")).FirstOrDefault(r => r is not null);
            if (reason is not null) return $"It's inside {reason}, which programs and games run from. PC Tidy never sorts there.";
        }
        var segments = p.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        foreach (var s in segments)
            if (LauncherSegments.Any(l => l.Equals(s, StringComparison.OrdinalIgnoreCase)))
                return $"It's inside \"{s}\", a game or launcher folder. PC Tidy never sorts there.";
        return null;
    }

    /// <summary>The first app-owned folder name between the user's top folder and <paramref name="path"/> (inclusive), or null.</summary>
    public static string? AppOwnedPart(string path, IKnownLocations? k)
    {
        var p = PathUtil.Normalize(path);
        var top = k is null ? null : UserRoots(k).Where(u => PathUtil.IsUnder(p, u)).OrderByDescending(u => u.Length).FirstOrDefault();
        var rest = top is null ? p : Path.GetRelativePath(top, p);
        if (rest == ".") return null;
        foreach (var s in rest.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            if (IsAppOwnedName(s)) return s;
        return null;
    }
}
