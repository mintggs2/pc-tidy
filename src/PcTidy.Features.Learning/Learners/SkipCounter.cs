using PcTidy.Features.Learning.Model;

namespace PcTidy.Features.Learning.Learners;

/// <summary>Result of the skip counter for one feature group.</summary>
/// <param name="Skipped">Skips among the last <paramref name="Of"/> votes.</param>
public sealed record SkipVerdict(bool UntickByDefault, int Skipped, int Of)
{
    public string Reason => UntickByDefault
        ? $"You skipped this {Skipped} of the last {Of} times, so it starts unticked."
        : Of == 0 ? "No choices recorded yet." : $"You skipped this {Skipped} of the last {Of} times.";
}

/// <summary>
/// "Items you always skip": the newest <see cref="LearningRules.SkipWindow"/> votes for a group must all be skips.
/// A reject is one skip vote, an undo is two (undoing is a stronger "no"), an accept is one keep vote.
/// Pure: works on the decisions, newest first.
/// </summary>
public static class SkipCounter
{
    public const char Skip = 'R';
    public const char Keep = 'A';

    /// <summary>Expands decisions (newest first) into votes (newest first): "R", "A", undo = "RR".</summary>
    public static string Votes(IEnumerable<LearningDecision> newestFirst)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var d in newestFirst)
        {
            switch (d)
            {
                case LearningDecision.Accept: sb.Append(Keep); break;
                case LearningDecision.Reject: sb.Append(Skip); break;
                case LearningDecision.Undo: sb.Append(Skip).Append(Skip); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Verdict from raw decisions (newest first) followed by the remembered votes of aggregated (older) decisions.
    /// </summary>
    public static SkipVerdict Evaluate(IEnumerable<LearningDecision> newestFirst, string? olderVotes = null)
    {
        var votes = (Votes(newestFirst) + (olderVotes ?? "")).AsSpan();
        var window = votes[..Math.Min(LearningRules.SkipWindow, votes.Length)];
        int skipped = 0;
        foreach (var v in window) if (v == Skip) skipped++;
        bool untick = window.Length == LearningRules.SkipWindow && skipped == LearningRules.SkipWindow;
        return new SkipVerdict(untick, skipped, window.Length);
    }

    /// <summary>The newest votes to remember when raw decisions are folded into totals.</summary>
    public static string KeepRecent(string newerVotes, string olderVotes)
    {
        var all = newerVotes + olderVotes;
        return all.Length <= LearningRules.SkipWindow ? all : all[..LearningRules.SkipWindow];
    }
}
