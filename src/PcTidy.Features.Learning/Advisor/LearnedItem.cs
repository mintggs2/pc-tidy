namespace PcTidy.Features.Learning.Advisor;

public enum LearnedKind { Rule, Skip, Folder, App, Drive, QuietHours }

/// <summary>One card on the "What PC Tidy has learned" page: what, why, how much evidence, examples, since when.</summary>
public sealed record LearnedItem
{
    public required string Id { get; init; }
    public required LearnedKind Kind { get; init; }
    public required string Title { get; init; }
    /// <summary>Why PC Tidy believes it, in plain words.</summary>
    public required string Reason { get; init; }
    /// <summary>The evidence counts ("7 moves on 3 days, 88% the same place").</summary>
    public required string Counts { get; init; }
    /// <summary>2–3 example files / items (may be empty).</summary>
    public IReadOnlyList<string> Examples { get; init; } = Array.Empty<string>();
    public required DateTime SinceUtc { get; init; }
    /// <summary>"Turn off": kept, but changes nothing and suggests nothing.</summary>
    public bool Off { get; init; }
    /// <summary>What it changes elsewhere, so the user knows where to look ("Clean starts with it unticked").</summary>
    public required string Effect { get; init; }

    /// <summary>The evidence behind it (rule candidate, group key, folder, app key or drive), for the page's actions.</summary>
    internal object? Payload { get; init; }
}
