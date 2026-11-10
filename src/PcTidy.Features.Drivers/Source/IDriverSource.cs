using PcTidy.Features.Drivers.Model;

namespace PcTidy.Features.Drivers.Source;

/// <summary>
/// Everything the Drivers section reads about this PC. READ-ONLY by contract: no method may change anything.
/// Real PC: <see cref="WindowsDriverSource"/> (pnputil enumerations, WMI, Windows Update search, registry).
/// Tests and the sandbox: <see cref="FixtureDriverSource"/> (captured or hand-written XML/JSON files).
/// Every method returns null / empty (never throws) when the information isn't available on this PC.
/// </summary>
public interface IDriverSource
{
    /// <summary>`pnputil /enum-drivers /devices /format xml`, or null when pnputil can't produce XML here.</summary>
    Task<string?> GetDriverPackagesXmlAsync(CancellationToken ct);

    /// <summary>`pnputil /enum-devices /drivers /format xml` (all devices with matching and installed drivers).</summary>
    Task<string?> GetDevicesXmlAsync(CancellationToken ct);

    /// <summary>`pnputil /enum-devices /disconnected /properties /format xml` (ghost devices with their last-seen dates).</summary>
    Task<string?> GetDisconnectedDevicesXmlAsync(CancellationToken ct);

    /// <summary>`pnputil /enum-devices /problem /deviceids /format xml` (problem devices with hardware ids).</summary>
    Task<string?> GetProblemDevicesXmlAsync(CancellationToken ct);

    /// <summary>WMI Win32_PnPEntity rows whose ConfigManagerErrorCode isn't 0.</summary>
    Task<IReadOnlyList<WmiProblemDevice>> GetWmiProblemDevicesAsync(CancellationToken ct);

    /// <summary>WMI Win32_PnPSignedDriver rows (present devices only): used when pnputil XML isn't available.</summary>
    Task<IReadOnlyList<WmiSignedDriver>> GetWmiSignedDriversAsync(CancellationToken ct);

    /// <summary>Windows Update driver search (read-only, works as a normal user). Never throws: problems are in the result.</summary>
    Task<DriverUpdateSearch> SearchWindowsUpdateDriversAsync(CancellationToken ct);

    /// <summary>Installed apps (Uninstall registry, machine and user).</summary>
    Task<IReadOnlyList<InstalledApp>> GetInstalledAppsAsync(CancellationToken ct);

    /// <summary>Motherboard maker and model, or null.</summary>
    Task<BoardInfo?> GetBoardAsync(CancellationToken ct);

    /// <summary>Size in bytes of a package's driver-store folder (FileRepository), or null if it can't be measured.</summary>
    Task<long?> GetPackageSizeAsync(string publishedName, CancellationToken ct);

    /// <summary>When a third-party driver was last added or changed on this PC (UTC), or null if unknown.</summary>
    Task<DateTime?> GetLastDriverChangeUtcAsync(CancellationToken ct);

    /// <summary>
    /// Typed facts Windows keeps for each device (present or not, last arrival / removal / install as FILETIME), keyed by
    /// instance id (case-insensitive). These replace pnputil's localized text. A device missing from the result, or a
    /// null field, is unknown. Default: nothing known (every date unknown, so no old device is ever offered).
    /// </summary>
    Task<IReadOnlyDictionary<string, DeviceFacts>> GetDeviceFactsAsync(IReadOnlyCollection<string> instanceIds, CancellationToken ct)
        => Task.FromResult<IReadOnlyDictionary<string, DeviceFacts>>(new Dictionary<string, DeviceFacts>(StringComparer.OrdinalIgnoreCase));
}

/// <summary>What Windows itself records about one device (never read from translated text).</summary>
/// <param name="IsPresent">True = connected now, false = known to Windows but not connected, null = couldn't tell.</param>
public sealed record DeviceFacts(bool? IsPresent, DateTime? LastArrivalUtc, DateTime? LastRemovalUtc, DateTime? InstallDateUtc);
