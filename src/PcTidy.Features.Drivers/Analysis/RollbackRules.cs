using PcTidy.Catalog.Drivers;
using PcTidy.Features.Drivers.Model;

namespace PcTidy.Features.Drivers.Analysis;

/// <summary>
/// Per-device rollback is only offered while the previous package is still in the driver store: an older third-party
/// package with the same original name and provider that Windows lists as a matching driver for this device.
/// </summary>
public static class RollbackRules
{
    /// <summary>The previous driver to go back to, or null when there is none.</summary>
    public static MatchingDriver? PreviousDriver(DeviceRecord device, IReadOnlyList<DriverPackage> storePackages)
    {
        if (!device.IsPresent || !DriverClassRules.IsSafeInstanceId(device.InstanceId)) return null;
        var installed = device.InstalledDriver;
        if (installed is null || !DriverClassRules.IsOemInf(installed.PublishedName) || installed.Version.Version is null) return null;

        var inStore = storePackages.Select(p => p.PublishedName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return device.MatchingDrivers
            .Where(m => !m.IsInstalled
                        && !string.Equals(m.PublishedName, installed.PublishedName, StringComparison.OrdinalIgnoreCase)
                        && DriverClassRules.IsOemInf(m.PublishedName)
                        && inStore.Contains(m.PublishedName)
                        && SameText(m.OriginalName, installed.OriginalName)
                        && SameText(m.Provider, installed.Provider)
                        && m.Version.Version is not null
                        && StoreCleanupRules.Compare(m.Version, installed.Version) < 0)
            .OrderByDescending(m => m.Version.Version).ThenByDescending(m => m.Version.Date)
            .FirstOrDefault();
    }

    private static bool SameText(string a, string b) => a.Length > 0 && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
