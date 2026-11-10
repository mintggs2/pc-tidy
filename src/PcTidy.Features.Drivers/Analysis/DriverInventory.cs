using PcTidy.Features.Drivers.Model;
using PcTidy.Features.Drivers.Parsing;
using PcTidy.Features.Drivers.Source;

namespace PcTidy.Features.Drivers.Analysis;

/// <summary>
/// One read-only snapshot of everything the Drivers section knows about this PC, read once per scan.
/// Each part may be missing (older Windows, no internet, WMI broken): the matching tab then says so in plain words.
/// </summary>
public sealed record DriverInventory
{
    /// <summary>True when pnputil gave XML (Windows 10 2004+ / 11); false = WMI fallback only.</summary>
    public bool HasPnpUtilXml { get; init; }
    public IReadOnlyList<DriverPackage> Packages { get; init; } = Array.Empty<DriverPackage>();
    /// <summary>All devices with their ranked drivers (present and disconnected).</summary>
    public IReadOnlyList<DeviceRecord> Devices { get; init; } = Array.Empty<DeviceRecord>();
    /// <summary>Disconnected devices, with last-seen dates when Windows provides them.</summary>
    public IReadOnlyList<DeviceRecord> Disconnected { get; init; } = Array.Empty<DeviceRecord>();
    /// <summary>Devices with a problem code (pnputil /problem), with hardware ids.</summary>
    public IReadOnlyList<DeviceRecord> ProblemDevices { get; init; } = Array.Empty<DeviceRecord>();
    public IReadOnlyList<WmiProblemDevice> WmiProblems { get; init; } = Array.Empty<WmiProblemDevice>();
    public IReadOnlyList<WmiSignedDriver> WmiDrivers { get; init; } = Array.Empty<WmiSignedDriver>();
    public DriverUpdateSearch Updates { get; init; } = DriverUpdateSearch.Unavailable("Windows Update wasn't checked.");
    public IReadOnlyList<InstalledApp> Apps { get; init; } = Array.Empty<InstalledApp>();
    public BoardInfo? Board { get; init; }
    /// <summary>Published name → driver-store folder size (only measured for packages that may be cleaned up).</summary>
    public IReadOnlyDictionary<string, long> PackageSizes { get; init; } = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    public DateTime? LastDriverChangeUtc { get; init; }
    public IReadOnlyList<DriverBackup> Backups { get; init; } = Array.Empty<DriverBackup>();

    /// <summary>Every device record from every list (present, disconnected, problem): what "in use" is judged against.</summary>
    public IEnumerable<DeviceRecord> AllDevices => Devices.Concat(Disconnected).Concat(ProblemDevices);

    /// <summary>Reads everything from <paramref name="source"/>. Never throws for missing data; honours cancellation.</summary>
    public static async Task<DriverInventory> LoadAsync(IDriverSource source, string backupsRoot, bool searchWindowsUpdate,
        IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("Reading the driver store…");
        var packagesXml = await source.GetDriverPackagesXmlAsync(ct).ConfigureAwait(false);
        progress?.Report("Reading devices…");
        var devicesXml = await source.GetDevicesXmlAsync(ct).ConfigureAwait(false);
        var disconnectedXml = await source.GetDisconnectedDevicesXmlAsync(ct).ConfigureAwait(false);
        var problemXml = await source.GetProblemDevicesXmlAsync(ct).ConfigureAwait(false);
        progress?.Report("Checking for problem devices…");
        var wmiProblems = await source.GetWmiProblemDevicesAsync(ct).ConfigureAwait(false);

        var packages = PnpUtilXml.ParseDriverPackages(packagesXml);
        var devices = PnpUtilXml.ParseDevices(devicesXml);
        var disconnected = PnpUtilXml.ParseDevices(disconnectedXml);
        if (disconnected.Count > 0)
        {
            var facts = await source.GetDeviceFactsAsync(disconnected.Select(d => d.InstanceId).ToList(), ct).ConfigureAwait(false);
            disconnected = disconnected.Select(d => WithFacts(d, facts.GetValueOrDefault(d.InstanceId))).ToList();
        }
        var problemDevices = PnpUtilXml.ParseDevices(problemXml);
        bool hasXml = packagesXml is not null && devicesXml is not null;
        var wmiDrivers = hasXml ? Array.Empty<WmiSignedDriver>() : await source.GetWmiSignedDriversAsync(ct).ConfigureAwait(false);

        DriverUpdateSearch updates;
        if (searchWindowsUpdate)
        {
            progress?.Report("Asking Windows Update for driver updates…");
            updates = await source.SearchWindowsUpdateDriversAsync(ct).ConfigureAwait(false);
        }
        else updates = DriverUpdateSearch.Unavailable("Windows Update wasn't checked.");

        progress?.Report("Looking for maker tools…");
        var apps = await source.GetInstalledAppsAsync(ct).ConfigureAwait(false);
        var board = await source.GetBoardAsync(ct).ConfigureAwait(false);
        var lastChange = await source.GetLastDriverChangeUtcAsync(ct).ConfigureAwait(false);

        // Only packages that could be cleaned up are measured (walking every FileRepository folder would be slow).
        progress?.Report("Measuring old driver packages…");
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in StoreCleanupRules.Candidates(packages, devices.Concat(disconnected).Concat(problemDevices)))
        {
            ct.ThrowIfCancellationRequested();
            if (await source.GetPackageSizeAsync(p.PublishedName, ct).ConfigureAwait(false) is { } size) sizes[p.PublishedName] = size;
        }

        return new DriverInventory
        {
            HasPnpUtilXml = hasXml,
            Packages = packages,
            Devices = devices,
            Disconnected = disconnected,
            ProblemDevices = problemDevices,
            WmiProblems = wmiProblems,
            WmiDrivers = wmiDrivers,
            Updates = updates,
            Apps = apps,
            Board = board,
            PackageSizes = sizes,
            LastDriverChangeUtc = lastChange,
            Backups = BackupFolders.List(backupsRoot),
        };
    }

    /// <summary>
    /// Puts Windows' typed facts over what pnputil printed. Dates: a typed value wins, else only an ISO date from the XML
    /// (localized text was already dropped by the parser). Presence: Windows' answer wins over the (translated) status word;
    /// a device Windows says is connected is never treated as a ghost.
    /// </summary>
    internal static DeviceRecord WithFacts(DeviceRecord d, DeviceFacts? facts)
    {
        if (facts is null) return d;
        var state = facts.IsPresent switch
        {
            false => DeviceState.Disconnected,
            true when d.State == DeviceState.Disconnected => DeviceState.Unknown,
            _ => d.State,
        };
        return d with
        {
            State = state,
            LastArrivalUtc = facts.LastArrivalUtc ?? d.LastArrivalUtc,
            LastRemovalUtc = facts.LastRemovalUtc ?? d.LastRemovalUtc,
            InstallDateUtc = facts.InstallDateUtc ?? d.InstallDateUtc,
        };
    }
}
