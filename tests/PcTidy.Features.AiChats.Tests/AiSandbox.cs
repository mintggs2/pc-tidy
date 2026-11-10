using System.Text;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.AiChats.Local;
using PcTidy.Hosting;

namespace PcTidy.Features.AiChats.Tests;

/// <summary>
/// One fake PC per test under testbed\aichats\ (real composition root in sandbox mode: real SafetyGuard, Executor, Undo).
/// Everything in it is made up by the test. Deleted afterwards.
/// </summary>
internal sealed class AiSandbox : IDisposable
{
    public static readonly string TestbedRoot = Path.Combine(FindCodeRoot(), "testbed", "aichats");
    public static readonly DateTime Now = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

    public AiSandbox(string name, SandboxProfile? profile = null)
    {
        Root = Path.Combine(TestbedRoot, $"{name}-{Guid.NewGuid().ToString("N")[..6]}");
        Directory.CreateDirectory(Root);
        Host = PcTidyHost.CreateSandbox(Root, profile ?? SandboxProfile.Minimal);
        K = (SandboxKnownLocations)Host.Locations;
        Directory.CreateDirectory(K.UserProfile);
    }

    public string Root { get; }
    public PcTidyHost Host { get; }
    public SandboxKnownLocations K { get; }
    public FakeProcesses Processes { get; } = new();
    public FixedClock Clock { get; } = new(Now);

    /// <summary>A folder for test inputs that isn't part of the fake PC's user folders (exports the "user downloaded").</summary>
    public string Inputs => Path.Combine(Root, "inputs");

    public ScanContext Context(Dictionary<string, object>? extra = null)
    {
        var c = Host.CreateScanContext(ScanDepth.Quick);
        var options = new Dictionary<string, object> { [AiChatsOptions.Clock] = Clock, [AiChatsOptions.ProcessProbe] = Processes };
        if (extra is not null) foreach (var (k, v) in extra) options[k] = v;
        return new ScanContext
        {
            Locations = c.Locations, Safety = c.Safety, InUse = c.InUse, Settings = c.Settings, Walker = c.Walker,
            Depth = c.Depth, System = c.System, RunningProcesses = new HashSet<string>(), Options = options,
        };
    }

    public LiveSessions Live() => new LiveSessionDetector(Processes, Clock).Detect(K);

    public static string Put(string path, string text, DateTime? writtenUtc = null) => Put(path, Encoding.UTF8.GetBytes(text), writtenUtc);

    public static string Put(string path, byte[] bytes, DateTime? writtenUtc = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        var w = writtenUtc ?? Now.AddDays(-40);
        File.SetCreationTimeUtc(path, w);
        File.SetLastWriteTimeUtc(path, w);
        return path;
    }

    public void Dispose()
    {
        Host.Dispose();
        try { DeleteTree(Root); }
        catch (Exception) { /* best effort; the next run uses a fresh folder name */ }
    }

    private static void DeleteTree(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
        {
            var isLink = (entry.Attributes & FileAttributes.ReparsePoint) != 0;
            if (entry is DirectoryInfo d && !isLink) { DeleteTree(d.FullName); continue; }
            entry.Attributes = FileAttributes.Normal;
            entry.Delete();
        }
        File.SetAttributes(dir, FileAttributes.Normal);
        Directory.Delete(dir, false);
    }

    private static string FindCodeRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "PcTidy.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("PcTidy.sln not found above " + AppContext.BaseDirectory);
    }
}

internal sealed class FixedClock(DateTime utc) : TimeProvider
{
    public DateTime Utc { get; set; } = utc;
    public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(Utc, DateTimeKind.Utc));
}

internal sealed class FakeProcesses : IProcessProbe
{
    public Dictionary<int, ProcessState> Running { get; } = new();
    public ProcessState? Find(int pid) => Running.TryGetValue(pid, out var s) ? s : null;
}

/// <summary>A folder under testbed\aichats\ for tests that don't need a whole fake PC.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder(string name)
    {
        Path = System.IO.Path.Combine(AiSandbox.TestbedRoot, $"{name}-{Guid.NewGuid().ToString("N")[..6]}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        // A virus scanner may hold the just-closed database for a moment: retry briefly.
        for (int attempt = 0; attempt < 5 && Directory.Exists(Path); attempt++)
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (Exception) { Thread.Sleep(200); }
        }
    }
}
