using System.Diagnostics;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.SpaceMap.Actions;
using PcTidy.Features.SpaceMap.Index;
using PcTidy.Features.SpaceMap.View;
using PcTidy.Safety;
using Xunit.Abstractions;

namespace PcTidy.Features.SpaceMap.Tests;

/// <summary>
/// Space map against the REAL SafetyGuard on a sandbox PC (SandboxKnownLocations). The fake guard can't show how
/// the real one treats drive roots, Users, Downloads and the folders that hold protected ones, nor what it costs.
/// </summary>
[Collection("perf")] // timed: run alone
public class RealGuardTests
{
    private readonly ITestOutputHelper _out;
    public RealGuardTests(ITestOutputHelper output) => _out = output;

    private static (SpaceIndex Index, CountingGuard Guard, SandboxKnownLocations Loc) ScanDrive(SpaceScanOptions? options = null, string? root = null)
    {
        var loc = RealGuardPc.Ensure();
        var guard = new CountingGuard(new SafetyGuard(loc, new AppSettings { DataRoot = loc.DataRoot }));
        var scanner = new SpaceIndexScanner(new ManagedFileWalker(), guard, drives: new FakeDrives());
        var ix = scanner.Scan(root ?? loc.SystemDrive, options ?? new SpaceScanOptions { IncludeUnscannedNode = false }, null, CancellationToken.None);
        return (ix, guard, loc);
    }

    [Fact]
    public void Real_guard_locks_only_truly_protected_areas()
    {
        var (ix, guard, loc) = ScanDrive();
        int Node(string path) { var n = ix.Find(path); Assert.True(n >= 0, path); return n; }
        var users = Node(Path.GetDirectoryName(loc.UserProfile)!);
        var downloads = Node(loc.Downloads);
        var oldFile = Node(Path.Combine(loc.Downloads, "batch 00", "set 00", "file 00.bin"));

        // The scanned drive, Users, the profile and Downloads stay put as folders, but aren't "protected".
        foreach (var n in new[] { SpaceIndex.RootNode, users, Node(loc.UserProfile), downloads })
        {
            Assert.True(ix.Has(n, SpaceNodeFlags.SelfLocked), $"{ix.FullPath(n)}: {ix.Flags(n)}");
            Assert.False(ix.Has(n, SpaceNodeFlags.Protected), ix.FullPath(n));
        }
        Assert.False(ix.Has(oldFile, SpaceNodeFlags.Protected | SpaceNodeFlags.SelfLocked));
        // Windows, apps and the user's installed app are protected with everything inside.
        Assert.True(ix.Has(Node(loc.WindowsDir), SpaceNodeFlags.Protected));
        Assert.True(ix.Has(Node(Path.Combine(loc.WindowsDir, "System32", "dir 00", "file 00.dll")), SpaceNodeFlags.Protected));
        Assert.True(ix.Has(Node(loc.AppInstallDirs[1]), SpaceNodeFlags.Protected));
        // Another account's folder is protected; Users holds protected folders (that one, the installed app), Downloads doesn't.
        Assert.True(ix.Has(Node(Path.Combine(Path.GetDirectoryName(loc.UserProfile)!, "guest", "Documents", "file 00.txt")), SpaceNodeFlags.Protected));
        Assert.True(ix.Has(users, SpaceNodeFlags.ContainsProtected));
        Assert.False(ix.Has(downloads, SpaceNodeFlags.ContainsProtected));
        // Code projects are ask-first, not locked.
        var project = Node(Path.Combine(loc.Documents, "Projects", "proj 00", "src", "file 00.cs"));
        Assert.True(ix.Has(project, SpaceNodeFlags.AskFirst));
        Assert.False(ix.Has(project, SpaceNodeFlags.Protected));

        // The list shows no lock on Users, just the hint; actions on a Downloads file pass the real guard.
        var row = FolderRow.From(ix, users, ix.Size(users));
        Assert.False(row.IsProtected);
        Assert.Contains("protected folders inside", row.Detail);
        Assert.True(FolderRow.From(ix, Node(loc.WindowsDir), 1).IsProtected);
        var actions = new SpaceActions(loc, guard, new FakeDrives(), 7);
        Assert.NotNull(actions.WhyLocked(ix, users));
        var quarantine = actions.Quarantine(ix, oldFile);
        Assert.True(quarantine.Ok, quarantine.WhyNot);
        Assert.False(actions.Quarantine(ix, Node(loc.WindowsDir)).Ok);
    }

    [Fact]
    public void Optimize_all_profile_scan_walks_downloads_with_the_real_guard()
    {
        var loc = RealGuardPc.Ensure();
        var (ix, guard, _) = ScanDrive(new SpaceScanOptions { SkipProtectedContents = true, SkipCloudRoots = true, IncludeUnscannedNode = false }, loc.UserProfile);
        // The profile root is blocked as a whole only, so everything in Downloads is still measured.
        Assert.Equal(RealGuardPc.DownloadsFiles, ix.FileCount(ix.Find(loc.Downloads)));
        var app = ix.Find(loc.AppInstallDirs[1]);
        Assert.True(ix.Has(app, SpaceNodeFlags.NotWalked));
        Assert.Equal(0, ix.FileCount(app)); // protected: nothing inside is measured

        // Deep "Optimize everything" scans whole drives the same way: Users blocks other accounts, not yours.
        var (drive, _, _) = ScanDrive(new SpaceScanOptions { SkipProtectedContents = true, SkipCloudRoots = true, IncludeUnscannedNode = false });
        Assert.Equal(RealGuardPc.DownloadsFiles, drive.FileCount(drive.Find(loc.Downloads)));
        Assert.Equal(0, drive.FileCount(drive.Find(loc.WindowsDir)));
        Assert.Equal(0, drive.FileCount(drive.Find(Path.Combine(Path.GetDirectoryName(loc.UserProfile)!, "guest"))));
        // Only folders the guard didn't call ordinary (libraries, projects, folders blocked as a whole) are asked about
        // the cloud: none of the 200 ordinary folders in Downloads.
        int notOrdinary = 0;
        for (int n = 0; n < ix.Count; n++)
            if (ix.IsDirectory(n) && ix.Has(n, SpaceNodeFlags.AskFirst | SpaceNodeFlags.SelfLocked)) notOrdinary++;
        Assert.InRange(guard.CloudCalls, 1, notOrdinary);
        Assert.True(guard.CloudCalls < ix.Summary.Folders - 200, $"{guard.CloudCalls} cloud checks for {ix.Summary.Folders} folders");
    }

    [Fact]
    public void Scan_of_20k_files_with_the_real_guard_stays_fast()
    {
        ScanDrive(); // warm the file-system cache and the JIT
        var best = TimeSpan.MaxValue;
        SpaceIndex ix = null!;
        CountingGuard guard = null!;
        for (int run = 0; run < 3; run++)
        {
            var sw = Stopwatch.StartNew();
            (ix, guard, _) = ScanDrive();
            if (sw.Elapsed < best) best = sw.Elapsed;
        }
        Assert.Equal(RealGuardPc.FileCount, ix.Summary.Files);
        var line = $"real SafetyGuard sandbox {ix.Summary.Files:N0} files / {ix.Summary.Folders:N0} folders: {best.TotalMilliseconds:N0} ms " +
                   $"({ix.Summary.Files / best.TotalSeconds:N0} files/s), {guard.ClassifyCalls:N0} classify + {guard.CloudCalls:N0} cloud calls";
        _out.WriteLine(line);
        Perf.Record(line);
        // Folders are asked once (plus a probe for the few blocked as a whole); files and protected contents never.
        Assert.True(guard.ClassifyCalls <= ix.Summary.Folders + 40, line);
        Assert.Equal(0, guard.CloudCalls); // the map scan doesn't skip cloud folders, so it never asks
        // Measured ~0.1 s on the dev PC (the fake guard: ~0.03 s); the budget leaves room for a slow CI box.
        Assert.True(best < TimeSpan.FromSeconds(3), line);
    }
}

/// <summary>Real guard with call counters, so tests can see how often the scan asks it.</summary>
internal sealed class CountingGuard(ISafetyGuard inner) : ISafetyGuard
{
    public int ClassifyCalls, CloudCalls;
    public SafetyVerdict ClassifyPath(string path) { Interlocked.Increment(ref ClassifyCalls); return inner.ClassifyPath(path); }
    public SafetyVerdict Check(PlanItem item) => inner.Check(item);
    public bool IsInCloudRoot(string path) { Interlocked.Increment(ref CloudCalls); return inner.IsInCloudRoot(path); }
    public bool IsInsideProjectOrAppFolder(string path, out string? why) => inner.IsInsideProjectOrAppFolder(path, out why);
}

/// <summary>
/// A persistent ~20k-file sandbox PC under testbed\spacemap\realguard-pc (Minimal profile: one drive, no OneDrive).
/// Built once and kept between runs, like the bench tree.
/// </summary>
internal static class RealGuardPc
{
    public static readonly string Root = Path.Combine(TestDir.AreaRoot, "realguard-pc");
    private static readonly string Marker = Path.Combine(TestDir.AreaRoot, "realguard-pc.v2.ok");
    private static readonly object Gate = new();

    public const int DownloadsFiles = 8 * 25 * 40;
    private const int PictureFiles = 10 * 10 * 40, ProjectFiles = 20 * 5 * 40, UserAppFiles = 1000, OtherAccountFiles = 40, WindowsFiles = 50 * 40, AppFiles = 800;
    public const int FileCount = DownloadsFiles + PictureFiles + ProjectFiles + 20 /* package.json */ + OtherAccountFiles + UserAppFiles + WindowsFiles + AppFiles;

    public static SandboxKnownLocations Ensure()
    {
        lock (Gate)
        {
            var loc = new SandboxKnownLocations(Root, SandboxProfile.Minimal);
            if (File.Exists(Marker) && Directory.Exists(Root)) return loc;
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            var bytes = new byte[512];
            void Files(string dir, int count, string ext)
            {
                Directory.CreateDirectory(dir);
                for (int f = 0; f < count; f++) File.WriteAllBytes(Path.Combine(dir, $"file {f:00}.{ext}"), bytes);
            }
            void Grid(string root, string a, int outer, string b, int inner, int perFolder, string ext)
            {
                for (int i = 0; i < outer; i++)
                    for (int j = 0; j < inner; j++)
                        Files(Path.Combine(root, $"{a} {i:00}", $"{b} {j:00}"), perFolder, ext);
            }

            Grid(loc.Downloads, "batch", 8, "set", 25, 40, "bin");
            Grid(loc.Pictures, "album", 10, "day", 10, 40, "jpg");
            for (int p = 0; p < 20; p++)
            {
                var proj = Path.Combine(loc.Documents, "Projects", $"proj {p:00}");
                Directory.CreateDirectory(proj);
                File.WriteAllText(Path.Combine(proj, "package.json"), "{}");
                Files(Path.Combine(proj, "src"), 40, "cs");
                for (int d = 1; d < 5; d++) Files(Path.Combine(proj, $"lib {d}"), 40, "cs");
            }
            for (int i = 0; i < 10; i++) Files(Path.Combine(loc.AppInstallDirs[1], $"part {i:00}"), 100, "dll");
            Files(Path.Combine(Path.GetDirectoryName(loc.UserProfile)!, "guest", "Documents"), OtherAccountFiles, "txt");
            for (int i = 0; i < 50; i++) Files(Path.Combine(loc.WindowsDir, "System32", $"dir {i:00}"), 40, "dll");
            for (int i = 0; i < 20; i++) Files(Path.Combine(loc.AppInstallDirs[0], $"module {i:00}"), 40, "dat");
            File.WriteAllText(Marker, $"{FileCount} files");
            return loc;
        }
    }
}
