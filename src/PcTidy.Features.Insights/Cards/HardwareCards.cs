using System.Globalization;
using PcTidy.Catalog.Insights;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Insights.Model;
using PcTidy.Features.Insights.Source;

namespace PcTidy.Features.Insights.Cards;

/// <summary>
/// Detection cards from the census (Community tweaks §"detection cards"): display refresh rate, memory speed and channels,
/// games on a hard disk, Resizable BAR, the system timer and the Vanguard anti-cheat requirements. All read-only.
/// </summary>
internal static class HardwareCards
{
    // ------------------------------------------------------------------ refresh rate

    /// <summary>A display counts as "below its top rate" from this many Hz under it (59.94 vs 60 Hz is the same mode).</summary>
    internal const int MinRefreshGapHz = 5;

    public static InsightCard RefreshRate(CardContext c)
    {
        const string title = "Display refresh rate";
        var displays = c.Source.ReadDisplays();
        if (displays.Count == 0)
            return InsightCard.NotAvailable(InsightsIds.RefreshRate, InsightCategory.Hardware, title, "Desktop24", "PC Tidy couldn't read the display settings on this PC.");
        var facts = displays.Select(d => new InsightFact(d.Name, $"{d.Width}×{d.Height} at {d.CurrentHz} Hz (up to {d.MaxHzAtCurrentResolution} Hz)")).ToList();
        // Any display below its fastest rate (60 of 144, but also 120 of 165), with a small margin so 59/60 Hz rounding
        // doesn't count. The largest gap is shown first.
        var stuck = displays.Where(d => d.MaxHzAtCurrentResolution - d.CurrentHz >= MinRefreshGapHz)
            .OrderByDescending(d => d.MaxHzAtCurrentResolution - d.CurrentHz).ToList();
        const string explanation = "A higher refresh rate makes moving pictures and the mouse smoother. Windows sometimes leaves a fast monitor below its top rate "
                                   + "(often at 60 Hz) after a new cable, driver or monitor.";
        if (stuck.Count == 0)
            return new InsightCard
            {
                Id = InsightsIds.RefreshRate, Category = InsightCategory.Hardware, Title = title, Icon = "Desktop24", Status = InsightStatus.Good,
                Headline = displays.Count == 1 ? $"Your display runs at its fastest rate ({displays[0].CurrentHz} Hz)" : "Every display runs at its fastest rate",
                Explanation = explanation, WhatToDo = "Nothing to do.", Facts = facts,
            };
        var s = stuck[0];
        return new InsightCard
        {
            Id = InsightsIds.RefreshRate, Category = InsightCategory.Hardware, Title = title, Icon = "Desktop24", Status = InsightStatus.Attention,
            Headline = $"{s.Name} runs at {s.CurrentHz} Hz but can do {s.MaxHzAtCurrentResolution} Hz",
            Explanation = explanation + (c.IsLaptop ? " On a laptop, Windows may lower it on battery to save power; that's fine." : ""),
            WhatToDo = "Open Settings → System → Display → Advanced display and pick the highest rate under \"Choose a refresh rate\".",
            Facts = facts,
            Links = new[] { new InsightLink("Open Advanced display", InsightsLinks.SettingsAdvancedDisplay) },
        };
    }

    // ------------------------------------------------------------------ memory speed and channels

    public static InsightCard Ram(CardContext c)
    {
        const string title = "Memory speed";
        var read = c.Source.ReadMemory();
        if (read.Value is not { Modules.Count: > 0 } mem)
            return InsightCard.NotAvailable(InsightsIds.Ram, InsightCategory.Hardware, title, "Ram20", "PC Tidy couldn't read the memory modules on this PC.");

        var facts = mem.Modules.Select(m => new InsightFact(m.Slot.Length > 0 ? m.Slot : "Module",
            $"{Sizes.Format(m.CapacityBytes)}, runs at {Mts(m.ConfiguredMts)}{(m.RatedMts > 0 ? $" (reports up to {Mts(m.RatedMts)})" : "")}")).ToList();
        if (mem.TotalSlots > 0) facts.Add(new InsightFact("Slots", $"{mem.Modules.Count} of {mem.TotalSlots} used"));

        var slow = mem.Modules.Where(m => m.RatedMts > 0 && m.ConfiguredMts > 0 && m.ConfiguredMts < m.RatedMts * 0.95).ToList();
        bool single = mem.Modules.Count == 1 && mem.TotalSlots >= 2;
        const string xmp = "If the box or listing for your memory says a higher speed (for example 6000 MT/s), turn on its XMP or EXPO profile in the BIOS: that's a proven gain in games.";
        const string explanation = "Two matched sticks run in dual channel, which is noticeably faster for games and integrated graphics than one stick. "
                                   + "Memory also runs slower than its rating until its XMP/EXPO profile is switched on in the BIOS.";

        if (single && !c.IsLaptop)
            return new InsightCard
            {
                Id = InsightsIds.Ram, Category = InsightCategory.Hardware, Title = title, Icon = "Ram20", Status = InsightStatus.Attention, IsHeuristic = true,
                Headline = "One memory stick: probably running in single channel",
                Explanation = explanation, Facts = facts,
                WhatToDo = "Adding a second, identical stick (in the slot your motherboard manual marks for dual channel) usually helps games. " + xmp,
            };
        if (slow.Count > 0)
            return new InsightCard
            {
                Id = InsightsIds.Ram, Category = InsightCategory.Hardware, Title = title, Icon = "Ram20", Status = InsightStatus.Attention, IsHeuristic = true,
                Headline = $"Memory runs at {Mts(slow[0].ConfiguredMts)}, below the {Mts(slow[0].RatedMts)} it reports",
                Explanation = explanation, Facts = facts,
                WhatToDo = "Check the memory settings in the BIOS: XMP/EXPO, or a manual speed set lower. " + (c.IsLaptop ? "Some laptops fix the speed; then there's nothing to change." : ""),
            };
        return new InsightCard
        {
            Id = InsightsIds.Ram, Category = InsightCategory.Hardware, Title = title, Icon = "Ram20",
            Status = single ? InsightStatus.Info : InsightStatus.Good, IsHeuristic = true,
            Headline = single ? "One memory module (common on laptops; some are built in)" : $"{mem.Modules.Count} memory modules at their reported speed",
            Explanation = explanation + " Windows reports the module's own top speed, which may be lower than its XMP/EXPO rating.",
            WhatToDo = xmp, Facts = facts,
        };
    }

    private static string Mts(int v) => v > 0 ? $"{v.ToString(CultureInfo.CurrentCulture)} MT/s" : "an unknown speed";

    // ------------------------------------------------------------------ games on a hard disk

    public static InsightCard GamesOnHdd(CardContext c)
    {
        const string title = "Games on a hard disk";
        if (c.Locations.GameInstallDirs.Count == 0)
            return InsightCard.NotAvailable(InsightsIds.GamesOnHdd, InsightCategory.Hardware, title, "Games24", "PC Tidy didn't find any game libraries on this PC.");
        if (c.System is null || c.System.Drives.Count == 0)
            return InsightCard.NotAvailable(InsightsIds.GamesOnHdd, InsightCategory.Hardware, title, "Games24", "PC Tidy couldn't tell which drives are SSDs on this PC.");

        var media = c.System.Drives.ToDictionary(d => d.Root, d => d.Media, StringComparer.OrdinalIgnoreCase);
        var onHdd = new List<(string Game, string Drive)>();
        int total = 0;
        foreach (var library in c.Locations.GameInstallDirs.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var drive = Words.DriveRootOf(library, c.Locations.FixedDrives);
            IEnumerable<string> games;
            try { games = Directory.EnumerateDirectories(library).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var g in games)
            {
                total++;
                if (drive is not null && media.TryGetValue(drive, out var m) && m == DriveMedia.Hdd) onHdd.Add((Path.GetFileName(g), drive));
            }
        }
        if (total == 0)
            return InsightCard.NotAvailable(InsightsIds.GamesOnHdd, InsightCategory.Hardware, title, "Games24", "PC Tidy didn't find any installed games.");

        bool hasSsd = c.System.Drives.Any(d => d.Media == DriveMedia.Ssd);
        const string explanation = "Games load levels and textures much faster from an SSD, and stutter less in open-world games. A hard disk is fine for games you rarely play.";
        if (onHdd.Count == 0)
            return new InsightCard
            {
                Id = InsightsIds.GamesOnHdd, Category = InsightCategory.Hardware, Title = title, Icon = "Games24", Status = InsightStatus.Good,
                Headline = $"All {Words.Plural(total, "game is", "games are")} on an SSD", Explanation = explanation, WhatToDo = "Nothing to do.",
            };
        bool sandbox = c.Locations.IsSandbox;
        var drives = string.Join(", ", onHdd.Select(x => Words.ShowDrive(x.Drive, sandbox)).Distinct());
        return new InsightCard
        {
            Id = InsightsIds.GamesOnHdd, Category = InsightCategory.Hardware, Title = title, Icon = "Games24", Status = InsightStatus.Attention,
            Headline = $"{Words.Plural(onHdd.Count, "game is", "games are")} on a hard disk ({drives})",
            Explanation = explanation,
            WhatToDo = hasSsd
                ? "Move the games you play most to an SSD with \"Move to another drive\": it uses each launcher's own Move, so nothing needs reinstalling."
                : "This PC has no SSD. For game loading times, an SSD is the biggest upgrade there is.",
            Facts = onHdd.Take(10).Select(x => new InsightFact(x.Game, Words.ShowDrive(x.Drive, sandbox))).ToList(),
            Navigate = hasSsd ? new InsightNavigation("Open Move to another drive", InsightsIds.DriveMoveModule) : null,
        };
    }

    // ------------------------------------------------------------------ Resizable BAR (can't be read reliably: guidance only)

    public static InsightCard ResizableBar(CardContext c)
    {
        const string title = "Resizable BAR";
        if (c.Gpu is not (GpuVendor.Nvidia or GpuVendor.Amd or GpuVendor.Intel))
            return InsightCard.NotAvailable(InsightsIds.ResizableBar, InsightCategory.Hardware, title, "DeveloperBoard24", "No NVIDIA, AMD or Intel graphics card was found.");
        bool arc = c.GpuNames.Any(n => n.Contains("Arc", StringComparison.OrdinalIgnoreCase));
        var where = c.Gpu switch
        {
            GpuVendor.Nvidia => "NVIDIA Control Panel → System Information shows \"Resizable BAR: Yes\" or \"No\".",
            GpuVendor.Amd => "AMD Software shows it as \"Smart Access Memory\".",
            _ => "Intel's graphics app shows whether Resizable BAR is on.",
        };
        return new InsightCard
        {
            Id = InsightsIds.ResizableBar, Category = InsightCategory.Hardware, Title = title, Icon = "DeveloperBoard24",
            Status = InsightStatus.Info, IsHeuristic = true,
            Headline = arc ? "Intel Arc: make sure Resizable BAR is on" : "Check whether Resizable BAR is on",
            Explanation = "Resizable BAR (AMD calls it Smart Access Memory) lets the processor reach all of the graphics card's memory at once. "
                          + (arc ? "Intel Arc cards lose a lot of speed without it. " : "It gives a small gain in many games. ")
                          + "Windows doesn't report it in a way PC Tidy can read reliably, so this card only explains where to look.",
            WhatToDo = where + " To turn it on, enable \"Above 4G decoding\" and \"Re-Size BAR support\" in the UEFI/BIOS (it needs UEFI mode, not legacy/CSM).",
        };
    }

    // ------------------------------------------------------------------ timer

    public static InsightCard Timer(CardContext c)
    {
        const string title = "System timer";
        var t = c.Source.ReadTimer();
        const string explanation = "Games time every frame with Windows' high-resolution timer. Timer and \"HPET\" tweaks from optimizer guides don't make it faster; "
                                   + "forcing HPET (bcdedit useplatformclock) makes it slower to read and can hurt frame times.";
        var facts = new[] { new InsightFact("Timer frequency", $"{t.Frequency / 1_000_000.0:0.###} MHz") };
        if (!t.IsHighResolution)
            return Card(InsightStatus.Attention, "Windows isn't using a high-resolution timer", "Check Tweaks → Find bad tweaks for forced timer settings, then restart.", nav: true);
        if (Near(t.Frequency, 14_318_180))
            return Card(InsightStatus.Attention, "HPET is forced as the system timer", "Open Tweaks → Find bad tweaks: it removes the \"useplatformclock\" boot setting, then restart.", nav: true);
        if (Near(t.Frequency, 3_579_545))
            return Card(InsightStatus.Info, "Windows uses the older ACPI timer", "This happens on old hardware or when a timer is forced. Tweaks → Find bad tweaks checks the boot settings.", nav: true);
        if (t.Frequency == 10_000_000)
            return Card(InsightStatus.Good, "Windows uses its normal fast timer", "Nothing to do. There's no timer tweak that helps.");
        return Card(InsightStatus.Info, "The timer runs at an unusual speed", "Usually harmless. Tweaks → Find bad tweaks checks for forced timer settings.", nav: true);

        InsightCard Card(InsightStatus s, string headline, string todo, bool nav = false) => new()
        {
            Id = InsightsIds.Timer, Category = InsightCategory.Hardware, Title = title, Icon = "Timer24", Status = s,
            Headline = headline, Explanation = explanation, WhatToDo = todo, Facts = facts,
            Navigate = nav ? new InsightNavigation("Open Tweaks", InsightsIds.TweaksModule) : null,
        };
    }

    private static bool Near(long value, long target) => Math.Abs(value - target) <= target / 1000;

    // ------------------------------------------------------------------ Vanguard / anti-cheat requirements

    public static InsightCard Vanguard(CardContext c)
    {
        const string title = "Anti-cheat requirements";
        var s = c.Source.ReadSecurity();
        string Show(TriState v) => v switch { TriState.Yes => "On", TriState.No => "Off", _ => "Unknown" };
        var tpm = s.TpmNeedsAdmin ? "Needs admin to check"
            : s.TpmPresent == TriState.Yes ? $"Found{(s.TpmVersion is { Length: > 0 } v ? $" (version {v})" : "")}"
            : s.TpmPresent == TriState.No ? "Not found" : "Unknown";
        var facts = new[]
        {
            new InsightFact("TPM", tpm),
            new InsightFact("Secure Boot", Show(s.SecureBoot)),
            new InsightFact("Memory integrity", Show(s.MemoryIntegrity)),
        };

        var problems = new List<string>();
        if (s.SecureBoot == TriState.No) problems.Add("Secure Boot is off");
        if (s.TpmPresent == TriState.No) problems.Add("no TPM was found");
        else if (s.TpmVersion is { Length: > 0 } ver && !ver.StartsWith('2')) problems.Add($"the TPM is version {ver}, not 2.0");

        var explanation = "Riot Vanguard (Valorant, League of Legends) and some other anti-cheats check the PC's security before a game starts. "
                          + "On Windows 11, Vanguard asks for TPM 2.0 and Secure Boot; some games also want memory integrity (Core isolation) on. "
                          + "PC Tidy only reads these settings; it never changes them.";
        var links = new[] { new InsightLink("Open Core isolation (Windows Security)", InsightsLinks.SecurityCoreIsolation) };
        if (problems.Count > 0)
            return new InsightCard
            {
                Id = InsightsIds.Vanguard, Category = InsightCategory.Hardware, Title = title, Icon = "ShieldCheckmark24", Status = InsightStatus.Attention,
                Headline = char.ToUpperInvariant(problems[0][0]) + string.Join(", ", problems)[1..],
                Explanation = explanation, Facts = facts, Links = links,
                WhatToDo = "Secure Boot and the TPM are switched on in the PC's UEFI/BIOS setup (look for \"Secure Boot\" and \"TPM\", \"fTPM\" or \"PTT\"). Follow your PC or motherboard maker's guide: Secure Boot needs UEFI mode.",
            };
        bool allKnown = s.SecureBoot == TriState.Yes && s.TpmPresent == TriState.Yes;
        // Nothing could be read at all: that's "Not available", like the other cards, not an Info card full of "Unknown".
        if (!s.TpmNeedsAdmin && s.TpmPresent == TriState.Unknown && s.SecureBoot == TriState.Unknown && s.MemoryIntegrity == TriState.Unknown)
            return InsightCard.NotAvailable(InsightsIds.Vanguard, InsightCategory.Hardware, title, "ShieldCheckmark24",
                "Windows didn't report the TPM, Secure Boot or memory integrity on this PC, so PC Tidy can't say what an anti-cheat would see. "
                + "Windows Security → Device security shows them.") with { Links = links };
        return new InsightCard
        {
            Id = InsightsIds.Vanguard, Category = InsightCategory.Hardware, Title = title, Icon = "ShieldCheckmark24",
            Status = allKnown ? InsightStatus.Good : s.TpmNeedsAdmin ? InsightStatus.NeedsAdmin : InsightStatus.Info,
            Headline = allKnown ? "TPM and Secure Boot are on" : s.TpmNeedsAdmin ? "Secure Boot read; the TPM needs admin to check" : "Some settings couldn't be read",
            Explanation = explanation, Facts = facts, Links = links,
            WhatToDo = s.MemoryIntegrity == TriState.No
                ? "If a game asks for memory integrity, turn it on in Windows Security → Device security → Core isolation."
                : s.TpmNeedsAdmin ? "Open Start, type \"tpm.msc\" to see the TPM yourself, or run PC Tidy as an administrator." : "Nothing to do.",
        };
    }
}
