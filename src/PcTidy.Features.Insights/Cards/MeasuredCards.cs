using PcTidy.Catalog.Insights;
using PcTidy.Catalog.Tweaks;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Insights.Model;
using PcTidy.Features.Insights.Source;

namespace PcTidy.Features.Insights.Cards;

/// <summary>
/// v3.1 measured info cards (tuning-tool comparison §8.2 C3-C6, FPS research §4 "read-only detect" / "info card" rows).
/// Read-only detect plus guidance. Every number carries its hardware, source and date (<see cref="MeasuredNumbers"/>);
/// none of these cards offers a change.
/// </summary>
internal static class MeasuredCards
{
    private const string MeasuredNote = " The numbers below were measured on other PCs: your own result can differ.";

    // ------------------------------------------------------------------ C3: NVIDIA App game filters

    /// <summary>The NVIDIA App's Uninstall display name. Verify on a PC with the app: taken from NVIDIA's installer, not re-checked.</summary>
    internal const string NvidiaAppName = "NVIDIA App";

    public static InsightCard NvidiaApp(CardContext c)
    {
        const string title = "NVIDIA App game filters";
        const string icon = "Filter24";
        bool installed = c.HasApp(NvidiaAppName);
        var measured = new[] { MeasuredNumbers.NvidiaAppFilters, MeasuredNumbers.NvidiaSharpenPlus };
        if (!installed && c.Gpu != GpuVendor.Nvidia)
            return InsightCard.NotAvailable(InsightsIds.NvidiaApp, InsightCategory.Hardware, title, icon, "No NVIDIA App and no NVIDIA graphics card were found.");

        const string explanation = "The NVIDIA App's in-game overlay can apply Game Filters and Photo Mode. With them on, games ran slower in most titles tested; "
                                   + "the app itself and its statistics overlay cost almost nothing. A filter you actually use, like Sharpen+, costs more." + MeasuredNote;
        if (!installed)
            return new InsightCard
            {
                Id = InsightsIds.NvidiaApp, Category = InsightCategory.Hardware, Title = title, Icon = icon, Status = InsightStatus.Good,
                Headline = "The NVIDIA App isn't installed, so its game filters can't cost frames",
                Explanation = explanation, WhatToDo = "Nothing to do.", Measured = measured,
            };
        return new InsightCard
        {
            Id = InsightsIds.NvidiaApp, Category = InsightCategory.Hardware, Title = title, Icon = icon, Status = InsightStatus.Info, IsHeuristic = true,
            Headline = "The NVIDIA App is installed: check its game filters",
            Explanation = explanation + " NVIDIA doesn't document where this setting is kept, so PC Tidy can't read it and only explains where it is.",
            WhatToDo = "If you don't use the filters, turn off Game Filters and Photo Mode in the NVIDIA App, then restart your game. "
                       + "Later app versions may have fixed this, so compare your own game before and after.",
            Steps = new[]
            {
                "Open the NVIDIA App and go to Settings → Features → Overlay.",
                "Turn off Game Filters and Photo Mode (keep the overlay if you use Instant Replay).",
                "Restart the game.",
            },
            Facts = new[] { new InsightFact("NVIDIA App", "Installed") },
            Measured = measured,
        };
    }

    // ------------------------------------------------------------------ C4: Memory integrity / VBS cost

    public static InsightCard MemoryIntegrity(CardContext c)
    {
        const string title = "Memory integrity and game speed";
        const string icon = "ShieldLock24";
        var s = c.Source.ReadSecurity();
        var links = new[] { new InsightLink("Open Core isolation (Windows Security)", InsightsLinks.SecurityCoreIsolation) };
        var measured = new[] { MeasuredNumbers.VbsCpuBound, MeasuredNumbers.VbsWorstLows, MeasuredNumbers.VbsAverageAnd4K };
        const string explanation = "Memory integrity (Core isolation) stops harmful and outdated drivers from running inside Windows, and some anti-cheats ask for it. "
                                   + "It costs a few percent when a game is held back by the processor and very little when the graphics card is the limit, for example at 4K. "
                                   + "PC Tidy only reads this setting. It never turns it off." + MeasuredNote;
        if (s.MemoryIntegrity == TriState.Unknown)
            return InsightCard.NotAvailable(InsightsIds.MemoryIntegrity, InsightCategory.Safety, title, icon,
                "Windows didn't report whether memory integrity is on. Windows Security → Device security → Core isolation shows it.") with { Links = links };

        bool on = s.MemoryIntegrity == TriState.Yes;
        return new InsightCard
        {
            Id = InsightsIds.MemoryIntegrity, Category = InsightCategory.Safety, Title = title, Icon = icon,
            Status = on ? InsightStatus.Good : InsightStatus.Info,
            Headline = on ? "Memory integrity is on: PC Tidy recommends keeping it on" : "Memory integrity is off",
            Explanation = explanation,
            WhatToDo = on
                ? "Nothing to do. Keep it on: the measured cost is small next to what it protects."
                : "PC Tidy recommends turning it on in Windows Security → Device security → Core isolation. If Windows names an incompatible driver, update or remove that driver first.",
            Facts = new[] { new InsightFact("Memory integrity", on ? "On" : "Off") },
            Links = links, Measured = measured,
        };
    }

    // ------------------------------------------------------------------ C5: Windows build vs processor

    public static InsightCard WindowsBuild(CardContext c)
    {
        const string title = "Windows build and your processor";
        const string icon = "Window24";
        var cpu = c.CpuName;
        var family = HardwareFamilies.Classify(cpu);
        var build = c.Build;
        var facts = new List<InsightFact>
        {
            new("Processor", cpu ?? "Couldn't be read"),
            new("Processor family", HardwareFamilies.Describe(family)),
            new("Windows", HardwareFamilies.BuildText(build, c.Identity.Ubr)),
        };
        var measured = new[] { MeasuredNumbers.Windows24H2, MeasuredNumbers.Windows25H2 };
        var links = new[] { new InsightLink("Open Windows Update", InsightsLinks.SettingsWindowsUpdate) };
        const string explanation = "Windows 11 24H2 changed how Windows schedules work on AMD Zen 4 and Zen 5 processors (Ryzen 7000, 8000 and 9000), "
                                   + "and games ran faster on them. In the same test Intel processors gained little." + MeasuredNote;
        bool zen45 = family is CpuFamily.AmdZen4 or CpuFamily.AmdZen5;

        if (zen45 && build >= HardwareFamilies.Build24H2)
            return Card(InsightStatus.Good, "You're on 24H2 or later, the build measured faster on your processor",
                "Nothing to do. Keep Windows Update on so you keep getting the fixes.");
        if (zen45)
            return Card(InsightStatus.Attention, "A newer Windows build was measured faster on your processor",
                build < 22000
                    ? "Windows 11 24H2 or later is where the gain was measured. If this PC can run Windows 11, upgrade through Windows Update; until then, keep updates on."
                    : "Keep Windows Update on and install the 24H2 or newer feature update when Windows offers it (Settings → Windows Update).");
        return Card(InsightStatus.Info,
            family == CpuFamily.Unknown ? "PC Tidy couldn't read the processor; keep Windows up to date" : "The 24H2 gain was measured on AMD Zen 4 and Zen 5",
            "Keep Windows Update on. There's no setting to change for this.");

        InsightCard Card(InsightStatus status, string headline, string todo) => new()
        {
            Id = InsightsIds.WindowsBuild, Category = InsightCategory.Hardware, Title = title, Icon = icon, Status = status,
            Headline = headline, Explanation = explanation, WhatToDo = todo, Facts = facts, Links = links, Measured = measured,
        };
    }

    // ------------------------------------------------------------------ C6: dual-CCD X3D

    /// <summary>Windows' Balanced plan (SCHEME_BALANCED, the same GUID on every PC).</summary>
    internal const string BalancedPlan = "381b4222-f694-41f0-9685-ff5bb260df2e";

    /// <summary>AMD's chipset installer's Uninstall name. Verify on a Ryzen PC: from AMD's installer, not re-checked.</summary>
    internal const string AmdChipsetName = "AMD Chipset Software";

    public static InsightCard DualCcdX3D(CardContext c)
    {
        const string title = "Ryzen X3D with two core groups";
        const string icon = "DeveloperBoard24";
        var cpu = c.CpuName;
        if (!HardwareFamilies.IsDualCcdX3D(cpu))
            return InsightCard.NotAvailable(InsightsIds.DualCcdX3D, InsightCategory.Hardware, title, icon,
                "Only for Ryzen 9 X3D processors with two core groups (for example 7950X3D or 9950X3D). This PC has a different processor.");

        var plan = c.Source.ReadActivePowerPlan();
        bool? balanced = plan is null ? null : string.Equals(plan, BalancedPlan, StringComparison.OrdinalIgnoreCase);
        bool chipset = c.HasApp(AmdChipsetName);
        var facts = new List<InsightFact>
        {
            new("Processor", cpu!),
            new("Power plan", balanced switch { true => "Balanced", false => "Not Balanced", _ => "Couldn't be read" }),
            new("AMD chipset driver", chipset ? "Found" : "Not found in the installed apps (best guess)"),
        };
        var problems = new List<string>();
        if (balanced == false) problems.Add("the power plan isn't Balanced");
        if (!chipset) problems.Add("AMD's chipset driver wasn't found");

        return new InsightCard
        {
            Id = InsightsIds.DualCcdX3D, Category = InsightCategory.Hardware, Title = title, Icon = icon, IsHeuristic = true,
            Status = problems.Count > 0 ? InsightStatus.Attention : InsightStatus.Good,
            Headline = problems.Count > 0
                ? char.ToUpperInvariant(problems[0][0]) + string.Join(", and ", problems)[1..]
                : "Balanced plan and AMD's chipset driver found: the game runs on the fast-cache cores",
            Explanation = CpuKinds.DualCcdX3DAdvice + MeasuredNote,
            WhatToDo = "Keep the Balanced plan, Game Mode and the Xbox Game Bar, and install AMD's chipset driver from amd.com (again after any processor swap). "
                       + "Process Lasso is an optional outside tool some people use to pin a game to the cache cores; PC Tidy doesn't install or control it.",
            Facts = facts,
            Links = new[] { new InsightLink("AMD chipset drivers (amd.com)", InsightsLinks.AmdDrivers) },
            Measured = new[] { MeasuredNumbers.ProcessLasso, MeasuredNumbers.X3DMisParked },
        };
    }

    // ------------------------------------------------------------------ thermal throttling (guidance: no driver-free read)

    public static InsightCard Thermal(CardContext c) => new()
    {
        Id = InsightsIds.Thermal, Category = InsightCategory.Guides, Title = "Heat slowing the PC down", Icon = "Temperature24",
        Status = InsightStatus.Info,
        Headline = c.IsLaptop ? "Laptop slower than it used to be? Check for heat" : "Games slower than they used to be? Check for heat",
        Explanation = "A processor or graphics chip that gets too hot slows itself down to stay safe (thermal throttling). Dusty fans and dried-out paste are the usual cause, "
                      + "mostly in laptops. Cleaning only helps when the PC is actually throttling. PC Tidy can't read temperatures without installing a driver, "
                      + "so this card only explains what to check." + MeasuredNote,
        WhatToDo = "Check the temperature while a game runs. If it stays very high and the speed drops, clean the fans and vents.",
        Steps = new[]
        {
            "Open Task Manager → Performance while a game runs. The GPU page shows the graphics temperature on most PCs.",
            "If the temperature stays very high and the clock speed falls during play, the PC is probably throttling.",
            "With the PC off and unplugged, blow the dust out of the vents and fans. New paste is a job for the maker's service or a repair shop: opening a laptop can void the warranty.",
        },
        Measured = new[] { MeasuredNumbers.ThermalThrottling },
    };
}
