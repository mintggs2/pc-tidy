using PcTidy.Catalog.DriveMove;
using PcTidy.Core;
using PcTidy.Features.DriveMove.Common;
using PcTidy.Features.DriveMove.PcFacts;

namespace PcTidy.Features.DriveMove.Folders;

/// <summary>A warning to show with a folder that may move (the row becomes "Check").</summary>
public sealed record JunctionWarning(string Text, IReadOnlyList<string> CloseFirst);

/// <summary>
/// Which folders may be moved with a junction (Research.md §7 "Things that break with junctions"). The safety guard enforces
/// the same limits again on the plan item; these rules exist to explain them clearly before anything is offered.
/// Order matters: the most specific explanation wins.
/// </summary>
internal sealed class JunctionRules
{
    public const string NotThere = "This folder isn't there any more.";
    public const string AlreadyLink = "This is already a link to another place, so there's nothing to move.";
    public const string DriveRoot = "This is a whole drive. Pick a folder on it instead.";
    public const string Drivers = "Drivers are part of Windows. Moving them stops devices from working.";
    public const string Windows = "Windows' own files never move. Moving them would stop Windows from starting or updating.";
    public const string Apps = "Apps are installed here. Moving them by hand breaks updates and uninstalls: reinstall the app on the other drive instead.";
    public const string StoreApps = "Microsoft Store apps keep their files and data here. Windows manages them, so moving them by hand breaks the app.";
    public const string Games = "Games move with their launcher's own Move button (see the Games tab). Copying a game folder by hand can break updates and anti-cheat.";
    public const string Cloud = "This is in (or holds) your OneDrive or another cloud folder. Cloud sync doesn't work through folder links, so PC Tidy never makes one here.";
    public const string UserFolderRoot = "This is one of your Windows user folders. Use the User folders tab: Windows' own Location setting moves it properly.";
    public const string ProfileOrAppData = "Windows expects this folder exactly here. Moving it breaks signing in and app settings, so it stays put.";
    public const string HoldsUserFolders = "This folder holds your Windows user folders. Windows updates break when it's moved, so it stays put.";
    public const string PcTidyData = "This is PC Tidy's own data (settings, history and quarantine).";
    public const string TooBigForDrives = "There isn't enough room on your other drives for this folder plus 10% to spare.";

    private readonly IKnownLocations _k;
    private readonly ISafetyGuard _guard;
    private readonly List<(string Path, string Reason)> _never = new();
    private readonly List<string> _localAppData = new();

    public JunctionRules(IKnownLocations k, ISafetyGuard guard)
    {
        _k = k;
        _guard = guard;
        void Never(string? path, string reason)
        {
            // A drive root here (an app that registered "C:\" as its folder) would refuse everything: drive roots have their own rule.
            if (PathText.TryNormalize(path) is { Length: > 3 } p) _never.Add((p, reason));
        }

        // Most specific first.
        Never(Path.Combine(k.WindowsDir, "System32", "drivers"), Drivers);
        Never(Path.Combine(k.WindowsDir, "System32", "DriverStore"), Drivers);
        Never(Path.Combine(k.LocalAppData, "Packages"), StoreApps);
        Never(Path.Combine(k.LocalAppData, "Microsoft", "WindowsApps"), StoreApps);
        foreach (var lib in k.SteamLibraries) Never(lib, Games);
        foreach (var game in k.GameInstallDirs) Never(game, Games);
        foreach (var drive in k.FixedDrives)
        {
            Never(Path.Combine(drive, "XboxGames"), Games);
            Never(Path.Combine(drive, "WindowsApps"), StoreApps);
            Never(Path.Combine(drive, "Program Files", "WindowsApps"), StoreApps);
        }
        if (k.SteamRoot is { } steam) Never(steam, Games);
        Never(k.WindowsDir, Windows);
        Never(k.ProgramFiles, Apps);
        Never(k.ProgramFilesX86, Apps);
        Never(k.ProgramData, Apps);
        foreach (var app in k.AppInstallDirs) Never(app, Apps);
        Never(k.DataRoot, PcTidyData);
        Never(AppSettings.DefaultDataRoot, PcTidyData);
        if (PathText.TryNormalize(k.LocalAppData) is { } local) _localAppData.Add(local);
    }

    /// <summary>Why this folder can't move with a link, or null when it may. Doesn't look at free space or open files.</summary>
    public string? Refusal(string folder)
    {
        var p = PathText.TryNormalize(folder);
        if (p is null || !FolderMeasure.DirectoryExists(p)) return NotThere;
        if (FolderMeasure.IsReparsePoint(p)) return AlreadyLink;
        if (_k.FixedDrives.Any(d => PathText.TryNormalize(d) is { } root && root.Equals(p, StringComparison.OrdinalIgnoreCase)) || p.Length <= 3)
            return DriveRoot;

        // 1. Inside a never-moved place.
        foreach (var (path, reason) in _never)
            if (PathUtil.IsUnder(p, path)) return reason;
        // 2. The user's own top-level folders themselves (they hold protected places too, so they're named first).
        foreach (var root in UserFolderRoots())
            if (root.Equals(p, StringComparison.OrdinalIgnoreCase)) return UserFolderRoot;
        foreach (var root in ProfileAndAppData())
            if (root.Equals(p, StringComparison.OrdinalIgnoreCase)) return ProfileOrAppData;
        var profile = PathText.TryNormalize(_k.UserProfile);
        var usersFolder = profile is null ? null : Path.GetDirectoryName(profile);
        if (usersFolder is not null && PathUtil.IsUnder(usersFolder, p)) return HoldsUserFolders;
        // 3. A folder that holds a protected place (e.g. a Steam library inside it) can't move either.
        foreach (var (path, reason) in _never)
            if (PathUtil.IsUnder(path, p)) return reason;
        // 4. Cloud folders, unregistered game libraries, and anything holding a user folder.
        if (_guard.IsInCloudRoot(p) || _k.CloudRoots.Any(c => PathUtil.IsUnder(c, p))) return Cloud;
        if (ContainsSteamLibrary(p)) return Games;
        foreach (var root in UserFolderRoots().Concat(ProfileAndAppData()))
            if (PathUtil.IsUnder(root, p)) return HoldsUserFolders;
        return null;
    }

    /// <summary>Warnings for a folder that may move: apps whose updater replaces their folder (Squirrel: Discord, Slack, Teams…).</summary>
    public JunctionWarning? Warning(string folder)
    {
        var p = PathText.TryNormalize(folder);
        if (p is null) return null;
        foreach (var local in _localAppData)
        {
            foreach (var app in JunctionRiskApps.All)
            {
                var appFolder = Path.Combine(local, app.FolderName);
                if (PathUtil.IsUnder(p, appFolder) || PathUtil.IsUnder(appFolder, p))
                    return new JunctionWarning(SquirrelText(app.Name), app.ProcessNames);
            }
        }
        if (LooksLikeSquirrel(p))
            return new JunctionWarning(SquirrelText(Path.GetFileName(p)), Array.Empty<string>());
        return null;
    }

    private static string SquirrelText(string app)
        => $"{app} replaces its own folder when it updates, which can swap the link for a new folder on this drive. Your moved files stay safe, but the space may fill up again. Close {app} first.";

    /// <summary>Squirrel's layout: Update.exe next to "app-1.2.3" folders.</summary>
    private static bool LooksLikeSquirrel(string folder)
        => FolderMeasure.FileExists(Path.Combine(folder, JunctionRiskApps.SquirrelUpdater))
           && FolderMeasure.SubFolders(folder).Any(d => Path.GetFileName(d).StartsWith(JunctionRiskApps.SquirrelVersionPrefix, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsSteamLibrary(string folder)
        => FolderMeasure.DirectoryExists(Path.Combine(folder, "steamapps"));

    private IEnumerable<string> UserFolderRoots()
        => new[] { _k.Desktop, _k.Documents, _k.Downloads, _k.Pictures, _k.Videos, _k.Music }.Select(PathText.TryNormalize).OfType<string>();

    private IEnumerable<string> ProfileAndAppData()
        => new[]
        {
            _k.UserProfile, Path.Combine(_k.UserProfile, "AppData"), _k.LocalAppData, _k.RoamingAppData,
            Path.Combine(_k.UserProfile, "AppData", "LocalLow"), _k.UserTemp,
        }.Concat(_k.CloudRoots).Select(PathText.TryNormalize).OfType<string>();
}
