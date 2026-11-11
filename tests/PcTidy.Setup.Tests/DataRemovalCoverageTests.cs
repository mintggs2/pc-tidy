using System.Text.RegularExpressions;
using PcTidy.Setup.Install;

namespace PcTidy.Setup.Tests;

/// <summary>
/// 3.1.1 review M1: "delete my data" must know every folder and file PC Tidy itself makes directly in its data folder,
/// or the unknown entry stays and so does the folder. These read PC Tidy's own source (no PC is touched) to find every
/// <c>Path.Combine(&lt;data root&gt;, "name")</c> and every section's folder id, and check each is in
/// <see cref="DataRemovalPlanner.OwnNames"/>. A new data folder in the app without an entry here fails the build.
/// </summary>
[Collection("setup")]
public class DataRemovalCoverageTests
{
    private static readonly string Src = Path.Combine(SetupBed.FindCodeRoot(), "src");

    private static readonly Regex DataRootChild = new(
        @"Path\.Combine\(\s*(?<root>[\w.]*(?:[Dd]ataRoot|defaultRoot)(?:\(\))?)\s*,\s*(?<arg>""[^""]*""|[\w.]+)",
        RegexOptions.Compiled);

    private static readonly Regex Const = new(@"const\s+string\s+(?<name>\w+)\s*=\s*(?<value>""[^""]*""|[\w.]+)\s*;", RegexOptions.Compiled);

    private static IEnumerable<string> SourceFiles()
        => Directory.EnumerateFiles(Src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase) && !f.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase));

    private sealed record Constant(string File, string Name, string Value);

    private static string ProjectOf(string file)
    {
        var rel = Path.GetRelativePath(Src, file);
        return rel.Split(Path.DirectorySeparatorChar)[0];
    }

    /// <summary>A literal, or a const (same file first, then the same project, then anywhere if only one has that name).</summary>
    private static string? Resolve(string arg, string file, IReadOnlyList<Constant> consts, int depth = 0)
    {
        if (arg.StartsWith('"')) return arg.Trim('"');
        if (depth > 5) return null;
        var name = arg.Split('.').Last();
        var owner = arg.Contains('.') ? arg.Split('.')[^2] : null;
        var named = consts.Where(c => c.Name == name).ToList();
        if (owner is not null)
        {
            // BoostSettings.FeatureKey: prefer the file that declares that type.
            var typed = named.Where(c => File.ReadAllText(c.File).Contains($"class {owner}", StringComparison.Ordinal)).ToList();
            if (typed.Count > 0) named = typed;
        }
        var pick = named.FirstOrDefault(c => c.File == file)
                   ?? named.Where(c => ProjectOf(c.File) == ProjectOf(file)).Take(2).ToList() switch { [var one] => one, _ => null }
                   ?? (named.Count == 1 ? named[0] : null);
        return pick is null ? null : Resolve(pick.Value, pick.File, consts, depth + 1);
    }

    [Fact]
    public void Every_entry_PC_Tidy_makes_directly_in_its_data_folder_is_on_the_delete_my_data_list()
    {
        var files = SourceFiles().ToList();
        var consts = files.SelectMany(f => Const.Matches(File.ReadAllText(f)).Select(m => new Constant(f, m.Groups["name"].Value, m.Groups["value"].Value))).ToList();
        var found = new List<(string Name, string Where)>();
        var unresolved = new List<string>();
        foreach (var f in files)
        {
            foreach (Match m in DataRootChild.Matches(File.ReadAllText(f)))
            {
                var name = Resolve(m.Groups["arg"].Value, f, consts);
                if (name is null) unresolved.Add($"{Path.GetRelativePath(Src, f)}: {m.Value}");
                else found.Add((name, Path.GetRelativePath(Src, f)));
            }
        }

        Assert.True(unresolved.Count == 0, "Couldn't work out the name these use (make it a const or a literal):\n" + string.Join("\n", unresolved));
        Assert.True(found.Count >= 20, $"Only {found.Count} data-folder entries were found, so the source scan has gone wrong.");
        var missing = found.Where(x => !DataRemovalPlanner.OwnNames.Contains(x.Name)).Select(x => $"{x.Name}  ({x.Where})").Distinct().ToList();
        Assert.True(missing.Count == 0, "Not on DataRemovalPlanner.OwnNames, so \"delete my data\" would leave them behind:\n" + string.Join("\n", missing));

        // The ones the review named, and the temp/backup names the settings save makes.
        foreach (var n in new[] { "scan-checkpoints", "updates", "measure", "settings.json.tmp", "settings.json.bak" })
            Assert.Contains(n, DataRemovalPlanner.OwnNames);
    }

    [Fact]
    public void Every_sections_own_folder_id_is_on_the_delete_my_data_list()
    {
        var featureFiles = SourceFiles().Where(f => ProjectOf(f).StartsWith("PcTidy.Features.", StringComparison.Ordinal)).ToList();
        var ids = featureFiles.SelectMany(f => Regex.Matches(File.ReadAllText(f), @"const\s+string\s+(?:FeatureId|FeatureKey)\s*=\s*""(?<v>[^""]+)""")
                .Select(m => m.Groups["v"].Value))
            .Distinct().ToList();
        Assert.True(ids.Count >= 15, $"Only {ids.Count} section ids were found, so the source scan has gone wrong.");
        Assert.All(ids, id => Assert.True(DataRemovalPlanner.OwnNames.Contains(id), $"Section folder '{id}' isn't on DataRemovalPlanner.OwnNames."));
    }

    [Fact]
    public async Task Delete_my_data_removes_scan_checkpoints_and_updates_and_then_the_data_folder_itself()
    {
        using var bed = new SetupBed("data-checkpoints");
        var (_, payload, _) = bed.MakeSetupExe();
        Assert.True((await bed.Install(payload)).Ok);
        var root = bed.Env.DataRoot;
        Directory.CreateDirectory(Path.Combine(root, "scan-checkpoints"));
        File.WriteAllText(Path.Combine(root, "scan-checkpoints", "cleanup.json"), """{"Format":2,"Sections":[]}""");
        Directory.CreateDirectory(Path.Combine(root, "updates"));
        File.WriteAllText(Path.Combine(root, "updates", "PC-Tidy-Setup-9.9.9.exe"), "fake installer");
        Directory.CreateDirectory(Path.Combine(root, "measure"));
        File.WriteAllText(Path.Combine(root, "settings.json"), "{}");

        var plan = new Uninstaller(bed.Env).PlanDataRemoval();
        Assert.Contains(Path.Combine(root, "scan-checkpoints"), plan.Delete);
        Assert.Contains(Path.Combine(root, "updates"), plan.Delete);
        Assert.Empty(plan.Keep);

        var result = await bed.Uninstall(keepData: false);
        Assert.True(result.Ok, result.Message + string.Join("\n", result.Notes));
        Assert.False(Directory.Exists(Path.Combine(root, "scan-checkpoints")));
        Assert.False(Directory.Exists(root)); // nothing unknown was in it, so the folder went too
    }
}
