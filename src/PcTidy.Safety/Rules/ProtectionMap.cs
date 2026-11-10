using Microsoft.Win32;
using PcTidy.Core;
using PcTidy.Safety.Paths;

namespace PcTidy.Safety.Rules;

public enum AreaScope
{
    /// <summary>The folder and everything inside it.</summary>
    Subtree,
    /// <summary>Only the folder itself (its contents follow the normal rules).</summary>
    ExactOnly,
}

/// <summary>One "never touched" place, with the plain-English reason shown to the user.</summary>
/// <param name="Label">Short noun phrase for "This folder contains {Label}".</param>
/// <param name="CarveOutsMayOpen">True if a cleanup carve-out may reach inside (Windows, Program Files, ProgramData, app folders, Steam).</param>
public sealed record ProtectedArea(string Path, AreaScope Scope, string Label, string Reason, bool CarveOutsMayOpen = false)
{
    public bool Covers(string normalized) => Scope == AreaScope.Subtree
        ? SafePath.IsSameOrUnder(normalized, Path)
        : SafePath.SameAs(normalized, Path);
}

/// <summary>
/// Plan §6 "never touched" list, built from one <see cref="IKnownLocations"/> (real PC or sandbox). Nothing
/// here is hard-coded to a particular PC: every path is derived from the detected locations and drives.
/// </summary>
public sealed class ProtectionMap
{
    /// <summary>Areas sorted most specific first, so the reason names the closest match (Steam before Program Files).</summary>
    public IReadOnlyList<ProtectedArea> Areas { get; }

    public ProtectionMap(IKnownLocations k)
    {
        var areas = new List<ProtectedArea>();
        void Add(string? path, AreaScope scope, string label, string reason, bool carveOutsMayOpen = false)
        {
            var p = SafePath.TryNormalize(path);
            if (p is not null) areas.Add(new ProtectedArea(p, scope, label, reason, carveOutsMayOpen));
        }

        // Windows, with the parts no carve-out may ever open.
        Add(k.WindowsDir, AreaScope.Subtree, "Windows", Reasons.Windows, carveOutsMayOpen: true);
        Add(Path.Combine(k.WindowsDir, "WinSxS"), AreaScope.Subtree, "Windows' component store", Reasons.WinSxS);
        Add(Path.Combine(k.WindowsDir, "System32", "DriverStore"), AreaScope.Subtree, "Windows' driver store", Reasons.DriverStore);
        Add(Path.Combine(k.WindowsDir, "Installer"), AreaScope.Subtree, "Windows' installer cache", Reasons.WindowsInstaller);

        // Apps.
        Add(k.ProgramFiles, AreaScope.Subtree, "installed apps", Reasons.ProgramFiles, carveOutsMayOpen: true);
        Add(k.ProgramFilesX86, AreaScope.Subtree, "installed apps", Reasons.ProgramFiles, carveOutsMayOpen: true);
        Add(k.ProgramData, AreaScope.Subtree, "shared app data", Reasons.ProgramData, carveOutsMayOpen: true);
        foreach (var app in k.AppInstallDirs) Add(app, AreaScope.Subtree, "an installed app", Reasons.AppInstall, carveOutsMayOpen: true);
        Add(Path.Combine(k.LocalAppData, "Microsoft", "WindowsApps"), AreaScope.Subtree, "Store app shortcuts", Reasons.StoreApps);
        // Store apps' own data (settings, offline content): managed by Windows per app, never moved or cleaned by hand.
        Add(Path.Combine(k.LocalAppData, "Packages"), AreaScope.Subtree, "Store app data", Reasons.StoreApps);

        // Games.
        if (k.SteamRoot is not null) Add(k.SteamRoot, AreaScope.Subtree, "Steam", Reasons.SteamRoot, carveOutsMayOpen: true);
        foreach (var lib in k.SteamLibraries) Add(Path.Combine(lib, "steamapps"), AreaScope.Subtree, "your Steam library", Reasons.SteamLibrary);
        // Steam games already sit inside a protected steamapps folder, whose reason ("your Steam library") is clearer.
        var steamapps = k.SteamLibraries.Select(l => SafePath.TryNormalize(Path.Combine(l, "steamapps"))).OfType<string>().ToList();
        foreach (var game in k.GameInstallDirs)
            if (SafePath.TryNormalize(game) is not { } g || !steamapps.Any(s => SafePath.IsSameOrUnder(g, s)))
                Add(game, AreaScope.Subtree, "a game", Reasons.Game);

        // Per-drive system places (any drive can hold Store apps, Xbox games, a pagefile...).
        foreach (var drive in k.FixedDrives)
        {
            Add(drive, AreaScope.ExactOnly, "a whole drive", Reasons.DriveRoot);
            Add(Path.Combine(drive, "WindowsApps"), AreaScope.Subtree, "Store apps", Reasons.StoreApps);
            Add(Path.Combine(drive, "Program Files", "WindowsApps"), AreaScope.Subtree, "Store apps", Reasons.StoreApps);
            Add(Path.Combine(drive, "XboxGames"), AreaScope.Subtree, "Xbox games", Reasons.Game);
            Add(Path.Combine(drive, "$Recycle.Bin"), AreaScope.Subtree, "the Recycle Bin", Reasons.RecycleBin);
            foreach (var name in SystemRootEntries)
                Add(Path.Combine(drive, name), AreaScope.Subtree, "Windows system files", Reasons.SystemRootEntry);
        }

        // The user's own top-level folders: their contents may be organized, the folders themselves stay put.
        Add(SafePath.Parent(SafePath.TryNormalize(k.UserProfile) ?? ""), AreaScope.ExactOnly, "everyone's user folders", Reasons.UsersFolder);
        Add(k.UserProfile, AreaScope.ExactOnly, "your user folder", Reasons.ProfileRoot);
        Add(Path.Combine(k.UserProfile, "AppData"), AreaScope.ExactOnly, "app settings", Reasons.AppDataRoot);
        Add(k.LocalAppData, AreaScope.ExactOnly, "app settings", Reasons.AppDataRoot);
        Add(Path.Combine(k.UserProfile, "AppData", "LocalLow"), AreaScope.ExactOnly, "app settings", Reasons.AppDataRoot);
        Add(k.RoamingAppData, AreaScope.ExactOnly, "app settings", Reasons.AppDataRoot);
        Add(k.UserTemp, AreaScope.ExactOnly, "your temp folder", Reasons.TempRoot);
        foreach (var (path, name) in UserLibraries(k).Append((k.Downloads, "Downloads")))
            Add(path, AreaScope.ExactOnly, $"your {name} folder", Reasons.KnownFolderRoot(name));
        foreach (var cloud in k.CloudRoots) Add(cloud, AreaScope.ExactOnly, "your cloud folder", Reasons.CloudRoot);

        // Other accounts' profiles that live outside the Users folder (everything inside it is covered by UsersFolder below).
        foreach (var other in OtherProfiles(k)) Add(other, AreaScope.Subtree, "another account's user folder", Reasons.OtherAccount);

        // PC Tidy itself.
        Add(k.DataRoot, AreaScope.Subtree, "PC Tidy's data", Reasons.DataRoot);
        Add(AppSettings.DefaultDataRoot, AreaScope.Subtree, "PC Tidy's data", Reasons.DataRoot);
        // The engine's optional per-drive quarantine folders (<drive>\PC Tidy Quarantine).
        foreach (var drive in k.FixedDrives)
            Add(Path.Combine(drive, "PC Tidy Quarantine"), AreaScope.Subtree, "PC Tidy's quarantine", Reasons.DataRoot);
        Add(AppContext.BaseDirectory, AreaScope.Subtree, "PC Tidy itself", Reasons.PcTidyInstall);

        Drives = NormalizeAll(k.FixedDrives);
        CloudRoots = NormalizeAll(k.CloudRoots);
        Libraries = UserLibraries(k).Select(l => (Path: SafePath.TryNormalize(l.Path), l.Name))
            .Where(l => l.Path is not null).Select(l => (l.Path!, l.Name)).ToList();
        ClaudeDir = SafePath.TryNormalize(k.ClaudeDir);
        CodexDir = SafePath.TryNormalize(k.CodexDir);
        // The default folders stay protected even when CLAUDE_CONFIG_DIR / CODEX_HOME point elsewhere (an older
        // ~\.claude still holds logins and settings). ~\.claude.json is Claude Code's main config (accounts, MCP keys).
        DefaultClaudeDir = SafePath.TryNormalize(Path.Combine(k.UserProfile, ".claude"));
        DefaultCodexDir = SafePath.TryNormalize(Path.Combine(k.UserProfile, ".codex"));
        ClaudeJson = SafePath.TryNormalize(Path.Combine(k.UserProfile, ".claude.json"));
        AiConfigDirs = NormalizeAll(new[] { k.ClaudeDir, k.CodexDir, Path.Combine(k.UserProfile, ".claude"), Path.Combine(k.UserProfile, ".codex") })
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        DataRoot = SafePath.TryNormalize(k.DataRoot);
        OwnProfile = SafePath.TryNormalize(k.UserProfile);
        UsersFolder = OwnProfile is null ? null : SafePath.Parent(OwnProfile);

        Areas = areas
            .GroupBy(a => (a.Path.ToUpperInvariant(), a.Scope))
            // Same folder listed twice (e.g. Steam is also an installed app): keep the strictest entry.
            .Select(g => g.OrderBy(a => a.CarveOutsMayOpen).First())
            .OrderByDescending(a => a.Path.Length)
            .ToList();
    }

    /// <summary>This PC's fixed drive roots ("C:\"). Anything elsewhere is never touched.</summary>
    public IReadOnlyList<string> Drives { get; }
    public IReadOnlyList<string> CloudRoots { get; }
    public IReadOnlyList<(string Path, string Name)> Libraries { get; }
    public IReadOnlyList<string> AiConfigDirs { get; }
    public string? ClaudeDir { get; }
    public string? CodexDir { get; }
    public string? DefaultClaudeDir { get; }
    public string? DefaultCodexDir { get; }
    /// <summary>%USERPROFILE%\.claude.json (and its ".backup" twins: same name plus a suffix).</summary>
    public string? ClaudeJson { get; }
    public string? DataRoot { get; }

    /// <summary>The signed-in user's profile. Everything else in <see cref="UsersFolder"/> belongs to someone else.</summary>
    public string? OwnProfile { get; }

    /// <summary>The folder holding every profile (C:\Users): only <see cref="OwnProfile"/> (and Public, for organizing) may be touched.</summary>
    public string? UsersFolder { get; }

    private static IReadOnlyList<string> NormalizeAll(IEnumerable<string?> paths)
        => paths.Select(SafePath.TryNormalize).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Desktop, Documents, Pictures, Videos, Music: organize-only libraries (Downloads is not one).</summary>
    public static IEnumerable<(string Path, string Name)> UserLibraries(IKnownLocations k) => new[]
    {
        (k.Desktop, "Desktop"), (k.Documents, "Documents"), (k.Pictures, "Pictures"), (k.Videos, "Videos"), (k.Music, "Music"),
    };

    /// <summary>
    /// Profile folders of the other accounts on this PC (ProfileList in the registry, read-only). Never on a sandbox:
    /// its fake PC has no other accounts, and tests must not depend on the real one.
    /// </summary>
    private static IEnumerable<string> OtherProfiles(IKnownLocations k)
    {
        if (k.IsSandbox) return Array.Empty<string>();
        var own = SafePath.TryNormalize(k.UserProfile);
        var list = new List<string>();
        try
        {
            using var profiles = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            if (profiles is null) return list;
            foreach (var sid in profiles.GetSubKeyNames())
            {
                using var key = profiles.OpenSubKey(sid);
                if (key?.GetValue("ProfileImagePath") is not string raw) continue;
                var p = SafePath.TryNormalize(Environment.ExpandEnvironmentVariables(raw));
                if (p is null || p.Length <= 3 || (own is not null && SafePath.SameAs(p, own))) continue;
                list.Add(p);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { /* unreadable: the Users folder rule still applies */ }
        return list;
    }

    /// <summary>Files and folders Windows keeps at the root of a drive.</summary>
    private static readonly string[] SystemRootEntries =
    {
        "System Volume Information", "pagefile.sys", "hiberfil.sys", "swapfile.sys", "DumpStack.log", "DumpStack.log.tmp",
        "Recovery", "Boot", "EFI", "bootmgr", "BOOTNXT", "BOOTSECT.BAK", "Config.Msi",
        "Windows.old", "$Windows.~BT", "$Windows.~WS", "$WinREAgent", "$SysReset", "$GetCurrent",
    };
}
