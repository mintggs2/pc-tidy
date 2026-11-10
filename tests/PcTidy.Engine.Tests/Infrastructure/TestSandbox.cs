using System.Runtime.CompilerServices;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Engine.IO;

namespace PcTidy.Engine.Tests;

/// <summary>
/// One isolated fake PC per test, under testbed\engine\. Everything a test changes lives in here,
/// and it's removed afterwards (with extended-length paths, so long/odd names clean up too).
/// </summary>
public sealed class TestSandbox : IDisposable
{
    private static readonly Lazy<string> TestbedRoot = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PcTidy.sln"))) dir = dir.Parent;
        if (dir is null) throw new InvalidOperationException("Can't find the solution folder (PcTidy.sln).");
        var root = Path.Combine(dir.FullName, "testbed", "engine-v2", "engine");
        Directory.CreateDirectory(root);
        return root;
    });

    /// <summary>testbed\engine-v2\engine (created on first use).</summary>
    public static string TestbedRootPath => TestbedRoot.Value;

    public string Root { get; }
    public SandboxKnownLocations Locations { get; }
    public AppSettings Settings { get; }
    public FakeSafetyGuard Safety { get; } = new();
    public FakeInUseChecker InUse { get; } = new();

    public TestSandbox(SandboxProfile? profile = null, [CallerMemberName] string name = "")
    {
        var shortName = name.Length > 24 ? name[..24] : name;
        Root = Path.Combine(TestbedRoot.Value, $"{shortName}-{Guid.NewGuid().ToString("N")[..6]}");
        // Guard rail: tests may only ever write below testbed\engine.
        if (!PathUtil.IsUnder(Root, TestbedRoot.Value)) throw new InvalidOperationException("Sandbox escaped the testbed.");
        Locations = new SandboxKnownLocations(Root, profile ?? SandboxProfile.Default);
        Settings = new AppSettings { DataRoot = Locations.DataRoot };
        Directory.CreateDirectory(Locations.UserProfile);
        Directory.CreateDirectory(Locations.Downloads);
        Directory.CreateDirectory(Locations.DataRoot);
        foreach (var d in Locations.FixedDrives) Directory.CreateDirectory(d);
    }

    /// <summary>A working folder inside the fake user profile (system drive of the sandbox).</summary>
    public string Work(string name = "work")
    {
        var p = Path.Combine(Locations.UserProfile, name);
        Directory.CreateDirectory(p);
        return p;
    }

    /// <summary>A folder on the sandbox's second fixed drive ("D").</summary>
    public string SecondDrive(string name = "data")
    {
        var p = Path.Combine(Locations.FixedDrives[^1], name);
        Directory.CreateDirectory(p);
        return p;
    }

    public EngineServices Engine(bool forceCopy = false, IElevationService? elevation = null, IRestorePointService? restore = null,
        IKnownLocations? locations = null, IEnumerable<IActionHandler>? extraHandlers = null)
        => EngineFactory.Create(locations ?? Locations, Safety, InUse, Settings, extraHandlers, restore, elevation, forceCopy);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(LongPath.Io(Root))) FsOps.DeleteTree(Root);
        }
        catch { /* best effort; the folder is unique to this test */ }
    }
}

/// <summary>Allows everything except paths the test blocks.</summary>
public sealed class FakeSafetyGuard : ISafetyGuard
{
    public HashSet<string> Blocked { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int Checks { get; private set; }

    public SafetyVerdict ClassifyPath(string path) => Blocked.Contains(path) ? SafetyVerdict.Block("Protected (test).") : SafetyVerdict.Ok;

    public SafetyVerdict Check(PlanItem item)
    {
        Checks++;
        foreach (var p in new[] { item.SourcePath, item.DestinationPath })
            if (p is not null && Blocked.Contains(p)) return SafetyVerdict.Block("It's in a protected folder.");
        return SafetyVerdict.Ok;
    }

    public bool IsInCloudRoot(string path) => false;

    public bool IsInsideProjectOrAppFolder(string path, out string? why) { why = null; return false; }
}

public sealed class FakeInUseChecker : IInUseChecker
{
    public Dictionary<string, string[]> InUse { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<InUseInfo>> CheckAsync(IReadOnlyList<string> paths, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<InUseInfo>>(paths.Select(p => InUse.TryGetValue(p, out var apps)
            ? new InUseInfo(p, true, apps) : new InUseInfo(p, false, Array.Empty<string>())).ToList());

    public bool IsLocked(string path) => InUse.ContainsKey(path);
}

/// <summary>Records elevated batches; runs them through the given handlers (as the elevated helper would).</summary>
public sealed class FakeElevation : IElevationService, IElevationSettlement
{
    public bool IsElevated { get; init; }
    public List<IReadOnlyList<StepRecord>> Batches { get; } = new();
    public Func<StepRecord, StepRecord>? Run { get; init; }

    public Task<IReadOnlyList<StepRecord>> ExecuteElevatedAsync(string runId, IReadOnlyList<StepRecord> steps, ExecutionMode mode, bool undo, CancellationToken ct)
    {
        Batches.Add(steps);
        return Task.FromResult<IReadOnlyList<StepRecord>>(steps.Select(s => Run?.Invoke(s)
            ?? s with { Status = undo ? StepStatus.Undone : StepStatus.Done, Message = "elevated (fake)" }).ToList());
    }

    /// <summary>The helper's ledger answers for Started steps (as Platform's ElevationService does). Null = unreachable (unchanged).</summary>
    public Func<StepRecord, StepRecord>? Settle { get; init; }
    public List<IReadOnlyList<StepRecord>> Settles { get; } = new();

    public Task<IReadOnlyList<StepRecord>> SettleElevatedAsync(string runId, IReadOnlyList<StepRecord> steps, CancellationToken ct)
    {
        Settles.Add(steps);
        return Task.FromResult<IReadOnlyList<StepRecord>>(steps.Select(s => Settle?.Invoke(s) ?? s).ToList());
    }
}

public sealed class FakeRestorePoints : IRestorePointService
{
    public RestorePointInfo Result { get; init; } = new(true, false, "test", DateTime.UtcNow, null);
    public int Calls { get; private set; }

    public Task<RestorePointInfo> EnsureAsync(string description, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(Result);
    }
}

/// <summary>The sandbox paths, but claiming to be a real PC (to exercise restore-point/elevation logic with fakes).</summary>
public sealed class NonSandboxLocations : IKnownLocations
{
    private readonly IKnownLocations _inner;
    public NonSandboxLocations(IKnownLocations inner) => _inner = inner;
    public string UserProfile => _inner.UserProfile;
    public string LocalAppData => _inner.LocalAppData;
    public string RoamingAppData => _inner.RoamingAppData;
    public string ProgramData => _inner.ProgramData;
    public string WindowsDir => _inner.WindowsDir;
    public string SystemDrive => _inner.SystemDrive;
    public string ProgramFiles => _inner.ProgramFiles;
    public string ProgramFilesX86 => _inner.ProgramFilesX86;
    public string UserTemp => _inner.UserTemp;
    public string WindowsTemp => _inner.WindowsTemp;
    public string Desktop => _inner.Desktop;
    public string Documents => _inner.Documents;
    public string Downloads => _inner.Downloads;
    public string Pictures => _inner.Pictures;
    public string Videos => _inner.Videos;
    public string Music => _inner.Music;
    public IReadOnlyList<string> CloudRoots => _inner.CloudRoots;
    public string? SteamRoot => _inner.SteamRoot;
    public IReadOnlyList<string> SteamLibraries => _inner.SteamLibraries;
    public string? EpicManifestsDir => _inner.EpicManifestsDir;
    public IReadOnlyList<string> GameInstallDirs => _inner.GameInstallDirs;
    public IReadOnlyList<string> AppInstallDirs => _inner.AppInstallDirs;
    public string ClaudeDir => _inner.ClaudeDir;
    public string CodexDir => _inner.CodexDir;
    public string DataRoot => _inner.DataRoot;
    public IReadOnlyList<string> FixedDrives => _inner.FixedDrives;
    public bool IsSandbox => false;
}
