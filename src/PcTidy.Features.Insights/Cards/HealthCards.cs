using System.Globalization;
using System.Text.RegularExpressions;
using PcTidy.Catalog.Insights;
using PcTidy.Core;
using PcTidy.Features.Insights.Model;
using PcTidy.Features.Insights.Source;

namespace PcTidy.Features.Insights.Cards;

/// <summary>Cards about how the PC is holding up: Reliability Monitor, the memory test and backups.</summary>
internal static class HealthCards
{
    // ------------------------------------------------------------------ Reliability Monitor

    public static InsightCard Reliability(CardContext c)
    {
        const string title = "Stability history";
        var read = c.Source.ReadStability(c.NowUtc.AddDays(-30));
        if (read.Problem == ReadProblem.NeedsAdmin)
            return new InsightCard
            {
                Id = InsightsIds.Reliability, Category = InsightCategory.Health, Title = title, Icon = "Pulse24", Status = InsightStatus.NeedsAdmin,
                Headline = "Needs admin to read", Explanation = "Windows only shows its stability history to an administrator on this PC.",
                WhatToDo = "Open Start, type \"reliability\" and pick \"View reliability history\" to see it yourself.",
            };
        if (read.Value is not { Count: > 0 } samples)
            return InsightCard.NotAvailable(InsightsIds.Reliability, InsightCategory.Health, title, "Pulse24",
                read.Detail ?? "Windows hasn't recorded a stability history on this PC (Reliability Monitor's data collection may be turned off).");

        var latest = samples[^1];
        var lowest = samples.MinBy(s => s.Index)!;
        var week = samples.Where(s => s.TimeUtc >= c.NowUtc.AddDays(-7)).Select(s => s.Index).DefaultIfEmpty(latest.Index).Average();
        var before = samples.Where(s => s.TimeUtc < c.NowUtc.AddDays(-7)).Select(s => s.Index).ToList();
        string trend = before.Count == 0 ? "" : (week - before.Average()) switch
        {
            >= 0.5 => "getting better",
            <= -0.5 => "getting worse",
            _ => "steady",
        };

        var status = latest.Index >= 7 ? InsightStatus.Good : latest.Index >= 4 ? InsightStatus.Info : InsightStatus.Attention;
        return new InsightCard
        {
            Id = InsightsIds.Reliability, Category = InsightCategory.Health, Title = title, Icon = "Pulse24", Status = status,
            Headline = $"Stability {Score(latest.Index)} out of 10{(trend.Length > 0 ? $", {trend}" : "")}",
            Explanation = "Windows scores its own stability from 1 to 10 every hour, from app crashes, Windows failures, failed updates and driver problems. "
                          + "After a problem the score drops and climbs back slowly over days, so one bad day keeps it low for a while.",
            WhatToDo = status == InsightStatus.Good
                ? "Nothing to do."
                : "Open Start, type \"reliability\" and pick \"View reliability history\": click the red marks to see what failed and when. PC care's Crashes tab explains blue screens.",
            Facts = new[]
            {
                new InsightFact("Now", Score(latest.Index)),
                new InsightFact("Last 7 days (average)", Score(week)),
                new InsightFact("Lowest in 30 days", $"{Score(lowest.Index)} ({Words.Date(lowest.TimeUtc)})"),
            },
        };
    }

    private static string Score(double v) => v.ToString("0.0", CultureInfo.CurrentCulture);

    // ------------------------------------------------------------------ Windows Memory Diagnostic

    private static readonly string[] MemoryTestSteps =
    {
        "Save your work and close your apps.",
        "Open Start, type \"Windows Memory Diagnostic\" and open it.",
        "Choose \"Restart now and check for problems\". The PC restarts and tests its memory for several minutes.",
        "After you sign in again, scan Insights: the result shows here.",
    };

    public static InsightCard MemoryTest(CardContext c)
    {
        const string title = "Memory test";
        var read = c.Source.ReadMemoryTests();
        if (read.Value is null)
            return InsightCard.NotAvailable(InsightsIds.MemoryTest, InsightCategory.Health, title, "Ram20", "PC Tidy couldn't read the System event log on this PC.");
        var latest = read.Value.FirstOrDefault();
        const string why = "Faulty memory causes random crashes, blue screens and corrupted files that no cleaner can fix. Windows has its own memory test; it runs while the PC restarts.";
        if (latest is null)
            return new InsightCard
            {
                Id = InsightsIds.MemoryTest, Category = InsightCategory.Health, Title = title, Icon = "Ram20", Status = InsightStatus.Info,
                Headline = "No memory test has been run on this PC", Explanation = why,
                WhatToDo = "Only worth running if you get random crashes or blue screens. PC Tidy can't start it for you: it restarts the PC.",
                Steps = MemoryTestSteps,
            };

        var when = Words.Date(latest.TimeUtc);
        return latest.Outcome switch
        {
            MemoryTestOutcome.ErrorsFound => new InsightCard
            {
                Id = InsightsIds.MemoryTest, Category = InsightCategory.Health, Title = title, Icon = "Ram20", Status = InsightStatus.Attention,
                Headline = $"The memory test found hardware errors ({when})", Explanation = why,
                WhatToDo = "If your memory runs an XMP/EXPO profile, try it at its default speed in the BIOS and test again. If errors stay, test one stick at a time or contact the PC or memory maker: faulty RAM is replaced, not repaired.",
                Steps = MemoryTestSteps,
            },
            MemoryTestOutcome.NoErrors => new InsightCard
            {
                Id = InsightsIds.MemoryTest, Category = InsightCategory.Health, Title = title, Icon = "Ram20", Status = InsightStatus.Good,
                Headline = $"The last memory test found no errors ({when})", Explanation = why, WhatToDo = "Nothing to do.",
            },
            _ => new InsightCard
            {
                Id = InsightsIds.MemoryTest, Category = InsightCategory.Health, Title = title, Icon = "Ram20", Status = InsightStatus.Info,
                Headline = $"The last memory test didn't finish ({when})", Explanation = why,
                WhatToDo = "Run it again and let it finish if you're chasing random crashes.", Steps = MemoryTestSteps,
            },
        };
    }

    // ------------------------------------------------------------------ backup health

    public static InsightCard Backup(CardContext c)
    {
        var facts = c.Source.ReadBackup();
        var apps = c.Identity.InstalledApps;
        var backupApps = apps.Where(a => InsightsData.BackupApps.Any(b => StartsWithWord(a, b))).Distinct().ToList();
        var syncApps = apps.Where(a => InsightsData.SyncApps.Any(s => StartsWithWord(a, s))).Distinct().ToList();
        var cloudFolders = new[] { ("Desktop", c.Locations.Desktop), ("Documents", c.Locations.Documents), ("Pictures", c.Locations.Pictures) }
            .Where(f => c.Locations.CloudRoots.Any(r => PathUtil.IsUnder(f.Item2, r))).Select(f => f.Item1).ToList();

        var list = new List<InsightFact>();
        if (facts.FileHistoryConfigured)
            list.Add(new InsightFact("File History", facts.FileHistoryLastBackupUtc is { } t ? $"On, last backup {Words.Ago(t, c.NowUtc)} ({Words.Date(t)})" : "On (last backup date unknown)"));
        list.AddRange(backupApps.Select(a => new InsightFact("Backup app", a)));
        if (cloudFolders.Count > 0) list.Add(new InsightFact("OneDrive", $"Syncs {string.Join(", ", cloudFolders)}"));
        list.AddRange(syncApps.Select(a => new InsightFact("Sync app", a)));

        const string syncIsNotBackup =
            "Sync is not a backup: OneDrive, Dropbox and Google Drive copy every change to the cloud, including a file you delete by mistake or one ransomware scrambles. "
            + "A backup is a separate copy, ideally on another drive, that a mistake on this PC can't reach.";
        var link = new[] { new InsightLink("Open Windows Backup settings", InsightsLinks.SettingsBackup) };

        bool fileHistoryRecent = facts.FileHistoryLastBackupUtc is { } last && c.NowUtc - last < TimeSpan.FromDays(30);
        if (fileHistoryRecent || backupApps.Count > 0)
            return Card(InsightStatus.Good,
                fileHistoryRecent ? $"File History backed up {Words.Ago(facts.FileHistoryLastBackupUtc!.Value, c.NowUtc)}" : $"Backup app found: {backupApps[0]}",
                "PC Tidy can see a backup tool. It can't check that the backups work: only a test restore proves that.",
                "Once in a while, restore one file from your backup to be sure it works.");
        if (facts.FileHistoryConfigured)
            return Card(InsightStatus.Attention,
                facts.FileHistoryLastBackupUtc is { } old ? $"File History's last backup was {Words.Ago(old, c.NowUtc)}" : "File History is set up, but PC Tidy can't see when it last ran",
                "File History only backs up while its drive is connected.",
                "Connect the backup drive and check File History in Control Panel. Then restore one file to be sure it works.");
        if (cloudFolders.Count > 0 || syncApps.Count > 0)
            return Card(InsightStatus.Attention, "Only sync found, no backup", syncIsNotBackup,
                "Add a real backup: File History or Windows Backup to an external drive, or a backup app. Keep OneDrive too: its recycle bin and version history help in between.");
        return Card(InsightStatus.Attention, "No backup found",
            "PC Tidy didn't find File History, OneDrive folder sync or a known backup app. " + syncIsNotBackup,
            "Set up File History (Control Panel → File History) with an external drive, or use a backup app you trust.");

        InsightCard Card(InsightStatus status, string headline, string explanation, string todo) => new()
        {
            Id = InsightsIds.Backup, Category = InsightCategory.Health, Title = "Backups", Icon = "History24", Status = status,
            Headline = headline, Explanation = explanation, WhatToDo = todo, Facts = list, Links = link,
            IsHeuristic = true, // read-only best effort: PC Tidy recognises File History and known apps only
        };
    }

    private static bool StartsWithWord(string text, string name)
        => Regex.IsMatch(text, $@"^{Regex.Escape(name)}(?![A-Za-z])", RegexOptions.IgnoreCase);
}
