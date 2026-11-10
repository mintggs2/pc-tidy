using System.Text.Json;

namespace PcTidy.Features.Schedule.DiskHealth;

public enum DiskHealthStatus { Healthy, Warning, Unhealthy, Unknown }

/// <summary>One physical disk at one moment. Temperature and wear are only there when Windows can read them.</summary>
public sealed record DiskReading
{
    /// <summary>Windows' disk number/id (stable across samples on the same PC; never a serial number).</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    public DriveMedia Media { get; init; }
    public DiskHealthStatus Health { get; init; } = DiskHealthStatus.Unknown;
    public double? TemperatureC { get; init; }
    /// <summary>SSD wear used, 0–100 %.</summary>
    public double? WearPercent { get; init; }
    public double? PowerOnHours { get; init; }
}

/// <summary>A daily sample of every disk.</summary>
public sealed record DiskHealthSample(DateTime TimeUtc, IReadOnlyList<DiskReading> Disks);

/// <summary>Reads disk health, read-only.</summary>
public interface IDiskHealthSource
{
    IReadOnlyList<DiskReading> Read(CancellationToken ct);
}

/// <summary>Plain-English warnings from the history: a disk that isn't healthy, or one whose wear or heat is rising.</summary>
public static class DiskHealthAdvice
{
    public const double HotSsdC = 70;
    public const double HotHddC = 55;
    public const double HighWearPercent = 90;

    public static IReadOnlyList<string> Warnings(IReadOnlyList<DiskHealthSample> history)
    {
        var warnings = new List<string>();
        if (history.Count == 0) return warnings;
        var latest = history.OrderByDescending(s => s.TimeUtc).First();
        foreach (var disk in latest.Disks)
        {
            switch (disk.Health)
            {
                case DiskHealthStatus.Unhealthy:
                    warnings.Add($"{disk.Name}: Windows reports this disk as unhealthy. Back up what's on it soon.");
                    break;
                case DiskHealthStatus.Warning:
                    warnings.Add($"{disk.Name}: Windows reports a health warning. A backup is a good idea.");
                    break;
            }
            if (disk.WearPercent is >= HighWearPercent)
                warnings.Add($"{disk.Name}: {disk.WearPercent:0} % of its rated wear is used. Plan to replace it.");
            var hot = disk.Media == DriveMedia.Hdd ? HotHddC : HotSsdC;
            if (disk.TemperatureC is { } t && t >= hot)
                warnings.Add($"{disk.Name}: running hot ({t:0} °C). Check the airflow around it.");
            if (WentDown(history, disk.Id))
                warnings.Add($"{disk.Name}: its health changed since earlier samples.");
        }
        return warnings;
    }

    /// <summary>True when an earlier sample was better than the latest one.</summary>
    private static bool WentDown(IReadOnlyList<DiskHealthSample> history, string id)
    {
        var states = history.OrderBy(s => s.TimeUtc)
            .Select(s => s.Disks.FirstOrDefault(d => d.Id == id)?.Health)
            .Where(h => h is not null and not DiskHealthStatus.Unknown)
            .Select(h => h!.Value).ToList();
        return states.Count >= 2 && states[^1] > states.Take(states.Count - 1).Min();
    }

    public static string StatusText(DiskHealthStatus s) => s switch
    {
        DiskHealthStatus.Healthy => "Healthy",
        DiskHealthStatus.Warning => "Warning",
        DiskHealthStatus.Unhealthy => "Unhealthy",
        _ => "Not reported",
    };
}

/// <summary>
/// Practice PC: reads <c>&lt;root&gt;\Sim\disks.json</c> (a JSON array of <see cref="DiskReading"/>) when present,
/// else reports no disks ("not available"). Never reads the real disks.
/// </summary>
public sealed class SimulatedDiskHealthSource : IDiskHealthSource
{
    private readonly string _file;
    public SimulatedDiskHealthSource(SandboxKnownLocations locations) => _file = Path.Combine(locations.Root, "Sim", "disks.json");

    public IReadOnlyList<DiskReading> Read(CancellationToken ct)
    {
        try
        {
            if (!File.Exists(_file)) return Array.Empty<DiskReading>();
            return JsonSerializer.Deserialize<List<DiskReading>>(File.ReadAllText(_file), AppSettings.Json) ?? new List<DiskReading>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return Array.Empty<DiskReading>(); }
    }
}
