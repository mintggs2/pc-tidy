using System.Windows;
using PcTidy.Core;
using PcTidy.Core.Model;
using Wpf.Ui.Controls;

namespace PcTidy.UI.Flow.Resume;

/// <summary>What starting a scan decided about checkpoints: the session that saves progress (null = none) and the preview's notes.</summary>
public sealed record ResumeStart(ScanCheckpointSession? Session, ResumeDecision? Decision, string? DiscardNote, bool StoppedByUser)
{
    public static readonly ResumeStart None = new(null, null, null, false);

    /// <summary>"Found at 12:41, before PC Tidy closed" for each reused section, by scanner position.</summary>
    public IReadOnlyDictionary<int, string> FoundTexts()
        => Session?.Reused.ToDictionary(kv => kv.Key, kv => ResumeText.FoundAt(kv.Value.FinishedUtc, StoppedByUser)) ?? new Dictionary<int, string>();
}

/// <summary>The part of resuming both flow pages share (Scan everything and the standard section page).</summary>
public static class ResumeFlow
{
    /// <summary>
    /// A fresh scan replaces the flow's checkpoint. A resume runs the staleness rules first: when the saved scan can't
    /// be used, everything is scanned again and <see cref="ResumeStart.DiscardNote"/> says why.
    /// </summary>
    public static ResumeStart Start(ScanResume? resume, string flow, string title, string navigateTo, IReadOnlyList<IFeatureScanner> scanners,
        ScanContext ctx, ScanCheckpoint? resumeFrom)
    {
        if (resume is null || scanners.Count == 0) return ResumeStart.None;
        var key = resume.KeyFor(flow, scanners, ctx.Depth, ctx.Preset, ctx.Roots, ctx.Locations);
        if (resumeFrom is not null)
        {
            var decision = resume.Evaluate(resumeFrom, key);
            if (decision.CanResume) return new(resume.Resume(resumeFrom, decision), decision, null, resumeFrom.StoppedByUser);
            return new(resume.StartFresh(key, title, navigateTo), decision,
                $"Your last scan couldn't be resumed because {decision.DiscardReason}, so it was scanned again from the start.", false);
        }
        return new(resume.StartFresh(key, title, navigateTo), null, null, false);
    }

    /// <summary>The preview's notes about a resumed scan, as calm info bars.</summary>
    public static IEnumerable<InfoBar> PreviewBars(IReadOnlyList<ScannerOutcome> outcomes, ResumeStart? resume)
        => PreviewMessages(outcomes, resume).Select(Bar);

    /// <summary>The preview's notes about a resumed scan: what was reused, what was scanned again and why.</summary>
    public static IEnumerable<string> PreviewMessages(IReadOnlyList<ScannerOutcome> outcomes, ResumeStart? resume)
    {
        if (resume is null) yield break;
        if (resume.DiscardNote is { } discarded) yield return discarded;
        int reused = outcomes.Count(o => o.FoundAtUtc is not null);
        if (reused > 0)
        {
            int fresh = outcomes.Count(o => o.Result is not null && o.FoundAtUtc is null);
            var msg = $"Resumed your last scan: {reused} section{PlanFacts.S(reused)} {(reused == 1 ? "comes" : "come")} from it and {fresh} {(fresh == 1 ? "was" : "were")} scanned now. "
                      + (resume.Decision is { } d && ResumeText.Rescanned(d.Rescan) is { } again ? again + " " : "")
                      + "Everything is checked again before anything changes.";
            yield return msg;
        }
        foreach (var note in resume.Session?.Notes ?? Array.Empty<string>()) yield return note;
    }

    private static InfoBar Bar(string message) => new()
    {
        Message = message, IsOpen = true, IsClosable = true, Severity = InfoBarSeverity.Informational, Margin = new Thickness(0, 0, 0, 8),
    };
}
