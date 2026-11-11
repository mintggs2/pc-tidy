using System.Globalization;
using PcTidy.Catalog.Insights;
using PcTidy.Core.Model;
using PcTidy.Features.Insights.Model;
using PcTidy.Features.Insights.Planning;
using PcTidy.Features.Insights.Source;

namespace PcTidy.Features.Insights.Cards;

/// <summary>
/// Shared runtimes games and apps ask for (Community tweaks §9.6): Visual C++, the old DirectX 9 add-on files and the
/// .NET Desktop Runtime. Read-only file checks. When winget is present, each runtime that's actually missing gets a row
/// the user can tick (v3.1 build list B4): a <c>winget.install</c> plan item with an allow-listed Microsoft id
/// (<see cref="RuntimeInstalls"/>), previewed and confirmed like every change. Nothing is ticked for the user.
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
            new(".NET Desktop Runtime", !r.DotNetKnown ? "Couldn't check"
                : r.DotNetDesktopVersions.Count > 0 ? string.Join(", ", r.DotNetDesktopVersions.Select(v => v.ToString(CultureInfo.InvariantCulture))) : "None"),
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
        if (r.DotNetKnown && r.DotNetDesktopVersions.Count == 0)
            steps.Add($".NET Desktop Runtime (only if an app asks for it): winget install Microsoft.DotNet.DesktopRuntime.{SuggestedDotNet}");

        var links = new[]
        {
            new InsightLink("Visual C++ downloads (Microsoft)", InsightsLinks.VisualCppRedist),
            new InsightLink("DirectX End-User Runtime (Microsoft)", InsightsLinks.DirectXRuntime),
            new InsightLink(".NET downloads (Microsoft)", InsightsLinks.DotNetDownloads),
        };
        const string explanation = "Many games and apps rely on shared Microsoft runtimes. Most installers add what they need, but a missing one gives errors like "
                                   + "\"VCRUNTIME140.dll was not found\". Installing a runtime you don't need does no harm, but there's no speed gain either.";
        const string howTo = "PC Tidy doesn't install anything by itself. If a game or app says one is missing, get it from Microsoft (links below) or, "
                             + "if you use winget, run the command shown in a terminal.";

        if (r.VisualCpp64 == TriState.Unknown && r.VisualCpp32 == TriState.Unknown && r.DirectX9Extras == TriState.Unknown)
            return InsightCard.NotAvailable(InsightsIds.Runtimes, InsightCategory.Hardware, title, icon, "PC Tidy couldn't check the runtimes on this PC.");

        var items = r.WingetPresent ? InstallRows(c, r) : Array.Empty<ScanItem>();
        string whatToDo = items.Count > 0
            ? "Tick a runtime only if you need it, then review: winget downloads it from Microsoft. The DirectX and .NET rows are only needed when a game or app asks for them."
            : missing.Count > 0 ? howTo
            : steps.Count > 0 ? "Nothing to do. The extras listed are only needed if a game or app asks for them." : "Nothing to do.";
        // Code review L5: an unreadable .NET folder is "couldn't check", never "missing", so nothing is offered for it.
        if (!r.DotNetKnown) whatToDo += " PC Tidy couldn't check which .NET versions are installed, so it doesn't offer to install .NET.";

        return new InsightCard
        {
            Id = InsightsIds.Runtimes, Category = InsightCategory.Hardware, Title = title, Icon = icon,
            Status = missing.Count > 0 ? InsightStatus.Attention : InsightStatus.Good,
            Headline = missing.Count > 0 ? $"Missing: {string.Join(", ", missing)}" : "The common game runtimes are installed",
            Explanation = explanation,
            WhatToDo = whatToDo,
            Facts = facts,
            // The tick rows replace the typed commands when winget is here.
            Steps = items.Count > 0 ? Array.Empty<string>() : steps,
            Links = links,
            Items = items,
        };
    }

    /// <summary>The runtimes that are actually missing, as allow-listed winget installs.</summary>
    internal static IReadOnlyList<RuntimeInstall> MissingInstalls(RuntimeFacts r)
    {
        var list = new List<RuntimeInstall>();
        if (r.VisualCpp64 == TriState.No) list.Add(RuntimeInstalls.For(RuntimeKind.VisualCpp64));
        if (r.VisualCpp32 == TriState.No) list.Add(RuntimeInstalls.For(RuntimeKind.VisualCpp32));
        if (r.DirectX9Extras == TriState.No) list.Add(RuntimeInstalls.For(RuntimeKind.DirectX));
        // Only when the .NET folder was read: an empty list from a failed read means "couldn't check", not "none".
        if (r.DotNetKnown && !r.DotNetDesktopVersions.Contains(8)) list.Add(RuntimeInstalls.For(RuntimeKind.DotNetDesktop8));
        if (r.DotNetKnown && !r.DotNetDesktopVersions.Contains(9)) list.Add(RuntimeInstalls.For(RuntimeKind.DotNetDesktop9));
        return list;
    }

    private static IReadOnlyList<ScanItem> InstallRows(CardContext c, RuntimeFacts r)
        => MissingInstalls(r).Select(runtime =>
        {
            var (allowed, refusal) = c.Vet(InsightPlans.InstallRuntime(runtime));
            bool optional = runtime.Kind is RuntimeKind.DirectX or RuntimeKind.DotNetDesktop8 or RuntimeKind.DotNetDesktop9;
            return new ScanItem
            {
                Id = $"{InsightsIds.Runtimes}.{runtime.Kind.ToString().ToLowerInvariant()}",
                Title = $"Install {runtime.Name}",
                Detail = optional ? "Only needed if a game or app asks for it." : "Missing on this PC.",
                Reason = RuntimeInstalls.PreviewText(runtime.Name),
                Badge = Badge.Check, DefaultSelected = false,
                CurrentState = "Not installed",
                Details = new Dictionary<string, string> { ["winget id"] = runtime.WingetId, ["Undo"] = "Can't be undone from History. Remove it in Settings → Apps if you need to." },
                Actions = allowed,
                DisabledReason = allowed.Count == 0 ? refusal ?? "PC Tidy's safety rules don't allow this one." : null,
            };
        }).ToList();

    private static string Show(TriState s) => s switch { TriState.Yes => "Installed", TriState.No => "Missing", _ => "Unknown" };
}
