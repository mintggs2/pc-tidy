using System.Globalization;
using PcTidy.Catalog.Insights;
using PcTidy.Features.Insights.Model;
using PcTidy.Features.Insights.Source;

namespace PcTidy.Features.Insights.Cards;

/// <summary>
/// Shared runtimes games and apps ask for (Community tweaks §9.6): Visual C++, the old DirectX 9 add-on files and the
/// .NET Desktop Runtime. Read-only file checks. PC Tidy installs nothing: the card says what's missing and how to get it
/// from Microsoft, and the user decides.
/// </summary>
internal static class RuntimeCards
{
    /// <summary>The .NET Desktop Runtime suggested when none is installed (the current long-term support release).</summary>
    public const int SuggestedDotNet = 10;

    public static InsightCard Runtimes(CardContext c)
    {
        const string title = "Game and app runtimes";
        const string icon = "PuzzlePiece24";
        var r = c.Source.ReadRuntimes();
        var missing = new List<string>();
        var steps = new List<string>();
        var facts = new List<InsightFact>
        {
            new("Visual C++ (64-bit)", Show(r.VisualCpp64)),
            new("Visual C++ (32-bit)", Show(r.VisualCpp32)),
            new("DirectX 9 add-on files", r.DirectX9Extras == TriState.Yes ? "Installed" : r.DirectX9Extras == TriState.No ? "Not installed (only older games need them)" : "Unknown"),
            new(".NET Desktop Runtime", r.DotNetDesktopVersions.Count > 0 ? string.Join(", ", r.DotNetDesktopVersions.Select(v => v.ToString(CultureInfo.InvariantCulture))) : "None"),
        };

        if (r.VisualCpp64 == TriState.No)
        {
            missing.Add("Visual C++ (64-bit)");
            steps.Add("Visual C++: winget install Microsoft.VCRedist.2015+.x64");
        }
        if (r.VisualCpp32 == TriState.No)
        {
            missing.Add("Visual C++ (32-bit)");
            steps.Add("Visual C++ 32-bit: winget install Microsoft.VCRedist.2015+.x86");
        }
        if (r.DirectX9Extras == TriState.No)
            steps.Add("DirectX 9 add-on files (only if an older game asks for d3dx9 files): winget install Microsoft.DirectX");
        if (r.DotNetDesktopVersions.Count == 0)
            steps.Add($".NET Desktop Runtime (only if an app asks for it): winget install Microsoft.DotNet.DesktopRuntime.{SuggestedDotNet}");

        var links = new[]
        {
            new InsightLink("Visual C++ downloads (Microsoft)", InsightsLinks.VisualCppRedist),
            new InsightLink("DirectX End-User Runtime (Microsoft)", InsightsLinks.DirectXRuntime),
            new InsightLink(".NET downloads (Microsoft)", InsightsLinks.DotNetDownloads),
        };
        const string explanation = "Many games and apps rely on shared Microsoft runtimes. Most installers add what they need, but a missing one gives errors like "
                                   + "\"VCRUNTIME140.dll was not found\". Installing a runtime you don't need does no harm, but there's no speed gain either.";
        const string howTo = "PC Tidy doesn't install anything. If a game or app says one is missing, get it from Microsoft (links below) or, "
                             + "if you use winget, run the command shown in a terminal.";

        if (r.VisualCpp64 == TriState.Unknown && r.VisualCpp32 == TriState.Unknown && r.DirectX9Extras == TriState.Unknown)
            return InsightCard.NotAvailable(InsightsIds.Runtimes, InsightCategory.Hardware, title, icon, "PC Tidy couldn't check the runtimes on this PC.");

        return new InsightCard
        {
            Id = InsightsIds.Runtimes, Category = InsightCategory.Hardware, Title = title, Icon = icon,
            Status = missing.Count > 0 ? InsightStatus.Attention : InsightStatus.Good,
            Headline = missing.Count > 0 ? $"Missing: {string.Join(", ", missing)}" : "The common game runtimes are installed",
            Explanation = explanation,
            WhatToDo = missing.Count > 0 ? howTo : steps.Count > 0 ? "Nothing to do. The extras listed are only needed if a game or app asks for them." : "Nothing to do.",
            Facts = facts, Steps = steps, Links = links,
        };
    }

    private static string Show(TriState s) => s switch { TriState.Yes => "Installed", TriState.No => "Missing", _ => "Unknown" };
}
