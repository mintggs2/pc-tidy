using Microsoft.Win32;
using PcTidy.Catalog.Games;
using PcTidy.Safety.Detection;

namespace PcTidy.Features.Games.Environment;

/// <summary>
/// Real-PC reads: launcher registry keys (read-only handles only), Windows' PackageManager for Store games, and NVIDIA's
/// capture folder setting. Any launcher that isn't installed simply contributes nothing; nothing here ever throws.
/// </summary>
public sealed class WindowsGamesEnvironment : IGamesEnvironment
{
    private static readonly RegistryView[] BothViews = { RegistryView.Registry32, RegistryView.Registry64 };

    /// <summary>EA/Origin keys that belong to the launcher itself, not to a game.</summary>
    private static readonly string[] EaLauncherKeys = { "EA Desktop", "EA Core", "EA Services", "Origin", "EA app", "EABackgroundService" };

    public IReadOnlyList<RegistryGame> RegistryGames()
    {
        var list = new List<RegistryGame>();
        list.AddRange(Safe(Gog));
        list.AddRange(Safe(Ea));
        list.AddRange(Safe(Ubisoft));
        list.AddRange(Safe(BattleNet));
        return list
            .Where(g => DirectoryExists(g.InstallDir))
            .GroupBy(g => g.InstallDir.TrimEnd('\\'), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    private static IEnumerable<RegistryGame> Gog()
    {
        foreach (var view in BothViews)
            foreach (var (name, key) in SubKeys(RegistryHive.LocalMachine, view, @"SOFTWARE\GOG.com\Games"))
                using (key)
                {
                    var path = Read(key, "path") ?? Read(key, "PATH");
                    if (path is null) continue;
                    yield return new RegistryGame(Launcher.Gog, Read(key, "gameName") ?? Path.GetFileName(path.TrimEnd('\\')), path,
                        Read(key, "gameID") ?? name);
                }
    }

    private static IEnumerable<RegistryGame> Ea()
    {
        foreach (var view in BothViews)
            foreach (var parent in new[] { @"SOFTWARE\Electronic Arts", @"SOFTWARE\EA Games" })
                foreach (var (name, key) in SubKeys(RegistryHive.LocalMachine, view, parent))
                    using (key)
                    {
                        if (EaLauncherKeys.Any(k => k.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                        var dir = Read(key, "Install Dir") ?? Read(key, "InstallDir");
                        if (dir is not null) yield return new RegistryGame(Launcher.Ea, Read(key, "DisplayName") ?? name, dir, null);
                    }
    }

    private static IEnumerable<RegistryGame> Ubisoft()
    {
        foreach (var view in BothViews)
            foreach (var (name, key) in SubKeys(RegistryHive.LocalMachine, view, @"SOFTWARE\Ubisoft\Launcher\Installs"))
                using (key)
                {
                    var dir = Read(key, "InstallDir");
                    if (dir is not null) yield return new RegistryGame(Launcher.Ubisoft, Path.GetFileName(dir.Replace('/', '\\').TrimEnd('\\')), dir.Replace('/', '\\'), name);
                }
    }

    /// <summary>Battle.net keeps its list in a binary database, but each game registers an Uninstall entry with Blizzard as publisher.</summary>
    private static IEnumerable<RegistryGame> BattleNet()
        => InstalledApps.Read()
            .Where(a => a.Publisher?.Contains("Blizzard", StringComparison.OrdinalIgnoreCase) == true
                        && !a.DisplayName.StartsWith("Battle.net", StringComparison.OrdinalIgnoreCase))
            .Select(a => new RegistryGame(Launcher.BattleNet, a.DisplayName, a.InstallLocation, null));

    public IReadOnlyList<StoreGame> StoreGames()
    {
        var list = new List<StoreGame>();
        try
        {
            var manager = new Windows.Management.Deployment.PackageManager();
            foreach (var package in manager.FindPackagesForUser(string.Empty))
            {
                try
                {
                    if (package.IsFramework || package.IsResourcePackage) continue;
                    var path = package.InstalledPath;
                    // GDK (Xbox app / Game Pass) games carry MicrosoftGame.config; ordinary Store apps don't.
                    if (string.IsNullOrEmpty(path) || !File.Exists(Path.Combine(path, "MicrosoftGame.config"))) continue;
                    list.Add(new StoreGame(package.DisplayName, path, package.Id.FamilyName));
                }
                catch (Exception) { /* one unreadable package never hides the others */ }
            }
        }
        catch (Exception) { /* PackageManager unavailable (e.g. Windows Sandbox / service off): no Store games */ }
        return list;
    }

    public byte[]? NvidiaCapturePathValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\NVIDIA Corporation\Global\ShadowPlay\NVSPCAPS", writable: false);
            return key?.GetValue("DefaultPathW") as byte[];
        }
        catch (Exception) { return null; }
    }

    private const string CompatibilityLayers = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
    private const int MaxCompatibilitySettings = 2_000;

    /// <summary>Compatibility-tab settings: value name = exe path, data = the flags text. Read-only handles only.</summary>
    public IReadOnlyList<CompatibilitySetting> CompatibilitySettings()
    {
        var list = new List<CompatibilitySetting>();
        foreach (var (hive, allUsers) in new[] { (RegistryHive.CurrentUser, false), (RegistryHive.LocalMachine, true) })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var key = root.OpenSubKey(CompatibilityLayers, writable: false);
                if (key is null) continue;
                foreach (var name in key.GetValueNames())
                {
                    if (list.Count >= MaxCompatibilitySettings) break;
                    if (name.Length > 0 && key.GetValue(name) is string flags) list.Add(new CompatibilitySetting(name, flags, allUsers));
                }
            }
            catch (Exception) { /* unreadable: nothing listed from this hive */ }
        }
        return list;
    }

    /// <summary>Per-app GPU preferences of the current user (value name = exe path). Read-only handle only.</summary>
    public IReadOnlyDictionary<string, string> GpuPreferences()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var key = root.OpenSubKey(GpuPreferenceAllowances.Key, writable: false);
            if (key is null) return map;
            foreach (var name in key.GetValueNames())
            {
                if (map.Count >= MaxCompatibilitySettings) break;
                if (name.Length > 0 && key.GetValue(name) is string data) map[name] = data;
            }
        }
        catch (Exception) { /* unreadable: nothing known */ }
        return map;
    }

    // ---------------- registry helpers (read-only handles) ----------------

    private static IEnumerable<(string Name, RegistryKey Key)> SubKeys(RegistryHive hive, RegistryView view, string path)
    {
        RegistryKey? parent;
        string[] names;
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            parent = root.OpenSubKey(path, writable: false);
            names = parent?.GetSubKeyNames() ?? Array.Empty<string>();
        }
        catch (Exception) { yield break; }
        using (parent)
        {
            foreach (var name in names)
            {
                RegistryKey? sub = null;
                try { sub = parent!.OpenSubKey(name, writable: false); } catch (Exception) { }
                if (sub is not null) yield return (name, sub);
            }
        }
    }

    private static string? Read(RegistryKey key, string name)
    {
        try { return key.GetValue(name) is string s && !string.IsNullOrWhiteSpace(s) ? s.Trim().Trim('"') : null; }
        catch (Exception) { return null; }
    }

    private static IEnumerable<RegistryGame> Safe(Func<IEnumerable<RegistryGame>> source)
    {
        try { return source().ToList(); }
        catch (Exception) { return Array.Empty<RegistryGame>(); }
    }

    private static bool DirectoryExists(string path)
    {
        try { return Directory.Exists(path); } catch { return false; }
    }
}
