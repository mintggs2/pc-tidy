using PcTidy.Core;
using PcTidy.Core.Model;

namespace PcTidy.Features.Organize;

/// <summary>A file PC Tidy put somewhere: its new path and the rule that put it there.</summary>
public sealed class LedgerEntry
{
    public string Path { get; set; } = "";
    public string RuleId { get; set; } = "";
    public long Size { get; set; }
    public DateTime Utc { get; set; }
}

/// <summary>
/// Processed-files ledger: every file a rule moved or renamed is remembered at its new path, so no rule ever
/// picks up its own output again (no loops, no ping-pong between watched folders). It lives inside the organize
/// settings blob; feature code only updates the in-memory <see cref="AppSettings"/>, the shell saves it.
/// </summary>
public static class OrganizeLedger
{
    public const int MaxEntries = 5000;
    /// <summary>Plan-item arg carrying the rule id, so a finished run can be recorded per rule.</summary>
    public const string RuleIdArg = "organize.ruleId";

    /// <summary>Serializes ledger updates (watch mode records from a background thread).</summary>
    public static readonly object SettingsLock = new();

    // The engine may report where a file really ended up (after auto-numbering) in the step's undo data.
    private static readonly string[] FinalPathKeys = { "finalPath", "destinationPath", "destination", "newPath", "to" };

    /// <summary>Adds the finished moves/renames of <paramref name="run"/> to the ledger in <paramref name="appSettings"/>.</summary>
    public static void RecordInto(AppSettings appSettings, RunRecord run, DateTime nowUtc)
    {
        lock (SettingsLock)
        {
            var settings = OrganizeSettings.Load(appSettings);
            Record(settings, run, nowUtc);
            settings.Store(appSettings);
        }
    }

    public static void Record(OrganizeSettings settings, RunRecord run, DateTime nowUtc)
    {
        if (run.Mode != ExecutionMode.Real) return;
        foreach (var step in run.Steps)
        {
            if (step.Status != StepStatus.Done || step.Item.FeatureId != OrganizeModule.FeatureId) continue;
            if (step.Item.Kind is not (ActionKind.MovePath or ActionKind.RenamePath)) continue;
            if (step.Item.Args?.TryGetValue(RuleIdArg, out var ruleId) != true || ruleId is null) continue;
            var final = FinalPathKeys.Select(k => step.UndoData?.GetValueOrDefault(k)).FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? step.Item.DestinationPath;
            if (string.IsNullOrEmpty(final)) continue;
            settings.Ledger.RemoveAll(e => e.Path.Equals(final, StringComparison.OrdinalIgnoreCase));
            settings.Ledger.Add(new LedgerEntry { Path = final, RuleId = ruleId, Size = step.Item.ExpectedSize, Utc = nowUtc });
        }
        Prune(settings);
    }

    /// <summary>Drops entries whose file is gone (moved on, undone, deleted) and keeps the newest <see cref="MaxEntries"/>.</summary>
    public static void Prune(OrganizeSettings settings)
    {
        settings.Ledger.RemoveAll(e => !File.Exists(e.Path) && !Directory.Exists(e.Path));
        if (settings.Ledger.Count > MaxEntries)
            settings.Ledger = settings.Ledger.OrderByDescending(e => e.Utc).Take(MaxEntries).ToList();
    }

    public static Dictionary<string, LedgerEntry> Index(IEnumerable<LedgerEntry> entries)
    {
        var map = new Dictionary<string, LedgerEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries) if (!string.IsNullOrEmpty(e.Path)) map[PathUtil.Normalize(e.Path)] = e;
        return map;
    }
}
