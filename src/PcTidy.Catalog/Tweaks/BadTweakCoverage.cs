namespace PcTidy.Catalog.Tweaks;

/// <summary>
/// How one HARMFUL row of the research ("FPS boost tweaks - do they work" §4, and the comparison page §8.3) is handled.
/// Every row has at least one finder check, or is covered by another section, or says in one line why it can't be detected.
/// </summary>
/// <param name="Row">The row's name as the research writes it.</param>
/// <param name="CheckIds">Bad-tweak finder checks that detect it (<see cref="BadTweakCatalog"/> ids).</param>
/// <param name="CoveredElsewhere">Another PC Tidy section that already detects it, when the finder doesn't.</param>
/// <param name="NotDetectable">Why PC Tidy can't detect it (or the part it can't), in one line.</param>
public sealed record HarmfulRowCoverage(string Row, IReadOnlyList<string> CheckIds, string? CoveredElsewhere = null, string? NotDetectable = null)
{
    public bool IsCovered => CheckIds.Count > 0 || CoveredElsewhere is not null || NotDetectable is not null;
}

/// <summary>The research's HARMFUL rows and where PC Tidy handles each one. A test keeps this complete.</summary>
public static class BadTweakCoverage
{
    private static HarmfulRowCoverage Row(string row, params string[] checks) => new(row, checks);

    public static readonly IReadOnlyList<HarmfulRowCoverage> HarmfulRows = new[]
    {
        Row("Spectre/Meltdown off", "security.spectre-mitigations-off"),
        Row("useplatformclock", "boot.useplatformclock"),
        Row("disabledynamictick on laptops", "boot.disabledynamictick"),
        Row("Idle disable / C-states off", "cpu.processor-idle-disabled") with
        {
            NotDetectable = "C-states switched off in the BIOS itself can't be read from Windows; the power plan's idle switch is detected.",
        },
        Row("Core parking off on dual-CCD X3D", "cpu.core-parking-off"),
        Row("Game Bar removal on dual-CCD X3D", "cpu.x3d-game-bar-missing"),
        Row("Priority Realtime", "scheduler.realtime-priority"),
        Row("Autotuning off", "network.autotuning-off"),
        Row("BBR2 on Windows 11 24H2", "network.bbr2-congestion"),
        Row("IPv6 off", "network.ipv6-disabled", "network.adapter-protocols-unbound"),
        Row("Jumbo frames", "network.jumbo-frames"),
        Row("Page file off", "storage.pagefile-off"),
        Row("Fixed/disabled page file", "storage.pagefile-off", "storage.fixed-pagefile"),
        Row("TRIM off", "storage.trim-off", "storage.optimize-schedule-off"),
        Row("Registry cleaners", "windows.registry-cleaner-installed"),
        Row("Windows Update / BITS / WpnService / DoSvc off", "update.services-disabled", "services.notifications-disabled"),
        Row("BIOS vars from Windows / UEFI variables protection off", "windows.bios-tool-leftovers") with
        {
            NotDetectable = "The firmware's own \"UEFI variable protection\" switch lives in the BIOS setup and isn't reported to Windows; the tools that change it are detected.",
        },
        Row("xHCI IMOD (RWEverything)", "services.hardware-access-drivers"),
        new HarmfulRowCoverage("Read-only on Fortnite ini", Array.Empty<string>(),
            CoveredElsewhere: "Games section: read-only settings files are flagged for every installed game (GameLeftoverChecks)."),
        new HarmfulRowCoverage("-onethread / -nothreading", Array.Empty<string>(),
            CoveredElsewhere: "Games section: launch options marked Harmful are flagged and can be removed (LaunchOptionFacts)."),
        Row("Anti-Lag+", "graphics.antilag-plus-driver"),
        Row("TCCSupported", "graphics.tcc-mode"),
        Row("Edge / WebView2 removal", "windows.webview2-removed") with
        {
            NotDetectable = "Removing Edge alone is a supported choice in the European Economic Area, so only a missing WebView2 runtime is flagged.",
        },
        Row("Defender real-time off", "security.defender-off", "security.defender-realtime-off", "security.defender-only-realtime"),
        Row("Tamper Protection off", "security.defender-protection-reduced", "security.defender-only-realtime"),
        Row("PC optimizer suites (Advanced SystemCare)", "windows.optimizer-suite-installed"),
        Row("SVM/VT-x/IOMMU off", "boot.hypervisor-off") with
        {
            NotDetectable = "Virtualization switched off in the BIOS: Windows only reports the firmware setting while no hypervisor runs, so PC Tidy can't read it reliably.",
        },
        Row("Services mass disabling (preset leftovers)", "services.third-party-all-disabled", "services.core-disabled") with
        {
            CoveredElsewhere = "Services section: a summary line when 5 or more Windows services are disabled against their default, pointing to its bad-services repair.",
        },
    };

    public static HarmfulRowCoverage? Find(string row) => HarmfulRows.FirstOrDefault(r => r.Row.Equals(row, StringComparison.OrdinalIgnoreCase));
}
