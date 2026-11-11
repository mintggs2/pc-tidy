using PcTidy.Catalog.Insights;
using PcTidy.Core.Model;

namespace PcTidy.Features.Insights.Model;

/// <summary>Card groups on the Insights page.</summary>
public enum InsightCategory { Safety, Health, Storage, Hardware, Guides }

/// <summary>A card's state. Calm wording only: "Attention" means "worth a look", never an alarm.</summary>
public enum InsightStatus { Good, Info, Attention, NotAvailable, NeedsAdmin }

public sealed record InsightFact(string Label, string Value);

/// <summary>A page to open (https or a Windows Settings / Security URI) through the standard confirm.</summary>
public sealed record InsightLink(string Label, string Target);

/// <summary>Another PC Tidy section that owns the fix (e.g. Tweaks' bad-tweak finder).</summary>
public sealed record InsightNavigation(string Label, string ModuleId);

/// <summary>
/// One read-only Insights card: what PC Tidy saw, a plain explanation and what to do. <see cref="Items"/> holds rows the
/// user can tick (only where a supported, reversible action exists); everything else is information.
/// </summary>
public sealed record InsightCard
{
    public required string Id { get; init; }
    public required InsightCategory Category { get; init; }
    public required string Title { get; init; }
    /// <summary>WPF-UI SymbolRegular name.</summary>
    public required string Icon { get; init; }
    public required InsightStatus Status { get; init; }
    /// <summary>One short line: what PC Tidy found ("Stable: 9.4 out of 10").</summary>
    public required string Headline { get; init; }
    /// <summary>Plain explanation of what this is and why it matters.</summary>
    public required string Explanation { get; init; }
    /// <summary>What to do (or "Nothing to do").</summary>
    public required string WhatToDo { get; init; }
    public IReadOnlyList<InsightFact> Facts { get; init; } = Array.Empty<InsightFact>();
    /// <summary>Numbered steps the user follows themselves (PC Tidy doesn't do them).</summary>
    public IReadOnlyList<string> Steps { get; init; } = Array.Empty<string>();
    public IReadOnlyList<InsightLink> Links { get; init; } = Array.Empty<InsightLink>();
    public InsightNavigation? Navigate { get; init; }
    /// <summary>Tickable rows (Check badge, never ticked by default) for cards with a supported reversible action.</summary>
    public IReadOnlyList<ScanItem> Items { get; init; } = Array.Empty<ScanItem>();
    /// <summary>The result is a best guess (heuristic signs or unverified reads), and the card says so.</summary>
    public bool IsHeuristic { get; init; }
    /// <summary>
    /// Measured results from the research, each with its hardware, source and date (shown as "measured on other PCs").
    /// Never a general claim: a card without a measurement leaves this empty.
    /// </summary>
    public IReadOnlyList<MeasuredNumber> Measured { get; init; } = Array.Empty<MeasuredNumber>();

    public bool IsAvailable => Status is not InsightStatus.NotAvailable;

    /// <summary>"Not available on this PC" card with the reason.</summary>
    public static InsightCard NotAvailable(string id, InsightCategory category, string title, string icon, string why) => new()
    {
        Id = id, Category = category, Title = title, Icon = icon, Status = InsightStatus.NotAvailable,
        Headline = "Not available on this PC", Explanation = why, WhatToDo = "Nothing to do.",
    };
}

/// <summary>Everything one Insights scan found.</summary>
public sealed record InsightsReport(IReadOnlyList<InsightCard> Cards, DateTime FinishedUtc, IReadOnlyList<string> Notes)
{
    public InsightCard? Find(string id) => Cards.FirstOrDefault(c => c.Id == id);
}
