using PcTidy.Core;
using PcTidy.Features.Search.Index;
using PcTidy.Features.Search.Query;
using Xunit;
using Xunit.Abstractions;

namespace PcTidy.Features.Search.Tests;

/// <summary>The own index built over a generated fake PC with the REAL SafetyGuard (sandbox composition).</summary>
[Collection("search-testbed")]
public class IndexBuildTests(ITestOutputHelper log)
{
    private static IndexSnapshot BuildAll(SearchSandbox s, bool appData = false)
    {
        var builder = new IndexBuilder(s.Host.Walker, s.Host.Safety, s.K, appData);
        var roots = IndexRoots.Resolve(s.K, new SearchSettings());
        var snapshot = IndexSnapshot.Empty;
        foreach (var r in roots) snapshot = snapshot.With(builder.Build(r, SearchSandbox.Now, null, CancellationToken.None));
        return snapshot;
    }

    private static IEnumerable<string> AllPaths(IndexSnapshot snapshot)
        => snapshot.Segments.SelectMany(s => Enumerable.Range(0, s.Count).Select(s.FullPath));

    [Fact]
    public void Default_roots_are_the_profile_and_other_drives()
    {
        using var s = new SearchSandbox("roots");
        var roots = IndexRoots.Resolve(s.K, new SearchSettings());
        Assert.Equal(new[] { PathUtil.Normalize(s.K.UserProfile), s.D }, roots);

        using var m = new SearchSandbox("roots-min", SandboxProfile.Minimal);
        Assert.Equal(new[] { PathUtil.Normalize(m.K.UserProfile) }, IndexRoots.Resolve(m.K, new SearchSettings()));

        // User roots: nested ones fold into the outer one, missing ones are ignored.
        var custom = new SearchSettings { IndexRoots = { s.K.Downloads, s.K.UserProfile, Path.Combine(s.Root, "nope") } };
        Assert.Equal(new[] { PathUtil.Normalize(s.K.UserProfile) }, IndexRoots.Resolve(s.K, custom));
    }

    [Fact]
    public void Testbed_index_never_follows_links_and_skips_protected_areas()
    {
        using var s = new SearchSandbox("build", SandboxProfile.Default, generate: true);
        var snapshot = BuildAll(s);
        var paths = AllPaths(snapshot).ToList();
        log.WriteLine($"{paths.Count:N0} entries, {snapshot.ArrayBytes:N0} bytes ({snapshot.BytesPerEntry:N1} B/entry)");
        Assert.True(paths.Count > 500);

        // Ordinary user files are there.
        Assert.Contains(paths, p => p.StartsWith(s.K.Downloads + "\\", StringComparison.OrdinalIgnoreCase) && p.EndsWith(".iso", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(paths, p => p.StartsWith(s.D + "\\Photos", StringComparison.OrdinalIgnoreCase));

        // Junctions are listed as entries, never entered (one points at a folder elsewhere, one is a loop).
        var linked = Path.Combine(s.K.Downloads, "Linked Projects");
        Assert.Contains(linked, paths, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(paths, p => p.StartsWith(linked + "\\", StringComparison.OrdinalIgnoreCase));
        var loop = Path.Combine(s.K.Downloads, "Old stuff", "loop back");
        Assert.DoesNotContain(paths, p => p.StartsWith(loop + "\\", StringComparison.OrdinalIgnoreCase));

        // Protected areas: game libraries, PC Tidy's data, AppData (off by default) are skipped with their contents.
        Assert.DoesNotContain(paths, p => p.Contains(@"\SteamLibrary\steamapps", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(paths, p => PathUtil.IsUnder(p, s.K.DataRoot));
        Assert.DoesNotContain(paths, p => PathUtil.IsUnder(p, s.K.LocalAppData));
        Assert.DoesNotContain(paths, p => PathUtil.IsUnder(p, s.K.WindowsDir) || PathUtil.IsUnder(p, s.K.ProgramFiles));
        Assert.True(snapshot.Segments.Sum(x => x.SkippedFolders) > 0);

        // Every entry's parent comes before it (the invariant the query engine relies on).
        foreach (var seg in snapshot.Segments)
            for (int i = 0; i < seg.Count; i++) Assert.True(seg.ParentOf(i) < i);
    }

    [Fact]
    public void AppData_can_be_included()
    {
        using var s = new SearchSandbox("appdata", SandboxProfile.Minimal, generate: true);
        var without = AllPaths(BuildAll(s)).ToList();
        var with = AllPaths(BuildAll(s, appData: true)).ToList();
        Assert.DoesNotContain(without, p => PathUtil.IsUnder(p, s.K.RoamingAppData));
        Assert.Contains(with, p => PathUtil.IsUnder(p, s.K.RoamingAppData));
    }

    [Fact]
    public void Search_over_the_testbed_finds_what_a_walk_finds()
    {
        using var s = new SearchSandbox("search", SandboxProfile.Default, generate: true);
        var snapshot = BuildAll(s);
        var jpgOnDisk = Directory.EnumerateFiles(s.K.Downloads, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System })
            .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var r = QueryEngine.Search(snapshot, QueryParser.Parse($"ext:jpg path:\"{s.K.Downloads}\\\""));
        var found = Enumerable.Range(0, r.Count).Select(i => r[i].FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Subset(jpgOnDisk, found);
        Assert.Superset(jpgOnDisk, found);
    }

    [Fact]
    public void Odd_names_long_paths_and_unicode_survive()
    {
        using var s = new SearchSandbox("odd", SandboxProfile.Minimal);
        var deep = s.K.Downloads;
        for (int i = 0; i < 12; i++) deep = Path.Combine(deep, "a fairly long folder name " + i);
        s.Put(Path.Combine(deep, "très_日本語 file.txt"), "x");
        s.Put(Path.Combine(s.K.Downloads, "emoji 😀.md"), "x");
        var snapshot = BuildAll(s);
        var hits = QueryEngine.Search(snapshot, QueryParser.Parse("日本語"));
        var hit = Assert.Single(Enumerable.Range(0, hits.Count).Select(i => hits[i]));
        Assert.True(hit.FullPath.Length > 260);
        Assert.Equal(Path.Combine(deep, "très_日本語 file.txt"), hit.FullPath);
        Assert.Equal(1, QueryEngine.Search(snapshot, QueryParser.Parse("😀")).Count);
    }
}
