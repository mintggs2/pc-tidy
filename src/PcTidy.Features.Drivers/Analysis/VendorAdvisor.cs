using PcTidy.Catalog.Drivers;
using PcTidy.Features.Drivers.Model;

namespace PcTidy.Features.Drivers.Analysis;

/// <summary>A maker tool to point at: installed (use it) or suggested because matching hardware is in this PC.</summary>
public sealed record VendorSuggestion(VendorTool Tool, bool Installed, string? MatchedDevice);

/// <summary>Where official drivers for this PC's hardware come from (maker tools and the motherboard support page).</summary>
public static class VendorAdvisor
{
    /// <summary>
    /// Installed maker tools (from the Uninstall list), plus tools for hardware that is present but has no tool installed.
    /// Graphics tools are only suggested for a matching display adapter.
    /// </summary>
    public static IReadOnlyList<VendorSuggestion> Tools(IReadOnlyList<InstalledApp> apps, IReadOnlyList<DeviceRecord> devices,
        IReadOnlyList<WmiSignedDriver> wmiDrivers)
    {
        var result = new List<VendorSuggestion>();
        var installedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in apps)
        {
            if (VendorTools.FindByUninstallName(app.DisplayName) is { } tool && installedIds.Add(tool.Id))
                result.Add(new VendorSuggestion(tool, Installed: true, MatchedDevice: null));
        }

        var present = PresentHardware(devices, wmiDrivers).ToList();
        foreach (var tool in VendorTools.All.Where(t => !installedIds.Contains(t.Id)))
        {
            var match = present.FirstOrDefault(h =>
                (tool.Hardware != VendorHardware.Graphics || string.Equals(h.ClassName, "Display", StringComparison.OrdinalIgnoreCase))
                && (tool.ServesProvider(h.Provider) || tool.ServesProvider(h.Manufacturer)));
            if (match.Name is not null) result.Add(new VendorSuggestion(tool, Installed: false, MatchedDevice: match.Name));
        }
        return result;
    }

    /// <summary>First present device whose driver comes from Realtek (its drivers come from the board or PC maker).</summary>
    public static string? RealtekDevice(IReadOnlyList<DeviceRecord> devices, IReadOnlyList<WmiSignedDriver> wmiDrivers)
        => PresentHardware(devices, wmiDrivers)
            .FirstOrDefault(h => IsRealtek(h.Provider) || IsRealtek(h.Manufacturer)).Name;

    private static bool IsRealtek(string text)
        => text.Length > 0 && System.Text.RegularExpressions.Regex.IsMatch(text.Trim(), VendorTools.RealtekProviderPattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static IEnumerable<(string Name, string ClassName, string Provider, string Manufacturer)> PresentHardware(
        IReadOnlyList<DeviceRecord> devices, IReadOnlyList<WmiSignedDriver> wmiDrivers)
    {
        foreach (var d in devices.Where(d => d.IsPresent))
            yield return (d.Description, d.ClassName, d.InstalledDriver?.Provider ?? "", d.Manufacturer);
        foreach (var w in wmiDrivers)
            yield return (w.DeviceName, w.ClassName, w.Provider, "");
    }
}
