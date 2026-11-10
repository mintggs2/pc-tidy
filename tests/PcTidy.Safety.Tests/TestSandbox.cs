using System.Diagnostics;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Safety;

namespace PcTidy.Safety.Tests;

/// <summary>
/// A fresh fake PC under testbed\safety-fix\&lt;id&gt; for one test. Creates the folders the rules look at and removes
/// everything (links first, without following them) when disposed. Nothing outside the testbed is ever written.
/// </summary>
public sealed class TestSandbox : IDisposable
{
    public static readonly string TestbedRoot = FindTestbed();

    public string Root { get; }
    public SandboxKnownLocations K { get; }
    public AppSettings Settings { get; }
    public SafetyGuard Guard { get; }
    private readonly List<string> _links = new();

    public TestSandbox(SandboxProfile? profile = null)
    {
        Root = Path.Combine(TestbedRoot, Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Root);
        K = new SandboxKnownLocations(Root, profile ?? SandboxProfile.Default);
        Settings = new AppSettings { DataRoot = K.DataRoot };
        foreach (var dir in new[]
                 {
                     K.WindowsDir, K.WindowsTemp, K.ProgramFiles, K.ProgramFilesX86, K.ProgramData, K.UserTemp, K.Desktop, K.Documents,
                     K.Downloads, K.Pictures, K.Videos, K.Music, K.ClaudeDir, K.CodexDir, K.DataRoot, K.RoamingAppData,
                 })
            Directory.CreateDirectory(dir);
        foreach (var dir in K.CloudRoots.Concat(K.AppInstallDirs).Concat(K.GameInstallDirs)) Directory.CreateDirectory(dir);
        foreach (var lib in K.SteamLibraries) Directory.CreateDirectory(Path.Combine(lib, "steamapps"));
        foreach (var d in K.FixedDrives) Directory.CreateDirectory(d);
        Guard = new SafetyGuard(K, Settings);
    }

    /// <summary>Creates a file (and its folder) inside the sandbox and returns its path.</summary>
    public string File(string path, string content = "x")
    {
        AssertInside(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    public string Dir(string path)
    {
        AssertInside(path);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Creates a directory junction with mklink /J (sandbox only). A name ending in a dot or space (e.g. "lnk.") is
    /// made as "lnk" and then renamed through the literal \\?\ form, because mklink trims such names.
    /// </summary>
    public string Junction(string link, string target)
    {
        AssertInside(link); AssertInside(target);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        var made = Path.Combine(Path.GetDirectoryName(link)!, Path.GetFileName(link).TrimEnd('.', ' '));
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{made}\" \"{target}\"")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        p.WaitForExit(15000);
        if (!Directory.Exists(made)) throw new InvalidOperationException("mklink /J failed: " + p.StandardError.ReadToEnd());
        if (made != link) Directory.Move(Literal(made), Literal(link));
        _links.Add(link);
        return link;
    }

    /// <summary>Creates a file whose name Windows' normal path rules would change ("x.", "nul"), through \\?\.</summary>
    public string OddFile(string path, string content = "x")
    {
        AssertInside(path);
        Directory.CreateDirectory(Literal(Path.GetDirectoryName(path)!));
        System.IO.File.WriteAllText(Literal(path), content);
        return path;
    }

    /// <summary>Creates a folder whose name Windows' normal path rules would change ("folder.", "nul"), through \\?\.</summary>
    public string OddDir(string path)
    {
        AssertInside(path);
        Directory.CreateDirectory(Literal(path));
        return path;
    }

    /// <summary>The \\?\ form of a sandbox path (read literally: trailing dots/spaces and reserved names kept).</summary>
    public static string Literal(string path) => @"\\?\" + path;

    public PlanItem Quarantine(string path, string feature = "cleanup", string? carveOut = null)
        => new() { Kind = ActionKind.QuarantinePath, FeatureId = feature, Description = "test", SourcePath = path, CarveOutId = carveOut };

    public PlanItem Move(string from, string to, string feature = "organize", ActionKind kind = ActionKind.MovePath)
        => new() { Kind = kind, FeatureId = feature, Description = "test", SourcePath = from, DestinationPath = to };

    private void AssertInside(string path)
    {
        // Literal check (no trimming): the name may end in a dot or space on purpose; ".." is never allowed.
        if (path.Contains(@"\..", StringComparison.Ordinal) || path.Contains('/')
            || !path.StartsWith(Root + "\\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test tried to write outside its sandbox: " + path);
    }

    public void Dispose()
    {
        try
        {
            // Everything through \\?\ so odd names ("lnk.", "nul") are removed too. Links first: deleting a
            // junction removes the link only, never what it points at.
            foreach (var link in _links) if (Directory.Exists(Literal(link))) Directory.Delete(Literal(link));
            var root = Literal(Root);
            foreach (var f in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
                System.IO.File.SetAttributes(f, FileAttributes.Normal | (Directory.Exists(f) ? FileAttributes.Directory : 0));
            Directory.Delete(root, recursive: true);
        }
        catch (IOException) { /* best effort: a leftover test folder is harmless */ }
        catch (UnauthorizedAccessException) { }
    }

    private static string FindTestbed()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (System.IO.File.Exists(Path.Combine(dir.FullName, "PcTidy.sln")))
            {
                var testbed = Path.Combine(dir.FullName, "testbed", "safety-fix");
                Directory.CreateDirectory(testbed);
                return testbed;
            }
        }
        throw new InvalidOperationException("Couldn't find the solution folder (PcTidy.sln) above " + AppContext.BaseDirectory);
    }
}
