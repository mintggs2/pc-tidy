using PcTidy.Core;
using PcTidy.Core.Model;

namespace PcTidy.App.WhatIs;

/// <summary>What kind of thing a path is, in PC Tidy's words.</summary>
public enum WhatIsKind
{
    Missing, PcTidyQuarantine, PcTidyData, Cache, Temp, CloudPlaceholder, Cloud, Game, App, Windows, ProgramFiles,
    SharedAppData, Protected, Project, DriveRoot, UserFolder, AppData, Personal,
}

/// <summary>"Safe to delete?" in three plain answers (plus "it isn't there").</summary>
public enum WhatIsVerdict { SafeToClean, ThinkFirst, DontDelete, NotThere }

/// <summary>Everything the "What is this?" window says about one file or folder. Read-only facts.</summary>
public sealed record WhatIsReport
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public bool IsFolder { get; init; }
    public required WhatIsKind Kind { get; init; }
    /// <summary>Short headline: "Chrome cache", "Part of Windows".</summary>
    public required string Title { get; init; }
    /// <summary>One or two plain sentences: what it is and what deleting it would do.</summary>
    public required string Explanation { get; init; }
    /// <summary>"Steam (Valve)", "Google Chrome (Google LLC)", or null when unknown.</summary>
    public string? Owner { get; init; }
    public required WhatIsVerdict Verdict { get; init; }
    /// <summary>"Safe to clean with PC Tidy (it goes to quarantine first)."</summary>
    public required string VerdictText { get; init; }
    /// <summary>Shell page key of the section that deals with it ("cleanup", "apps"…), or null.</summary>
    public string? Section { get; init; }
    /// <summary>Button text for that section ("Open Clean").</summary>
    public string? SectionLabel { get; init; }
}

/// <summary>
/// Explains a path for Explorer's "What is this?" from PC Tidy's own knowledge: the data folder and quarantine, the
/// cleanup carve-outs (caches and temp files PC Tidy knows), OneDrive, game libraries, the Uninstall registry, the
/// safety guard's protected areas and project detection, and the user's folders. Pure and read-only: it never opens
/// a file's content (an online-only OneDrive file stays online) and never changes anything.
/// </summary>
public sealed class WhatIsClassifier
{
    private readonly IKnownLocations _k;
    private readonly ISafetyGuard _guard;
    private readonly IInstalledApps _apps;
    private readonly string _dataRoot;

    public WhatIsClassifier(IKnownLocations locations, ISafetyGuard guard, IInstalledApps apps, string dataRoot)
    {
        _k = locations;
        _guard = guard;
        _apps = apps;
        _dataRoot = dataRoot;
    }

    /// <param name="attributes">The item's attributes if known (tests); otherwise read from the file system (no recall).</param>
    public WhatIsReport Classify(string rawPath, FileAttributes? attributes = null)
    {
        string path;
        try { path = PathUtil.Normalize(rawPath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Report(rawPath, false, WhatIsKind.Missing, "Not a path on this PC", "PC Tidy can only explain files and folders on this PC.", null, WhatIsVerdict.NotThere, "Nothing to decide.");
        }

        var attrs = attributes ?? ReadAttributes(path);
        if (attrs is null)
            return Report(path, false, WhatIsKind.Missing, "It isn't there any more", "This file or folder has been moved or deleted since you right-clicked it.", null, WhatIsVerdict.NotThere, "Nothing to decide.");
        bool folder = attrs.Value.HasFlag(FileAttributes.Directory);

        return Quarantine(path, folder)
            ?? OwnData(path, folder)
            ?? Carved(path, folder, attrs.Value)
            ?? Cloud(path, folder, attrs.Value)
            ?? Game(path, folder)
            ?? App(path, folder)
            ?? WindowsAndPrograms(path, folder)
            ?? DriveRoot(path)
            ?? UserFolders(path, folder)
            ?? Guarded(path, folder)
            ?? AppData(path, folder)
            ?? Personal(path, folder);
    }

    private static FileAttributes? ReadAttributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    // ---------------- PC Tidy's own things ----------------

    private WhatIsReport? Quarantine(string path, bool folder)
    {
        var inData = PathUtil.IsUnder(path, DataPaths.Quarantine(_dataRoot));
        var inDrive = _k.FixedDrives.Any(d => PathUtil.IsUnder(path, Path.Combine(d, "PC Tidy Quarantine")));
        if (!inData && !inDrive) return null;
        return Report(path, folder, WhatIsKind.PcTidyQuarantine, "Put aside by PC Tidy (quarantine)",
            "Something PC Tidy cleaned waits here, so you can put it back. It's emptied after the days you chose, and only after you agree.",
            "PC Tidy", WhatIsVerdict.DontDelete, "Don't delete it by hand. Restore or empty it from PC Tidy's Quarantine page.", "quarantine", "Open Quarantine");
    }

    private WhatIsReport? OwnData(string path, bool folder)
    {
        if (!PathUtil.IsUnder(path, _dataRoot)) return null;
        return Report(path, folder, WhatIsKind.PcTidyData, "PC Tidy's own data",
            "Settings, run history (what Undo needs), logs and reports. It stays on this PC and is never sent anywhere.",
            "PC Tidy", WhatIsVerdict.DontDelete, "Keep it: without it PC Tidy can't undo earlier runs.", "settings", "Open Settings");
    }

    // ---------------- known caches and temp files (the carve-out table PC Tidy cleans from) ----------------

    private WhatIsReport? Carved(string path, bool folder, FileAttributes attrs)
    {
        foreach (var carve in CarveOuts.All)
        {
            IEnumerable<string> roots;
            try { roots = carve.Roots(_k).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { continue; }
            foreach (var root in roots)
            {
                if (string.IsNullOrEmpty(root) || !PathUtil.IsUnder(path, root)) continue;
                var relative = Path.GetRelativePath(PathUtil.Normalize(root), path);
                bool isRoot = relative == ".";
                if (!isRoot && carve.IsDenied(relative))
                    return Report(path, folder, WhatIsKind.AppData, "App data PC Tidy never cleans",
                        "This is next to a cache, but it holds sign-ins, settings or saved data, so PC Tidy never cleans it.",
                        Owner(path) ?? Vendor(path), WhatIsVerdict.DontDelete, "Don't delete it: you could be signed out or lose saved data.", null, null);
                if (carve.Id == CarveOuts.OldInstallers)
                {
                    if (folder || carve.ExtensionNotAllowed(path)) continue; // an ordinary download, decided below
                    return Report(path, folder, WhatIsKind.Personal, "An installer you downloaded",
                        "A setup file. Once the app is installed it's usually not needed any more, and you can download it again.",
                        null, WhatIsVerdict.ThinkFirst, "Usually safe to remove. PC Tidy's Clean offers old installers (unticked) and quarantines them first.", "cleanup", "Open Clean");
                }
                bool temp = carve.Id is CarveOuts.UserTemp or CarveOuts.WindowsTemp;
                var (title, why) = CacheText(carve);
                return Report(path, folder, temp ? WhatIsKind.Temp : WhatIsKind.Cache, title,
                    why + (isRoot && folder ? " The folder itself stays; what's inside can be cleaned." : ""),
                    Owner(path) ?? (carve.Id == CarveOuts.UserTemp && !isRoot ? TempFileOwner(path) : null) ?? OwnerFromCarve(carve), WhatIsVerdict.SafeToClean,
                    carve.NeedsAdmin ? "Safe to clean with PC Tidy (asks for admin once; everything goes to quarantine first)." : "Safe to clean with PC Tidy (it goes to quarantine first, so it can be put back).",
                    "cleanup", "Open Clean");
            }
        }
        foreach (var (id, file) in CarveOuts.AllowedSingleFiles(_k))
            if (PathUtil.Normalize(file).Equals(path, StringComparison.OrdinalIgnoreCase))
                return Report(path, folder, WhatIsKind.Cache, "A Windows crash dump", "A memory snapshot Windows saved after a crash. Only useful for diagnosing that crash.",
                    "Windows", WhatIsVerdict.SafeToClean, "Safe to clean with PC Tidy (asks for admin once).", "cleanup", "Open Clean");
        return null;
    }

    private static (string Title, string Why) CacheText(CarveOut c) => c.Id switch
    {
        CarveOuts.UserTemp => ("Temporary files", "Files apps leave behind while they work. Apps that still need them are skipped."),
        CarveOuts.WindowsTemp => ("Windows temporary files", "Leftovers from installers and Windows itself."),
        CarveOuts.BrowserCache or CarveOuts.FirefoxCache => ("Browser cache", "Copies of web pages and images your browser downloads again when needed. Logins and bookmarks aren't in here."),
        CarveOuts.BrowserPrivacy => ("Browser history and cookies", "Your browsing history and site cookies. Clearing cookies signs you out of websites."),
        CarveOuts.InetCache => ("Old Windows web cache", "A cache from older Windows web components. Rebuilt when needed."),
        CarveOuts.ThumbnailCache => ("Thumbnail cache", "Small previews Explorer shows. Windows makes them again."),
        CarveOuts.ShaderCache => ("Graphics shader cache", "Pre-built graphics code. Games rebuild it (the first launch afterwards can be a little slower)."),
        CarveOuts.CrashDumps => ("Crash dumps", "Snapshots saved when an app or Windows crashed. Only useful for diagnosing those crashes."),
        CarveOuts.ErrorReports => ("Error reports", "Reports Windows kept about problems. Already sent or no longer needed."),
        CarveOuts.WindowsLogs => ("Old Windows logs", "Logs from Windows servicing. Kept only for troubleshooting."),
        CarveOuts.UpdateDownloads => ("Windows Update downloads", "Update files Windows already installed. Windows downloads again anything it still needs."),
        CarveOuts.Discord => ("Discord cache", "Images and videos Discord downloads again. Your login isn't touched."),
        CarveOuts.SteamCache => ("Steam web cache", "Steam's store pages and crash dumps. Rebuilt automatically; your games aren't in here."),
        CarveOuts.Telegram => ("Telegram media cache", "Media Telegram downloads again when you open a chat. Your chats stay in the cloud."),
        _ => (c.Title, "A cache PC Tidy knows. Rebuilt automatically."),
    };

    /// <summary>
    /// Who a file in the user's Temp folder belongs to, from its name when it names a well-known app
    /// ("chrome_installer.log" → "Chrome (installer log)"). Null when the name doesn't say.
    /// </summary>
    public static string? TempFileOwner(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        var app = TempNames.FirstOrDefault(t => name.Contains(t.Key, StringComparison.Ordinal)).App;
        if (app is null) return null;
        bool log = name.EndsWith(".log", StringComparison.Ordinal);
        var what = log && name.Contains("install", StringComparison.Ordinal) ? "installer log" : log ? "log" : "temporary file";
        return $"{app} ({what})";
    }

    private static readonly (string Key, string App)[] TempNames =
    {
        ("chrome", "Chrome"), ("msedge", "Microsoft Edge"), ("edgeupdate", "Microsoft Edge"), ("firefox", "Firefox"), ("mozilla", "Firefox"),
        ("steam", "Steam"), ("discord", "Discord"), ("nvidia", "NVIDIA"), ("onedrive", "OneDrive"), ("teams", "Microsoft Teams"),
        ("zoom", "Zoom"), ("adobe", "Adobe"), ("dotnet", ".NET"), ("vscode", "Visual Studio Code"), ("python", "Python"),
    };

    private static string? OwnerFromCarve(CarveOut c) => c.Id switch
    {
        // The user's Temp folder is shared by every app: "Windows" would be wrong for most of what's in it.
        CarveOuts.UserTemp => "Apps (temporary files)",
        CarveOuts.WindowsTemp or CarveOuts.InetCache or CarveOuts.ThumbnailCache or CarveOuts.ErrorReports
            or CarveOuts.WindowsLogs or CarveOuts.UpdateDownloads or CarveOuts.CrashDumps => "Windows",
        CarveOuts.Discord => "Discord",
        CarveOuts.SteamCache => "Steam",
        CarveOuts.Telegram => "Telegram",
        _ => null,
    };

    // ---------------- OneDrive ----------------

    private WhatIsReport? Cloud(string path, bool folder, FileAttributes attrs)
    {
        if (!_guard.IsInCloudRoot(path) && !_k.CloudRoots.Any(r => PathUtil.IsUnder(path, r))) return null;
        if (!folder && CloudFile.IsPlaceholder(attrs))
            return Report(path, folder, WhatIsKind.CloudPlaceholder, "An online-only OneDrive file",
                "It lives in OneDrive and takes no space on this PC until you open it. Deleting it here deletes it from OneDrive and your other devices too.",
                "OneDrive", WhatIsVerdict.DontDelete, "Deleting it frees nothing on this PC, and removes it everywhere. Leave it.", null, null);
        return Report(path, folder, WhatIsKind.Cloud, folder ? "A OneDrive folder" : "A OneDrive file",
            "Synced with OneDrive. Deleting it removes it from OneDrive and your other devices too (it waits in OneDrive's recycle bin for a while).",
            "OneDrive", WhatIsVerdict.ThinkFirst, "Only if you don't need it anywhere. PC Tidy leaves OneDrive alone unless you allow a folder in Settings.", "spacemap", "Open Space map");
    }

    // ---------------- games and apps ----------------

    private WhatIsReport? Game(string path, bool folder)
    {
        var lib = _k.GameInstallDirs.Concat(_k.SteamLibraries.Select(l => Path.Combine(l, "steamapps")))
            .Where(d => !string.IsNullOrEmpty(d) && PathUtil.IsUnder(path, d)).OrderByDescending(d => d.Length).FirstOrDefault();
        if (lib is null) return null;
        var launcher = _k.SteamLibraries.Any(l => PathUtil.IsUnder(path, l)) ? "Steam"
            : lib.Contains("Epic Games", StringComparison.OrdinalIgnoreCase) ? "Epic Games" : null;
        var rel = Path.GetRelativePath(PathUtil.Normalize(lib), path);
        var game = rel == "." ? null : rel.Split('\\')[0];
        var owner = game is null ? launcher : launcher is null ? game : $"{game} ({launcher})";
        return Report(path, folder, WhatIsKind.Game, game is null ? "Your game library" : $"Part of the game {game}",
            "Installed game files. Deleting them by hand breaks the game; the launcher would download it all again.",
            owner, WhatIsVerdict.DontDelete,
            launcher is null ? "Don't delete it. Uninstall the game from its launcher to free the space." : $"Don't delete it. Uninstall the game from {launcher} to free the space.",
            "games", "Open Games");
    }

    private WhatIsReport? App(string path, bool folder)
    {
        if (_apps.OwnerOf(path) is not { } app) return null;
        return Report(path, folder, WhatIsKind.App, $"Part of {app.Name}",
            "Files of an installed app. Deleting them can break it and leaves it half-installed.",
            Named(app), WhatIsVerdict.DontDelete, "Don't delete it. Uninstall the app instead (PC Tidy's Apps section helps).", "apps", "Open Apps");
    }

    private WhatIsReport? WindowsAndPrograms(string path, bool folder)
    {
        if (PathUtil.IsUnder(path, _k.WindowsDir))
            return Report(path, folder, WhatIsKind.Windows, "Part of Windows",
                "Windows needs this to start and work. Its safe-to-clean parts (temp files, update leftovers) are in PC Tidy's Clean.",
                "Windows", WhatIsVerdict.DontDelete, "Never delete it by hand.", "cleanup", "Open Clean");
        if (PathUtil.IsUnder(path, _k.ProgramFiles) || PathUtil.IsUnder(path, _k.ProgramFilesX86))
            return Report(path, folder, WhatIsKind.ProgramFiles, "Installed programs",
                "Where apps are installed. Deleting files here breaks apps.",
                Owner(path), WhatIsVerdict.DontDelete, "Don't delete it. Uninstall apps from PC Tidy's Apps section or Windows Settings.", "apps", "Open Apps");
        if (PathUtil.IsUnder(path, _k.ProgramData))
            return Report(path, folder, WhatIsKind.SharedAppData, "Shared app data",
                "Data apps keep for every user of this PC (licences, settings, databases).",
                Owner(path), WhatIsVerdict.DontDelete, "Leave it: the apps that own it may stop working.", null, null);
        return null;
    }

    private WhatIsReport? DriveRoot(string path)
    {
        bool isRoot = _k.FixedDrives.Any(d => PathUtil.Normalize(d).Equals(path, StringComparison.OrdinalIgnoreCase))
                      || (Path.GetPathRoot(path) is { } root && PathUtil.Normalize(root).Equals(path, StringComparison.OrdinalIgnoreCase));
        if (!isRoot) return null;
        bool system = PathUtil.Normalize(_k.SystemDrive).Equals(path, StringComparison.OrdinalIgnoreCase);
        return Report(path, true, WhatIsKind.DriveRoot, system ? "Your Windows drive" : "A whole drive",
            system ? "The drive Windows runs from. The Space map shows what takes the room." : "A whole drive. The Space map shows what takes the room.",
            null, WhatIsVerdict.DontDelete, "It's a drive, not something to delete. Look inside to decide piece by piece.", "spacemap", "Open Space map");
    }

    // ---------------- the user's own things ----------------

    private WhatIsReport? UserFolders(string path, bool folder)
    {
        var known = new (string Path, string Name)[]
        {
            (_k.Downloads, "Downloads"), (_k.Desktop, "Desktop"), (_k.Documents, "Documents"),
            (_k.Pictures, "Pictures"), (_k.Videos, "Videos"), (_k.Music, "Music"), (_k.UserProfile, "your user folder"),
        };
        foreach (var (p, name) in known)
            if (!string.IsNullOrEmpty(p) && PathUtil.Normalize(p).Equals(path, StringComparison.OrdinalIgnoreCase))
                return Report(path, true, WhatIsKind.UserFolder, name == "your user folder" ? "Your user folder" : $"Your {name} folder",
                    $"One of Windows' own folders for your files. Keep the folder itself; {(name == "Downloads" || name == "Desktop" ? "Organize can sort what's inside." : "the Space map shows what's big inside.")}",
                    "You (a Windows folder for your files)", WhatIsVerdict.DontDelete, "Don't delete the folder. Decide about what's inside, one item at a time.",
                    name is "Downloads" or "Desktop" ? "organize" : "spacemap", name is "Downloads" or "Desktop" ? "Open Organize" : "Open Space map");
        return null;
    }

    private WhatIsReport? Guarded(string path, bool folder)
    {
        var verdict = _guard.ClassifyPath(path);
        if (verdict.Protection == PathProtection.Never)
            return Report(path, folder, WhatIsKind.Protected, "Protected by PC Tidy",
                (verdict.Reason ?? "PC Tidy never touches this.") + " PC Tidy never changes it.",
                Owner(path), WhatIsVerdict.DontDelete, "Don't delete it.", null, null);
        if (_guard.IsInsideProjectOrAppFolder(path, out var why))
            return Report(path, folder, WhatIsKind.Project, "Part of a project or app folder",
                (why is { Length: > 0 } ? why.TrimEnd('.') + ". " : "") + "Deleting single files from it can break the project or app.",
                Owner(path), WhatIsVerdict.ThinkFirst, "Only if you know the project. Developer cleanup finds build folders that are safe to rebuild.", "developer", "Open Developer cleanup");
        return null;
    }

    private WhatIsReport? AppData(string path, bool folder)
    {
        foreach (var root in new[] { _k.LocalAppData, _k.RoamingAppData })
        {
            if (string.IsNullOrEmpty(root) || !PathUtil.IsUnder(path, root)) continue;
            var rel = Path.GetRelativePath(PathUtil.Normalize(root), path);
            var vendor = rel == "." ? null : rel.Split('\\')[0];
            var guess = vendor is null ? null : _apps.Guess(vendor);
            return Report(path, folder, WhatIsKind.AppData, vendor is null ? "Where apps keep your settings" : $"Data an app keeps for you ({vendor})",
                "Settings, sign-ins and saved data of an app. Some of it may be cache, but PC Tidy only cleans what it knows is safe.",
                guess is null ? vendor : Named(guess), WhatIsVerdict.ThinkFirst,
                "Leave it unless you've uninstalled that app. PC Tidy's Apps section finds leftovers of removed apps.", "apps", "Open Apps");
        }
        return null;
    }

    private WhatIsReport Personal(string path, bool folder)
    {
        var inDownloads = !string.IsNullOrEmpty(_k.Downloads) && PathUtil.IsUnder(path, _k.Downloads);
        var (title, why) = folder ? ("A folder of your own", "Files you or your apps saved.") : FileText(path);
        if (inDownloads) why += " It's in Downloads, so you probably fetched it from the internet.";
        return Report(path, folder, WhatIsKind.Personal, title, why, Owner(path),
            WhatIsVerdict.ThinkFirst, "Only you know if you still need it. If PC Tidy cleans it, it goes to quarantine first.",
            folder ? "organize" : "spacemap", folder ? "Open Organize" : "Open Space map");
    }

    private static (string Title, string Why) FileText(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".tmp" or ".temp" => ("A temporary file", "Apps make these while they work; one left lying around is usually a leftover."),
        ".log" => ("A log file", "A record an app wrote about what it did. Useful only for troubleshooting."),
        ".dmp" => ("A crash dump", "A memory snapshot saved when an app crashed."),
        ".bak" or ".old" => ("A backup copy", "An older copy of a file, kept just in case."),
        ".iso" or ".img" => ("A disk image", "A whole disc or installer packed in one file. Often large."),
        ".zip" or ".7z" or ".rar" => ("An archive", "Files packed into one. If you already unpacked it, the archive may not be needed."),
        ".exe" or ".msi" or ".msix" => ("A program or installer", "Something that runs or installs an app."),
        ".mp4" or ".mkv" or ".mov" or ".avi" => ("A video", "Video files are often the biggest things on a PC."),
        ".jpg" or ".jpeg" or ".png" or ".heic" or ".gif" => ("A picture", "One of your images."),
        _ => ("A file of your own", "A file you or one of your apps saved."),
    };

    // ---------------- helpers ----------------

    private string? Owner(string path) => _apps.OwnerOf(path) is { } a ? Named(a) : null;

    /// <summary>"Google Chrome" for …\AppData\Local\Google\Chrome\…: the two folder names under AppData.</summary>
    private string? Vendor(string path)
    {
        foreach (var root in new[] { _k.LocalAppData, _k.RoamingAppData })
        {
            if (string.IsNullOrEmpty(root) || !PathUtil.IsUnder(path, root)) continue;
            var parts = Path.GetRelativePath(PathUtil.Normalize(root), path).Split('\\');
            if (parts.Length == 0 || parts[0] == ".") return null;
            if (_apps.Guess(parts[0]) is { } app) return Named(app);
            return parts.Length > 2 ? $"{parts[0]} {parts[1]}" : parts[0];
        }
        return null;
    }

    private static string Named(InstalledApp a)
        => a.Publisher is { Length: > 0 } p && !a.Name.Contains(p, StringComparison.OrdinalIgnoreCase) ? $"{a.Name} ({p})" : a.Name;

    private static WhatIsReport Report(string path, bool folder, WhatIsKind kind, string title, string explanation, string? owner,
        WhatIsVerdict verdict, string verdictText, string? section = null, string? sectionLabel = null) => new()
    {
        Path = path,
        Name = System.IO.Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } n ? n : path,
        IsFolder = folder,
        Kind = kind,
        Title = title,
        Explanation = explanation,
        Owner = owner,
        Verdict = verdict,
        VerdictText = verdictText,
        Section = section,
        SectionLabel = sectionLabel,
    };
}
