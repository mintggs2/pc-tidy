namespace PcTidy.Safety.Rules;

/// <summary>Every plain-English reason the safety guard can give. Short, friendly, specific.</summary>
public static class Reasons
{
    // ---- never touched: places ----
    public const string Windows = "This is part of Windows. PC Tidy never changes Windows' own files.";
    public const string WinSxS = "This is Windows' component store. Only Windows' own cleanup tool may change it.";
    public const string DriverStore = "This is Windows' driver store. Drivers are only removed with Windows' own tool.";
    public const string WindowsInstaller = "Windows needs this folder to repair and uninstall apps. It's never touched.";
    public const string ProgramFiles = "This is where apps are installed. PC Tidy never changes app files.";
    public const string ProgramData = "This is shared app data for everyone on this PC. PC Tidy never changes it.";
    public const string AppInstall = "This is where an installed app lives. Apps are only removed with their own uninstaller.";
    public const string StoreApps = "This belongs to Microsoft Store apps. Windows manages them, so PC Tidy never touches them.";
    public const string SteamRoot = "This is the Steam folder. Games and Steam's own files are never touched.";
    public const string SteamLibrary = "This is inside your Steam library. Games are never touched.";
    public const string Game = "This is a game's install folder. Games are never touched. Use the game's launcher to move or remove it.";
    public const string RecycleBin = "This is the Recycle Bin. PC Tidy only empties it through Windows.";
    public const string SystemRootEntry = "Windows keeps this at the top of the drive to start up and recover. It's never touched.";
    public const string DriveRoot = "This is a whole drive. PC Tidy never moves or removes a drive itself.";
    public const string UsersFolder = "This folder holds everyone's user folders. It stays where it is.";
    public const string ProfileRoot = "This is your user folder itself. What's inside can be tidied, but the folder stays where it is.";
    public const string AppDataRoot = "This folder holds your apps' settings. It stays where it is.";
    public const string TempRoot = "This is your temp folder itself. Old files inside can be cleaned, but the folder stays.";
    public const string CloudRoot = "This is your cloud-synced folder itself. It stays where it is.";
    public const string DataRoot = "This is PC Tidy's own data (settings, history and quarantine). It's never touched.";
    public const string PcTidyInstall = "This is where PC Tidy itself is installed.";
    public const string ExtraProtected = "You asked PC Tidy never to touch this folder.";
    public const string ScanExcluded = "You excluded this folder from scans, so PC Tidy leaves it alone.";
    public const string SyncInternals = "This is a sync tool's own bookkeeping (Syncthing or Dropbox). Touching it could break syncing.";
    public const string AiConfig = "This holds your AI assistant's settings, logins or memory. Only chat transcripts may be archived.";
    public const string AiChatInUse = "Claude Code is running this chat right now, so it stays where it is. Try again after closing that session.";
    public const string OtherAccount = "This belongs to another account on this PC (or to Windows' shared user folders). PC Tidy only tidies your own user folder.";

    public static string KnownFolderRoot(string name)
        => $"This is your {name} folder itself. What's inside can be organized, but the folder stays where it is.";

    public static string Contains(string label) => $"This folder contains {label}, which PC Tidy never touches.";

    // ---- never touched: what the item is ----
    public const string SystemAttribute = "Windows marks this as a system file. PC Tidy never touches system files.";
    public const string ThroughLink = "This is reached through a folder link (junction or shortcut-like link). PC Tidy never follows links.";
    public const string NotThisPc = "This isn't on one of this PC's own drives. PC Tidy only changes files on local drives.";
    public const string OnlineOnly = "This file is online-only in your cloud folder. Moving it here would download it, so it's skipped.";
    public const string CantCheck = "PC Tidy couldn't look inside a folder on the way to this, so it can't rule out a folder link. It won't touch it.";

    // ---- ask first ----
    public const string Cloud = "This is in a cloud-synced folder (like OneDrive). Changes sync everywhere, so PC Tidy asks first. Allow this folder in Settings to continue.";
    public const string CloudMoveOut = "Moving this out of your cloud-synced folder deletes the cloud copy too, so PC Tidy asks first. Allow this folder in Settings to continue.";
    public static string Library(string name)
        => $"This is in your {name} folder. PC Tidy only organizes files here unless you allow it in Settings.";
    public static string Project(string root, string why)
        => $"This is inside \"{System.IO.Path.GetFileName(root)}\". {why} PC Tidy keeps it as one unit. Allow the folder in Settings to continue.";
    public const string PublicFolder = "This is in the Public folder that everyone on this PC shares. PC Tidy only organizes it if you allow the folder in Settings.";

    // ---- carve-outs ----
    public const string UnknownCarveOut = "This cleanup target isn't on PC Tidy's approved list, so nothing was touched.";
    public const string CarveOutWrongFeature = "Only Disk cleanup may use this cleanup target.";
    public const string CarveOutWrongAction = "Cleanup targets only allow moving files to quarantine.";
    public static string OutsideCarveOut(string title)
        => $"This is outside \"{title}\", the only place this cleanup may touch.";
    public static string CarveOutDeniedName(string title)
        => $"This is part of \"{title}\" that holds logins, saved data or settings. PC Tidy never touches it.";
    public static string CarveOutFolderItself(string title)
        => $"This is the \"{title}\" folder itself. Only the files inside it can be cleaned.";
    public static string CarveOutFilesOnly(string title)
        => $"\"{title}\" only cleans single files, never folders.";
    public static string CarveOutFileType(string title)
        => $"This kind of file isn't part of \"{title}\", so it's left alone.";
    public const string AdminOnlyForCarveOuts = "This needs administrator rights. PC Tidy only uses them for its approved Windows cleanup targets.";

    // ---- plan item shape ----
    public const string MissingSource = "This change doesn't say which file or folder it's for.";
    public const string MissingDestination = "This change doesn't say where things should go.";
    public const string MissingFeature = "This change doesn't say which part of PC Tidy made it.";
    public const string MoveIntoItself = "A folder can't be moved into itself.";
    public const string JunctionCloud = "Folder links don't work with cloud-synced folders, so this move isn't allowed.";
    public const string UnknownAction = "PC Tidy doesn't know this kind of change, so it won't make it.";
    public const string MustBeMarkedPermanent = "This can't be undone, but it isn't marked as permanent. It was stopped so you'd be warned first.";
    public const string TextFileType = "PC Tidy only writes reports as plain text, Markdown, CSV, JSON, HTML, XML or log files.";
    public const string ZipOnly = "Only .zip archives can be made or opened.";

    // ---- registry, services, tools ----
    public const string RegistryNotAllowed = "PC Tidy only changes the exact settings it knows are safe. This one isn't on that list.";
    public const string RegistryMalformed = "This setting's location isn't written the way PC Tidy expects, so it won't change it.";
    public static string ServiceNever(string name) => $"\"{name}\" keeps Windows, its security or your games working. PC Tidy never changes it.";
    public const string ServiceBad = "This service change is incomplete or not allowed.";
    public static string ServiceWindowsOwn(string name) => $"\"{name}\" is one of Windows' own services. Only the Services section, with its checked list, changes those.";
    public static string ServiceUnknown(string name) => $"PC Tidy couldn't tell which program \"{name}\" runs, so it leaves this service alone.";
    public const string WindowsTask = "This is one of Windows' own scheduled tasks. PC Tidy never changes those.";
    public const string UnknownTool = "This isn't one of the Windows tools PC Tidy is allowed to run.";
    public static string ToolArgument(string what) => $"This Windows tool was asked to do something PC Tidy doesn't allow ({what}).";
    public const string ExternalTarget = "PC Tidy only opens web pages, Windows Settings and game launchers. It never runs programs this way.";
    public const string CriticalStoreApp = "Removing this app breaks other parts of Windows, so PC Tidy never offers it.";
    public const string BadRedirect = "Your user folders can only move to a new, empty folder on one of this PC's drives, not a drive itself or a cloud folder.";
    public const string RedirectCloudBacked = "This folder is backed up by your cloud service. Stop the backup in its settings first if you want to move it.";
    public const string EditFileType = "PC Tidy only edits settings and text files (like .ini, .cfg, .json, .vdf), never programs or scripts.";
    public const string ShredFilesOnly = "Only single files can be shredded, never whole folders.";
    public const string OwnProfile = "This is your own user profile. PC Tidy never removes the profile you're signed in with.";
    public const string CriticalFeature = "Removing this Windows feature breaks security or common apps, so PC Tidy never offers it.";
    public const string NeverClose = "This is part of Windows, your security or a game's anti-cheat. PC Tidy never closes it.";
    public const string Incomplete = "This change is missing details PC Tidy needs to do it safely.";
}
