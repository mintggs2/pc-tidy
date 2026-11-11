using System.Text.Json;
using PcTidy.Core;

namespace PcTidy.Features.Insights.Source;

/// <summary>
/// The simulated PC in a testbed: reads &lt;root&gt;\Sim\insights.json when present (same shapes as the real reads),
/// otherwise reports every hardware fact as "not available". Never touches the real system.
/// </summary>
public sealed class SandboxInsightsSource : IInsightsSource
{
    private sealed record SimFile
    {
        public WindowsIdentity? Windows { get; init; }
        public List<StabilitySample>? Stability { get; init; }
        public List<MemoryTestResult>? MemoryTests { get; init; }
        public BackupFacts? Backup { get; init; }
        public List<DisplayFacts>? Displays { get; init; }
        public MemoryFacts? Memory { get; init; }
        public Dictionary<string, string>? FileSystems { get; init; }
        public TimerFacts? Timer { get; init; }
        public SecurityFacts? Security { get; init; }
        public RuntimeFacts? Runtimes { get; init; }
        public string? Cpu { get; init; }
        public string? PowerPlan { get; init; }
        public InventoryFacts? Inventory { get; init; }
    }

    private readonly SimFile _sim;

    public SandboxInsightsSource(string sandboxRoot)
    {
        var path = Path.Combine(sandboxRoot, "Sim", "insights.json");
        try
        {
            _sim = File.Exists(path)
                ? JsonSerializer.Deserialize<SimFile>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new SimFile()
                : new SimFile();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            _sim = new SimFile();
        }
    }

    public static SandboxInsightsSource For(IKnownLocations locations)
        => new(locations is SandboxKnownLocations s ? s.Root : Path.GetDirectoryName(locations.DataRoot) ?? locations.DataRoot);

    public WindowsIdentity ReadWindowsIdentity() => _sim.Windows ?? new WindowsIdentity { Build = 26100 };

    public ReadOutcome<IReadOnlyList<StabilitySample>> ReadStability(DateTime sinceUtc)
        => _sim.Stability is { Count: > 0 } s
            ? ReadOutcome<IReadOnlyList<StabilitySample>>.Ok(s.Where(x => x.TimeUtc >= sinceUtc).OrderBy(x => x.TimeUtc).ToList())
            : ReadOutcome<IReadOnlyList<StabilitySample>>.Missing();

    public ReadOutcome<IReadOnlyList<MemoryTestResult>> ReadMemoryTests()
        => _sim.MemoryTests is { } m ? ReadOutcome<IReadOnlyList<MemoryTestResult>>.Ok(m.OrderByDescending(x => x.TimeUtc).ToList()) : ReadOutcome<IReadOnlyList<MemoryTestResult>>.Missing();

    public BackupFacts ReadBackup() => _sim.Backup ?? new BackupFacts();
    public IReadOnlyList<DisplayFacts> ReadDisplays() => _sim.Displays ?? new List<DisplayFacts>();
    public ReadOutcome<MemoryFacts> ReadMemory() => _sim.Memory is { } m ? ReadOutcome<MemoryFacts>.Ok(m) : ReadOutcome<MemoryFacts>.Missing();
    public IReadOnlyDictionary<string, string> ReadFileSystems() => _sim.FileSystems ?? new Dictionary<string, string>();
    public TimerFacts ReadTimer() => _sim.Timer ?? new TimerFacts(10_000_000, true);
    public SecurityFacts ReadSecurity() => _sim.Security ?? new SecurityFacts();
    public RuntimeFacts ReadRuntimes() => _sim.Runtimes ?? new RuntimeFacts();
    public string? ReadCpuName() => _sim.Cpu;
    public string? ReadActivePowerPlan() => _sim.PowerPlan;
    public InventoryFacts ReadInventory() => _sim.Inventory ?? new InventoryFacts();
}
