using PcTidy.Catalog.Insights;
using PcTidy.Core.Model;
using PcTidy.Features.Insights.Model;

namespace PcTidy.Features.Insights.Cards;

/// <summary>Step-by-step guides PC Tidy explains but never carries out itself.</summary>
internal static class GuideCards
{
    /// <summary>Clean GPU driver reinstall (Feature census C5 "DDU"): links to the official pages only, never runs DDU.</summary>
    public static InsightCard GpuReinstall(CardContext c)
    {
        const string title = "Clean graphics driver reinstall";
        var vendor = c.Gpu switch
        {
            GpuVendor.Nvidia => ("NVIDIA", InsightsLinks.NvidiaDrivers),
            GpuVendor.Amd => ("AMD", InsightsLinks.AmdDrivers),
            GpuVendor.Intel => ("Intel", InsightsLinks.IntelDrivers),
            _ => ((string, string)?)null,
        };
        if (vendor is not { } v)
            return InsightCard.NotAvailable(InsightsIds.GpuReinstall, InsightCategory.Guides, title, "Wrench24", "No NVIDIA, AMD or Intel graphics driver was found.");

        return new InsightCard
        {
            Id = InsightsIds.GpuReinstall, Category = InsightCategory.Guides, Title = title, Icon = "Wrench24", Status = InsightStatus.Info,
            Headline = $"A checklist for when the {v.Item1} driver misbehaves",
            Explanation = "Only worth doing for black screens, a driver install that keeps failing, or after swapping to a graphics card from another maker. "
                          + "For a normal update, just install the new driver over the old one. Display Driver Uninstaller (DDU) is a free third-party tool that also cleans the registry, "
                          + "which PC Tidy never does itself, so PC Tidy only links to its official page.",
            WhatToDo = "Follow the steps in order. Try the driver's own \"clean install\" or \"factory reset\" option first: it's gentler than DDU.",
            Steps = new[]
            {
                $"Download the new {v.Item1} driver first, from the official page below.",
                "In PC Tidy's Drivers section, back up the current driver, and make a restore point.",
                $"Try the gentle way: run the new installer and choose its clean install / factory reset option. If that fixes it, stop here.",
                "Still broken? Get DDU only from its official site (fake download sites exist) and follow its own guide: it runs in Safe Mode.",
                "Unplug the network cable or turn off Wi-Fi so Windows Update doesn't install a driver in the middle.",
                "Restart, install the driver you downloaded, then reconnect to the internet.",
            },
            Links = new[]
            {
                new InsightLink($"Open {v.Item1}'s driver page", v.Item2),
                new InsightLink("Open DDU's official site (Wagnardsoft)", InsightsLinks.Ddu),
            },
            Navigate = new InsightNavigation("Open Drivers", InsightsIds.DriversModule),
        };
    }
}
