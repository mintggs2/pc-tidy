using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace PcTidy.Features.Schedule.Schedules;

/// <summary>The two report-only schedules PC Tidy offers. Nothing else can be scheduled.</summary>
public enum ScheduleKind { WeeklyScan, MonthlyCheckup }

/// <summary>
/// Fixed facts for each schedule: the task name under "\PC Tidy\", the cadence and the only command-line switches the
/// task may pass. The switches are constants (never built from user input), so a schedule can never run anything
/// but PC Tidy's own report-only scan.
/// <para>
/// Task names carry a short tag of the Windows account ("Weekly scan - 1a2b3c4d"), so two accounts on one PC each
/// get their own task instead of fighting over one name. The tag is a hash of the account's SID (no user name).
/// Tasks from older versions (without a tag) are still recognised so they can be shown and removed.
/// </para>
/// </summary>
public static class ScheduleIds
{
    public const string FeatureId = "schedule";
    /// <summary>Task Scheduler folder PC Tidy owns. Only tasks in here are ever read or changed.</summary>
    public const string TaskFolder = @"\PC Tidy";

    public const string WeeklyTaskName = "Weekly scan";
    public const string MonthlyTaskName = "Monthly checkup";

    public const string ScanSwitch = "--scheduled-scan";
    public const string CheckupSwitch = "--checkup";
    public const string WeeklyArguments = ScanSwitch;
    public const string MonthlyArguments = ScanSwitch + " " + CheckupSwitch;

    private static readonly Lazy<string> _userTag = new(() => UserTagFor(CurrentSid()));

    /// <summary>8 hex characters from SHA-256 of the account SID ("" when the SID can't be read).</summary>
    public static string UserTag => _userTag.Value;

    internal static string UserTagFor(string? sid)
    {
        if (string.IsNullOrEmpty(sid)) return "";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sid.ToUpperInvariant()));
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    private static string? CurrentSid()
    {
        try { return WindowsIdentity.GetCurrent().User?.Value; }
        catch { return null; }
    }

    private static string Tagged(string baseName, string tag) => tag.Length == 0 ? baseName : baseName + " - " + tag;

    public static string TaskName(ScheduleKind kind) => TaskName(kind, UserTag);
    internal static string TaskName(ScheduleKind kind, string tag) => Tagged(kind == ScheduleKind.WeeklyScan ? WeeklyTaskName : MonthlyTaskName, tag);
    public static string Cadence(ScheduleKind kind) => kind == ScheduleKind.WeeklyScan ? "weekly" : "monthly";
    public static string Arguments(ScheduleKind kind) => kind == ScheduleKind.WeeklyScan ? WeeklyArguments : MonthlyArguments;
    public static string TaskPath(ScheduleKind kind) => TaskFolder + "\\" + TaskName(kind);
    public static string Title(ScheduleKind kind) => kind == ScheduleKind.WeeklyScan ? "Weekly scan" : "Monthly checkup";

    /// <summary>This account's task (tagged), or a task from an older version (untagged). Another account's: null.</summary>
    public static ScheduleKind? FromTaskName(string? name) => FromTaskName(name, UserTag);

    internal static ScheduleKind? FromTaskName(string? name, string tag)
    {
        if (name is null) return null;
        foreach (var kind in new[] { ScheduleKind.WeeklyScan, ScheduleKind.MonthlyCheckup })
        {
            var baseName = kind == ScheduleKind.WeeklyScan ? WeeklyTaskName : MonthlyTaskName;
            if (name == baseName || name == Tagged(baseName, tag)) return kind;
        }
        return null;
    }
}
