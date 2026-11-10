using System.Text.RegularExpressions;
using PcTidy.Core;
using PcTidy.Features.Games.Common;

namespace PcTidy.Features.Games.Clips;

/// <summary>A folder a capture app saves clips or screenshots to.</summary>
/// <param name="Recursive">Look in subfolders (most apps make one folder per game).</param>
/// <param name="NameFilter">Only files whose name matches (used when an app saves straight into a library root such as Videos).</param>
/// <param name="ManagedByLauncher">Inside a launcher's own folder (Steam): shown with its size, cleaned in the launcher.</param>
public sealed record ClipSource(string Id, string Name, string Folder, bool Recursive, Regex? NameFilter = null, bool ManagedByLauncher = false);

/// <summary>
/// Where capture apps put clips (research "PC care, games, dev" → Clips and screenshots). Every location is derived from
/// this PC's own folders and the apps' own settings; only folders that exist are returned.
/// </summary>
internal static class ClipSources
{
    /// <summary>
    /// Capture apps' time-stamped default names: OBS "2026-10-02 21-10-00.mkv" (and "Replay …"), NVIDIA
    /// "Game 2026.10.02 - 21.10.00.01.mp4". Used when an app saves straight into a library root, so ordinary videos there are never listed.
    /// </summary>
    private static readonly Regex CaptureTimestampName = new(@"\d{4}-\d{2}-\d{2} \d{2}-\d{2}-\d{2}|\d{4}\.\d{2}\.\d{2} - \d{2}\.\d{2}\.\d{2}", RegexOptions.CultureInvariant);

    public static IReadOnlyList<ClipSource> Detect(GamesContext g)
    {
        var k = g.Locations;
        var list = new List<ClipSource>
        {
            new("xbox", "Xbox Game Bar captures", Path.Combine(k.Videos, "Captures"), true),
            new("snipping", "Screenshots (Snipping Tool, Win + Print Screen)", Path.Combine(k.Pictures, "Screenshots"), true),
            new("snipping-video", "Snipping Tool screen recordings", Path.Combine(k.Videos, "Screen Recordings"), true),
            new("steelseries", "SteelSeries Moments", Path.Combine(k.Videos, "SteelSeries Moments"), true),
            new("medal", "Medal clips", Path.Combine(k.Videos, "Medal"), true),
        };
        foreach (var drive in k.FixedDrives) list.Add(new("medal", "Medal clips", Path.Combine(drive, "Medal"), true));

        var nvidia = NvidiaCapturePath.Decode(SafeBytes(g));
        list.Add(new("nvidia", "NVIDIA clips (ShadowPlay / NVIDIA App)", nvidia ?? Path.Combine(k.Videos, "NVIDIA"), true));

        foreach (var folder in ObsFolders(k))
            list.Add(new("obs", "OBS Studio recordings", folder, true));

        if (k.SteamRoot is { } steam) list.AddRange(SteamFolders(steam));

        return list
            .Select(s => IsLibraryRoot(k, s.Folder) ? s with { Recursive = false, NameFilter = CaptureTimestampName } : s)
            .Where(s => FolderSize.DirectoryExists(s.Folder))
            .GroupBy(s => PathUtil.Normalize(s.Folder), StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToList();
    }

    private static byte[]? SafeBytes(GamesContext g)
    {
        try { return g.Environment.NvidiaCapturePathValue(); } catch (Exception) { return null; }
    }

    /// <summary>Each OBS profile's basic.ini. OBS keeps profiles in %APPDATA%\obs-studio\basic\profiles\&lt;name&gt;.</summary>
    private static IEnumerable<string> ObsFolders(IKnownLocations k)
    {
        var profiles = Path.Combine(k.RoamingAppData, "obs-studio", "basic", "profiles");
        string[] dirs;
        try { dirs = Directory.Exists(profiles) ? Directory.GetDirectories(profiles) : Array.Empty<string>(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var dir in dirs)
        {
            string text;
            try
            {
                var ini = Path.Combine(dir, "basic.ini");
                if (!File.Exists(ini)) continue;
                text = File.ReadAllText(ini);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var f in ObsConfig.RecordingFolders(text)) yield return f;
        }
    }

    /// <summary>Steam screenshots (userdata\&lt;id&gt;\760\remote\&lt;appid&gt;\screenshots) and Game Recording clips (userdata\&lt;id&gt;\gamerecordings).</summary>
    private static IEnumerable<ClipSource> SteamFolders(string steamRoot)
    {
        var userdata = Path.Combine(steamRoot, "userdata");
        string[] users;
        try { users = Directory.Exists(userdata) ? Directory.GetDirectories(userdata) : Array.Empty<string>(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var user in users)
        {
            var remote = Path.Combine(user, "760", "remote");
            string[] apps;
            try { apps = Directory.Exists(remote) ? Directory.GetDirectories(remote) : Array.Empty<string>(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { apps = Array.Empty<string>(); }
            foreach (var app in apps)
                yield return new("steam-screenshots", "Steam screenshots", Path.Combine(app, "screenshots"), false, ManagedByLauncher: true);
            yield return new("steam-recordings", "Steam game recordings", Path.Combine(user, "gamerecordings"), true, ManagedByLauncher: true);
        }
    }

    /// <summary>An app saving straight into Videos/Pictures/Desktop/Documents/Downloads or the profile: never walk the whole library.</summary>
    private static bool IsLibraryRoot(IKnownLocations k, string folder)
        => new[] { k.Videos, k.Pictures, k.Desktop, k.Documents, k.Downloads, k.UserProfile, k.Music }
            .Concat(k.FixedDrives)
            .Any(root => string.Equals(PathUtil.Normalize(root), PathUtil.Normalize(folder), StringComparison.OrdinalIgnoreCase));

    public static readonly IReadOnlySet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".mov", ".webm", ".flv", ".ts", ".avi", ".m4v" };

    public static readonly IReadOnlySet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".jxr", ".gif" };

    public static bool IsMedia(string name) => VideoExtensions.Contains(Path.GetExtension(name)) || ImageExtensions.Contains(Path.GetExtension(name));
}
