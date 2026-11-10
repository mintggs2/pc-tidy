using System.Text.Json;
using System.Text.Json.Serialization;
using PcTidy.Features.Drivers.Model;

namespace PcTidy.Features.Drivers.Source;

/// <summary>
/// A simulated PC for tests and the sandbox: reads captured or hand-written files from one folder.
/// <list type="bullet">
/// <item>drivers.xml, devices.xml, disconnected.xml, problem.xml: pnputil XML output (a missing file = "not available").</item>
/// <item>machine.json: everything else (see <see cref="MachineFile"/>); a missing file = nothing extra on this PC.</item>
/// </list>
/// Only reads files; never touches the real system.
/// </summary>
public sealed class FixtureDriverSource : IDriverSource
{
    private readonly string _folder;
    private readonly Lazy<MachineFile> _machine;

    public FixtureDriverSource(string folder)
    {
        _folder = folder;
        _machine = new Lazy<MachineFile>(LoadMachine);
    }

    /// <summary>Layout of machine.json. All parts are optional.</summary>
    public sealed record MachineFile
    {
        public List<WmiProblemDevice>? WmiProblems { get; init; }
        public List<WmiSignedDriver>? WmiDrivers { get; init; }
        /// <summary>Null = Windows Update couldn't be searched (see <see cref="UpdatesProblem"/>).</summary>
        public List<DriverUpdateOffer>? Updates { get; init; }
        public string? UpdatesProblem { get; init; }
        public List<InstalledApp>? Apps { get; init; }
        public BoardInfo? Board { get; init; }
        /// <summary>Published name ("oem12.inf") → driver-store folder size in bytes.</summary>
        public Dictionary<string, long>? PackageSizes { get; init; }
        public DateTime? LastDriverChangeUtc { get; init; }
        /// <summary>Instance id → typed facts, as the Configuration Manager would give them (ISO dates in JSON).</summary>
        public Dictionary<string, DeviceFacts>? DeviceFacts { get; init; }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private MachineFile LoadMachine()
    {
        try
        {
            var p = Path.Combine(_folder, "machine.json");
            return File.Exists(p) ? JsonSerializer.Deserialize<MachineFile>(File.ReadAllText(p), Json) ?? new MachineFile() : new MachineFile();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new MachineFile();
        }
    }

    private Task<string?> ReadAsync(string name, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var p = Path.Combine(_folder, name);
            return Task.FromResult(File.Exists(p) ? File.ReadAllText(p) : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult<string?>(null);
        }
    }

    public Task<string?> GetDriverPackagesXmlAsync(CancellationToken ct) => ReadAsync("drivers.xml", ct);
    public Task<string?> GetDevicesXmlAsync(CancellationToken ct) => ReadAsync("devices.xml", ct);
    public Task<string?> GetDisconnectedDevicesXmlAsync(CancellationToken ct) => ReadAsync("disconnected.xml", ct);
    public Task<string?> GetProblemDevicesXmlAsync(CancellationToken ct) => ReadAsync("problem.xml", ct);

    public Task<IReadOnlyList<WmiProblemDevice>> GetWmiProblemDevicesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<WmiProblemDevice>>(_machine.Value.WmiProblems ?? new List<WmiProblemDevice>());

    public Task<IReadOnlyList<WmiSignedDriver>> GetWmiSignedDriversAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<WmiSignedDriver>>(_machine.Value.WmiDrivers ?? new List<WmiSignedDriver>());

    public Task<DriverUpdateSearch> SearchWindowsUpdateDriversAsync(CancellationToken ct)
    {
        var m = _machine.Value;
        return Task.FromResult(m.Updates is { } list && m.UpdatesProblem is null
            ? new DriverUpdateSearch(list, null)
            : DriverUpdateSearch.Unavailable(m.UpdatesProblem ?? "Windows Update can't be checked on this PC."));
    }

    public Task<IReadOnlyList<InstalledApp>> GetInstalledAppsAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<InstalledApp>>(_machine.Value.Apps ?? new List<InstalledApp>());

    public Task<BoardInfo?> GetBoardAsync(CancellationToken ct) => Task.FromResult(_machine.Value.Board);

    public Task<long?> GetPackageSizeAsync(string publishedName, CancellationToken ct)
        => Task.FromResult(_machine.Value.PackageSizes is { } sizes && sizes.TryGetValue(publishedName, out var b) ? b : (long?)null);

    public Task<DateTime?> GetLastDriverChangeUtcAsync(CancellationToken ct) => Task.FromResult(_machine.Value.LastDriverChangeUtc);

    public Task<IReadOnlyDictionary<string, DeviceFacts>> GetDeviceFactsAsync(IReadOnlyCollection<string> instanceIds, CancellationToken ct)
    {
        var all = new Dictionary<string, DeviceFacts>(_machine.Value.DeviceFacts ?? new(), StringComparer.OrdinalIgnoreCase);
        var wanted = instanceIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Task.FromResult<IReadOnlyDictionary<string, DeviceFacts>>(
            all.Where(p => wanted.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase));
    }
}
