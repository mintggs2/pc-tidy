using Microsoft.Win32;

namespace PcTidy.Safety.Detection;

/// <summary>
/// Install folders of games from launchers other than Steam and Epic (GOG, EA, Ubisoft, Battle.net, Xbox).
/// Best effort, read-only: a launcher that isn't installed contributes nothing.
/// </summary>
public static class LauncherGames
{
    private static readonly RegistryView[] BothViews = { RegistryView.Registry32, RegistryView.Registry64 };

    public static IEnumerable<string> Gog()
        => BothViews.SelectMany(v => DetectionUtil.ReadFromSubKeys(RegistryHive.LocalMachine, v, @"SOFTWARE\GOG.com\Games", "path", "PATH"));

    public static IEnumerable<string> Ubisoft()
        => BothViews.SelectMany(v => DetectionUtil.ReadFromSubKeys(RegistryHive.LocalMachine, v, @"SOFTWARE\Ubisoft\Launcher\Installs", "InstallDir"));

    /// <summary>EA app / Origin write one key per game under "Electronic Arts" or "EA Games" with an "Install Dir" value.</summary>
    public static IEnumerable<string> Ea()
        => from v in BothViews
           from parent in new[] { @"SOFTWARE\Electronic Arts", @"SOFTWARE\EA Games" }
           from p in DetectionUtil.ReadFromSubKeys(RegistryHive.LocalMachine, v, parent, "Install Dir", "InstallDir")
           select p;

    /// <summary>Battle.net keeps its own list in a binary database; its games do register Uninstall entries with Blizzard as publisher.</summary>
    public static IEnumerable<string> BattleNet(IEnumerable<InstalledAppEntry> apps)
        => apps.Where(a => a.Publisher?.Contains("Blizzard", StringComparison.OrdinalIgnoreCase) == true).Select(a => a.InstallLocation);

    /// <summary>Xbox app / Game Pass PC games: "XboxGames" at the root of any fixed drive.</summary>
    public static IEnumerable<string> Xbox(IEnumerable<string> fixedDrives)
        => fixedDrives.Select(d => Path.Combine(d, "XboxGames")).Where(DetectionUtil.DirectoryExists);
}
