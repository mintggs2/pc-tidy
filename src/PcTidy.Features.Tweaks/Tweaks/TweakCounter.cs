using PcTidy.Features.Tweaks.Reading;

namespace PcTidy.Features.Tweaks.Tweaks;

/// <summary>"7 of 31 on": how many of the settings PC Tidy can apply are already set, read from the real system state.</summary>
public readonly record struct TweakCount(int On, int Total)
{
    /// <summary>"N of M on", or null when nothing could be read (no badge then).</summary>
    public string? Text => Total == 0 ? null : $"{On} of {Total} on";
    public string AutomationText => Total == 0 ? "" : $"{On} of {Total} settings PC Tidy can apply are already on";
}

/// <summary>
/// One definition for the page header and the rail badge, so both always agree. Counted: offered tweaks whose changes are
/// registry values or service start types (on/off settings PC Tidy can apply and read back). Not counted: Store-app removal
/// and Windows features (not on/off settings), guidance and "keep it on" rows, the shelf, and anything that couldn't be read.
/// Read-only: it only uses the state readers.
/// </summary>
public static class TweakCounter
{
    public static bool IsCounted(TweakDefinition t)
        => t.IsOffered && !t.IsKeepOn && t.HasActions
           && t.ActionableChanges.All(c => c is RegistryValueChange or RegistryTokenChange or ServiceStartChange);

    public static bool IsReadable(TweakStatus s) => s is TweakStatus.Available or TweakStatus.Applied or TweakStatus.Differs;

    /// <summary>Counts already-read rows (the page uses its scan).</summary>
    public static TweakCount Count(IEnumerable<(TweakDefinition Tweak, TweakState State)> rows)
    {
        int on = 0, total = 0;
        foreach (var (t, s) in rows)
        {
            if (!IsCounted(t) || !IsReadable(s.Status)) continue;
            total++;
            if (s.Status == TweakStatus.Applied) on++;
        }
        return new TweakCount(on, total);
    }

    /// <summary>Reads just the counted tweaks (registry only, so it's cheap) and counts them. Same filters as the scanner.</summary>
    public static TweakCount Read(TweakEnvironment env, CancellationToken ct)
    {
        var rows = new List<(TweakDefinition, TweakState)>();
        foreach (var t in TweakCatalog.Shown.Where(IsCounted))
        {
            ct.ThrowIfCancellationRequested();
            if (!TweaksScanner.Applies(t, env)) continue;
            if (t.Changes.Any(c => TweakSafetyRules.WhyForbidden(c) is not null)) continue;
            try { rows.Add((t, TweakStateReader.Read(t, env))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { /* not counted */ }
        }
        return Count(rows);
    }
}
