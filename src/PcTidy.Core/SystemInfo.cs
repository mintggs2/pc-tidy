using System.Text.Json;
using PcTidy.Core.Model;

namespace PcTidy.Core;

public sealed record DriveDetails(string Root, string Label, DriveMedia Media, long TotalBytes, long FreeBytes, bool IsSystem)
{
    /// <summary>
    /// On an external bus (USB, SD/MMC card, FireWire): Windows often reports such disks as "fixed", but they can be
    /// unplugged, so nothing should be moved or linked onto them. False when unknown.
    /// </summary>
    public bool IsExternal { get; init; }
}

/// <summary>
/// Facts about this PC that features adapt to (PC Tidy runs on any PC).
/// Real implementation: PcTidy.Platform (WMI/registry/APIs, never localized text).
/// Sandbox implementation: <see cref="SandboxSystemInfo"/> reading &lt;testbed&gt;\Sim\sim.json.
/// </summary>
public interface ISystemInfo
{
    GpuVendor PrimaryGpu { get; }
    IReadOnlyList<string> GpuNames { get; }
    bool IsLaptop { get; }
    bool HasBattery { get; }
    bool IsAdmin { get; }
    /// <summary>Windows build number (19045 = Win10 22H2, 22000+ = Win11).</summary>
    int WindowsBuild { get; }
    bool IsWindows11 => WindowsBuild >= 22000;
    bool RebootPending { get; }
    IReadOnlyList<DriveDetails> Drives { get; }
    /// <summary>Running process names (lower-case, no .exe).</summary>
    IReadOnlySet<string> RunningProcesses();
}

/// <summary>Simulated hardware for tests. Reads &lt;root&gt;\Sim\sim.json if present, else sensible defaults.</summary>
public sealed class SandboxSystemInfo : ISystemInfo
{
    private sealed record SimFile(string? GpuVendor, bool? IsLaptop, bool? IsAdmin, bool? RebootPending, int? WindowsBuild, string[]? RunningProcesses);

    public SandboxSystemInfo(SandboxKnownLocations loc)
    {
        SimFile? sim = null;
        var p = Path.Combine(loc.Root, "Sim", "sim.json");
        try { if (File.Exists(p)) sim = JsonSerializer.Deserialize<SimFile>(File.ReadAllText(p), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch { /* defaults */ }
        PrimaryGpu = Enum.TryParse<GpuVendor>(sim?.GpuVendor, true, out var g) ? g : GpuVendor.Unknown;
        GpuNames = PrimaryGpu == GpuVendor.Unknown ? Array.Empty<string>() : new[] { PrimaryGpu + " Simulated GPU" };
        IsLaptop = sim?.IsLaptop ?? false;
        HasBattery = IsLaptop;
        IsAdmin = sim?.IsAdmin ?? false;
        RebootPending = sim?.RebootPending ?? false;
        WindowsBuild = sim?.WindowsBuild ?? 26100;
        _running = new HashSet<string>(sim?.RunningProcesses ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        Drives = loc.FixedDrives.Select((d, i) => new DriveDetails(d, i == 0 ? "System" : "Data", i == 0 ? DriveMedia.Ssd : DriveMedia.Hdd,
            512L << 30, 128L << 30, i == 0)).ToList();
    }

    private readonly HashSet<string> _running;
    public GpuVendor PrimaryGpu { get; }
    public IReadOnlyList<string> GpuNames { get; }
    public bool IsLaptop { get; }
    public bool HasBattery { get; }
    public bool IsAdmin { get; }
    public int WindowsBuild { get; }
    public bool RebootPending { get; }
    public IReadOnlyList<DriveDetails> Drives { get; }
    public IReadOnlySet<string> RunningProcesses() => _running;
}
