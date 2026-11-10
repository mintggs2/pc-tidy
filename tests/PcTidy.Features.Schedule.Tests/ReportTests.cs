using PcTidy.Features.Schedule.DiskHealth;
using PcTidy.Features.Schedule.Drift;
using PcTidy.Features.Schedule.Metrics;
using PcTidy.Features.Schedule.Reports;
using PcTidy.Features.Schedule.Storage;
using PcTidy.UI.Flow;

namespace PcTidy.Features.Schedule.Tests;

public class ReportTests
{
    private static RunRecord SampleRun()
    {
        var q = new PlanItem { Kind = ActionKind.QuarantinePath, FeatureId = "cleanup", Description = "Clean 'a*b_[x].tmp'", SourcePath = @"E:\Temp\a*b_[x].tmp", ExpectedSize = 2L << 30 };
        var skipped = new PlanItem { Kind = ActionKind.QuarantinePath, FeatureId = "cleanup", Description = "Clean Chrome cache", SourcePath = @"E:\Chrome\Cache", ExpectedSize = 1 << 20 };
        var tweak = Runs.SetDword(@"Software\T", "V", "1");
        var folder = new PlanItem { Kind = ActionKind.CreateDirectory, FeatureId = "organize", Description = "Make folder", DestinationPath = @"E:\x" };
        return Runs.Run("cleanup", new DateTime(2026, 11, 10, 14, 0, 0, DateTimeKind.Utc), RunStatus.CompletedWithSkips,
            Runs.Step(0, q, bytes: 2L << 30),
            Runs.Step(1, skipped, StepStatus.Skipped, message: "Chrome is open"),
            Runs.Step(2, tweak),
            Runs.Step(3, folder));
    }

    [Fact]
    public void Run_report_has_the_summary_the_done_card_shows_and_every_step()
    {
        var run = SampleRun();
        var changes = new[] { new MetricChange("freeC", "C: free space", 1, 2, "C: free space: 40 GB → 42 GB (+2 GB)", true, false) };
        var doc = RunReportBuilder.Build(new[] { run }, changes, new RunSummaryContext(@"Q:\Quarantine", 7));
        var md = doc.ToMarkdown();

        Assert.StartsWith("---\npctidy-id: run-" + run.RunId + "\npctidy-kind: run\n", md);
        Assert.Contains("# PC Tidy: cleanup", md);
        Assert.Contains("| Space freed | 2 GB |", md);                 // quarantine on another drive frees space now
        Assert.Contains("| Items cleaned | 1 of 2 |", md);
        Assert.Contains("| Settings and tweaks applied | 1 |", md);
        Assert.Contains("## Before and after", md);
        Assert.Contains("C: free space: 40 GB → 42 GB (+2 GB)", md);
        Assert.Contains("## Skipped, and why", md);
        Assert.Contains("- Chrome is open (1)", md);
        Assert.Contains(@"Clean 'a\*b\_\[x\].tmp'", md);               // file names can't become formatting
        Assert.DoesNotContain("Make folder", md);                     // housekeeping isn't listed as a step
        Assert.Contains("History and pick this run to undo it", md);
    }

    [Fact]
    public void Html_report_is_encoded_and_self_contained()
    {
        var run = SampleRun() with { Title = "<script>alert(1)</script>" };
        var html = RunReportBuilder.Build(new[] { run }).ToHtml();
        Assert.StartsWith("<!doctype html>", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("prefers-color-scheme:dark", html);
        Assert.DoesNotContain("http", html);                        // no external resources
    }

    [Fact]
    public void Optimize_everything_runs_make_one_report_and_simulations_say_so()
    {
        var parent = "20261110-140000-abcdef";
        var a = SampleRun() with { ParentRunId = parent };
        var b = SampleRun() with { ParentRunId = parent, FeatureId = "tweaks" };
        var doc = RunReportBuilder.Build(new[] { a, b });
        Assert.Equal("run-" + parent, doc.Id);
        Assert.Equal("PC Tidy: Optimize & Organize everything", doc.Title);

        var sim = SampleRun() with { Mode = ExecutionMode.Simulate };
        Assert.Contains("Simulation: nothing was changed.", RunReportBuilder.Build(new[] { sim }).ToMarkdown());
    }

    [Fact]
    public void A_scan_you_started_is_never_called_weekly()
    {
        // UI3-22: "Scan now" with no schedule on used to be saved as "PC Tidy weekly scan".
        var when = new DateTime(2026, 11, 10, 13, 1, 0, DateTimeKind.Utc);
        var date = when.ToLocalTime().ToString("d MMM yyyy", System.Globalization.CultureInfo.CurrentCulture);
        ScanFindings Findings(bool manual, bool checkup) => new() { TimeUtc = when, Checkup = checkup, Manual = manual, Sections = Array.Empty<SectionFinding>() };

        Assert.Equal($"PC Tidy scan (you started it): {date}", ScanReportBuilder.Build(Findings(manual: true, checkup: false)).Title);
        Assert.Equal($"PC Tidy checkup (you started it): {date}", ScanReportBuilder.Build(Findings(manual: true, checkup: true)).Title);
        Assert.Equal($"PC Tidy weekly scan: {date}", ScanReportBuilder.Build(Findings(manual: false, checkup: false)).Title);
        Assert.Equal($"PC Tidy monthly checkup: {date}", ScanReportBuilder.Build(Findings(manual: false, checkup: true)).Title);
    }

    [Fact]
    public void Scan_report_lists_sections_drift_and_disks()
    {
        var spec = new RegistryValueSpec { Hive = RegistryHive.CurrentUser, KeyPath = @"Software\T", ValueName = "V", Kind = RegistryKind.DWord, Data = "0" };
        var f = new ScanFindings
        {
            TimeUtc = new DateTime(2026, 11, 10, 18, 0, 0, DateTimeKind.Utc), Checkup = true,
            Sections = new[]
            {
                new SectionFinding("cleanup", "Clean", 7L << 30, 12, 3, new[] { "Chrome is open: its cache was skipped" }, null),
                new SectionFinding("dupes", "Duplicates", 0, 0, 0, Array.Empty<string>(), "it couldn't finish (x)."),
            },
            Previous = new ScanSummary { TimeUtc = new DateTime(2026, 10, 10, 18, 0, 0, DateTimeKind.Utc), CleanBytes = 5L << 30, BySection = new() { ["cleanup"] = 5L << 30 } },
            Drift = new DriftReport(4, new[] { new DriftItem("Turn off web search in Start", spec, "0", "1", "tweaks", "r1", DateTime.UtcNow) }),
            Disks = new[] { new DiskReading { Id = "0", Name = "Fast SSD", Health = DiskHealthStatus.Healthy } },
        };
        var md = ScanReportBuilder.Build(f).ToMarkdown();

        Assert.Contains("PC Tidy found 7 GB you can clean. This was a scan only: nothing on this PC was changed.", md);
        Assert.Contains("2 GB more to clean (Clean +2 GB).", md);
        Assert.Contains("| Clean | 7 GB | 3 to look at |", md);
        Assert.Contains("| Duplicates | Couldn't finish |  |", md);
        Assert.Contains("1 of your 4 tweaks was changed back, usually by a Windows update.", md);
        Assert.Contains("| Turn off web search in Start | 0 | 1 |", md);
        Assert.Contains("| Fast SSD | Healthy | Not readable | Not readable |", md);
        Assert.Contains("pctidy-kind: checkup", md);
    }

    [Fact]
    public void Store_writes_only_its_own_folders_never_overwrites_and_lists_newest_first()
    {
        using var box = new ScheduleSandbox("store");
        var store = new ScheduleStore(box.Host.Settings.DataRoot) { MaxMetricsEntries = 10 };
        var t = new DateTime(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc);
        var doc = RunReportBuilder.Build(new[] { SampleRun() });

        var first = store.SaveReport(t, @"..\..\evil:name", doc.ToMarkdown(), doc.ToHtml());
        var second = store.SaveReport(t, @"..\..\evil:name", doc.ToMarkdown(), null);

        Assert.NotEqual(first.Markdown, second.Markdown);
        foreach (var p in new[] { first.Markdown, first.Html!, second.Markdown })
            Assert.True(PathUtil.IsUnder(p, store.ReportsFolder), p);
        Assert.Null(second.Html);
        var listed = store.ListReports();
        Assert.Equal(2, listed.Count);
        Assert.All(listed, r => Assert.Equal("PC Tidy: cleanup", r.Title));
        Assert.Contains(listed, r => r.HtmlPath is not null);
        Assert.Empty(Directory.GetFiles(store.ReportsFolder, "*.tmp"));

        for (int i = 0; i < store.MaxMetricsEntries + 5; i++)
            store.AppendMetrics(new MetricsHistoryEntry { TimeUtc = t.AddMinutes(i), Kind = SampleKind.Daily, Values = new() { ["processes"] = i } });
        var metrics = store.LoadMetrics();
        Assert.Equal(store.MaxMetricsEntries, metrics.Count);
        Assert.Equal(store.MaxMetricsEntries + 4, metrics[^1].Values["processes"]);
        Assert.True(PathUtil.IsUnder(Path.Combine(store.OwnFolder, "metrics-history.json"), Path.Combine(box.Host.Settings.DataRoot, "schedule")));
    }

    [Fact]
    public void Corrupt_history_reads_as_empty_and_is_started_again()
    {
        using var box = new ScheduleSandbox("store-corrupt");
        var store = new ScheduleStore(box.Host.Settings.DataRoot);
        box.Put(Path.Combine(store.OwnFolder, "disk-health.json"), "{ not json");
        Assert.Empty(store.LoadDiskHealth());
        Assert.True(store.DiskSampleDueToday(DateTime.UtcNow));
        store.AppendDiskHealth(new DiskHealthSample(DateTime.UtcNow, new[] { new DiskReading { Id = "1", Name = "D" } }));
        Assert.Single(store.LoadDiskHealth());
        Assert.False(store.DiskSampleDueToday(DateTime.UtcNow));
    }

    [Fact]
    public void Copy_planner_makes_a_new_file_never_overwrites_and_skips_its_own_copy()
    {
        using var box = new ScheduleSandbox("copy");
        var vault = Path.Combine(box.K.Documents, "Notes");
        Directory.CreateDirectory(vault);
        var doc = RunReportBuilder.Build(new[] { SampleRun() });
        var md = doc.ToMarkdown();

        var item = ReportCopyPlanner.Plan(doc, vault, md)!;
        Assert.Equal(ActionKind.WriteTextFile, item.Kind);
        Assert.Equal(Path.Combine(vault, ReportCopyPlanner.FileName(doc)), item.DestinationPath);
        Assert.Equal(md, item.Args!["content"]);
        Assert.True(item.Reversible);
        Assert.False(File.Exists(item.DestinationPath));            // only planned

        // A file of the user's with that name: never touched, a new name is used.
        box.Put(item.DestinationPath!, "my own note");
        var next = ReportCopyPlanner.Plan(doc, vault, md)!;
        Assert.EndsWith(" (2).md", next.DestinationPath);
        Assert.Equal("my own note", File.ReadAllText(item.DestinationPath!));

        // PC Tidy already copied this report: nothing to do.
        box.Put(next.DestinationPath!, md);
        Assert.Null(ReportCopyPlanner.Plan(doc, vault, md));

        Assert.Null(ReportCopyPlanner.Plan(doc, "relative\\folder", md));
    }

    [Fact]
    public void Saved_report_copy_reads_the_id_back_from_the_file()
    {
        using var box = new ScheduleSandbox("copy-saved");
        var store = new ScheduleStore(box.Host.Settings.DataRoot);
        var doc = RunReportBuilder.Build(new[] { SampleRun() });
        var saved = store.SaveReport(doc.TimeUtc, doc.Slug, doc.ToMarkdown(), null).Markdown;
        var vault = Path.Combine(box.Root, "Vault");
        Directory.CreateDirectory(vault);

        var item = ReportCopyPlanner.PlanSaved(saved, vault)!;
        Assert.Contains("PC Tidy: cleanup", item.Description);
        box.Put(item.DestinationPath!, item.Args!["content"]);
        Assert.Null(ReportCopyPlanner.PlanSaved(saved, vault));

        var foreign = box.Put(Path.Combine(store.ReportsFolder, "x.md"), "# not ours");
        Assert.Null(ReportCopyPlanner.PlanSaved(foreign, vault));
    }

    [Fact]
    public void Disk_advice_warns_about_health_wear_heat_and_a_drop()
    {
        var t = DateTime.UtcNow;
        var history = new[]
        {
            new DiskHealthSample(t.AddDays(-2), new[] { new DiskReading { Id = "0", Name = "SSD", Media = DriveMedia.Ssd, Health = DiskHealthStatus.Healthy } }),
            new DiskHealthSample(t, new[] { new DiskReading { Id = "0", Name = "SSD", Media = DriveMedia.Ssd, Health = DiskHealthStatus.Warning, WearPercent = 93, TemperatureC = 75 } }),
        };
        var w = DiskHealthAdvice.Warnings(history);
        Assert.Contains(w, x => x.Contains("health warning"));
        Assert.Contains(w, x => x.Contains("93 %"));
        Assert.Contains(w, x => x.Contains("hot"));
        Assert.Contains(w, x => x.Contains("changed since"));
        Assert.Empty(DiskHealthAdvice.Warnings(history.Take(1).ToList()));
    }
}
