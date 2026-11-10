using System.Diagnostics;
using PcTidy.Features.Privacy.Common;

namespace PcTidy.Features.Privacy.Sweep;

/// <summary>
/// Privacy sweep (Plan "More approved features"): opt-in per item, nothing ticked. Recent files list, jump lists and
/// File Explorer's Quick access history are quarantined file by file (restorable); clipboard and activity history can
/// only be cleared by Windows itself, so they're shown with a button to the right Settings page; browser history lives
/// in Clean → Privacy extras (cleanup carve-outs belong to Clean), and cookies use the Cookie keeper so the sites you
/// choose stay signed in. Saved passwords are never touched. Read-only: it only builds PlanItems.
/// </summary>
public sealed class PrivacySweepScanner : IFeatureScanner
{
    public string FeatureId => PrivacyIds.Privacy;
    public string Title => "Privacy sweep";
    public Release Release => Release.V3;
    /// <summary>Never part of "Optimize everything": every row signs you out or loses a list, so each is your own pick.</summary>
    public bool IncludedInOptimizeAll => false;

    public Task<ScanResult> ScanAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
        => Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            var notes = new List<string>();
            progress?.Report(new ScanProgress("Looking at recent files and jump lists", 0.1));
            var recent = RecentGroup(ctx, notes, ct);
            progress?.Report(new ScanProgress("Looking at browser cookies", 0.6));
            var browsers = BrowsersGroup(ctx, notes);
            var groups = new List<ScanGroup>();
            if (recent.Items.Count > 0) groups.Add(recent);
            groups.Add(WindowsGroup());
            groups.Add(browsers);
            progress?.Report(new ScanProgress("Done", 1));
            return new ScanResult { FeatureId = FeatureId, Title = Title, Depth = ctx.Depth, Duration = sw.Elapsed, Groups = groups, Notes = notes };
        }, ct);

    // ------------------------------------------------------------------ recent activity

    private static ScanGroup RecentGroup(ScanContext ctx, List<string> notes, CancellationToken ct)
    {
        var items = new List<ScanItem>();
        var recent = RecentActivity.RecentFiles(ctx, ct);
        var jump = RecentActivity.JumpLists(ctx, ct);
        var quick = RecentActivity.QuickAccess(ctx, ct);
        int days = ctx.Settings.QuarantineDays;

        if (recent.Files.Count > 0)
            items.Add(FilesItem("Recent files list", recent,
                "Start's and File Explorer's \"Recent\" lists start empty. Only the shortcuts go: your files themselves aren't touched, and Windows builds a new list as you open things.",
                "The list of files you opened recently (and the shortcuts behind it).", days, "Clear recent file {0}"));
        if (jump.Files.Count > 0)
            items.Add(FilesItem("Jump lists", jump,
                "The recent files each app shows when you right-click it on the taskbar or in Start. Items you pinned in those menus are lost too.",
                "Recent and pinned items in every app's right-click menu (taskbar and Start).", days, "Clear jump list {0}"));
        if (quick.Files.Count > 0)
            items.Add(FilesItem("File Explorer Quick access history", quick,
                "Quick access forgets its frequent folders and recent files. Folders you pinned to Quick access are unpinned too: Undo brings them back.",
                "Frequent folders, recent files and your Quick access pins.", days, "Clear Quick access history ({0})"));

        int skipped = recent.Skipped + jump.Skipped + quick.Skipped;
        if (skipped > 0)
            notes.Add($"{skipped:N0} recent-activity file{(skipped == 1 ? " was" : "s were")} left alone (a link, an online-only file, or a protected place).");
        return new ScanGroup
        {
            Id = PrivacyIds.RecentGroup, Title = "Recent activity on this PC",
            Description = "Lists Windows keeps of what you opened. They're moved to quarantine, so Undo puts them back exactly.",
            Badge = Badge.Check, Items = items,
        };
    }

    private static ScanItem FilesItem(string title, RecentList list, string reason, string whatsLost, int days, string stepFormat)
        => new()
        {
            Title = title,
            Detail = list.Folder,
            Reason = reason,
            Badge = Badge.Check,
            DefaultSelected = false,
            SizeBytes = list.Files.Sum(f => f.Size),
            SizeOnDiskBytes = list.Files.Sum(f => f.Size),
            FileCount = list.Files.Count,
            LastModifiedUtc = list.Files.Max(f => f.LastWriteUtc),
            OwnerApp = "Windows",
            Details = new Dictionary<string, string>
            {
                ["What's lost"] = whatsLost,
                ["Files"] = $"{list.Files.Count:N0}",
                ["Undo"] = $"Everything waits in quarantine for {days} days; Undo in History puts it back.",
            },
            Actions = list.Files.Select(f => PrivacyPlanItems.Quarantine(PrivacyIds.Privacy, f, string.Format(stepFormat, f.Name))).ToList(),
        };

    // ------------------------------------------------------------------ Windows-managed history

    /// <summary>Shown so you know: only Windows can clear these, so the page's buttons open the right Settings page.</summary>
    private static ScanGroup WindowsGroup() => new()
    {
        Id = PrivacyIds.WindowsGroup, Title = "Cleared in Windows Settings",
        Description = "Windows doesn't let other apps clear these. Use the buttons at the top of this page to open the right page.",
        Badge = Badge.Safe,
        Items = new[]
        {
            new ScanItem
            {
                Title = "Clipboard history",
                Detail = "Settings → System → Clipboard → Clear",
                Reason = "Win+V keeps the last things you copied, which can include text copied from a password manager. Clearing removes them; pinned clips stay.",
                DisabledReason = "Only Windows can clear this. Use \"Clipboard history\" at the top of the page, then press Clear.",
                Details = new Dictionary<string, string> { ["What's lost"] = "Everything you copied recently (except pinned clips)." },
            },
            new ScanItem
            {
                Title = "Activity history",
                Detail = "Settings → Privacy & security → Activity history → Clear history",
                Reason = "Windows' record of the apps and files you used on this PC. Clearing it doesn't touch the apps or files.",
                DisabledReason = "Only Windows can clear this. Use \"Activity history\" at the top of the page, then press Clear history.",
                Details = new Dictionary<string, string> { ["What's lost"] = "The record of which apps and files you used, on this PC." },
            },
        },
    };

    // ------------------------------------------------------------------ browsers

    private static ScanGroup BrowsersGroup(ScanContext ctx, List<string> notes)
    {
        var keep = PrivacySettings.Load(ctx.Settings).KeepHosts();
        var items = new List<ScanItem>
        {
            new()
            {
                Title = "Browser history",
                Detail = "Clean → Privacy extras",
                Reason = "Each browser's list of pages you visited. Saved passwords, bookmarks and settings are never touched.",
                DisabledReason = "This lives in Clean → Privacy extras, so it's in one place. Use \"Browser history\" at the top of the page to go there.",
                Details = new Dictionary<string, string> { ["What's lost"] = "The pages you visited, per browser profile (restorable from quarantine)." },
            },
        };
        var open = new HashSet<string>();
        foreach (var p in CookieKeeper.FindProfiles(ctx.Locations))
        {
            var description = keep.Count == 0
                ? $"Delete all {p.Browser} cookies ({p.ProfileName})"
                : $"Delete {p.Browser} cookies ({p.ProfileName}) except {keep.Count} site{(keep.Count == 1 ? "" : "s")} you keep";
            var action = PrivacyPlanItems.CookiesExcept(PrivacyIds.Privacy, p.BrowserArg, p.ProfileDir, keep, p.Process, description);
            var verdict = ctx.Safety.Check(action);
            if (ctx.RunningProcesses.Contains(p.Process)) open.Add(p.Browser);
            items.Add(new ScanItem
            {
                Title = $"{p.Browser} cookies ({p.ProfileName})",
                Detail = p.ProfileDir,
                Reason = keep.Count == 0
                    ? "Signs you out of every website in this browser profile. Pick sites that stay signed in at the top of the page first. Saved passwords stay."
                    : $"Signs you out of every website except {Summary(keep)}. Saved passwords and history stay.",
                Badge = Badge.Check,
                DefaultSelected = false,
                OwnerApp = p.Browser,
                RequiresClosedApps = new[] { p.Process },
                CurrentState = keep.Count == 0 ? "No sites kept" : $"Keeps {keep.Count} site{(keep.Count == 1 ? "" : "s")}",
                Details = new Dictionary<string, string>
                {
                    ["What's lost"] = "Website sign-ins and site preferences stored in cookies, except for the sites you keep.",
                    ["Sites kept"] = keep.Count == 0 ? "None" : string.Join(", ", keep),
                    ["Undo"] = "The old cookie database is kept in quarantine; Undo puts it back (close the browser first).",
                },
                DisabledReason = verdict.Allowed ? null : verdict.Reason,
                Actions = new[] { action },
            });
        }
        if (open.Count > 0)
            notes.Add($"Close {string.Join(" and ", open.OrderBy(b => b))} before clearing its cookies, or that step is skipped.");
        return new ScanGroup
        {
            Id = PrivacyIds.BrowsersGroup, Title = "Browsers",
            Description = "Cookies use the Cookie keeper: everything goes except the sites you choose. Saved passwords are never touched.",
            Badge = Badge.Check, Items = items,
        };
    }

    private static string Summary(IReadOnlyList<string> keep)
        => keep.Count <= 3 ? string.Join(", ", keep) : $"{string.Join(", ", keep.Take(3))} and {keep.Count - 3} more";
}
