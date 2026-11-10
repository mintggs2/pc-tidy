using System.Runtime.InteropServices;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Duplicates.Model;
using PcTidy.Hosting;
using PcTidy.TestbedGen;
using PcTidy.TestbedGen.Manifest;

namespace PcTidy.Features.Duplicates.Tests;

/// <summary>Everything this test project writes lives under testbed\duplicates\.</summary>
internal static class Area
{
    public static readonly string Root = Path.Combine(FindCodeRoot(), "testbed", "duplicates");

    public static string NewFolder(string name)
    {
        var p = Path.Combine(Root, $"{name}-{Guid.NewGuid().ToString("N")[..6]}");
        Directory.CreateDirectory(p);
        return p;
    }

    public static string FindCodeRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "PcTidy.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("PcTidy.sln not found above " + AppContext.BaseDirectory);
    }
}

/// <summary>A generated fake PC (TestbedGen) with the real composition root: real SafetyGuard, Executor and Undo.</summary>
public sealed class GeneratedPc : IDisposable
{
    public GeneratedPc(string name, SandboxProfile profile)
    {
        Profile = profile;
        Root = Path.Combine(Area.Root, $"{name}-{Guid.NewGuid().ToString("N")[..6]}");
        Info = TestbedBuilder.Generate(Root, TestbedSize.Small, profile);
        Host = PcTidyHost.CreateSandbox(Root, profile);
    }

    public SandboxProfile Profile { get; }
    public string Root { get; }
    public TestbedInfo Info { get; }
    public PcTidyHost Host { get; }
    public IKnownLocations K => Host.Locations;

    public ScanContext Context(ScanDepth depth = ScanDepth.Quick, IReadOnlyList<string>? roots = null) => Host.CreateScanContext(depth, roots);

    public string Abs(string relative) => Info.Abs(relative);

    /// <summary>Absolute paths of the hand-placed files carrying all these tags.</summary>
    public IReadOnlyList<string> Tagged(params string[] tags) => Info.Tagged(tags).Select(e => Info.Abs(e.RelativePath)).ToList();

    /// <summary>Differences vs. a manifest, ignoring PC Tidy's own data folder and per-drive quarantine (journal, quarantine).</summary>
    public ManifestDiff DiffFrom(TestbedManifest before)
    {
        var diff = TestbedBuilder.Compare(before, TestbedBuilder.Manifest(Root));
        return new ManifestDiff(diff.Differences.Where(d => !IsPcTidyOwn(d.Path)).ToList());
    }

    private static bool IsPcTidyOwn(string relative)
        => relative.StartsWith("Data\\", StringComparison.OrdinalIgnoreCase) || relative.Equals("Data", StringComparison.OrdinalIgnoreCase)
           || relative.Contains("PC Tidy Quarantine", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        Host.Dispose();
        try { TestbedBuilder.Delete(Root); } catch { /* disposable testbed */ }
    }
}

/// <summary>One read-only Default-profile scan shared by the detection tests (nothing in it is changed).</summary>
public sealed class DefaultPcFixture : IDisposable
{
    public DefaultPcFixture()
    {
        Pc = new GeneratedPc("default", SandboxProfile.Default);
        Cache = new Hashing.HashCache();
        Report = new DuplicatesEngine(Cache).ScanAsync(Pc.Context(ScanDepth.Quick)).GetAwaiter().GetResult();
        Before = TestbedBuilder.Manifest(Pc.Root);
    }

    public GeneratedPc Pc { get; }
    public Hashing.HashCache Cache { get; }
    public DuplicatesReport Report { get; }
    public TestbedManifest Before { get; }

    public DuplicateGroup GroupWith(string path)
        => Report.ExactGroups.Single(g => g.Copies.Any(c => Same(c.Path, path)));

    public static bool Same(string a, string b) => string.Equals(PathUtil.Normalize(a), PathUtil.Normalize(b), StringComparison.OrdinalIgnoreCase);

    public void Dispose() => Pc.Dispose();
}

/// <summary>Small hand-made sandbox for focused tests (hard links, folders, master compare).</summary>
public sealed class HandMadePc : IDisposable
{
    public HandMadePc(string name, SandboxProfile? profile = null)
    {
        Root = Area.NewFolder(name);
        var p = profile ?? SandboxProfile.Minimal;
        var k = new SandboxKnownLocations(Root, p);
        foreach (var d in new[] { k.Desktop, k.Documents, k.Downloads, k.Pictures, k.Videos, k.Music, k.LocalAppData, k.RoamingAppData, k.ProgramFiles, k.WindowsDir })
            Directory.CreateDirectory(d);
        Host = PcTidyHost.CreateSandbox(Root, p);
    }

    public string Root { get; }
    public PcTidyHost Host { get; }
    public IKnownLocations K => Host.Locations;

    public ScanContext Context(ScanDepth depth = ScanDepth.Quick, IReadOnlyList<string>? roots = null) => Host.CreateScanContext(depth, roots);

    /// <summary>Writes a file with given bytes and an old modified time (so junk age rules apply).</summary>
    public string Put(string path, byte[] bytes, int daysOld = 30)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        var t = DateTime.UtcNow.AddDays(-daysOld);
        File.SetLastWriteTimeUtc(path, t);
        File.SetCreationTimeUtc(path, t);
        return path;
    }

    public static byte[] Bytes(int seed, int length)
    {
        var b = new byte[length];
        new Random(seed).NextBytes(b);
        return b;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string newFile, string existingFile, IntPtr security);

    public static void HardLink(string newFile, string existing)
    {
        if (!CreateHardLinkW(newFile, existing, IntPtr.Zero)) throw new IOException("CreateHardLink failed: " + Marshal.GetLastWin32Error());
    }

    public void Dispose()
    {
        Host.Dispose();
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

[CollectionDefinition("default-pc")]
public sealed class DefaultPcCollection : ICollectionFixture<DefaultPcFixture> { }
