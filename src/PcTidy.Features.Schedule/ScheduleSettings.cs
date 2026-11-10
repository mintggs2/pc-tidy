using PcTidy.Features.Schedule.Schedules;

namespace PcTidy.Features.Schedule;

/// <summary>When one schedule runs. Day = a day name for weekly ("Sunday"), a day of the month for monthly ("1"…"28").</summary>
public sealed class ScheduleChoice
{
    public bool Enabled { get; set; }
    public string Day { get; set; } = "Sunday";
    /// <summary>24-hour "HH:mm".</summary>
    public string Time { get; set; } = "19:00";
}

/// <summary>
/// What Schedule &amp; reports remembers. Stored with <c>AppSettings.SetFeature("schedule", …)</c>; the shell saves.
/// Everything that reaches outside PC Tidy's own data folder is off by default.
/// </summary>
public sealed class ScheduleSettings
{
    public const string FeatureKey = ScheduleIds.FeatureId;
    public const long DefaultNotifyMinBytes = 500L * 1024 * 1024;

    public ScheduleChoice Weekly { get; set; } = new() { Day = "Sunday", Time = "19:00" };
    public ScheduleChoice Monthly { get; set; } = new() { Day = "1", Time = "19:00" };

    /// <summary>Also save an HTML copy of each report next to the Markdown one.</summary>
    public bool SaveHtmlReports { get; set; } = true;
    /// <summary>Copy each run report into a folder you pick (e.g. an Obsidian vault's "40 Reports"). Off by default.</summary>
    public bool CopyReportsToFolder { get; set; }
    public string? ReportCopyFolder { get; set; }
    /// <summary>A scheduled scan only notifies when at least this much can be cleaned (or something else needs a look).</summary>
    public long NotifyMinBytes { get; set; } = DefaultNotifyMinBytes;
    /// <summary>Keep one disk-health reading a day for the history card.</summary>
    public bool DiskHealthHistory { get; set; } = true;

    public ScheduleChoice For(ScheduleKind kind) => kind == ScheduleKind.WeeklyScan ? Weekly : Monthly;

    public static ScheduleSettings Load(AppSettings settings)
    {
        try { return settings.GetFeature<ScheduleSettings>(FeatureKey) ?? new ScheduleSettings(); }
        catch (System.Text.Json.JsonException) { return new ScheduleSettings(); }
    }

    public void Store(AppSettings settings) => settings.SetFeature(FeatureKey, this);
}
