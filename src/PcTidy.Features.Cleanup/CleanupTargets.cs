using PcTidy.Core;
using PcTidy.Core.Model;

namespace PcTidy.Features.Cleanup;

/// <summary>One row of Plan §7: the group's id (= its carve-out id), name, badge, default and the reason shown.</summary>
/// <param name="Number">Row number in Plan §7 (also the display order).</param>
/// <param name="DefaultSelected">Ticked after a scan. Only ever true for <see cref="Badge.Safe"/> rows.</param>
public sealed record CleanupTargetInfo(int Number, string Id, string Title, Badge Badge, bool DefaultSelected, string Reason, string? Description = null)
{
    /// <summary>
    /// The line under the group title: always the plain "why it's safe" reason, then the extra hint (age rule,
    /// how it's cleared) when there is one. A description that already starts with the reason is used as is.
    /// </summary>
    public string GroupText => Description switch
    {
        null or "" => Reason,
        { } d when d.StartsWith(Reason, StringComparison.Ordinal) => d,
        { } d => $"{Reason} {d}",
    };
}

/// <summary>The exact cleanup list from Plan §7, in order. Single source of the group ids, badges, defaults and reasons.</summary>
public static class CleanupTargets
{
    public const string TempReason = "Leftovers apps meant to delete. Nothing needs them after a couple of days.";
    public const string BrowserCacheReason = "Copies of web pages and images. The browser re-downloads them. Your logins, passwords, history and bookmarks are not touched.";
    public const string CookiesReason = "Cookies: signs you out of every website.";
    public const string HistoryReason = "History: your browsing list is gone.";
    public const string CrashDumpReason = "Memory snapshots from past crashes. Only useful if you're troubleshooting a crash right now.";
    public const string SpotifyReason = "Songs Spotify streamed. Use Spotify's button so offline downloads are kept.";

    public static readonly CleanupTargetInfo UserTemp = new(1, CarveOuts.UserTemp, "Your temp files", Badge.Safe, true, TempReason);
    public static readonly CleanupTargetInfo WindowsTemp = new(2, CarveOuts.WindowsTemp, "Windows temp files", Badge.Safe, true, TempReason);
    public static readonly CleanupTargetInfo BrowserCache = new(3, CarveOuts.BrowserCache, "Chrome, Edge and Brave cache", Badge.Safe, true, BrowserCacheReason);
    public static readonly CleanupTargetInfo FirefoxCache = new(4, CarveOuts.FirefoxCache, "Firefox cache", Badge.Safe, true, BrowserCacheReason);
    public static readonly CleanupTargetInfo InetCache = new(5, CarveOuts.InetCache, "Old Windows web cache", Badge.Safe, true,
        "Cache used by older Windows web components. Rebuilt automatically.");
    public static readonly CleanupTargetInfo BrowserPrivacy = new(6, CarveOuts.BrowserPrivacy, "Privacy extras", Badge.Check, false,
        CookiesReason + " " + HistoryReason,
        "Cookies: signs you out of every website. History: your browsing list is gone. Saved passwords, Local State and site data are never offered.");
    public static readonly CleanupTargetInfo CrashDumps = new(7, CarveOuts.CrashDumps, "Crash dumps", Badge.Safe, true, CrashDumpReason,
        "Ticked only when older than 14 days.");
    public static readonly CleanupTargetInfo ErrorReports = new(8, CarveOuts.ErrorReports, "Error reports", Badge.Safe, true,
        "Reports about app crashes, already sent (or never sent) to Microsoft.", "Older than 30 days.");
    public static readonly CleanupTargetInfo WindowsLogs = new(9, CarveOuts.WindowsLogs, "Old Windows logs", Badge.Check, false,
        "Old setup/repair logs. Only needed for troubleshooting Windows Update.", "30+ days old. The live log is never touched.");
    public static readonly CleanupTargetInfo UpdateDownloads = new(10, CarveOuts.UpdateDownloads, "Windows Update downloads", Badge.Check, false,
        "Update files Windows already installed. It re-downloads anything it still needs.", "10+ days old. Skipped while an update is installing or a restart is pending.");
    public static readonly CleanupTargetInfo DeliveryOptimization = new(11, CarveOuts.DeliveryOptimization, "Delivery Optimization cache", Badge.Check, false,
        "Update pieces Windows keeps to share with other PCs. Re-downloaded if needed.", "Cleared with Windows' own command. Not restorable.");
    public static readonly CleanupTargetInfo ShaderCache = new(12, CarveOuts.ShaderCache, "GPU shader caches", Badge.Check, false,
        "Pre-built graphics for your games. Games stutter for a few minutes while they rebuild it. Only worth it if it's huge or a game has graphics glitches.",
        "Close your games first.");
    public static readonly CleanupTargetInfo ThumbnailCache = new(13, CarveOuts.ThumbnailCache, "Thumbnail and icon cache", Badge.Check, false,
        "Small previews of your pictures. Folders show previews slowly for a bit.", "Cleared with Windows' own Thumbnail cleaner. Not restorable.");
    public static readonly CleanupTargetInfo RecycleBin = new(14, CarveOuts.RecycleBin, "Recycle Bin", Badge.Permanent, false,
        "Files you already deleted. Emptying removes them for good.", "Can't be undone.");
    public static readonly CleanupTargetInfo Discord = new(15, CarveOuts.Discord, "Discord cache", Badge.Safe, true,
        "Images and videos Discord re-downloads. Your login is not touched.");
    public static readonly CleanupTargetInfo Steam = new(16, CarveOuts.SteamCache, "Steam web cache and dumps", Badge.Safe, true,
        "Steam store/browser cache and crash dumps. Games, saves and settings are not touched.");
    public static readonly CleanupTargetInfo Spotify = new(17, CarveOuts.Spotify, "Spotify cache", Badge.Check, false, SpotifyReason,
        "Clear it in Spotify: Settings > Storage > Clear cache.");
    public static readonly CleanupTargetInfo Telegram = new(18, CarveOuts.Telegram, "Telegram media cache", Badge.Check, false,
        "Photos/videos from chats, re-downloaded when you open them. Your login is not touched.");
    public static readonly CleanupTargetInfo OldInstallers = new(19, CarveOuts.OldInstallers, "Old installers in Downloads", Badge.Check, false,
        "Installers for apps you've probably already installed. Unticked in case you want to reinstall.", "30+ days old, each listed.");
    public static readonly CleanupTargetInfo WindowsOld = new(20, CarveOuts.WindowsOld, "Previous Windows install", Badge.Permanent, false,
        "Your old Windows version, kept so you can roll back. Removing it means you can't go back. Windows deletes it itself after about 10 days.",
        "Can't be undone. A restore point is made first.");
    public static readonly CleanupTargetInfo ComponentStore = new(21, CarveOuts.ComponentStore, "Windows component cleanup", Badge.Permanent, false,
        "Removes old versions of Windows components that updates replaced. Windows does this slowly by itself; this just does it now.",
        "Can't be undone. A restore point is made first. Takes 5 to 30 minutes.");

    /// <summary>All 21 rows in Plan §7 order.</summary>
    public static readonly IReadOnlyList<CleanupTargetInfo> All = new[]
    {
        UserTemp, WindowsTemp, BrowserCache, FirefoxCache, InetCache, BrowserPrivacy, CrashDumps, ErrorReports,
        WindowsLogs, UpdateDownloads, DeliveryOptimization, ShaderCache, ThumbnailCache, RecycleBin, Discord,
        Steam, Spotify, Telegram, OldInstallers, WindowsOld, ComponentStore,
    };
}
