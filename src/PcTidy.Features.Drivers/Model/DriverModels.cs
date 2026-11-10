namespace PcTidy.Features.Drivers.Model;

/// <summary>A driver version as Windows reports it: the INF date plus a four-part version ("01/15/2024 1.2.3.4").</summary>
public sealed record DriverVersionInfo(DateTime? Date, Version? Version, string Raw)
{
    public static readonly DriverVersionInfo Unknown = new(null, null, "");

    /// <summary>"32.0.15.2000 (20 Aug 2026)" or the raw text when it couldn't be read.</summary>
    public string Display
    {
        get
        {
            if (Version is null) return string.IsNullOrWhiteSpace(Raw) ? "Unknown" : Raw;
            return Date is { } d ? $"{Version} ({d:d MMM yyyy})" : Version.ToString();
        }
    }
}

/// <summary>A device bound to a driver package (from pnputil /enum-drivers /devices).</summary>
public sealed record BoundDevice(string InstanceId, string Description, string Status);

/// <summary>One third-party driver package in the driver store (pnputil /enum-drivers).</summary>
public sealed record DriverPackage
{
    /// <summary>Published name, e.g. "oem212.inf".</summary>
    public required string PublishedName { get; init; }
    /// <summary>Original INF name, e.g. "contoso-gfx.inf".</summary>
    public string OriginalName { get; init; } = "";
    public string Provider { get; init; } = "";
    public string ClassName { get; init; } = "";
    public string ClassGuid { get; init; } = "";
    public DriverVersionInfo Version { get; init; } = DriverVersionInfo.Unknown;
    public string Signer { get; init; } = "";
    /// <summary>Devices using this package (present or not). Empty = not in use.</summary>
    public IReadOnlyList<BoundDevice> Devices { get; init; } = Array.Empty<BoundDevice>();
    public bool IsSigned => !string.IsNullOrWhiteSpace(Signer);
}

/// <summary>A driver that matches a device, as ranked by Windows (pnputil /enum-devices /drivers).</summary>
public sealed record MatchingDriver
{
    public required string PublishedName { get; init; }
    public string OriginalName { get; init; } = "";
    public string Provider { get; init; } = "";
    public string ClassName { get; init; } = "";
    public DriverVersionInfo Version { get; init; } = DriverVersionInfo.Unknown;
    public string Signer { get; init; } = "";
    /// <summary>Raw status flags, e.g. "BestRanked/Installed" or "Outranked".</summary>
    public string Status { get; init; } = "";
    public bool IsInstalled => Status.Contains("Installed", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Device status from pnputil (the Status element; not localized).</summary>
public enum DeviceState { Started, Disabled, Problem, Disconnected, Stopped, Unknown }

/// <summary>One device (pnputil /enum-devices), present or not.</summary>
public sealed record DeviceRecord
{
    public required string InstanceId { get; init; }
    public string Description { get; init; } = "";
    public string ClassName { get; init; } = "";
    public string ClassGuid { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public DeviceState State { get; init; } = DeviceState.Unknown;
    /// <summary>Device Manager problem code (0 = none).</summary>
    public int ProblemCode { get; init; }
    /// <summary>The installed driver's INF ("oem212.inf" or an in-box "usbhub3.inf"); empty = no driver.</summary>
    public string DriverInf { get; init; } = "";
    public IReadOnlyList<string> ExtensionInfs { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> HardwareIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> CompatibleIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<MatchingDriver> MatchingDrivers { get; init; } = Array.Empty<MatchingDriver>();
    public DateTime? LastArrivalUtc { get; init; }
    public DateTime? LastRemovalUtc { get; init; }
    public DateTime? InstallDateUtc { get; init; }

    public bool IsPresent => State != DeviceState.Disconnected;
    public MatchingDriver? InstalledDriver => MatchingDrivers.FirstOrDefault(m => m.IsInstalled)
        ?? MatchingDrivers.FirstOrDefault(m => string.Equals(m.PublishedName, DriverInf, StringComparison.OrdinalIgnoreCase));

    /// <summary>Most recent time Windows saw this device arrive, leave or get installed.</summary>
    public DateTime? LastSeenUtc => new[] { LastArrivalUtc, LastRemovalUtc, InstallDateUtc }.Where(d => d.HasValue).Max();
}

/// <summary>A device with a problem code from WMI (Win32_PnPEntity.ConfigManagerErrorCode).</summary>
public sealed record WmiProblemDevice(string InstanceId, string Name, string ClassName, int Code, IReadOnlyList<string> HardwareIds);

/// <summary>A present device's driver from WMI (Win32_PnPSignedDriver): fallback inventory when pnputil can't give XML.</summary>
public sealed record WmiSignedDriver(
    string InstanceId, string DeviceName, string ClassName, string Provider, string InfName,
    string DriverVersion, DateTime? DriverDate, bool IsSigned, string Signer);

/// <summary>A driver update offered by Windows Update (WUA, Type='Driver', not installed).</summary>
public sealed record DriverUpdateOffer
{
    public required string UpdateId { get; init; }
    public required string Title { get; init; }
    public string DriverClass { get; init; } = "";
    public string Model { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public string Provider { get; init; } = "";
    public DateTime? DriverDate { get; init; }
    public string HardwareId { get; init; } = "";
    public long DownloadBytes { get; init; }
    /// <summary>"Optional update" in Settings (Windows doesn't install it on its own).</summary>
    public bool IsOptional { get; init; }
}

/// <summary>Outcome of the Windows Update driver search.</summary>
public sealed record DriverUpdateSearch(IReadOnlyList<DriverUpdateOffer> Updates, string? Problem)
{
    public static DriverUpdateSearch Unavailable(string problem) => new(Array.Empty<DriverUpdateOffer>(), problem);
    public bool IsAvailable => Problem is null;
}

/// <summary>An installed app from the Uninstall registry (only the fields vendor-tool detection needs).</summary>
public sealed record InstalledApp(string DisplayName, string Publisher);

/// <summary>Motherboard from Win32_BaseBoard.</summary>
public sealed record BoardInfo(string Manufacturer, string Product, string Version);

/// <summary>An earlier driver backup made by PC Tidy (a dated folder under the backups folder).</summary>
public sealed record DriverBackup(string Folder, DateTime CreatedLocal, int PackageCount);
