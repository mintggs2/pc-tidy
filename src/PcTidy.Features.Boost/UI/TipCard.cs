using PcTidy.Catalog.Games;

namespace PcTidy.Features.Boost.UI;

/// <summary>One guidance card on the Game boost page, flattened for binding. Information only.</summary>
public sealed record TipCard(string Id, string Title, string Summary, IReadOnlyList<string> Points, IReadOnlyList<string> Measured)
{
    public string AutomationName => Title + ". " + Summary;

    /// <summary>Catalog cards for Game boost (C12 first), with every measurement as "Measured: … · hardware · source" + its link.</summary>
    public static IReadOnlyList<TipCard> ForBoost()
        => GuidanceCards.For(CardPlace.Boost)
            .Select(c => new TipCard(c.Id, c.Title, c.Summary, c.Points.Select(p => "•  " + p).ToList(),
                c.Measured.Select(m => $"{m.Line}. {m.SourceUrl}").ToList()))
            .ToList();
}
