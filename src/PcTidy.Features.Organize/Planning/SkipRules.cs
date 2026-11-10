using PcTidy.Core;

namespace PcTidy.Features.Organize.Planning;

public enum SkipKind
{
    Shortcut,
    SystemFile,
    Hidden,
    Link,
    StillDownloading,
    RecentlyChanged,
    InUse,
    OnlineOnly,
    Folder,
    ProjectOrApp,
    CloudFolder,
    Protected,
    AlreadySorted,
    NoRule,
    LeaveInPlace,
    PortableApp,
    NameClash,
    AlreadyThere,
    RuleBlocked,
    Unreadable,
}

/// <summary>Plan §9 "Never moved by default": checks that need only the directory entry (no file opened).</summary>
public static class SkipRules
{
    public static readonly TimeSpan QuietPeriod = TimeSpan.FromMinutes(10);

    public static readonly IReadOnlySet<string> ShortcutExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "lnk", "url", "website", "appref-ms" };

    public static readonly IReadOnlySet<string> PartialDownloadExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "crdownload", "part", "partial", "opdownload", "download", "!ut", "!qb", "bc!", "tmp" };

    private static readonly IReadOnlySet<string> ShellFiles =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "desktop.ini", "thumbs.db" };

    /// <summary>Why this loose file must stay put, or null if it may be considered by the rules.</summary>
    /// <param name="quietByWriteTimeOnly">
    /// Judge "recently changed" by the modified time alone. For freshly unpacked archives (Google Takeout): the
    /// unpacker sets each finished file's modified time from the archive, while a file still being written shows "now".
    /// </param>
    public static (SkipKind Kind, string Reason)? Check(FileEntry e, DateTime nowUtc, bool quietByWriteTimeOnly = false)
    {
        var ext = Rules.OrganizeCategories.ExtensionOf(e.Name);
        if (e.IsReparsePoint) return (SkipKind.Link, "It's a link to somewhere else, so it's never moved.");
        if (ShortcutExtensions.Contains(ext)) return (SkipKind.Shortcut, "Shortcuts are never moved.");
        if (ShellFiles.Contains(e.Name)) return (SkipKind.SystemFile, "Windows uses this file for the folder's look.");
        if (e.IsSystem) return (SkipKind.SystemFile, "System file. Never moved.");
        if (e.IsHidden) return (SkipKind.Hidden, "Hidden file. Left alone.");
        if (e.IsCloudPlaceholder) return (SkipKind.OnlineOnly, "Online-only OneDrive file. Never downloaded or moved.");
        if (PartialDownloadExtensions.Contains(ext) || e.Name.StartsWith("~$", StringComparison.Ordinal) || e.Name.StartsWith(".~lock", StringComparison.OrdinalIgnoreCase))
            return (SkipKind.StillDownloading, "Still downloading or a temporary file.");
        if (nowUtc - (quietByWriteTimeOnly ? e.LastWriteUtc : e.LastTouchedUtc) < QuietPeriod)
            return (SkipKind.RecentlyChanged, "Changed in the last 10 minutes. It may still be in use.");
        return null;
    }
}
