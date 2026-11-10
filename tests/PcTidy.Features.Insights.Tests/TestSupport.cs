using System.Text.Json;
using PcTidy.Core;
using PcTidy.Features.Insights.Source;

namespace PcTidy.Features.Insights.Tests;

/// <summary>A clock frozen at 10 Nov 2026 12:00 UTC.</summary>
internal sealed class FixedClock : TimeProvider
{
    public static readonly DateTime Now = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
    public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
}

internal static class Fixtures
{
    public static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}

/// <summary>A fresh folder under testbed\insights\, deleted afterwards. All file-changing tests stay inside it.</summary>
internal sealed class TestbedFolder : IDisposable
{
    public string Root { get; }

    public TestbedFolder()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(System.IO.Path.Combine(d.FullName, "PcTidy.sln"))) d = d.Parent;
        if (d is null) throw new InvalidOperationException("PcTidy.sln not found above the test output.");
        Root = System.IO.Path.Combine(d.FullName, "testbed", "insights", Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Root);
    }

    /// <summary>Writes Sim\sim.json (read by SandboxSystemInfo).</summary>
    public void Sim(string gpu, bool laptop, int build = 26100, bool admin = false)
    {
        var dir = System.IO.Path.Combine(Root, "Sim");
        Directory.CreateDirectory(dir);
        File.WriteAllText(System.IO.Path.Combine(dir, "sim.json"),
            JsonSerializer.Serialize(new { GpuVendor = gpu, IsLaptop = laptop, WindowsBuild = build, IsAdmin = admin }));
    }

    /// <summary>A file of <paramref name="size"/> bytes (sparse-fast via SetLength) with old timestamps.</summary>
    public static string BigOldFile(string path, long size, DateTime whenUtc)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write)) fs.SetLength(size);
        File.SetCreationTimeUtc(path, whenUtc);
        File.SetLastWriteTimeUtc(path, whenUtc);
        return path;
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Injectable facts for the card tests (no WMI, no registry).</summary>
internal sealed class FakeSource : IInsightsSource
{
    public WindowsIdentity Identity { get; set; } = new() { Build = 26100, Ubr = 1000, ProductName = "Windows 11 Pro" };
    public ReadOutcome<IReadOnlyList<StabilitySample>> Stability { get; set; } = ReadOutcome<IReadOnlyList<StabilitySample>>.Missing();
    public ReadOutcome<IReadOnlyList<MemoryTestResult>> MemoryTests { get; set; } = ReadOutcome<IReadOnlyList<MemoryTestResult>>.Ok(Array.Empty<MemoryTestResult>());
    public BackupFacts Backup { get; set; } = new();
    public List<DisplayFacts> Displays { get; set; } = new();
    public ReadOutcome<MemoryFacts> Memory { get; set; } = ReadOutcome<MemoryFacts>.Missing();
    public Dictionary<string, string> FileSystems { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public TimerFacts Timer { get; set; } = new(10_000_000, true);
    public SecurityFacts Security { get; set; } = new();
    public RuntimeFacts Runtimes { get; set; } = new();

    public WindowsIdentity ReadWindowsIdentity() => Identity;
    public ReadOutcome<IReadOnlyList<StabilitySample>> ReadStability(DateTime sinceUtc) => Stability;
    public ReadOutcome<IReadOnlyList<MemoryTestResult>> ReadMemoryTests() => MemoryTests;
    public BackupFacts ReadBackup() => Backup;
    public IReadOnlyList<DisplayFacts> ReadDisplays() => Displays;
    public ReadOutcome<MemoryFacts> ReadMemory() => Memory;
    public IReadOnlyDictionary<string, string> ReadFileSystems() => FileSystems;
    public TimerFacts ReadTimer() => Timer;
    public SecurityFacts ReadSecurity() => Security;
    public RuntimeFacts ReadRuntimes() => Runtimes;

    /// <summary>Every fixed drive of the sandbox reported as NTFS.</summary>
    public FakeSource WithNtfs(IKnownLocations loc)
    {
        foreach (var d in loc.FixedDrives) FileSystems[d] = "NTFS";
        return this;
    }
}
