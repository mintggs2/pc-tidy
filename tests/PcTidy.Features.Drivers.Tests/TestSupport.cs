using PcTidy.Core.Model;
using PcTidy.Features.Drivers.Analysis;
using PcTidy.Features.Drivers.Scanning;
using PcTidy.Features.Drivers.Source;

namespace PcTidy.Features.Drivers.Tests;

/// <summary>A clock frozen at the scenario's "now" (10 Nov 2026 12:00 UTC).</summary>
internal sealed class FixedClock : TimeProvider
{
    public static readonly DateTime ScenarioNowUtc = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
    private readonly DateTimeOffset _now;
    public FixedClock(DateTime? utc = null) => _now = new DateTimeOffset(utc ?? ScenarioNowUtc, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
}

internal static class Fixtures
{
    public static string Folder(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    public static FixtureDriverSource Source(string name) => new(Folder(name));

    public static Task<DriverInventory> InventoryAsync(string name = "scenario", bool searchWindowsUpdate = true)
        => DriverInventory.LoadAsync(Source(name), Path.Combine(Path.GetTempPath(), "pctidy-drivers-no-backups-" + Guid.NewGuid().ToString("N")),
            searchWindowsUpdate, null, CancellationToken.None);

    public static async Task<DriverScanReport> ReportAsync(string name = "scenario", DriverScanOptions? options = null, DateTime? nowUtc = null,
        Func<DriverInventory, DriverInventory>? tweak = null)
    {
        var inv = await InventoryAsync(name);
        if (tweak is not null) inv = tweak(inv);
        return DriverResultBuilder.Build(inv, DataRoot, nowUtc ?? FixedClock.ScenarioNowUtc, options ?? new DriverScanOptions());
    }

    /// <summary>A data root used only to build paths (nothing is written there).</summary>
    public static string DataRoot => Path.Combine(Path.GetTempPath(), "pctidy-drivers-tests-dataroot");

    public static ScanGroup? Group(this ScanResult r, string tab, string name) => r.Groups.FirstOrDefault(g => g.Id == DriverTabs.GroupId(tab, name));

    public static IEnumerable<PlanItem> AllActions(this ScanResult r) => r.AllItems.SelectMany(i => i.Actions);
}

/// <summary>A per-test sandbox folder under testbed\drivers (removed afterwards).</summary>
internal sealed class TestbedFolder : IDisposable
{
    public TestbedFolder()
    {
        Root = Path.Combine(FindRepoRoot(), "testbed", "drivers", Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    /// <summary>Copies a fixture folder to &lt;root&gt;\Sim\drivers (where the sandbox source reads from).</summary>
    public void InstallFixture(string name)
    {
        var target = Path.Combine(Root, "Sim", "drivers");
        Directory.CreateDirectory(target);
        foreach (var f in Directory.EnumerateFiles(Fixtures.Folder(name)))
            File.Copy(f, Path.Combine(target, Path.GetFileName(f)), overwrite: true);
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "PcTidy.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("PcTidy.sln not found above " + AppContext.BaseDirectory);
    }
}
