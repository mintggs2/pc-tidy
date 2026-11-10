using PcTidy.Core;
using PcTidy.Core.Model;

namespace PcTidy.App.Composition.Demo;

/// <summary>
/// Sample scan results for demo mode and screenshots. All fake, all anonymous (user "tester", generic paths).
/// Built with the same rules real scanners follow: Check/Permanent never pre-ticked, permanent = Reversible false.
/// </summary>
public static class DemoData
{
    private const long KB = 1024, MB = 1024 * KB, GB = 1024 * MB;
    private const string Profile = @"C:\Users\tester";
    private const string Local = Profile + @"\AppData\Local";
    private const string Roaming = Profile + @"\AppData\Roaming";
    private const string Downloads = Profile + @"\Downloads";

    private static PlanItem Quarantine(string feature, string path, long size, string what, bool admin = false, bool dir = true, string? skip = null) => new()
    {
        Kind = ActionKind.QuarantinePath, FeatureId = feature, Description = what, SourcePath = path, ExpectedSize = size, IsDirectory = dir, NeedsAdmin = admin,
        Args = skip is null ? null : new Dictionary<string, string> { ["demoSkip"] = skip },
    };

    private static ScanItem Item(string title, string? detail, string reason, Badge badge, long size, IReadOnlyList<PlanItem> actions,
        bool ticked = false, bool admin = false, int files = 0, string? owner = null, IReadOnlyList<string>? closeApps = null, int ageDays = 3, string? disabled = null, bool restart = false, string? state = null)
        => new()
        {
            Title = title, Detail = detail, Reason = reason, Badge = badge, SizeBytes = size, SizeOnDiskBytes = size, Actions = actions,
            DefaultSelected = ticked && badge == Badge.Safe, NeedsAdmin = admin, FileCount = files, OwnerApp = owner, RequiresClosedApps = closeApps,
            LastModifiedUtc = DateTime.UtcNow.AddDays(-ageDays), DisabledReason = disabled, NeedsRestart = restart, CurrentState = state,
        };

    // ------------------------------------------------------------------ Clean

    public static IReadOnlyList<ScanGroup> CleanupGroups()
    {
        const string f = "cleanup";
        var temp = new List<ScanItem>
        {
            Item("Your temp folder", Local + @"\Temp", "Files apps parked while they were working. Nothing reads them after a couple of days.", Badge.Safe, 1434 * MB,
                new[] { Quarantine(f, Local + @"\Temp", 1434 * MB, "Move your old temp files to quarantine") }, ticked: true, files: 2_861, ageDays: 4),
            Item("Windows temp", @"C:\Windows\Temp", "Windows' own scratch folder. Anything still in use is skipped automatically.", Badge.Safe, 702 * MB,
                new[] { Quarantine(f, @"C:\Windows\Temp", 702 * MB, "Windows temp files", admin: true) }, ticked: true, admin: true, files: 418, ageDays: 6),
        };
        for (int i = 1; i <= 39; i++)
        {
            long size = (37 * i % 23 + 2) * MB / 3;
            var name = $"setup-{1000 + i * 17:x4}.tmp";
            temp.Add(Item(name, Local + @"\Temp\" + name, "Leftover from an installer that already finished.", Badge.Safe, size,
                new[] { Quarantine(f, Local + @"\Temp\" + name, size, "Leftover installer file " + name, dir: false) }, ticked: true, files: 1, ageDays: 5 + i));
        }

        return new[]
        {
            new ScanGroup { Id = "user-temp", Title = "Temp files", Badge = Badge.Safe, Description = "Leftovers apps make while installing or running. Windows never needs them again.", Items = temp },
            new ScanGroup
            {
                Id = "browser-cache", Title = "Browser caches", Badge = Badge.Safe,
                Description = "Copies of web pages kept to load sites faster. Rebuilt automatically as you browse.",
                Items = new[]
                {
                    Item("Chrome cache", Local + @"\Google\Chrome\User Data\Default\Cache", "Copies of web pages, rebuilt automatically. Your logins and history are not touched.", Badge.Safe, 1640 * MB,
                        new[] { Quarantine(f, Local + @"\Google\Chrome\User Data\Default\Cache\Cache_Data", 1594 * MB, "Chrome page cache"),
                                Quarantine(f, Local + @"\Google\Chrome\User Data\Default\Code Cache", 46 * MB, "Chrome code cache", skip: "In use by Chrome") },
                        ticked: true, files: 6_203, owner: "Google Chrome", closeApps: new[] { "Chrome" }),
                    Item("Edge cache", Local + @"\Microsoft\Edge\User Data\Default\Cache", "Same as Chrome: saved page copies. Passwords, favourites and history stay.", Badge.Safe, 701 * MB,
                        new[] { Quarantine(f, Local + @"\Microsoft\Edge\User Data\Default\Cache\Cache_Data", 701 * MB, "Edge page cache") }, ticked: true, files: 2_114, owner: "Microsoft Edge"),
                    Item("Firefox cache", Local + @"\Mozilla\Firefox\Profiles\default\cache2", "Page copies only. Your Firefox profile and bookmarks are left alone.", Badge.Safe, 298 * MB,
                        new[] { Quarantine(f, Local + @"\Mozilla\Firefox\Profiles\default\cache2", 298 * MB, "Firefox cache") }, ticked: true, files: 1_480, owner: "Firefox"),
                },
            },
            new ScanGroup
            {
                Id = "crash-dumps", Title = "Crash dumps & error reports", Badge = Badge.Safe,
                Description = "Snapshots saved when apps crashed. Only useful if someone is helping you fix a crash right now.",
                Items = new[]
                {
                    Item("Windows error reports", @"C:\ProgramData\Microsoft\Windows\WER\ReportArchive", "Reports already sent (or never needed) by Windows Error Reporting.", Badge.Safe, 583 * MB,
                        new[] { Quarantine(f, @"C:\ProgramData\Microsoft\Windows\WER\ReportArchive", 583 * MB, "Old Windows error reports", admin: true) }, ticked: true, admin: true, files: 17, ageDays: 41),
                    Item("App crash dumps", Local + @"\CrashDumps", "Memory snapshots from old app crashes.", Badge.Safe, 338 * MB,
                        new[] { Quarantine(f, Local + @"\CrashDumps", 338 * MB, "Old app crash dumps") }, ticked: true, files: 6, ageDays: 22),
                },
            },
            new ScanGroup
            {
                Id = "app-cache", Title = "App caches", Badge = Badge.Safe,
                Description = "Images and media apps download again when needed. You stay signed in.",
                Items = new[]
                {
                    Item("Discord cache", Roaming + @"\discord\Cache", "Images and videos Discord downloads again. Your login is not touched.", Badge.Safe, 412 * MB,
                        new[] { Quarantine(f, Roaming + @"\discord\Cache\Cache_Data", 412 * MB, "Discord cache") }, ticked: true, owner: "Discord"),
                    Item("Steam web cache", Local + @"\Steam\htmlcache", "Steam store pages. Games, saves and settings are not touched.", Badge.Safe, 167 * MB,
                        new[] { Quarantine(f, Local + @"\Steam\htmlcache", 167 * MB, "Steam web cache") }, ticked: true, owner: "Steam"),
                },
            },
            new ScanGroup
            {
                Id = "shader-cache", Title = "Game shader caches", Badge = Badge.Check,
                Description = "Pre-built graphics. Games rebuild them, but the first launch afterwards may stutter for a minute.",
                Items = new[]
                {
                    Item("DirectX shader cache", Local + @"\D3DSCache", "Only worth clearing if it's huge or a game shows graphics glitches.", Badge.Check, 1310 * MB,
                        new[] { Quarantine(f, Local + @"\D3DSCache", 1310 * MB, "DirectX shader cache") }),
                },
            },
            new ScanGroup
            {
                Id = "old-installers", Title = "Old installers in Downloads", Badge = Badge.Check,
                Description = "Setup files for apps you probably installed already. Unticked in case you want to reinstall.",
                Items = Enumerable.Range(1, 23).Select(i =>
                {
                    var name = i switch { 1 => "VideoEditorSetup-4.2.exe", 2 => "PrinterDriver_x64.msi", 3 => "ChatAppSetup.exe", _ => $"installer-{i:00}.msi" };
                    long size = (i * 53 % 160 + 12) * MB;
                    return Item(name, Downloads + @"\" + name, $"Downloaded {30 + i * 4} days ago. Keep it if you can't download it again.", Badge.Check, size,
                        new[] { Quarantine(f, Downloads + @"\" + name, size, "Old installer " + name, dir: false) }, files: 1, ageDays: 30 + i * 4);
                }).ToList(),
            },
            new ScanGroup
            {
                Id = "privacy-extras", Title = "Browser history & cookies", Badge = Badge.Check,
                Description = "Privacy extras. Your saved passwords are never offered.",
                Items = new[]
                {
                    Item("Chrome cookies", Local + @"\Google\Chrome\User Data\Default\Network\Cookies", "Signs you out of every website.", Badge.Check, 6 * MB,
                        new[] { Quarantine(f, Local + @"\Google\Chrome\User Data\Default\Network\Cookies", 6 * MB, "Chrome cookies", dir: false) }, owner: "Google Chrome"),
                    Item("Chrome history", Local + @"\Google\Chrome\User Data\Default\History", "Your list of visited pages goes. Bookmarks stay.", Badge.Check, 38 * MB,
                        new[] { Quarantine(f, Local + @"\Google\Chrome\User Data\Default\History", 38 * MB, "Chrome history", dir: false) }, owner: "Google Chrome"),
                },
            },
            new ScanGroup
            {
                Id = "recycle-bin", Title = "Recycle Bin", Badge = Badge.Permanent,
                Description = "Things you already deleted. Emptying the bin can't be undone, not even by PC Tidy.",
                Items = new[]
                {
                    Item("Recycle Bin", "212 items you deleted", "Files you already deleted. Emptying removes them for good.", Badge.Permanent, 3277 * MB,
                        new[] { new PlanItem { Kind = ActionKind.EmptyRecycleBin, FeatureId = f, Description = "Empty the Recycle Bin (212 items)", ExpectedSize = 3277 * MB, Reversible = false } }, files: 212),
                    Item("Delivery Optimization cache", "Windows Update sharing cache", "Update pieces Windows keeps to share with other PCs. Re-downloaded if needed.", Badge.Check, 905 * MB,
                        new[] { new PlanItem { Kind = ActionKind.RunSystemTool, ToolId = ToolIds.DeliveryOptimizationClear, FeatureId = f, Description = "Clear the Delivery Optimization cache (Windows' own command)", ExpectedSize = 905 * MB, Reversible = false, NeedsAdmin = true } }, admin: true),
                },
            },
            new ScanGroup
            {
                Id = "not-cleaned", Title = "Not cleaned, on purpose", Badge = Badge.Check,
                Description = "Things other cleaners delete that you should keep.",
                Items = new[]
                {
                    Item("Prefetch", @"C:\Windows\Prefetch", "Windows uses it to start your apps faster.", Badge.Check, 61 * MB, Array.Empty<PlanItem>(),
                        disabled: "Kept on purpose: cleaning it makes apps start slower for days."),
                    Item("Saved passwords", "Every browser", "Never offered. Losing them would sign you out everywhere.", Badge.Check, 0, Array.Empty<PlanItem>(),
                        disabled: "Never offered: PC Tidy doesn't touch saved passwords."),
                },
            },
        };
    }

    // ------------------------------------------------------------------ Organize

    public static IReadOnlyList<ScanGroup> OrganizeGroups()
    {
        const string f = "organize";
        ScanGroup Sort(string id, string category, string[] names, string reason)
            => new()
            {
                Id = id, Title = $"Downloads → {category}", Badge = Badge.Safe, Description = reason,
                Items = names.Select((n, i) =>
                {
                    long size = (i * 37 % 90 + 1) * MB / 4;
                    var dest = $@"{Downloads}\{category}\{n}";
                    return Item(n, $@"{Downloads}\{n} → Downloads\{category}", $"Moves into Downloads\\{category}. Same drive, instant, undoable.", Badge.Safe, size,
                        new[] { new PlanItem { Kind = ActionKind.MovePath, FeatureId = f, Description = $"Move {n} to Downloads\\{category}", SourcePath = $@"{Downloads}\{n}", DestinationPath = dest, ExpectedSize = size } },
                        ticked: true, files: 1, ageDays: 2 + i);
                }).ToList(),
            };

        string[] Many(string stem, string ext, int count) => Enumerable.Range(1, count).Select(i => $"{stem}{i:000}.{ext}").ToArray();

        return new[]
        {
            Sort("downloads-pictures", "Pictures", Many("IMG_2026", "jpg", 24), "Photos and images, sorted by type."),
            Sort("downloads-documents", "Documents", Many("document-", "pdf", 31), "PDFs, Word and Excel files."),
            Sort("downloads-archives", "Archives", Many("backup-", "zip", 8), "Zip and other archive files."),
            new ScanGroup
            {
                Id = "downloads-installers-old", Title = "Installers older than 30 days", Badge = Badge.Check,
                Description = "Starter rule: old installers go to quarantine. Unticked: your call.",
                Items = Enumerable.Range(1, 6).Select(i =>
                {
                    var n = $"tool-setup-{i}.exe"; long size = (i * 31 % 70 + 5) * MB;
                    return Item(n, $@"{Downloads}\{n}", "Installer from more than a month ago.", Badge.Check, size,
                        new[] { Quarantine(f, $@"{Downloads}\{n}", size, "Old installer " + n, dir: false) }, files: 1, ageDays: 40 + i);
                }).ToList(),
            },
            new ScanGroup
            {
                Id = "never-moved", Title = "Left where they are", Badge = Badge.Check,
                Description = "Never moved by default.",
                Items = new[]
                {
                    Item("Unfinished download", $@"{Downloads}\movie.mkv.crdownload", "Still downloading.", Badge.Check, 702 * MB, Array.Empty<PlanItem>(), disabled: "Still downloading: left alone."),
                    Item("Shortcuts", $@"{Downloads}\*.lnk", "Shortcuts stay put.", Badge.Check, 12 * KB, Array.Empty<PlanItem>(), disabled: "Shortcuts are never moved unless you say so."),
                },
            },
        };
    }

    // ------------------------------------------------------------------ Speed / Tweaks / Games

    private static PlanItem Startup(string name, bool enable = false) => new()
    {
        Kind = ActionKind.StartupItemSetEnabled, FeatureId = "speed", Description = $"Stop {name} starting with Windows", Enable = enable,
        Args = new Dictionary<string, string> { ["location"] = "HKCU Run", ["name"] = name },
    };

    public static IReadOnlyList<ScanGroup> SpeedGroups() => new[]
    {
        new ScanGroup
        {
            Id = "startup-high", Title = "Slow startup apps", Badge = Badge.Safe, Description = "High impact, not from Microsoft. You can still open them any time.",
            Items = new[]
            {
                Item("Music player helper", "Starts with Windows", "Adds about 3 seconds to sign-in. Opens fine when you start it yourself.", Badge.Safe, 0, new[] { Startup("Music player helper") }, ticked: true, state: "High impact"),
                Item("Game launcher", "Starts with Windows", "Adds about 4 seconds. Games still work: the launcher opens when you start one.", Badge.Check, 0, new[] { Startup("Game launcher") }, state: "High impact"),
            },
        },
        new ScanGroup
        {
            Id = "startup-other", Title = "Other startup apps", Badge = Badge.Check, Description = "Lower impact. Leave on anything you rely on.",
            Items = new[]
            {
                Item("Chat app", "Starts with Windows", "Medium impact. You'd miss messages until you open it.", Badge.Check, 0, new[] { Startup("Chat app") }, state: "Medium impact"),
                Item("Cloud sync", "Starts with Windows", "Keeps your files in sync. Leave it on.", Badge.Check, 0, new[] { Startup("Cloud sync") }, state: "Low impact"),
            },
        },
    };

    private static PlanItem Reg(string what, string key, string value, string data, bool admin = false) => new()
    {
        Kind = ActionKind.RegistrySetValue, FeatureId = "tweaks", Description = what, NeedsAdmin = admin, IsSystemChange = admin,
        Registry = new RegistryValueSpec { Hive = admin ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, KeyPath = key, ValueName = value, Data = data },
    };

    public static IReadOnlyList<ScanGroup> TweakGroups() => new[]
    {
        new ScanGroup
        {
            Id = "privacy-basic", Title = "Privacy basics", Badge = Badge.Safe, Description = "Fewer ads and less tracking. Nothing breaks.",
            Items = new[]
            {
                Item("Turn off the advertising ID", "Settings → Privacy", "Apps can't use it to show you personalised ads.", Badge.Safe, 0,
                    new[] { Reg("Turn off the advertising ID", @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", "0") }, ticked: true, state: "On"),
                Item("Turn off tailored experiences", "Settings → Privacy", "Windows stops using diagnostic data for tips and ads.", Badge.Safe, 0,
                    new[] { Reg("Turn off tailored experiences", @"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", "0") }, ticked: true, state: "On"),
            },
        },
        new ScanGroup
        {
            Id = "clean-explorer", Title = "Clean Explorer", Badge = Badge.Safe, Description = "Small changes that make File Explorer clearer.",
            Items = new[]
            {
                Item("Show file extensions", "File Explorer", "See .pdf, .exe and so on. Makes fake files easier to spot.", Badge.Safe, 0,
                    new[] { Reg("Show file extensions", @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "HideFileExt", "0") }, ticked: true, state: "Hidden"),
            },
        },
        new ScanGroup
        {
            Id = "safe-gaming", Title = "Safe gaming", Badge = Badge.Safe, Description = "Proven settings for smoother games.",
            Items = new[]
            {
                Item("Game Mode on", "Settings → Gaming", "Windows gives the game priority while you play.", Badge.Safe, 0,
                    new[] { Reg("Turn Game Mode on", @"Software\Microsoft\GameBar", "AutoGameModeEnabled", "1") }, ticked: true, state: "Off"),
                Item("Hardware-accelerated GPU scheduling", "Settings → Display → Graphics", "Can lower input lag on newer graphics cards. Needs a restart.", Badge.Check, 0,
                    new[] { Reg("Turn on GPU scheduling", @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", "2", admin: true) }, admin: true, restart: true, state: "Off"),
            },
        },
        new ScanGroup
        {
            Id = "placebo", Title = "Doesn't help (shown so you know)", Badge = Badge.Check, Description = "Popular tweaks that do nothing on modern Windows.",
            Items = new[]
            {
                Item("Disable Nagle's algorithm", "Network", "Often recommended for games.", Badge.Check, 0, Array.Empty<PlanItem>(),
                    disabled: "Doesn't help: modern games already turn it off for themselves."),
            },
        },
    };

    // ------------------------------------------------------------------ v3: Privacy sweep, Notifications

    private static PlanItem DemoCookies(string browser, string profile) => new()
    {
        Kind = ActionKind.CookiesDeleteExcept, FeatureId = "privacy", Description = $"Delete {browser} cookies ({profile}) except 2 sites you keep",
        Args = new Dictionary<string, string> { ["browser"] = browser.ToLowerInvariant(), ["profileDir"] = profile, ["keepHosts"] = "github.com\ngoogle.com" },
        RequiresClosedApps = new[] { browser.ToLowerInvariant() },
    };

    public static IReadOnlyList<ScanGroup> PrivacyGroups() => new[]
    {
        new ScanGroup
        {
            Id = "recent", Title = "Recent files and jump lists", Badge = Badge.Safe, Description = "Lists of what you opened lately. The files themselves stay.",
            Items = new[]
            {
                Item("Recent files list", "File Explorer · 48 entries", "Explorer's list of files you opened. It refills as you work.", Badge.Safe, 96 * 1024,
                    new[] { Quarantine("privacy", @"C:\Users\demo\AppData\Roaming\Microsoft\Windows\Recent", 96 * 1024, "Recent files list") }, state: "48 entries"),
                Item("Jump lists", "Taskbar and Start · 12 apps", "The recent items in each app's right-click menu. Your pinned items stay.", Badge.Safe, 210 * 1024,
                    new[] { Quarantine("privacy", @"C:\Users\demo\AppData\Roaming\Microsoft\Windows\Recent\AutomaticDestinations", 210 * 1024, "Jump lists") }),
            },
        },
        new ScanGroup
        {
            Id = "browsers", Title = "Browsers", Badge = Badge.Check, Description = "Cookies use the Cookie keeper: everything goes except the sites you choose. Saved passwords are never touched.",
            Items = new[]
            {
                Item("Chrome cookies (Default)", "Keeps github.com and google.com", "Signs you out of every website except github.com and google.com. Saved passwords and history stay.", Badge.Check, 0,
                    new[] { DemoCookies("Chrome", "Default") }, owner: "Chrome", state: "Keeps 2 sites"),
                Item("Edge cookies (Default)", "Keeps github.com and google.com", "Signs you out of every website except github.com and google.com. Saved passwords and history stay.", Badge.Check, 0,
                    new[] { DemoCookies("Edge", "Default") }, owner: "Edge", state: "Keeps 2 sites"),
            },
        },
    };

    private static PlanItem DemoSwitch(string what, string key, string value) => Reg(what, key, value, "0") with { FeatureId = "notifications" };

    public static IReadOnlyList<ScanGroup> NotificationGroups() => new[]
    {
        new ScanGroup
        {
            Id = "notifications.apps", Title = "Apps that can send notifications", Badge = Badge.Check,
            Description = "Tick an app to flip its switch. The app keeps working either way, and Undo puts each switch back exactly.",
            Items = new[]
            {
                Item("Contoso Chat", "Notifications on", "Turns its pop-ups off. Messages still arrive in the app.", Badge.Check, 0,
                    new[] { DemoSwitch("Turn off notifications from Contoso Chat", @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\Contoso.Chat", "Enabled") }, state: "On"),
                Item("Fabrikam Music", "Notifications on", "Turns its pop-ups off. Playback controls stay.", Badge.Check, 0,
                    new[] { DemoSwitch("Turn off notifications from Fabrikam Music", @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\Fabrikam.Music", "Enabled") }, state: "On"),
            },
        },
        new ScanGroup
        {
            Id = "notifications.tips", Title = "Windows tips, suggestions and ads", Badge = Badge.Safe, Description = "Windows' own suggestions. Turning them off changes nothing else.",
            Items = new[]
            {
                Item("Turn off: tips after updates", "Settings → System → Notifications", "No more \"Get even more out of Windows\" screens.", Badge.Safe, 0,
                    new[] { DemoSwitch("Turn off tips after updates", @"Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement", "ScoobeSystemSettingEnabled") }, ticked: true, state: "On"),
                Item("Turn off: suggested content in Settings", "Settings → Privacy → General", "Settings stops showing suggested apps and content.", Badge.Safe, 0,
                    new[] { DemoSwitch("Turn off suggested content in Settings", @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338393Enabled") }, ticked: true, state: "On"),
            },
        },
    };

    public static IReadOnlyList<ScanGroup> GameGroups() => new[]
    {
        new ScanGroup
        {
            Id = "old-clips", Title = "Old game clips", Badge = Badge.Check, Description = "Recordings older than 6 months. Check the previews first.",
            Items = Enumerable.Range(1, 14).Select(i =>
            {
                var n = $"Clip {i:00} - match highlight.mp4"; long size = (i * 47 % 300 + 80) * MB;
                var p = Profile + @"\Videos\Captures\" + n;
                return Item(n, p, $"Recorded {190 + i * 9} days ago.", Badge.Check, size, new[] { Quarantine("games", p, size, "Old clip " + n, dir: false) }, files: 1, ageDays: 190 + i * 9);
            }).ToList(),
        },
        new ScanGroup
        {
            Id = "unplayed", Title = "Games you haven't played in 6 months", Badge = Badge.Check, Description = "Suggestions only. Uninstall or move through the launcher.",
            Items = new[]
            {
                Item("Racing game", "Steam library", "Last played 9 months ago. Use Steam → Uninstall or Move.", Badge.Check, 61 * GB, Array.Empty<PlanItem>()),
                Item("Puzzle game", "Steam library", "Last played 1 year ago.", Badge.Check, 4 * GB, Array.Empty<PlanItem>()),
            },
        },
    };

    // ------------------------------------------------------------------ History seed

    /// <summary>A few believable past runs and their quarantine entries.</summary>
    public static void SeedHistory(DemoRunJournal journal, DemoQuarantineStore quarantine)
    {
        var now = DateTime.UtcNow;
        AddRun(journal, quarantine, "cleanup", "Clean", now.AddDays(-1).AddHours(-3), RunStatus.CompletedWithSkips, quarantineSteps: 128, totalBytes: 6_660L * MB, skipped: 3, skipReason: "In use by Chrome");
        AddRun(journal, quarantine, "organize", "Organize", now.AddDays(-3).AddHours(-1), RunStatus.Completed, moveSteps: 241);
        AddRun(journal, quarantine, "cleanup", "Clean", now.AddDays(-9), RunStatus.Completed, quarantineSteps: 41, totalBytes: 2_150L * MB);
        AddRun(journal, quarantine, "tweaks", "Tweaks", now.AddDays(-12), RunStatus.Undone, settingSteps: 4, keepInQuarantine: false);
    }

    /// <summary>A run that "lost power" half way (shows the Resume / Roll back banner).</summary>
    public static void AddInterruptedRun(DemoRunJournal journal, DemoQuarantineStore quarantine)
        => AddRun(journal, quarantine, "organize", "Organize Desktop", DateTime.UtcNow.AddHours(-2), RunStatus.Interrupted, moveSteps: 40, pendingFrom: 12);

    private static void AddRun(DemoRunJournal journal, DemoQuarantineStore quarantine, string feature, string title, DateTime when, RunStatus status,
        int quarantineSteps = 0, long totalBytes = 0, int moveSteps = 0, int settingSteps = 0, int skipped = 0, string? skipReason = null,
        bool keepInQuarantine = true, int pendingFrom = int.MaxValue)
    {
        var runId = when.ToLocalTime().ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
        var steps = new List<StepRecord>();
        long each = quarantineSteps > 0 ? totalBytes / quarantineSteps : 0;
        for (int i = 0; i < quarantineSteps; i++)
        {
            var path = $@"{Local}\Temp\old-{i:000}.tmp";
            var item = Quarantine(feature, path, each, $"Temp file old-{i:000}.tmp", dir: false);
            bool skip = i < skipped;
            steps.Add(new StepRecord { Index = steps.Count, Item = item, Status = skip ? StepStatus.Skipped : status == RunStatus.Undone ? StepStatus.Undone : StepStatus.Done, Message = skip ? skipReason : "Moved to quarantine", BytesAffected = skip ? 0 : each, StartedUtc = when, FinishedUtc = when });
            if (!skip && keepInQuarantine && status != RunStatus.Undone)
                quarantine.Add(new QuarantineEntry { Id = Guid.NewGuid().ToString("N"), RunId = runId, OriginalPath = path, StoredPath = @"(demo)\" + i, SizeBytes = each, FileCount = 1, QuarantinedUtc = when });
        }
        for (int i = 0; i < moveSteps; i++)
        {
            var n = $"file-{i:000}.jpg";
            var item = new PlanItem { Kind = ActionKind.MovePath, FeatureId = feature, Description = $"Move {n} to Pictures", SourcePath = $@"{Downloads}\{n}", DestinationPath = $@"{Downloads}\Pictures\{n}", ExpectedSize = 2 * MB };
            var st = i >= pendingFrom ? StepStatus.Pending : i == pendingFrom - 1 ? StepStatus.Started : StepStatus.Done;
            steps.Add(new StepRecord { Index = steps.Count, Item = item, Status = st, BytesAffected = st == StepStatus.Done ? 2 * MB : 0, StartedUtc = when });
        }
        for (int i = 0; i < settingSteps; i++)
            steps.Add(new StepRecord { Index = steps.Count, Item = Reg($"Sample setting {i + 1}", @"Software\Sample", "Value" + i, "0"), Status = status == RunStatus.Undone ? StepStatus.Undone : StepStatus.Done, StartedUtc = when });

        journal.Seed(new RunRecord
        {
            RunId = runId, FeatureId = feature, Title = title, Mode = ExecutionMode.Real, CreatedUtc = when,
            FinishedUtc = status == RunStatus.Interrupted ? null : when.AddMinutes(2).AddSeconds(14), Status = status, Steps = steps, AppVersion = "1.0.0",
            RestorePoint = settingSteps > 0 ? new RestorePointInfo(true, false, "PC Tidy", when, null) : null,
        });
    }
}
