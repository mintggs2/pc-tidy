namespace PcTidy.Catalog.Insights;

/// <summary>Small lookup lists the Insights cards use (data only).</summary>
public static class InsightsData
{
    /// <summary>
    /// Backup apps recognised by their Uninstall display name (Feature census C6: File History, Veeam, restic, Duplicati…).
    /// Sync tools are deliberately not here: a synced folder is not a backup.
    /// </summary>
    public static readonly IReadOnlyList<string> BackupApps = new[]
    {
        "Veeam Agent", "Macrium Reflect", "Acronis", "Duplicati", "Backblaze", "Carbonite", "EaseUS Todo Backup", "AOMEI Backupper",
        "Paragon Backup", "Arq", "Cobian", "Kopia", "IDrive", "Duplicacy", "UrBackup", "Iperius Backup",
    };

    /// <summary>Sync tools: they copy deletions too, so they're explained as "not a backup".</summary>
    public static readonly IReadOnlyList<string> SyncApps = new[]
    {
        "Dropbox", "Google Drive", "Syncthing", "pCloud", "MEGAsync", "Box Drive", "Nextcloud", "iCloud",
    };

    /// <summary>
    /// File types that are already compressed: NTFS compression gains nothing on them (Feature census C2 "NTFS compression").
    /// </summary>
    public static readonly IReadOnlySet<string> AlreadyCompressedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".avif", ".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v", ".wmv",
        ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".flac", ".wma", ".zip", ".7z", ".rar", ".gz", ".tgz", ".bz2", ".xz", ".zst",
        ".cab", ".msi", ".msix", ".appx", ".iso", ".docx", ".xlsx", ".pptx", ".odt", ".epub", ".pdf", ".jar", ".apk", ".vhdx", ".pak",
    };

    /// <summary>Package caches Microsoft's Dev Drive page suggests moving (with the variable that moves each one).</summary>
    public static readonly IReadOnlyList<(string Name, string RelativeToProfile, string Variable)> DevCaches = new[]
    {
        ("npm cache", @"AppData\Local\npm-cache", "npm_config_cache"),
        ("NuGet packages", @".nuget\packages", "NUGET_PACKAGES"),
        ("pip cache", @"AppData\Local\pip\Cache", "PIP_CACHE_DIR"),
        ("Cargo", @".cargo", "CARGO_HOME"),
        ("Gradle", @".gradle", "GRADLE_USER_HOME"),
        ("Maven", @".m2\repository", "MAVEN_OPTS (-Dmaven.repo.local)"),
        ("vcpkg binary cache", @"AppData\Local\vcpkg\archives", "VCPKG_DEFAULT_BINARY_CACHE"),
    };
}
