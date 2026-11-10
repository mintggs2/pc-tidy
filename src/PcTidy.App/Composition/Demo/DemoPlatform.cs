using PcTidy.Core;
using PcTidy.Core.Model;

namespace PcTidy.App.Composition.Demo;

// Demo / design-time fakes. Everything here is in memory: nothing reads or changes the real PC.

/// <summary>A believable, anonymous PC: one or two drives, no personal data.</summary>
public sealed class DemoSystemInfo : ISystemInfo
{
    public DemoSystemInfo(bool minimal)
    {
        const long GB = 1L << 30;
        Drives = minimal
            ? new[] { new DriveDetails(@"C:\", "Windows", DriveMedia.Ssd, 476 * GB, 61 * GB, true) }
            : new[]
            {
                new DriveDetails(@"C:\", "Windows", DriveMedia.Ssd, 953 * GB, 541 * GB, true),
                new DriveDetails(@"D:\", "Data", DriveMedia.Hdd, 1863 * GB, 1308 * GB, false),
            };
    }

    public GpuVendor PrimaryGpu => GpuVendor.Unknown;
    public IReadOnlyList<string> GpuNames => Array.Empty<string>();
    public bool IsLaptop => false;
    public bool HasBattery => false;
    public bool IsAdmin => false;
    public int WindowsBuild => 26100;
    public bool RebootPending => false;
    public IReadOnlyList<DriveDetails> Drives { get; }
    public IReadOnlySet<string> RunningProcesses() => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "chrome" };
}

/// <summary>Demo safety guard: everything in the sample data is allowed (the demo executor touches nothing anyway).</summary>
public sealed class DemoSafetyGuard : ISafetyGuard
{
    public SafetyVerdict ClassifyPath(string path) => SafetyVerdict.Ok;
    public SafetyVerdict Check(PlanItem item) => SafetyVerdict.Ok;
    public bool IsInCloudRoot(string path) => false;
    public bool IsInsideProjectOrAppFolder(string path, out string? why) { why = null; return false; }
}

public sealed class DemoInUseChecker : IInUseChecker
{
    public Task<IReadOnlyList<InUseInfo>> CheckAsync(IReadOnlyList<string> paths, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<InUseInfo>>(paths.Select(p => new InUseInfo(p, false, Array.Empty<string>())).ToList());
    public bool IsLocked(string path) => false;
}

/// <summary>Pretends to make a restore point (reuses "one from this morning").</summary>
public sealed class DemoRestorePointService : IRestorePointService
{
    public async Task<RestorePointInfo> EnsureAsync(string description, CancellationToken ct)
    {
        await Task.Delay(400, ct);
        return new RestorePointInfo(Created: true, Reused: false, description, DateTime.UtcNow, null);
    }
}
