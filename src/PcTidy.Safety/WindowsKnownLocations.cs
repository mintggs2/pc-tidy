using Microsoft.Win32;
using PcTidy.Core;
using PcTidy.Safety.Detection;
using PcTidy.Safety.Paths;

namespace PcTidy.Safety;

/// <summary>
/// Every location PC Tidy needs, detected on the real PC, read-only. Nothing is hard-coded: folders come from the
/// known-folder API and environment, launchers from their own registry keys and config files. Anything missing
/// (no OneDrive, no Steam, one drive) is an empty list or null, never an error. Results are cached; call
/// <see cref="Refresh"/> after the user installs something or changes settings.
/// </summary>
public sealed class WindowsKnownLocations : IKnownLocations
{
    private readonly AppSettings _settings;
    private volatile Snapshot _current;
    private int _generation;

    public WindowsKnownLocations(AppSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _current = Snapshot.Detect(settings);
    }

    /// <summary>Re-detects everything (cheap enough to call after installs or a settings change).</summary>
    public void Refresh()
    {
        _current = Snapshot.Detect(_settings);
        Interlocked.Increment(ref _generation);
    }

    /// <summary>Bumped on every <see cref="Refresh"/>, so consumers with their own caches (SafetyGuard) know to rebuild.</summary>
    public int Generation => Volatile.Read(ref _generation);

    public string UserProfile => _current.UserProfile;
    public string LocalAppData => _current.LocalAppData;
    public string RoamingAppData => _current.RoamingAppData;
    public string ProgramData => _current.ProgramData;
    public string WindowsDir => _current.WindowsDir;
    public string SystemDrive => _current.SystemDrive;
    public string ProgramFiles => _current.ProgramFiles;
    public string ProgramFilesX86 => _current.ProgramFilesX86;
    public string UserTemp => _current.UserTemp;
    public string WindowsTemp => _current.WindowsTemp;
    public string Desktop => _current.Desktop;
    public string Documents => _current.Documents;
    public string Downloads => _current.Downloads;
    public string Pictures => _current.Pictures;
    public string Videos => _current.Videos;
    public string Music => _current.Music;
    public IReadOnlyList<string> CloudRoots => _current.CloudRoots;
    public string? SteamRoot => _current.SteamRoot;
    public IReadOnlyList<string> SteamLibraries => _current.SteamLibraries;
    public string? EpicManifestsDir => _current.EpicManifestsDir;
    public IReadOnlyList<string> GameInstallDirs => _current.GameInstallDirs;
    public IReadOnlyList<string> AppInstallDirs => _current.AppInstallDirs;
    public string ClaudeDir => _current.ClaudeDir;
    public string CodexDir => _current.CodexDir;
    public string DataRoot => _current.DataRoot;
    public IReadOnlyList<string> FixedDrives => _current.FixedDrives;
    public bool IsSandbox => false;

    /// <summary>One immutable detection result; swapped atomically on refresh.</summary>
    private sealed record Snapshot
    {
        public required string UserProfile { get; init; }
        public required string LocalAppData { get; init; }
        public required string RoamingAppData { get; init; }
        public required string ProgramData { get; init; }
        public required string WindowsDir { get; init; }
        public required string SystemDrive { get; init; }
        public required string ProgramFiles { get; init; }
        public required string ProgramFilesX86 { get; init; }
        public required string UserTemp { get; init; }
        public required string WindowsTemp { get; init; }
        public required string Desktop { get; init; }
        public required string Documents { get; init; }
        public required string Downloads { get; init; }
        public required string Pictures { get; init; }
        public required string Videos { get; init; }
        public required string Music { get; init; }
        public required IReadOnlyList<string> CloudRoots { get; init; }
        public required string? SteamRoot { get; init; }
        public required IReadOnlyList<string> SteamLibraries { get; init; }
        public required string? EpicManifestsDir { get; init; }
        public required IReadOnlyList<string> GameInstallDirs { get; init; }
        public required IReadOnlyList<string> AppInstallDirs { get; init; }
        public required string ClaudeDir { get; init; }
        public required string CodexDir { get; init; }
        public required string DataRoot { get; init; }
        public required IReadOnlyList<string> FixedDrives { get; init; }

        public static Snapshot Detect(AppSettings settings)
        {
            static string Env(Environment.SpecialFolder f) => Environment.GetFolderPath(f, Environment.SpecialFolderOption.DoNotVerify);

            var windows = KnownFolders.Get(KnownFolders.Windows, () => Env(Environment.SpecialFolder.Windows));
            var profile = KnownFolders.Get(KnownFolders.Profile, () => Env(Environment.SpecialFolder.UserProfile));
            var local = KnownFolders.Get(KnownFolders.LocalAppData, () => Env(Environment.SpecialFolder.LocalApplicationData));
            var roaming = KnownFolders.Get(KnownFolders.RoamingAppData, () => Env(Environment.SpecialFolder.ApplicationData));
            var programData = KnownFolders.Get(KnownFolders.ProgramData, () => Env(Environment.SpecialFolder.CommonApplicationData));
            var programFiles = KnownFolders.Get(KnownFolders.ProgramFiles, () => Env(Environment.SpecialFolder.ProgramFiles));
            var programFilesX86 = KnownFolders.Get(KnownFolders.ProgramFilesX86, () => Env(Environment.SpecialFolder.ProgramFilesX86));
            var fixedDrives = DetectionUtil.Safe(FixedDriveRoots);
            var cloud = DetectionUtil.Safe(() => CloudSyncRoots.Detect(local, roaming));

            var userFolders = new
            {
                Desktop = KnownFolders.Get(KnownFolders.Desktop, () => Env(Environment.SpecialFolder.DesktopDirectory)),
                Documents = KnownFolders.Get(KnownFolders.Documents, () => Env(Environment.SpecialFolder.MyDocuments)),
                Downloads = KnownFolders.Get(KnownFolders.Downloads, () => Path.Combine(profile, "Downloads")),
                Pictures = KnownFolders.Get(KnownFolders.Pictures, () => Env(Environment.SpecialFolder.MyPictures)),
                Videos = KnownFolders.Get(KnownFolders.Videos, () => Env(Environment.SpecialFolder.MyVideos)),
                Music = KnownFolders.Get(KnownFolders.Music, () => Env(Environment.SpecialFolder.MyMusic)),
            };
            var userTemp = SafePath.TryNormalize(Path.GetTempPath()) ?? Path.Combine(local, "Temp");

            // Folders that must never be swallowed by a bogus InstallLocation such as "C:\Users\me" or "C:\".
            var userAreas = new List<string> { profile, local, roaming, userTemp, userFolders.Desktop, userFolders.Documents,
                userFolders.Downloads, userFolders.Pictures, userFolders.Videos, userFolders.Music };
            userAreas.AddRange(cloud);
            bool Plausible(string dir) => dir.Length > 3 && !userAreas.Any(a => SafePath.IsSameOrUnder(a, dir));

            var appEntries = DetectionUtil.SafeList(InstalledApps.Read);
            var apps = appEntries.Select(a => a.InstallLocation);
            var steamRoot = DetectSteamRoot();
            var steamLibraries = steamRoot is null ? Array.Empty<string>() : DetectionUtil.Safe(() => SteamConfig.ReadLibraries(steamRoot));
            var epicDir = Path.Combine(programData, "Epic", "EpicGamesLauncher", "Data", "Manifests");
            var epicManifests = DetectionUtil.DirectoryExists(epicDir) ? epicDir : null;

            var games = new List<string>();
            games.AddRange(steamLibraries.Select(l => Path.Combine(l, "steamapps", "common")));
            games.AddRange(DetectionUtil.Safe(() => EpicManifests.Read(epicManifests).Select(e => e.InstallLocation)));
            games.AddRange(DetectionUtil.Safe(LauncherGames.Gog));
            games.AddRange(DetectionUtil.Safe(LauncherGames.Ea));
            games.AddRange(DetectionUtil.Safe(LauncherGames.Ubisoft));
            games.AddRange(DetectionUtil.Safe(() => LauncherGames.BattleNet(appEntries)));
            games.AddRange(DetectionUtil.Safe(() => LauncherGames.Xbox(fixedDrives)));

            return new Snapshot
            {
                UserProfile = profile,
                LocalAppData = local,
                RoamingAppData = roaming,
                ProgramData = programData,
                WindowsDir = windows,
                SystemDrive = Path.GetPathRoot(windows) is { Length: > 0 } root ? root : "C:\\",
                ProgramFiles = programFiles,
                ProgramFilesX86 = programFilesX86,
                UserTemp = userTemp,
                WindowsTemp = Path.Combine(windows, "Temp"),
                Desktop = userFolders.Desktop,
                Documents = userFolders.Documents,
                Downloads = userFolders.Downloads,
                Pictures = userFolders.Pictures,
                Videos = userFolders.Videos,
                Music = userFolders.Music,
                CloudRoots = cloud,
                SteamRoot = steamRoot,
                SteamLibraries = steamLibraries,
                EpicManifestsDir = epicManifests,
                GameInstallDirs = Clean(games, Plausible),
                AppInstallDirs = Clean(apps, Plausible),
                ClaudeDir = ConfigDir("CLAUDE_CONFIG_DIR", profile, ".claude"),
                CodexDir = ConfigDir("CODEX_HOME", profile, ".codex"),
                DataRoot = SafePath.TryNormalize(string.IsNullOrWhiteSpace(settings.DataRoot) ? AppSettings.ResolveDataRoot() : settings.DataRoot)
                           ?? AppSettings.DefaultDataRoot,
                FixedDrives = fixedDrives,
            };
        }

        private static IReadOnlyList<string> Clean(IEnumerable<string> raw, Func<string, bool> plausible)
            => raw.Select(DetectionUtil.CleanPath).OfType<string>().Where(plausible)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        private static IEnumerable<string> FixedDriveRoots()
            => DriveInfo.GetDrives()
                .Where(d => { try { return d.DriveType == DriveType.Fixed && d.IsReady; } catch { return false; } })
                .Select(d => d.RootDirectory.FullName.ToUpperInvariant());

        private static string? DetectSteamRoot()
        {
            using var user = DetectionUtil.OpenKey(RegistryHive.CurrentUser, RegistryView.Default, @"Software\Valve\Steam");
            using var machine = DetectionUtil.OpenKey(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Valve\Steam");
            foreach (var raw in new[] { DetectionUtil.ReadString(user, "SteamPath"), DetectionUtil.ReadString(machine, "InstallPath") })
            {
                var path = DetectionUtil.CleanPath(raw);
                if (path is not null && path.Length > 3 && DetectionUtil.DirectoryExists(path)) return path;
            }
            return null;
        }

        /// <summary>Claude Code and Codex both let users move their config folder with an environment variable.</summary>
        private static string ConfigDir(string envVar, string profile, string defaultName)
        {
            var custom = DetectionUtil.CleanPath(Environment.GetEnvironmentVariable(envVar));
            return custom is { Length: > 3 } ? custom : Path.Combine(profile, defaultName);
        }
    }
}
