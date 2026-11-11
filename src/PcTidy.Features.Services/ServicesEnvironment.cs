using PcTidy.Features.Services.Sources;

namespace PcTidy.Features.Services;

/// <summary>
/// Every read-only source the Services scanners use. <see cref="For"/> picks the real PC (Service Control Manager,
/// process queries, WinVerifyTrust) or, for a sandbox, a practice PC that reads nothing real. Tests build their own.
/// </summary>
public sealed class ServicesEnvironment
{
    public required IServiceSource Services { get; init; }
    public required IFileFactsProvider Files { get; init; }
    public required IProcessUsageSource Usage { get; init; }
    /// <summary>True on the real PC: unknown %variables% in service paths may be expanded from this process's environment.</summary>
    public bool IsRealPc { get; init; }

    private static readonly Lazy<ServicesEnvironment> RealPc = new(() => new ServicesEnvironment
    {
        Services = new WindowsServiceSource(),
        Files = new WindowsFileFacts(),
        Usage = new WindowsProcessUsage(),
        IsRealPc = true,
    });

    /// <summary>The real PC's read-only sources (shared, created on first use).</summary>
    public static ServicesEnvironment Real => RealPc.Value;

    /// <summary>The environment for a scan: the real PC, or the practice PC of a sandbox.</summary>
    public static ServicesEnvironment For(ScanContext ctx) => ctx.Locations.IsSandbox ? ForSandbox(ctx.Locations) : RealPc.Value;

    /// <summary>A sandbox's practice PC: &lt;root&gt;\Sim\services.json, else the built-in sample.</summary>
    public static ServicesEnvironment ForSandbox(IKnownLocations locations)
    {
        var sim = SimulatedServices.ForSandbox(locations, locations is SandboxKnownLocations s ? Path.Combine(s.Root, "Sim") : null);
        return From(sim);
    }

    public static ServicesEnvironment From(SimulatedServices sim) => new() { Services = sim, Files = sim.Files, Usage = sim };
}
