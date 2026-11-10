using PcTidy.Core;
using PcTidy.TestbedGen.Generation;
using PcTidy.TestbedGen.Io;
using PcTidy.TestbedGen.Manifest;

namespace PcTidy.TestbedGen;

/// <summary>
/// In-process API for tests: generate a fake PC, take hash manifests, compare them, hold files locked.
/// Typical end-to-end test:
/// <code>
/// var tb = TestbedBuilder.Generate(dir);                       // or (dir, TestbedSize.Small, SandboxProfile.Minimal)
/// var before = TestbedBuilder.Manifest(dir);
/// ... run a feature against tb.Locations, then undo it ...
/// Assert.True(TestbedBuilder.Compare(before, TestbedBuilder.Manifest(dir)).Identical);
/// </code>
/// </summary>
public static class TestbedBuilder
{
    /// <summary>Creates the fake PC under <paramref name="root"/> (empty/missing folder, or an older testbed with Force).</summary>
    public static TestbedInfo Generate(string root, TestbedSize size = TestbedSize.Small, SandboxProfile? profile = null,
        TestbedOptions? options = null, CancellationToken ct = default)
        => TestbedGenerator.Generate(root, size, profile ?? SandboxProfile.Default, options ?? new TestbedOptions(), ct);

    /// <summary>Every file (size, XXH3, attributes, created/modified), folder and junction target under the root.</summary>
    public static TestbedManifest Manifest(string root, CancellationToken ct = default) => ManifestBuilder.Build(root, ct);

    public static ManifestDiff Compare(TestbedManifest before, TestbedManifest after, ManifestCompareOptions? options = null)
        => ManifestComparer.Compare(before, after, options ?? ManifestCompareOptions.Default);

    /// <summary>Differences that touch a canary file or anything inside a canary tree.</summary>
    public static IReadOnlyList<ManifestDifference> CanaryViolations(TestbedManifest before, TestbedManifest after,
        IEnumerable<Canary> canaries, ManifestCompareOptions? options = null)
    {
        var list = canaries.ToList();
        return Compare(before, after, options).Differences
            .Where(d => list.Any(k => k.IsTree ? IsUnderRelative(d.Path, k.RelativePath)
                                               : string.Equals(d.Path, k.RelativePath, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>Holds an exclusive handle (no sharing) until disposed: the file is "in use".</summary>
    public static IDisposable LockFile(string path)
        => new FileStream(LongPath.Ext(path), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

    /// <summary>Deletes a testbed made by this tool (refuses any other folder).</summary>
    public static void Delete(string root) => TestbedCleaner.Delete(root);

    /// <summary>Reads Sim\canaries.tsv of a generated testbed.</summary>
    public static IReadOnlyList<Canary> LoadCanaries(string root)
        => ReadTsv(root, TestbedGenerator.CanariesFile)
            .Select(f => new Canary(f[2], Enum.Parse<CanaryTier>(f[0]), f[1] == "tree", f.Length > 3 ? f[3] : ""))
            .ToList();

    /// <summary>Reads Sim\placeholders.txt: relative paths of simulated OneDrive online-only files.</summary>
    public static IReadOnlyList<string> LoadPlaceholders(string root)
        => ReadTsv(root, TestbedGenerator.PlaceholdersFile).Select(f => f[0]).ToList();

    /// <summary>Self-check of a generated testbed: every catalogued path, canary and placeholder exists, placeholders
    /// carry the Offline attribute, junctions are junctions pointing where junctions.tsv says. Empty list = healthy.</summary>
    public static IReadOnlyList<string> Verify(string root)
    {
        root = PathUtil.Normalize(root);
        var problems = new List<string>();
        bool Exists(string rel) => File.Exists(LongPath.Ext(Path.Combine(root, rel))) || Directory.Exists(LongPath.Ext(Path.Combine(root, rel)));
        foreach (var f in ReadTsv(root, TestbedGenerator.CatalogFile))
            if (!Exists(f[0])) problems.Add("missing catalogued " + f[0]);
        foreach (var k in LoadCanaries(root))
            if (!Exists(k.RelativePath)) problems.Add("missing canary " + k.RelativePath);
        foreach (var p in LoadPlaceholders(root))
        {
            var ext = LongPath.Ext(Path.Combine(root, p));
            if (!File.Exists(ext)) problems.Add("missing placeholder " + p);
            else if (!CloudFile.IsPlaceholder(File.GetAttributes(ext))) problems.Add("placeholder without Offline attribute " + p);
        }
        foreach (var j in ReadTsv(root, TestbedGenerator.JunctionsFile))
        {
            var link = new DirectoryInfo(LongPath.Ext(Path.Combine(root, j[0])));
            var expected = Path.Combine(root, j[1]);
            if (!link.Exists || !link.Attributes.HasFlag(FileAttributes.ReparsePoint)) problems.Add("not a junction " + j[0]);
            else if (!string.Equals(LongPath.Strip(link.LinkTarget ?? ""), expected, StringComparison.OrdinalIgnoreCase))
                problems.Add($"junction {j[0]} points to {link.LinkTarget}, expected {expected}");
        }
        return problems;
    }

    private static IEnumerable<string[]> ReadTsv(string root, string file)
        => File.ReadLines(Path.Combine(root, TestbedGenerator.SimFolder, file))
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split('\t'));

    private static bool IsUnderRelative(string path, string folder)
        => path.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase) || path.Equals(folder, StringComparison.OrdinalIgnoreCase);
}
