namespace PcTidy.Safety.Detection;

/// <summary>One installed Steam game, read from its appmanifest_&lt;id&gt;.acf.</summary>
public sealed record SteamApp(
    string AppId,
    string Name,
    /// <summary>Full path of the game folder (library\steamapps\common\installdir).</summary>
    string InstallPath,
    string LibraryPath,
    long SizeOnDisk,
    DateTime? LastPlayedUtc,
    int StateFlags)
{
    /// <summary>StateFlags bit 4 = fully installed (other bits: update running, paused, etc.).</summary>
    public bool IsFullyInstalled => (StateFlags & 4) != 0;
}

/// <summary>Read-only reader for Steam's library list and app manifests.</summary>
public static class SteamConfig
{
    /// <summary>
    /// Library folders listed in libraryfolders.vdf (new format: "0" { "path" "..." }; old format: "1" "D:\\Lib").
    /// The Steam folder itself is always a library. Only folders that exist are returned.
    /// </summary>
    public static IReadOnlyList<string> ReadLibraries(string steamRoot)
    {
        var result = new List<string> { steamRoot };
        foreach (var file in new[] { Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"), Path.Combine(steamRoot, "config", "libraryfolders.vdf") })
        {
            var root = Vdf.TryLoad(file);
            var list = root?["libraryfolders"] ?? root?["LibraryFolders"];
            if (list is null) continue;
            foreach (var (key, node) in list.Children)
            {
                if (!int.TryParse(key, out _)) continue; // skip "contentstatsid" and similar
                var path = node.Value ?? node.GetString("path");
                if (!string.IsNullOrWhiteSpace(path)) result.Add(path);
            }
        }
        return result
            .Select(DetectionUtil.CleanPath)
            .OfType<string>()
            .Where(DetectionUtil.DirectoryExists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Every appmanifest_*.acf in a library. Damaged manifests are skipped.</summary>
    public static IEnumerable<SteamApp> ReadApps(string libraryPath)
    {
        var steamapps = Path.Combine(libraryPath, "steamapps");
        string[] manifests;
        try { manifests = Directory.Exists(steamapps) ? Directory.GetFiles(steamapps, "appmanifest_*.acf") : Array.Empty<string>(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }

        foreach (var file in manifests)
        {
            var app = Vdf.TryLoad(file)?["AppState"];
            var installDir = app?.GetString("installdir");
            if (app is null || installDir is null || !DetectionUtil.IsPlainFolderName(installDir)) continue;
            var lastPlayed = app.GetLong("LastPlayed");
            yield return new SteamApp(
                AppId: app.GetString("appid") ?? Path.GetFileNameWithoutExtension(file).Replace("appmanifest_", ""),
                Name: app.GetString("name") ?? installDir,
                InstallPath: Path.Combine(steamapps, "common", installDir),
                LibraryPath: libraryPath,
                SizeOnDisk: app.GetLong("SizeOnDisk") ?? 0,
                LastPlayedUtc: lastPlayed is > 0 ? DateTimeOffset.FromUnixTimeSeconds(lastPlayed.Value).UtcDateTime : null,
                StateFlags: (int)(app.GetLong("StateFlags") ?? 0));
        }
    }
}
