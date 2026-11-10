namespace PcTidy.Catalog.PcCare;

/// <summary>A signed kernel driver with a published vulnerability that lets any program take over Windows.</summary>
/// <param name="FileName">Driver file name, matched case-insensitively against the service's image path.</param>
/// <param name="ShippedWith">Plain-English list of apps known to install it.</param>
/// <param name="Advisory">CVE or advisory id.</param>
public sealed record VulnerableDriver(string FileName, string ShippedWith, string Advisory, string Source);

/// <summary>
/// Well-documented vulnerable drivers (Microsoft's recommended driver block rules and loldrivers.io). Kept short on purpose:
/// only drivers with a public CVE that ship with mainstream hardware/monitoring tools. Matching is by file name only and the
/// card says "may be": an updated app can ship a fixed driver with the same name.
/// </summary>
public static class VulnerableDriverCatalog
{
    public static readonly IReadOnlyList<VulnerableDriver> All = new VulnerableDriver[]
    {
        // Microsoft Defender flags WinRing0 itself (HackTool:Win32/Winring0) since 2025; updating the app is the fix, never an exclusion.
        new("WinRing0x64.sys", "older fan-control, RGB and hardware-monitor tools (older Fan Control, OpenHardwareMonitor, older LibreHardwareMonitor, some RGB apps)", "CVE-2020-14979", PcCareSources.LolDrivers),
        new("WinRing0.sys", "older fan-control, RGB and hardware-monitor tools (older Fan Control and others)", "CVE-2020-14979", PcCareSources.LolDrivers),
        new("RTCore64.sys", "older MSI Afterburner / RivaTuner versions", "CVE-2019-16098", PcCareSources.MsVulnerableDriverBlocklist),
        new("RTCore32.sys", "older MSI Afterburner / RivaTuner versions", "CVE-2019-16098", PcCareSources.MsVulnerableDriverBlocklist),
        new("gdrv.sys", "older Gigabyte tools (App Center, AORUS Engine, EasyTune)", "CVE-2018-19320", PcCareSources.MsVulnerableDriverBlocklist),
        new("AsIO.sys", "older ASUS tools (AI Suite, Aura)", "listed on loldrivers.io", PcCareSources.LolDrivers),
        new("AsIO2.sys", "older ASUS tools (AI Suite, Aura)", "listed on loldrivers.io", PcCareSources.LolDrivers),
        new("AsrDrv10.sys", "older ASRock tools (RGB, A-Tuning)", "listed on loldrivers.io", PcCareSources.LolDrivers),
        new("AsrDrv101.sys", "older ASRock tools (RGB, A-Tuning)", "listed on loldrivers.io", PcCareSources.LolDrivers),
        new("AsrDrv103.sys", "older ASRock tools (RGB, A-Tuning)", "CVE-2020-15368", PcCareSources.LolDrivers),
        new("dbutil_2_3.sys", "older Dell firmware update utilities", "CVE-2021-21551", PcCareSources.MsVulnerableDriverBlocklist),
        new("iqvw64e.sys", "older Intel network adapter diagnostics", "CVE-2015-2291", PcCareSources.MsVulnerableDriverBlocklist),
        new("cpuz141_x64.sys", "older CPU-Z versions", "CVE-2017-15303", PcCareSources.LolDrivers),
        new("ene.sys", "older ENE-based RGB lighting tools", "listed on loldrivers.io", PcCareSources.LolDrivers),
        new("EneIo64.sys", "older ENE-based RGB lighting tools", "listed on loldrivers.io", PcCareSources.LolDrivers),
        new("NTIOLib_X64.sys", "older MSI utilities", "listed on loldrivers.io", PcCareSources.LolDrivers),
        new("mhyprot2.sys", "an older Genshin Impact anti-cheat", "CVE-2020-36603", PcCareSources.LolDrivers),
        new("atillk64.sys", "older ATI/AMD overclocking tools", "CVE-2019-7246", PcCareSources.LolDrivers),
    };

    public static VulnerableDriver? Match(string? imagePathOrFileName)
    {
        if (string.IsNullOrWhiteSpace(imagePathOrFileName)) return null;
        var file = imagePathOrFileName.Trim().Trim('"');
        var slash = file.LastIndexOfAny(new[] { '\\', '/' });
        if (slash >= 0) file = file[(slash + 1)..];
        return All.FirstOrDefault(d => string.Equals(d.FileName, file, StringComparison.OrdinalIgnoreCase));
    }
}
