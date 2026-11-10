using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Organize;
using PcTidy.Features.Organize.Planning;

namespace PcTidy.Features.Organize.Tests;

/// <summary>
/// A fresh fake PC under testbed\organize\&lt;guid&gt; (SandboxKnownLocations). Deleted on dispose.
/// Every file it creates is "old" (1 day) unless told otherwise, so the 10-minute rule doesn't hide it.
/// </summary>
public sealed class Sandbox : IDisposable
{
    public static readonly string TestbedRoot = FindTestbed();

    public Sandbox(SandboxProfile? profile = null)
    {
        Root = Path.Combine(TestbedRoot, Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Root);
        Locations = new SandboxKnownLocations(Root, profile ?? SandboxProfile.Default);
        Directory.CreateDirectory(Locations.Downloads);
        Directory.CreateDirectory(Locations.Desktop);
        foreach (var cloud in Locations.CloudRoots) Directory.CreateDirectory(cloud);
        AppSettings = new AppSettings { DataRoot = Locations.DataRoot };
        Safety = new FakeSafetyGuard(Locations, AppSettings);
    }

    public string Root { get; }
    public SandboxKnownLocations Locations { get; }
    public AppSettings AppSettings { get; }
    public FakeSafetyGuard Safety { get; }
    public FakeInUse InUse { get; } = new();
    public FakeClock Clock { get; } = new(DateTime.UtcNow);
    public string Downloads => Locations.Downloads;

    public string File(string relativeOrFull, string content = "x", double ageDays = 1, string? folder = null)
    {
        var path = Path.IsPathFullyQualified(relativeOrFull) ? relativeOrFull : Path.Combine(folder ?? Downloads, relativeOrFull);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        SetAge(path, ageDays);
        return path;
    }

    public string Bytes(string relative, byte[] content, double ageDays = 1, string? folder = null)
    {
        var path = Path.Combine(folder ?? Downloads, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, content);
        SetAge(path, ageDays);
        return path;
    }

    public static void SetAge(string path, double ageDays)
    {
        var when = DateTime.UtcNow.AddDays(-ageDays);
        System.IO.File.SetCreationTimeUtc(path, when);
        System.IO.File.SetLastWriteTimeUtc(path, when);
    }


    /// <summary>Makes a folder look untouched for a day (created and modified).</summary>
    public static void AgeDir(string path)
    {
        var when = DateTime.UtcNow.AddDays(-1);
        Directory.SetCreationTimeUtc(path, when);
        Directory.SetLastWriteTimeUtc(path, when);
    }
    public OrganizePlanner Planner() => new(Safety, InUse, new ManagedFileWalker(), AppSettings, Clock);

    public Task<OrganizePreview> PlanAsync(OrganizeSettings? settings = null, ScanDepth depth = ScanDepth.Quick, params string[] roots)
        => Planner().PlanAsync(new OrganizeRequest
        {
            Roots = roots.Length > 0 ? roots : new[] { Downloads },
            Settings = settings ?? new OrganizeSettings(),
            Depth = depth,
            Culture = System.Globalization.CultureInfo.InvariantCulture,
        }, null, CancellationToken.None);

    public ScanContext Context(string? preset = null, IReadOnlyList<string>? roots = null) => new()
    {
        Locations = Locations, Safety = Safety, InUse = InUse, Settings = AppSettings, Walker = new ManagedFileWalker(), Preset = preset, Roots = roots,
    };

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                System.IO.File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string FindTestbed()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (System.IO.File.Exists(Path.Combine(d.FullName, "PcTidy.sln")))
                return Path.Combine(d.FullName, "testbed", "organize");
        throw new InvalidOperationException("Can't find PcTidy.sln above the test folder.");
    }
}

public static class PreviewExtensions
{
    public static PlannedAction ActionFor(this OrganizePreview p, string name)
        => p.Actions.Single(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static SkippedEntry SkipFor(this OrganizePreview p, string name)
        => p.Skipped.Single(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static string RelativeDestFolder(this PlannedAction a)
        => Path.GetRelativePath(a.Root, Path.GetDirectoryName(a.DestinationPath!)!);
}

public sealed class FakeClock(DateTime start) : IClock
{
    public DateTime UtcNow { get; set; } = start;
    public void Advance(TimeSpan by) => UtcNow += by;
}

public sealed class FakeInUse : IInUseChecker
{
    public Dictionary<string, string> Locked { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<InUseInfo>> CheckAsync(IReadOnlyList<string> paths, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<InUseInfo>>(paths.Select(p => Locked.TryGetValue(p, out var app)
            ? new InUseInfo(p, true, new[] { app })
            : new InUseInfo(p, false, Array.Empty<string>())).ToList());

    public bool IsLocked(string path) => Locked.ContainsKey(path);
}

/// <summary>Plan §6 in miniature, enough to exercise Organize's use of the guard.</summary>
public sealed class FakeSafetyGuard(IKnownLocations k, AppSettings settings) : ISafetyGuard
{
    private static readonly string[] Markers = { ".git", ".svn", ".hg", "package.json", "pyproject.toml", "Cargo.toml", "go.mod", "node_modules", ".venv", "steamapps" };

    public SafetyVerdict ClassifyPath(string path)
    {
        foreach (var never in new[] { k.WindowsDir, k.ProgramFiles, k.ProgramFilesX86, k.ProgramData, k.DataRoot }.Concat(k.GameInstallDirs).Concat(k.AppInstallDirs))
            if (PathUtil.IsUnder(path, never)) return SafetyVerdict.Block("Protected.", PathProtection.Never);
        if (IsInCloudRoot(path)) return new SafetyVerdict(true, "OneDrive", PathProtection.AskFirst);
        return SafetyVerdict.Ok;
    }

    public SafetyVerdict Check(PlanItem item)
    {
        foreach (var p in new[] { item.SourcePath, item.DestinationPath })
        {
            if (p is null) continue;
            var v = ClassifyPath(p);
            if (v.Protection == PathProtection.Never) return v;
            if (v.Protection == PathProtection.AskFirst && !settings.AllowedAskFirstFolders.Any(f => PathUtil.IsUnder(p, f)))
                return SafetyVerdict.Block("Needs your OK first.", PathProtection.AskFirst);
        }
        return SafetyVerdict.Ok;
    }

    public bool IsInCloudRoot(string path) => k.CloudRoots.Any(c => PathUtil.IsUnder(path, c));

    public bool IsInsideProjectOrAppFolder(string path, out string? why)
    {
        // Stop at the sandbox root: the real code tree above it is itself a project.
        var stopAt = ((SandboxKnownLocations)k).Root;
        for (var d = new DirectoryInfo(path); d is not null && PathUtil.IsUnder(d.FullName, stopAt); d = d.Parent)
        {
            if (!d.Exists) continue;
            foreach (var m in Markers)
                if (Directory.Exists(Path.Combine(d.FullName, m)) || System.IO.File.Exists(Path.Combine(d.FullName, m))) { why = m; return true; }
            if (d.EnumerateFiles("*.sln").Any() || d.EnumerateFiles("*.csproj").Any()) { why = "project file"; return true; }
            if (d.EnumerateFiles("*.exe").Any() && d.EnumerateFiles("*.dll").Any()) { why = "app files"; return true; }
        }
        why = null;
        return false;
    }
}

public sealed class FakeFlowHost : IFlowHost
{
    public List<ActionPlan> Approved { get; } = new();
    public List<ActionPlan> Reviewed { get; } = new();

    public Task<RunRecord?> ReviewAndRunAsync(ActionPlan plan, CancellationToken ct) { Reviewed.Add(plan); return Task.FromResult<RunRecord?>(Record(plan)); }
    public Task<RunRecord?> SimulateAsync(ActionPlan plan, CancellationToken ct) => Task.FromResult<RunRecord?>(Record(plan, ExecutionMode.Simulate));
    public Task<RunRecord?> RunApprovedAsync(ActionPlan plan, CancellationToken ct) { Approved.Add(plan); return Task.FromResult<RunRecord?>(Record(plan)); }
    public void OpenInExplorer(string path) { }
    public void ShowMessage(string title, string message, bool isError = false) { }

    private static RunRecord Record(ActionPlan plan, ExecutionMode mode = ExecutionMode.Real) => new()
    {
        RunId = plan.RunId, FeatureId = plan.FeatureId, Title = plan.Title, Mode = mode, Status = RunStatus.Completed,
        Steps = plan.Items.Select((item, i) => new StepRecord { Index = i, Item = item, Status = StepStatus.Done }).ToList(),
    };
}
