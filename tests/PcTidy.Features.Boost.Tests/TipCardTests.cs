using System.Text.RegularExpressions;
using PcTidy.Features.Boost.UI;
using Xunit;

namespace PcTidy.Features.Boost.Tests;

/// <summary>
/// v3.1 Game boost "Good to know" cards (comparison §8.2 C12, C1, C7): text only, every number with its hardware and its
/// dated source. They describe what was measured; Game boost's behaviour doesn't change.
/// </summary>
public class TipCardTests
{
    [Fact]
    public void Closing_background_apps_card_comes_first_with_both_measurements_and_their_sources()
    {
        var cards = TipCard.ForBoost();
        var c12 = cards[0];
        Assert.Equal("C12", c12.Id);
        var text = string.Join(" ", c12.Points.Append(c12.Summary).Concat(c12.Measured));
        foreach (var n in new[] { "CS:GO at 1440p 423 FPS", "403", "about 5%", "Windows 10", "Linus Tech Tips", "2020",
                     "+2.5%", "9800X3D", "PCWorld", "2025", "https://" })
            Assert.Contains(n, text);
        // The card is honest about what Game boost does: it never touches services, priority or power.
        Assert.Contains("never touches services, priority or power", text);
    }

    [Fact]
    public void Cap_and_reflex_cards_carry_numbers_hardware_and_dated_sources()
    {
        var cards = TipCard.ForBoost().ToDictionary(c => c.Id);
        var c1 = string.Join(" ", cards["C1"].Points.Concat(cards["C1"].Measured));
        foreach (var n in new[] { "in-game limiter", "refresh rate minus 3", "11 to 22 ms", "42%", "9 to 10 ms", "about 12 ms", "about 20 ms", "RX 5700", "2019" })
            Assert.Contains(n, c1);
        var c7 = string.Join(" ", cards["C7"].Points.Concat(cards["C7"].Measured));
        foreach (var n in new[] { "20 → 16.7 ms", "0.2 ms", "1.1 ms", "Anti-Lag+", "23.10.1", "VAC" })
            Assert.Contains(n, c7);

        foreach (var line in cards.Values.SelectMany(c => c.Measured))
        {
            Assert.StartsWith("Measured: ", line);
            Assert.Matches(@"\d", line);
            Assert.True(Regex.IsMatch(line, @"\b(19|20)\d{2}\b") || line.Contains("undated"), line);
            Assert.Contains("https://", line);
        }
    }
}
