namespace PcTidy.Catalog.Apps;

/// <summary>
/// Name rules for leftover matching after an uninstall (Research §4 "Leftovers", Feature census: SD Maid's
/// "common names" guard and keeper list, BCU's generic-name penalty). A folder or registry key is only ever a
/// leftover candidate on an exact name match, and never when its name is on one of these lists.
/// </summary>
public static class LeftoverNameRules
{
    /// <summary>Names shorter than this are too likely to belong to something else ("Go", "VLC", "Zip"), so they're never matched.</summary>
    public const int MinimumNameLength = 4;

    /// <summary>
    /// Generic folder names that no single app owns ("Update", "Common", "Tools", "Backup"...).
    /// An app called "Tools" still never gets a folder called "Tools" matched.
    /// </summary>
    public static readonly IReadOnlySet<string> GenericNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "App", "Apps", "Application", "Applications", "Program", "Programs", "Software", "Data", "AppData", "UserData",
        "Update", "Updates", "Updater", "Installer", "Installers", "Install", "Setup", "Uninstall", "Uninstaller", "Package Cache",
        "Common", "Common Files", "Shared", "Tools", "Tool", "Utilities", "Utils", "Runtime", "Runtimes", "Bin", "Lib", "Libs",
        "Plugins", "Plugin", "Extensions", "Addons", "Add-ins", "Modules", "Components", "Drivers", "Driver", "Services", "Service",
        "Temp", "Tmp", "Cache", "Caches", "Logs", "Log", "Crashpad", "CrashDumps", "Crash Reports", "Diagnostics", "Telemetry",
        "Config", "Configuration", "Settings", "Preferences", "Profiles", "Profile", "User", "Users", "Default", "Public",
        "Backup", "Backups", "Notes", "Games", "Game", "Music", "Videos", "Pictures", "Photos", "Documents", "Downloads", "Desktop",
        "Projects", "Workspace", "Files", "Media", "Assets", "Resources", "Content", "Saves", "Save", "Library", "Libraries",
        "Packages", "Microsoft", "Windows", "System", "Intel", "AMD", "NVIDIA", "Google", "Apple", "Adobe", "JetBrains", "Autodesk", "Mozilla", "Steam",
        "Client", "Server", "Launcher", "Helper", "Agent", "Host", "Core", "Engine", "Framework", "Support", "Help", "Docs",
        "x64", "x86", "amd64", "Win32", "Win64", "Local", "LocalLow", "Roaming", "Programs (x86)", "New Folder",
    };

    /// <summary>
    /// Folders and keys never touched by leftover cleanup, whatever app was removed: shared runtimes, drivers'
    /// vendor folders and Windows parts. A trailing * means "starts with".
    /// </summary>
    public static readonly IReadOnlyList<string> KeeperPatterns = new[]
    {
        "Common Files", "Microsoft*", "Windows*", "WindowsApps", "WindowsPowerShell", "Windows Defender*", "Windows NT", "Windows Mail",
        "Windows Media Player", "Windows Photo Viewer", "Windows Sidebar", "Windows Kits", "Internet Explorer", "MSBuild", "Reference Assemblies",
        "dotnet", ".NET*", "NuGet", "IIS*", "WebView2*", "EdgeUpdate", "EdgeWebView", "Edge",
        "NVIDIA*", "Intel*", "AMD*", "ATI*", "Realtek*", "Dolby*", "Synaptics*", "Elan*", "Qualcomm*", "Broadcom*", "Killer*",
        "MediaTek*", "Logitech*", "Razer*", "Corsair*", "SteelSeries*", "ASUS*", "MSI*", "Gigabyte*", "Dell*", "HP*", "Lenovo*", "Acer*",
        "Package Cache", "Installer", "Temp", "Packages", "Programs", "Comms", "ConnectedDevicesPlatform", "D3DSCache", "PeerDistRepub",
        "VirtualStore", "Publishers", "CrashDumps", "SoftwareDistribution", "regid.*", "Uninstall Information",
        "InstallShield Installation Information", "InstallShield", "Downloaded Installations",
        "Visual C++*", "Microsoft Visual C++*", "VC_redist*", "DirectX*", "Java*", "Oracle*", "Python*", "OpenSSL*", "Git", "Docker*",
        "Steam", "Epic Games", "EpicGamesLauncher", "Origin", "EA Desktop", "Electronic Arts", "Ubisoft", "Battle.net", "GOG Galaxy",
        "Classes", "Clients", "Policies", "RegisteredApplications", "Wow6432Node", "SharedDLLs", "ODBC", "Khronos", "Vulkan*",
        "PC Tidy*",
    };

    /// <summary>
    /// Signs that a leftover folder holds the user's own content (SD Maid's KEEPER class): it is then never
    /// pre-ticked and is shown with "may hold your own files". Checked one or two levels deep, by name only.
    /// </summary>
    public static readonly IReadOnlySet<string> UserContentFolderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Saves", "Save", "SaveGames", "Saved Games", "Backups", "Backup", "Projects", "Documents", "My Documents", "Notes", "Vault",
        "Vaults", "Library", "Libraries", "Recordings", "Screenshots", "Exports", "Profiles", "Database", "Databases", "Wallet",
    };

    /// <summary>
    /// File types that are almost always the user's own work. Types apps ship themselves (readme .txt/.md, manual .pdf,
    /// icon .png, sound .wav, data .db) are left out on purpose: they would mark every app folder as "yours".
    /// </summary>
    public static readonly IReadOnlySet<string> UserContentExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp",
        ".psd", ".kra", ".xcf", ".blend", ".heic", ".cr2", ".cr3", ".nef", ".arw", ".dng",
        ".mp4", ".mov", ".mkv", ".sav", ".kdbx", ".wallet",
    };

    public static bool IsGeneric(string name) => GenericNames.Contains(name.Trim());

    public static bool IsKeeper(string name)
    {
        var n = name.Trim();
        return KeeperPatterns.Any(p => PreinstalledApps.PatternMatches(p, n));
    }

    /// <summary>Too short, generic, or on the keeper list: never matched as a leftover.</summary>
    public static bool IsUnsafeName(string? name, out string? why)
    {
        why = null;
        var n = name?.Trim() ?? "";
        if (n.Length < MinimumNameLength) why = "The name is too short to be sure which app owns it.";
        else if (IsKeeper(n)) why = "Windows, drivers or other apps share this folder.";
        else if (IsGeneric(n)) why = "The name is too general to belong to one app.";
        return why is not null;
    }
}
