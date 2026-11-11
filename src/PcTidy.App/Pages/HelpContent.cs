namespace PcTidy.App.Pages;

/// <summary>
/// Help / FAQ / About text. Plain words for non-technical people.
/// LEAD: feature modules can supply better text, e.g. <c>HelpContent.WhatWeDontClean = CleanupModule.WhatWeDontCleanText;</c>
/// in AppServices.Register.
/// </summary>
public static class HelpContent
{
    /// <summary>The FAQ entry that also offers an "Open PC Tidy Rescue" button.</summary>
    public const string SomethingWentWrong = "Something went wrong?";

    public const string Privacy = "Everything stays on this PC. No account, no telemetry, nothing is uploaded. Only the daily update check goes online by itself, and it sends just PC Tidy's version number. Turn it off in Settings › Updates.";

    /// <summary>The one-time notice for people who updated from 3.1.0, which had no automatic update check (3.1.1 review L8).</summary>
    public const string UpdateCheckNotice = "PC Tidy now checks GitHub once a day for updates (only its version number is sent). Turn it off in Settings.";

    /// <summary>What PC Tidy does online, honestly (About and the FAQ). Keep in step with PRIVACY.md.</summary>
    public const string Online =
        "The update check is the only thing PC Tidy does online by itself. Once a day it asks GitHub " +
        "(api.github.com, PC Tidy's releases at mintggs2/pc-tidy) whether there's a newer version. The request carries only " +
        "\"PC-Tidy/\" and the version number, and no cookies, account or anything about this PC or you. GitHub sees your IP address, " +
        "as with any web page. An update is downloaded only when you press Update now. Switch the check off in Settings › Updates. " +
        "Everything else that goes online happens only as part of something you start: winget (a PC care scan asks it about app updates, and it installs apps you pick), the DNS speed test, " +
        "the Lag check (off until you choose a test server) and web pages you open.";

    public static string WhatWeDontClean { get; set; } =
        "Some things look like junk but aren't, so PC Tidy leaves them alone: Prefetch (Windows uses it to start apps faster), " +
        "event logs, the Windows Installer folder, the driver store, the page file and hibernation file, your browser's saved " +
        "passwords and site data, your whole Downloads folder, and \"registry junk\" (cleaning the registry doesn't speed anything up " +
        "and can break apps). PC Tidy also never wipes free space or shreds files.";

    public static readonly IReadOnlyList<(string Question, string Answer)> Faq = new[]
    {
        ("Is this safe?",
         "Yes, by design. PC Tidy only looks until you say go. Every item shows its size and why it's safe. Removed files go to " +
         "quarantine instead of being deleted, settings are saved before they change, and every run can be undone from History. " +
         "Windows, your apps, your games and your documents are protected and never offered for cleaning."),
        ("Where do deleted files go?",
         "Into quarantine inside PC Tidy's data folder. They stay there, restorable with one click, for the number of days you pick " +
         "in Settings. PC Tidy asks before emptying it. Only a few things can't be undone (like emptying the Recycle Bin); those are " +
         "marked Permanent in red and always need an extra \"I understand\" tick."),
        ("Why does it sometimes need admin?",
         "A few clean-ups touch Windows' own folders (for example Windows temp files or old error reports). For those, Windows asks you " +
         "once per run. PC Tidy itself always runs as a normal user, and the confirm screen shows a shield next to every step that needs admin."),
        ("What is Simulation mode?",
         "\"Simulate\" walks through exactly what a run would do, step by step, including what would be skipped and why, and changes nothing. " +
         "It's a good way to try PC Tidy the first time."),
        ("What we don't clean, and why", ""), // filled from WhatWeDontClean at display time
        (SomethingWentWrong,
         "Open History: every run has Undo, and a run that stopped part way can be resumed or rolled back. If PC Tidy itself " +
         "won't start, use PC Tidy Rescue: a small separate tool that lists recent runs and puts things back. It's next to " +
         "PC Tidy, and a spare copy can live in the Rescue folder of your data folder."),
        ("What does PC Tidy never do?",
         "It never deletes anything without showing you first, never runs a \"registry cleaner\" or \"RAM booster\", never turns off " +
         "Windows Update, Defender or other security features, never installs anything else, and never sends anything about you or your PC off it."),
        ("Does PC Tidy go online?", Online),
    };

    public const string Licences =
        "PC Tidy uses these open-source libraries, each under its own licence:\n" +
        "• WPF-UI (MIT): Fluent design controls\n" +
        "• CommunityToolkit.Mvvm (MIT): app plumbing\n" +
        "• System.IO.Hashing (MIT): file fingerprints for safe moves and undo\n" +
        "• Microsoft.Data.Sqlite (MIT): local learning data\n" +
        "• ZstdSharp (MIT): reading compressed chat transcripts\n" +
        "• Fluent System Icons (MIT), via WPF-UI\n" +
        "PC Tidy itself contains no ads, no trackers and no bundled software.";

    public const string Changelog =
        "3.1.1 (beta)\n" +
        "• Updates from inside PC Tidy: it checks GitHub at most once a day (or when you click Check for updates), shows what's new and asks first. Checked against the release's checksums. You can switch the check off in Settings.\n" +
        "• Resume last scan: a scan keeps its progress after each section, so a restart or crash doesn't start it over.\n" +
        "• A note before long scans when Windows Update is waiting to restart the PC. PC Tidy's own code is now MIT-licensed.\n" +
        "\n3.1.0 (beta)\n" +
        "• New: Measure (compare frame-time recordings before and after a change), Lag check (off until a test server is chosen), Fixes, System info, an optional live \"Now\" card.\n" +
        "• Tweaks: search, categories, \"N of M on\", cards that show what's on now, presets that show what they'll change, and a \"Doesn't help (measured)\" shelf with the numbers.\n" +
        "• Games: a game list with details beside it, tips with measured numbers, GPU choice per game on laptops with two GPUs.\n" +
        "• More bad-tweak checks, Insights cards with measured numbers, install missing runtimes, restore points in History, a replayable tour.\n" +
        "\n3.0.0 (beta)\n" +
        "• New: Services, AI chats, Search, Insights (with Scan this script), Game boost, Schedule and reports, Ask PC Tidy, Learned, Privacy sweep, Notifications.\n" +
        "• Organize: Google Takeout, one-by-one sorting, presets. Duplicates: very large files, compare, wrong file types.\n" +
        "• Right-click \"What is this?\" and \"Tidy this folder\", optional tray icon, settings export/import, safer updates.\n" +
        "\n2.0.0\n" +
        "• New: Duplicates, Apps, Speed, Tweaks with the bad-tweak finder, Drivers, Games, Move to drive, Developer cleanup, PC care, Photos, Desktop tidy.\n" +
        "• PC Tidy now installs for everyone in Program Files, so its admin parts can't be changed by other programs.\n" +
        "\n1.0.0\n" +
        "• First release: Clean, Space map, Organize, Optimize & Organize everything, Home, History, Quarantine, Settings.\n" +
        "• Every change goes through Scan → Preview → Confirm → Done, with Undo.\n" +
        "• Simulation mode: see exactly what would happen, nothing changes.\n" +
        "• Rescue tool: undo recent runs even if the main app won't start.";
}
