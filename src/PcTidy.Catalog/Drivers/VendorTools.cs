using System.Text.RegularExpressions;

namespace PcTidy.Catalog.Drivers;

/// <summary>What kind of hardware a vendor tool keeps up to date (decides when it is suggested).</summary>
public enum VendorHardware { Graphics, Chipset, Peripherals, Audio }

/// <summary>
/// A hardware maker's own driver tool. PC Tidy only points people at these official tools and pages:
/// never a third-party driver database, never a download of its own.
/// </summary>
/// <param name="Id">Stable id ("nvidia-app").</param>
/// <param name="Name">Shown name ("NVIDIA App").</param>
/// <param name="DisplayNamePattern">Matches the tool's Uninstall entry DisplayName (case-insensitive, anchored by the pattern itself).</param>
/// <param name="OfficialUrl">The maker's official page for the tool or its drivers (https only).</param>
/// <param name="Covers">Plain words for what it updates ("your NVIDIA graphics driver").</param>
/// <param name="Hardware">Hardware family, used to suggest the tool when it isn't installed.</param>
/// <param name="DeviceProviderPattern">Matches a driver provider / device maker that this tool serves (to suggest it when missing).</param>
public sealed record VendorTool(
    string Id,
    string Name,
    string DisplayNamePattern,
    string OfficialUrl,
    string Covers,
    VendorHardware Hardware,
    string? DeviceProviderPattern)
{
    public bool MatchesUninstallName(string? displayName)
        => !string.IsNullOrWhiteSpace(displayName)
           && Regex.IsMatch(displayName.Trim(), DisplayNamePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    public bool ServesProvider(string? providerOrMaker)
        => DeviceProviderPattern is not null && !string.IsNullOrWhiteSpace(providerOrMaker)
           && Regex.IsMatch(providerOrMaker, DeviceProviderPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
}

/// <summary>
/// Vendor driver tools detected from the Uninstall registry (Research: Updates). The URLs are the makers'
/// official pages. Realtek has no consumer update tool: its drivers come from the PC or motherboard maker,
/// so Realtek points at the motherboard support page instead (see <see cref="BoardVendors"/>).
/// </summary>
public static class VendorTools
{
    public static readonly IReadOnlyList<VendorTool> All = new VendorTool[]
    {
        new("nvidia-app", "NVIDIA App", @"^NVIDIA App\b(?! driver settings)|^NVIDIA GeForce Experience\b",
            "https://www.nvidia.com/en-us/drivers/", "your NVIDIA graphics driver", VendorHardware.Graphics, @"^NVIDIA\b"),
        new("amd-software", "AMD Software", @"^AMD Software\b|^AMD Radeon Software\b",
            "https://www.amd.com/en/support/download/drivers.html", "your AMD Radeon graphics and chipset drivers", VendorHardware.Graphics, @"^(Advanced Micro Devices|AMD)\b"),
        new("intel-dsa", "Intel Driver & Support Assistant", @"Intel.{0,4}Driver\s*&\s*Support Assistant",
            "https://www.intel.com/content/www/us/en/support/detect.html", "your Intel graphics, Wi-Fi and Bluetooth drivers", VendorHardware.Chipset, @"^Intel\b"),
        new("logitech-ghub", "Logitech G HUB", @"^Logitech G HUB\b",
            "https://www.logitechg.com/innovation/g-hub.html", "your Logitech G mice, keyboards and headsets", VendorHardware.Peripherals, @"^Logitech\b"),
        new("steelseries-gg", "SteelSeries GG", @"^SteelSeries GG\b|^SteelSeries Engine\b",
            "https://steelseries.com/gg", "your SteelSeries mice, keyboards and headsets", VendorHardware.Peripherals, @"^SteelSeries\b"),
        new("razer-synapse", "Razer Synapse", @"^Razer Synapse\b",
            "https://www.razer.com/synapse-4", "your Razer devices", VendorHardware.Peripherals, @"^Razer\b"),
        new("corsair-icue", "Corsair iCUE", @"^(Corsair )?iCUE",
            "https://www.corsair.com/us/en/s/downloads", "your Corsair devices", VendorHardware.Peripherals, @"^Corsair\b"),
    };

    /// <summary>Realtek drivers (audio, LAN, Wi-Fi, Bluetooth) come from the motherboard or PC maker.</summary>
    public const string RealtekProviderPattern = @"^Realtek\b";

    public static VendorTool? FindByUninstallName(string? displayName)
        => All.FirstOrDefault(t => t.MatchesUninstallName(displayName));
}
