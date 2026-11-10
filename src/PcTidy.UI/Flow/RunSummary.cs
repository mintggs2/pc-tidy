using System.Globalization;
using PcTidy.Core;
using PcTidy.Core.Model;

namespace PcTidy.UI.Flow;

/// <summary>
/// Where quarantine lives and how long it keeps things, so a summary can say honestly whether space was freed
/// (quarantine on another drive) or is only freed when quarantine is emptied (same drive).
/// </summary>
public sealed record RunSummaryContext(string? QuarantineRoot, int QuarantineDays)
{
    /// <summary>Set by the shell at start-up (and when settings change). Unknown root = "moved to quarantine".</summary>
    public static RunSummaryContext Current { get; set; } = new(null, 7);
}

/// <summary>
/// Plain-English numbers for one run or a group of runs (Optimize everything). Pure logic.
/// Counts use <see cref="PlanFacts.Categorize"/>, the same classification as the bottom bar and the confirm dialog,
/// and byte totals only use what the engine measured (<see cref="StepRecord.BytesAffected"/>).
/// </summary>
public sealed class RunSummary
{
    public RunSummary(IReadOnlyList<RunRecord> runs, RunSummaryContext? context = null)
    {
        var ctx = context ?? RunSummaryContext.Current;
        Runs = runs;
        var steps = runs.SelectMany(r => r.Steps).Where(s => PlanFacts.Categorize(s.Item) != PlanCategory.Housekeeping).ToList();
        Planned = PlanCounts.Of(steps.Select(s => s.Item));
        var done = steps.Where(s => s.Status == StepStatus.Done).ToList();
        int DoneOf(PlanCategory c) => done.Count(s => PlanFacts.Categorize(s.Item) == c);
        Quarantined = DoneOf(PlanCategory.Quarantine);
        Moved = DoneOf(PlanCategory.Move);
        StartupChanged = DoneOf(PlanCategory.Startup);
        SettingsChanged = DoneOf(PlanCategory.Setting);
        PermanentDone = DoneOf(PlanCategory.Permanent);
        // Pages PC Tidy opened (a launcher's uninstall screen...): not a change, but the Done card shouldn't say "nothing".
        Opened = runs.SelectMany(r => r.Steps).Count(s => PlanFacts.IsOpen(s.Item) && s.Status == StepStatus.Done);
        Skipped =steps.Count(s => s.Status is StepStatus.Skipped or StepStatus.Failed);
        Failed = steps.Count(s => s.Status == StepStatus.Failed);
        Simulated = steps.Count(s => s.Status == StepStatus.Simulated);

        // Quarantine on the same drive is a rename: nothing is freed until quarantine is emptied.
        string? qDrive = DriveOf(ctx.QuarantineRoot);
        foreach (var s in done)
        {
            switch (PlanFacts.Categorize(s.Item))
            {
                case PlanCategory.Quarantine:
                    BytesQuarantined += s.BytesAffected;
                    var src = DriveOf(s.Item.SourcePath);
                    if (qDrive != null && src != null && !string.Equals(qDrive, src, StringComparison.OrdinalIgnoreCase)) BytesFreed += s.BytesAffected;
                    else BytesHeld += s.BytesAffected;
                    break;
                case PlanCategory.Permanent:
                    BytesFreed += s.BytesAffected;
                    break;
            }
        }

        // The plan's estimate for space-freeing steps, against what was measured, and where the difference went.
        var spaceSteps = steps.Where(s => PlanFacts.Categorize(s.Item) is PlanCategory.Quarantine or PlanCategory.Permanent).ToList();
        EstimatedBytes = spaceSteps.Sum(s => Math.Max(0, s.Item.ExpectedSize));
        foreach (var s in spaceSteps.Where(s => s.Status is StepStatus.Skipped or StepStatus.Failed))
        {
            long bytes = Math.Max(0, s.Item.ExpectedSize);
            switch (GapReasonOf(s.Message))
            {
                case GapReason.InUse: GapInUse += bytes; break;
                case GapReason.Changed: GapChanged += bytes; break;
                case GapReason.Gone: GapGone += bytes; break;
                default: GapOther += bytes; break;
            }
        }

        SkipReasons = steps.Where(s => s.Status is StepStatus.Skipped or StepStatus.Failed)
            .GroupBy(s => string.IsNullOrWhiteSpace(s.Message) ? "No reason given" : s.Message!)
            .Select(g => (Reason: g.Key, Count: g.Count(), Bytes: g.Sum(s => s.Item.ExpectedSize)))
            .OrderByDescending(x => x.Count).ToList();
        var start = runs.Count > 0 ? runs.Min(r => r.CreatedUtc) : DateTime.UtcNow;
        var end = runs.Select(r => r.FinishedUtc ?? r.CreatedUtc).DefaultIfEmpty(start).Max();
        Duration = end - start;
        EmptiedAfterLocal = start.ToLocalTime().AddDays(ctx.QuarantineDays);
        RestorePoint = runs.Select(r => r.RestorePoint).FirstOrDefault(p => p != null);
        HadSystemChange = done.Any(s => s.Item.IsSystemChange);
        IsUndone = runs.Count > 0 && runs.All(r => r.Status is RunStatus.Undone or RunStatus.RolledBack);
        IsSimulation = runs.Count > 0 && runs.All(r => r.Mode == ExecutionMode.Simulate);
        AnyReversible = done.Any(s => s.Item.Reversible);
    }

    public IReadOnlyList<RunRecord> Runs { get; }
    /// <summary>What the run set out to do (every step, same counting as the confirm dialog).</summary>
    public PlanCounts Planned { get; }
    public int Quarantined { get; }
    public int Moved { get; }
    public int StartupChanged { get; }
    public int SettingsChanged { get; }
    public int PermanentDone { get; }
    /// <summary>Pages or launcher screens opened (nothing on the PC changed).</summary>
    public int Opened { get; }
    /// <summary>Startup items + settings changed.</summary>
    public int Changed => StartupChanged + SettingsChanged;
    public int Skipped { get; }
    public int Failed { get; }
    public int Simulated { get; }
    /// <summary>Measured bytes now in quarantine.</summary>
    public long BytesQuarantined { get; }
    /// <summary>Measured bytes really freed now: permanent deletes, and quarantine that sits on another drive.</summary>
    public long BytesFreed { get; }
    /// <summary>Measured bytes moved to quarantine on the same drive: freed only when quarantine is emptied.</summary>
    public long BytesHeld { get; }
    /// <summary>When quarantine for this run is due to be emptied (it always asks first unless set otherwise).</summary>
    public DateTime EmptiedAfterLocal { get; }
    public IReadOnlyList<(string Reason, int Count, long Bytes)> SkipReasons { get; }
    public TimeSpan Duration { get; }
    public RestorePointInfo? RestorePoint { get; }
    /// <summary>Something system-level changed (so a restore point was expected).</summary>
    public bool HadSystemChange { get; }
    public bool IsUndone { get; }
    public bool IsSimulation { get; }
    public bool AnyReversible { get; }

    /// <summary>"6.2 GB moved to quarantine" / "6.2 GB freed" / "241 files sorted" / "9 changes made" / "Nothing needed doing".</summary>
    public string Headline
    {
        get
        {
            if (BytesHeld > 0) return Sizes.Format(BytesHeld) + " moved to quarantine" + (BytesFreed > 0 ? $" · {Sizes.Format(BytesFreed)} freed" : "");
            if (BytesFreed > 0) return Sizes.Format(BytesFreed) + " freed";
            if (Quarantined > 0) return $"{PlanFacts.Items(Quarantined)} moved to quarantine";
            if (Moved > 0) return $"{Moved:N0} file{PlanFacts.S(Moved)} sorted";
            if (Changed > 0) return $"{Changed} change{PlanFacts.S(Changed)} made";
            if (PermanentDone > 0) return $"{PermanentDone} done";
            if (Opened > 0) return $"{Opened} page{PlanFacts.S(Opened)} opened";
            return Skipped > 0 ? "Nothing could be done this time" : "Nothing needed doing";
        }
    }

    /// <summary>Honest note about the space: null when nothing is waiting in quarantine on the same drive.</summary>
    public string? SpaceNote => BytesHeld > 0
        ? $"The space is freed when quarantine is emptied ({EmptiedAfterLocal.ToString("d MMM", CultureInfo.CurrentCulture)} or later). Until then you can put everything back."
        : null;

    // ---------------- freed vs the plan's estimate ----------------

    private enum GapReason { InUse, Changed, Gone, Other }

    /// <summary>The engine's own skip messages, sorted into the reasons the Done card names.</summary>
    private static GapReason GapReasonOf(string? message)
    {
        var m = message ?? "";
        bool Has(string s) => m.Contains(s, StringComparison.OrdinalIgnoreCase);
        if (Has("in use") || Has("locked") || Has("is open") || Has("being used") || Has("sharing")) return GapReason.InUse;
        if (Has("changed since") || Has("changed after") || Has("was changed") || Has("newer") || Has("different size")) return GapReason.Changed;
        if (Has("no longer there") || Has("isn't there") || Has("not there") || Has("already gone") || Has("is gone") || Has("already")) return GapReason.Gone;
        return GapReason.Other;
    }

    /// <summary>What the plan expected to free or set aside (the confirm dialog's number).</summary>
    public long EstimatedBytes { get; }
    /// <summary>Measured: freed now plus moved to quarantine.</summary>
    public long MeasuredBytes => BytesFreed + BytesHeld;
    /// <summary>Estimated bytes of steps skipped because a file was in use or locked.</summary>
    public long GapInUse { get; }
    /// <summary>... because it changed since the scan.</summary>
    public long GapChanged { get; }
    /// <summary>... because it was already gone.</summary>
    public long GapGone { get; }
    /// <summary>... for any other reason (or files smaller than when they were scanned).</summary>
    public long GapOther { get; }

    /// <summary>The run measured clearly less than the estimate: more than 5 % or more than 50 MB below it.</summary>
    public bool FellShort
    {
        get
        {
            if (IsSimulation || IsUndone || EstimatedBytes <= 0) return false;
            long gap = EstimatedBytes - MeasuredBytes;
            return gap > 50L * 1024 * 1024 || gap > EstimatedBytes * 0.05;
        }
    }

    /// <summary>
    /// "Freed 4.1 GB of about 6.2 GB. In use or locked: 1.5 GB · changed since the scan: 400 MB · already gone: 200 MB."
    /// Null when the run did about what the estimate said.
    /// </summary>
    public string? ShortfallLine
    {
        get
        {
            if (!FellShort) return null;
            var verb = BytesHeld > 0
                ? $"{Sizes.Format(MeasuredBytes)} of about {Sizes.Format(EstimatedBytes)} went to quarantine."
                : $"Freed {Sizes.Format(MeasuredBytes)} of about {Sizes.Format(EstimatedBytes)}.";
            long explained = GapInUse + GapChanged + GapGone + GapOther;
            long smaller = Math.Max(0, EstimatedBytes - MeasuredBytes - explained);
            var parts = new List<string>();
            if (GapInUse > 0) parts.Add($"in use or locked: {Sizes.Format(GapInUse)}");
            if (GapChanged > 0) parts.Add($"changed since the scan: {Sizes.Format(GapChanged)}");
            if (GapGone > 0) parts.Add($"already gone: {Sizes.Format(GapGone)}");
            if (GapOther > 0) parts.Add($"skipped for other reasons: {Sizes.Format(GapOther)}");
            if (smaller > 1024 * 1024) parts.Add($"smaller than when scanned: {Sizes.Format(smaller)}");
            if (parts.Count == 0) return verb;
            var detail = string.Join(" · ", parts);
            return verb + " " + char.ToUpperInvariant(detail[0]) + detail[1..] + ".";
        }
    }

    /// <summary>"48 of 49" when some were skipped, else "48".</summary>
    public static string OfPlanned(int done, int planned) => planned > done ? $"{done:N0} of {planned:N0}" : done.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>One line for the restore point, honest for every case (never "not needed" when settings changed).</summary>
    public string RestorePointLine => RestorePoint switch
    {
        { Created: true, CreatedUtc: { } at } => $"Restore point made at {at.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)}",
        { Reused: true } => "A recent restore point was reused",
        _ when HadSystemChange => "No restore point was recorded for this run. Undo still puts every change back.",
        _ => "No restore point needed: nothing system-level changed",
    };

    public string DurationText => Duration.TotalSeconds < 60
        ? $"{Math.Max(1, (int)Duration.TotalSeconds)} s"
        : $"{(int)Duration.TotalMinutes} min {Duration.Seconds} s";

    public string SkipLine => string.Join(" · ", SkipReasons.Take(3).Select(r => $"{r.Reason} ({r.Count})"))
                              + (SkipReasons.Count > 3 ? $" · {SkipReasons.Count - 3} other reasons" : "");

    private static string? DriveOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return System.IO.Path.IsPathFullyQualified(path) ? System.IO.Path.GetPathRoot(path) : null; }
        catch (ArgumentException) { return null; }
    }

    public static string When(DateTime utc)
    {
        var local = utc.ToLocalTime();
        var today = DateTime.Now.Date;
        var time = local.ToString("t", CultureInfo.CurrentCulture);
        if (local.Date == today) return "Today " + time;
        if (local.Date == today.AddDays(-1)) return "Yesterday " + time;
        return local.ToString("d MMM yyyy", CultureInfo.CurrentCulture) + " " + time;
    }

    /// <summary>For use mid-sentence: "today 14:32", "8 Nov 2026 14:32".</summary>
    public static string WhenInSentence(DateTime utc)
    {
        var w = When(utc);
        return w.StartsWith("Today", StringComparison.Ordinal) || w.StartsWith("Yesterday", StringComparison.Ordinal)
            ? char.ToLowerInvariant(w[0]) + w[1..]
            : w;
    }

    public static string StatusText(RunStatus s) => s switch
    {
        RunStatus.Completed => "Done",
        RunStatus.CompletedWithSkips => "With skips",
        RunStatus.Failed => "Failed",
        RunStatus.Interrupted => "Interrupted",
        RunStatus.RolledBack => "Rolled back",
        RunStatus.Undone => "Undone",
        RunStatus.PartiallyUndone => "Partly undone",
        RunStatus.Simulated => "Simulated",
        RunStatus.Running => "Running",
        _ => "Planned",
    };
}
