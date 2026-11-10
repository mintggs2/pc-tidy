using System.Globalization;
using PcTidy.Features.Schedule.Common;

namespace PcTidy.Features.Schedule.Drift;

/// <summary>One tweak whose value is no longer what PC Tidy set.</summary>
/// <param name="Applied">What PC Tidy set ("1"), or null for "removed".</param>
/// <param name="Current">What the registry holds now, or null for "not there".</param>
public sealed record DriftItem(
    string Description,
    RegistryValueSpec Value,
    string? Applied,
    string? Current,
    string FeatureId,
    string RunId,
    DateTime AppliedUtc)
{
    public string AppliedText => Applied ?? "removed";
    public string CurrentText => Current ?? "not set";
    public string Path => (Value.Hive == RegistryHive.LocalMachine ? "HKLM\\" : "HKCU\\") + Value.KeyPath + "\\" + (Value.ValueName is { Length: > 0 } n ? n : "(Default)");
}

public sealed record DriftReport(int Checked, IReadOnlyList<DriftItem> Drifted)
{
    public static readonly DriftReport Empty = new(0, Array.Empty<DriftItem>());

    /// <summary>Calm, specific wording; null when nothing drifted.</summary>
    public string? Headline => Drifted.Count == 0
        ? null
        : $"{Drifted.Count} of your {Checked} tweak{(Checked == 1 ? "" : "s")} {(Drifted.Count == 1 ? "was" : "were")} changed back, usually by a Windows update.";

    public string Advice => "Nothing was changed. A Windows update often puts settings back to its own defaults. Open Tweaks or Privacy to see them and re-apply the ones you still want.";
}

/// <summary>
/// Tweak drift check (census v3, monthly checkup): compares the value each finished, not-undone run last set with
/// what the registry holds now. Read-only: it never re-applies anything (a resident "re-apply tweaks" service is on
/// the never-build list); the page links to Tweaks instead.
/// </summary>
public sealed class TweakDriftChecker
{
    /// <summary>
    /// The sections whose switches Windows updates tend to put back (Community tweaks §9.6): Tweaks and the bad-tweak finder
    /// ("tweaks", "tweaks.badtweaks"), and Privacy's sweep and notification switches ("privacy", "notifications").
    /// </summary>
    public static bool IsTweakFeature(string featureId)
        => featureId.StartsWith("tweaks", StringComparison.OrdinalIgnoreCase)
           || featureId.StartsWith("privacy", StringComparison.OrdinalIgnoreCase)
           || featureId.Equals("notifications", StringComparison.OrdinalIgnoreCase)
           || featureId.StartsWith("notifications.", StringComparison.OrdinalIgnoreCase);

    private readonly IRunJournal _journal;
    private readonly IRegistryReader _registry;
    private readonly Func<string, bool> _includeFeature;

    public TweakDriftChecker(IRunJournal journal, IRegistryReader registry, Func<string, bool>? includeFeature = null)
    {
        _journal = journal;
        _registry = registry;
        _includeFeature = includeFeature ?? IsTweakFeature;
    }

    public async Task<DriftReport> CheckAsync(CancellationToken ct)
    {
        var runs = await _journal.ListAsync(ct).ConfigureAwait(false);
        // Latest applied value per registry value, oldest run first so later runs win.
        var latest = new Dictionary<string, (StepRecord Step, RunRecord Run)>(StringComparer.OrdinalIgnoreCase);
        foreach (var run in runs.Where(IsApplied).OrderBy(r => r.FinishedUtc ?? r.CreatedUtc))
        {
            foreach (var step in run.Steps.OrderBy(s => s.Index))
            {
                if (step.Status != StepStatus.Done || step.Item.Registry is not { } spec || spec.ValueName is null) continue;
                if (step.Item.Kind is not (ActionKind.RegistrySetValue or ActionKind.RegistryDeleteValue)) continue;
                if (!_includeFeature(step.Item.FeatureId)) continue;
                latest[Key(spec)] = (step, run);
            }
        }

        var drifted = new List<DriftItem>();
        foreach (var (step, run) in latest.Values)
        {
            ct.ThrowIfCancellationRequested();
            var spec = step.Item.Registry!;
            var now = _registry.Read(spec.Hive, spec.KeyPath, spec.ValueName!, spec.View64);
            bool set = step.Item.Kind == ActionKind.RegistrySetValue;
            bool ok = set ? now.ValueExists && SameData(spec, now.Data) : !now.ValueExists;
            if (ok) continue;
            drifted.Add(new DriftItem(step.Item.Description, spec, set ? spec.Data : null,
                now.ValueExists ? Format(now.Data) : null, step.Item.FeatureId, run.RunId, step.FinishedUtc ?? run.FinishedUtc ?? run.CreatedUtc));
        }
        return new DriftReport(latest.Count, drifted.OrderBy(d => d.Description, StringComparer.CurrentCultureIgnoreCase).ToList());
    }

    /// <summary>Only runs that really changed things and still stand (not simulations, not undone or rolled back).</summary>
    private static bool IsApplied(RunRecord r)
        => r.Mode == ExecutionMode.Real && r.FinishedUtc is not null
           && r.Status is RunStatus.Completed or RunStatus.CompletedWithSkips or RunStatus.Failed or RunStatus.PartiallyUndone;

    private static string Key(RegistryValueSpec s) => $"{s.Hive}|{s.View64}|{s.KeyPath.Trim('\\')}|{s.ValueName}";

    /// <summary>Compares the planned invariant data with what the registry returned, by value kind.</summary>
    internal static bool SameData(RegistryValueSpec spec, object? current)
    {
        if (current is null) return false;
        switch (spec.Kind)
        {
            case RegistryKind.DWord:
            case RegistryKind.QWord:
                var expected = ParseNumber(spec.Data);
                ulong? actual = current switch
                {
                    int i => unchecked((uint)i),
                    long l => unchecked((ulong)l),
                    _ => ParseNumber(current.ToString()),
                };
                return expected is not null && expected == actual;
            case RegistryKind.MultiString:
                var lines = current is string[] arr ? string.Join("\n", arr) : current.ToString();
                return string.Equals((spec.Data ?? "").Replace("\r", ""), lines, StringComparison.Ordinal);
            case RegistryKind.Binary:
                return current is byte[] bytes && string.Equals(HexOnly(spec.Data), Convert.ToHexString(bytes), StringComparison.OrdinalIgnoreCase);
            default:
                return string.Equals(spec.Data ?? "", current.ToString(), StringComparison.Ordinal);
        }
    }

    internal static ulong? ParseNumber(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ulong.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h) ? h : null;
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d)) return d < 0 ? unchecked((uint)(int)d) : (ulong)d;
        return ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var u) ? u : null;
    }

    private static string HexOnly(string? s) => new((s ?? "").Where(Uri.IsHexDigit).ToArray());

    private static string Format(object? data) => data switch
    {
        null => "not set",
        int i => unchecked((uint)i).ToString(CultureInfo.InvariantCulture),
        long l => unchecked((ulong)l).ToString(CultureInfo.InvariantCulture),
        string[] a => string.Join(", ", a),
        byte[] b => b.Length <= 16 ? Convert.ToHexString(b) : Convert.ToHexString(b, 0, 16) + "…",
        _ => data.ToString() ?? "",
    };
}
