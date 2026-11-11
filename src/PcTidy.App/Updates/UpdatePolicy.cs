using PcTidy.UI.Flow;

namespace PcTidy.App.Updates;

/// <summary>
/// When PC Tidy checks and when it offers (pure, so tests pin it down):
/// <list type="bullet">
/// <item>The automatic check runs at most once a day, a while after start, off the UI thread, and only when
/// "Check for updates automatically" is on (the default) and PC Tidy isn't in demo or practice mode.</item>
/// <item>A version the user skipped isn't offered again by the automatic check ("Check for updates" still shows it).</item>
/// <item>Nothing is offered or installed while a run, a scan or an undo is going (<see cref="BusyReason"/>).</item>
/// </list>
/// </summary>
public static class UpdatePolicy
{
    public static readonly TimeSpan CheckEvery = TimeSpan.FromDays(1);
    /// <summary>How long after start the automatic check waits, so start-up stays quick.</summary>
    public static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(45);

    public static bool AutoCheckDue(bool enabled, bool practice, DateTime? lastCheckUtc, DateTime nowUtc)
    {
        if (!enabled || practice) return false;
        if (lastCheckUtc is not { } last) return true;
        // A clock that went backwards (or a hand-edited time in the future) doesn't block checks forever.
        return last > nowUtc || nowUtc - last >= CheckEvery;
    }

    /// <summary>Whether a check result becomes a notice: an available update that the user hasn't skipped (manual checks show skipped ones too).</summary>
    public static bool ShouldNotify(UpdateCheck check, string? skippedVersion, bool manual)
        => check is { State: UpdateState.Available, Offer: { } offer } && (manual || !string.Equals(skippedVersion, offer.VersionText, StringComparison.Ordinal));

    /// <summary>
    /// Why an update can't be offered or installed right now (null = it can). The one-task gate covers runs, undos,
    /// resumes and roll backs (<see cref="RunGate"/>, FlowHost.IsWorking and the executor's IsChanging); scans only read
    /// and aren't on that gate, so they are passed in separately: Setup closes PC Tidy and would cut a scan off.
    /// An open dialog counts too (3.1.1 review M3): "Undo this run?" is about to start an undo, and a notice queued
    /// behind it would open on top of that undo.
    /// </summary>
    public static string? BusyReason(RunGate gate, bool flowWorking, bool executorChanging, bool scanning, bool dialogOpen = false)
    {
        if (gate.IsBusy) return gate.Reason ?? RunGate.BusyReason;
        if (flowWorking || executorChanging) return RunGate.BusyReason;
        if (scanning) return "Available when the scan finishes";
        if (dialogOpen) return DialogOpenReason;
        return null;
    }

    public const string DialogOpenReason = "Available when the open question is answered";

    /// <summary>
    /// The notice may still be shown now that it has the dialog gate: nothing is busy, the user hasn't skipped this
    /// version meanwhile, and (for the automatic check) the switch is still on. "Check for updates" shows a skipped
    /// version too, but never while something is busy.
    /// </summary>
    public static bool StillOffer(string? busyReason, string? skippedVersion, string offerVersion, bool autoCheckOn, bool manual)
        => busyReason is null && (manual || (autoCheckOn && !string.Equals(skippedVersion, offerVersion, StringComparison.Ordinal)));

    /// <summary>
    /// The one-time "PC Tidy now checks GitHub once a day" notice (3.1.1 review L8): for people who used PC Tidy before
    /// (welcome screen done) and haven't seen it, with the check on, on a real PC only. New users read it on the welcome
    /// screen instead, which marks it seen.
    /// </summary>
    public static bool ShowUpdateCheckNotice(bool onboardingDone, bool seen, bool autoCheckOn, bool practice)
        => onboardingDone && !seen && autoCheckOn && !practice;
}
