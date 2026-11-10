using System.Runtime.CompilerServices;
using PcTidy.Core;
using PcTidy.Core.Model;

namespace PcTidy.Features.Cleanup.Tests;

/// <summary>
/// A throw-away fake PC under testbed\cleanup\&lt;test&gt;-&lt;id&gt;, laid out by <see cref="SandboxKnownLocations"/>.
/// Files are written with exact sizes and back-dated times; the whole folder is removed on Dispose.
/// </summary>
internal sealed class FakePc : IDisposable
{
    public const int Old = 60;
    public const int Recent = 1;
    private const int FolderAgeDays = 400;
    private readonly HashSet<string> _freshFolders = new(StringComparer.OrdinalIgnoreCase);

    private FakePc(string root, SandboxProfile profile)
    {
        Root = root;
        K = new SandboxKnownLocations(root, profile);
        Directory.CreateDirectory(K.SystemDrive);
    }

    public string Root { get; }
    public SandboxKnownLocations K { get; }

    public static FakePc Create(SandboxProfile? profile = null, [CallerMemberName] string test = "")
    {
        var name = test.Length > 40 ? test[..40] : test;
        var root = Path.Combine(RepoPaths.TestbedRoot, $"{name}-{Guid.NewGuid().ToString("N")[..8]}");
        return new FakePc(root, profile ?? SandboxProfile.Default);
    }

    /// <summary>Creates a file of exactly <paramref name="size"/> bytes, last touched <paramref name="ageDays"/> ago.</summary>
    public string File(string path, long size, int ageDays, FileAttributes attributes = 0)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write)) fs.SetLength(size);
        var t = DateTime.UtcNow.AddDays(-ageDays);
        System.IO.File.SetCreationTimeUtc(path, t);
        System.IO.File.SetLastWriteTimeUtc(path, t);
        if (attributes != 0) System.IO.File.SetAttributes(path, System.IO.File.GetAttributes(path) | attributes);
        return path;
    }

    /// <summary>Creates an (empty) folder; <paramref name="fresh"/> keeps its times "now" when <see cref="Seal"/> runs.</summary>
    public string Folder(string path, bool fresh = false)
    {
        Directory.CreateDirectory(path);
        if (fresh) _freshFolders.Add(PathUtil.Normalize(path));
        return path;
    }

    public void State(string content)
    {
        var path = PcTidy.Features.Cleanup.SystemState.SandboxSystemState.StateFilePath(K);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
    }

    /// <summary>Back-dates every folder (creating files bumps their parents' times), except ones marked fresh.</summary>
    public void Seal()
    {
        var old = DateTime.UtcNow.AddDays(-FolderAgeDays);
        foreach (var dir in Directory.EnumerateDirectories(Root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
                     .OrderByDescending(d => d.Length))
        {
            if (_freshFolders.Contains(PathUtil.Normalize(dir))) continue;
            Directory.SetCreationTimeUtc(dir, old);
            Directory.SetLastWriteTimeUtc(dir, old);
        }
    }

    public ScanContext Context(ScanDepth depth, params string[] running) => new()
    {
        Locations = K,
        Safety = new FakeSafety(),
        InUse = new FakeInUse(),
        Settings = new AppSettings { DataRoot = K.DataRoot },
        Walker = new ManagedFileWalker(),
        Depth = depth,
        RunningProcesses = new HashSet<string>(running),
    };

    public Task<ScanResult> CleanupScan(ScanDepth depth = ScanDepth.Quick, params string[] running)
        => new CleanupScanner().ScanAsync(Context(depth, running), null, CancellationToken.None);

    public Task<ScanResult> HiddenScan()
        => new HiddenFilesScanner().ScanAsync(Context(ScanDepth.Quick), null, CancellationToken.None);

    public void Dispose()
    {
        if (!Directory.Exists(Root)) return;
        foreach (var f in Directory.EnumerateFileSystemEntries(Root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            try { System.IO.File.SetAttributes(f, Directory.Exists(f) ? FileAttributes.Directory : FileAttributes.Normal); } catch (IOException) { }
        }
        // Unlink junctions first (removes only the link, never the target), then delete; retry briefly
        // because Explorer/indexer can hold a just-emptied folder for a moment.
        foreach (var link in Directory.EnumerateDirectories(Root, "*", SearchOption.AllDirectories)
                     .Where(d => (System.IO.File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0).ToList())
            Directory.Delete(link);
        for (var attempt = 0; attempt < 5 && Directory.Exists(Root); attempt++)
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(100); }
        }
    }
}

internal static class RepoPaths
{
    public static string RepoRoot { get; } = FindRepoRoot();
    public static string TestbedRoot => Path.Combine(RepoRoot, "testbed", "cleanup");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(Path.Combine(dir.FullName, "PcTidy.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("PcTidy.sln not found above the test folder.");
    }
}

internal sealed class FakeSafety : ISafetyGuard
{
    public SafetyVerdict ClassifyPath(string path) => SafetyVerdict.Ok;
    public SafetyVerdict Check(PlanItem item) => SafetyVerdict.Ok;
    public bool IsInCloudRoot(string path) => false;

    public bool IsInsideProjectOrAppFolder(string path, out string? why)
    {
        why = null;
        if (Directory.Exists(Path.Combine(path, ".git")) || System.IO.File.Exists(Path.Combine(path, "package.json")))
        {
            why = "code project";
            return true;
        }
        return false;
    }
}

internal sealed class FakeInUse : IInUseChecker
{
    public Task<IReadOnlyList<InUseInfo>> CheckAsync(IReadOnlyList<string> paths, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<InUseInfo>>(Array.Empty<InUseInfo>());

    public bool IsLocked(string path) => false;
}
