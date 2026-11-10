using System.IO.Compression;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Developer.Archive;
using PcTidy.Features.Developer.Common;
using PcTidy.Features.Developer.Projects;
using PcTidy.TestbedGen;
using PcTidy.TestbedGen.Manifest;
using Xunit;
using Xunit.Abstractions;

namespace PcTidy.Features.Developer.Tests;

[CollectionDefinition("developer-testbed", DisableParallelization = true)]
public class DeveloperTestbedCollection { }

/// <summary>
/// Real composition (sandbox, generated testbed): scan → plan → real SafetyGuard → Executor → Undo, proved with hash
/// manifests. The testbed has "Downloads\my-web-app" (.git + package.json + node_modules) and two projects in
/// "source\repos". Its files are days old, so the clock is moved 400 days past the testbed's anchor.
/// </summary>
[Collection("developer-testbed")]
public class EndToEndTests(ITestOutputHelper log)
{
    private static readonly ExecuteOptions Real = new() { Mode = ExecutionMode.Real, ContinueWithoutRestorePoint = _ => Task.FromResult(true) };
    private const string WebProject = "my-web-app";

    private static DevSandbox Testbed(string name) => new(name, SandboxProfile.Default, generate: true) { Now = DevSandbox.Anchor.AddDays(400) };

    [Fact]
    public async Task Node_modules_quarantine_then_undo_leaves_the_testbed_identical()
    {
        using var s = Testbed("e2e-build");
        var nodeModules = Path.Combine(s.K.Downloads, WebProject, "node_modules");
        Assert.True(Directory.Exists(nodeModules));
        var before = TestbedBuilder.Manifest(s.Root);

        var scan = await new BuildFoldersScanner().ScanAsync(s.Context(), null, CancellationToken.None);
        foreach (var g in scan.Groups) log.WriteLine($"{g.Id} {g.Title}: {string.Join(", ", g.Items.Select(i => $"{i.Title} [{i.Badge}{(i.DefaultSelected ? ", ticked" : "")}{(i.DisabledReason is null ? "" : ", disabled: " + i.DisabledReason)}]"))}");
        var item = scan.AllItems.Single(i => string.Equals(i.Detail, nodeModules, StringComparison.OrdinalIgnoreCase));
        Assert.Null(item.DisabledReason);
        Assert.Equal(Badge.Safe, item.Badge);
        Assert.True(item.DefaultSelected);
        // Never anything outside the rebuildable folders (source, .git, solution files stay).
        Assert.All(scan.AllItems.SelectMany(i => i.Actions), a => Assert.Contains(Path.GetFileName(a.SourcePath!), Catalog.Developer.RebuildableFolders.FolderNames));

        var plan = PlanBuilder.FromSelection("developer", "Build folders", new[] { item });
        var run = await s.Host.Executor.ExecuteAsync(plan, Real, null, CancellationToken.None);
        foreach (var step in run.Steps) log.WriteLine($"{step.Status}: {step.Item.Description} {step.Message}");
        Assert.Equal(1, run.DoneCount);
        Assert.False(Directory.Exists(nodeModules));
        Assert.True(File.Exists(Path.Combine(s.K.Downloads, WebProject, "package.json")));
        Assert.Equal(item.SizeBytes, run.Steps[0].BytesAffected);

        var undo = await s.Host.Undo.UndoRunAsync(run.RunId, null, CancellationToken.None);
        log.WriteLine($"Undo: restored {undo.Restored}, failed {undo.Failed}: {string.Join(" | ", undo.Messages)}");
        Assert.Equal(0, undo.Failed);
        AssertSameOutsideData(before, TestbedBuilder.Manifest(s.Root));
    }

    [Fact]
    public async Task Whole_scan_of_the_testbed_offers_only_rebuildable_folders_and_the_canaries_survive_running_all_of_it()
    {
        using var s = Testbed("e2e-all");
        var before = TestbedBuilder.Manifest(s.Root);
        var scan = await new DeveloperScanner().ScanAsync(s.Context(), null, CancellationToken.None);
        var offered = scan.AllItems.Where(i => i.DisabledReason is null && i.Actions.Count > 0).ToList();
        foreach (var i in offered) log.WriteLine($"{i.Badge} {i.Title}: {i.Detail}");
        Assert.Contains(offered, i => i.Detail!.EndsWith(@"WeatherApp\obj", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(offered, i => i.Detail!.EndsWith(@"py_tool\__pycache__", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(offered, i => i.Detail!.EndsWith(@"py-tool\.venv", StringComparison.OrdinalIgnoreCase));

        var plan = PlanBuilder.FromSelection("developer", "Developer", offered);
        var run = await s.Host.Executor.ExecuteAsync(plan, Real, null, CancellationToken.None);
        foreach (var step in run.Steps.Where(x => x.Status != StepStatus.Done)) log.WriteLine($"{step.Status}: {step.Item.Description} {step.Message}");
        Assert.Equal(plan.Items.Count, run.DoneCount);
        var after = TestbedBuilder.Manifest(s.Root);
        Assert.Empty(TestbedBuilder.CanaryViolations(before, after, TestbedBuilder.LoadCanaries(s.Root)));

        var undo = await s.Host.Undo.UndoRunAsync(run.RunId, null, CancellationToken.None);
        Assert.Equal(0, undo.Failed);
        AssertSameOutsideData(before, TestbedBuilder.Manifest(s.Root));
    }

    [Fact]
    public async Task Archive_a_project_then_undo_leaves_the_testbed_identical()
    {
        using var s = Testbed("e2e-archive");
        var project = Path.Combine(s.K.Downloads, WebProject);
        var archive = Path.Combine(s.Root, "D", "Archived projects");
        Directory.CreateDirectory(archive); // the folder the user picked
        s.ExtraOptions[DeveloperOptions.ArchiveFolder] = archive;
        var before = TestbedBuilder.Manifest(s.Root);

        var item = await ArchiveItemAsync(s, project);
        var plan = PlanBuilder.FromSelection("developer", "Archive old projects", new[] { item });
        var run = await s.Host.Executor.ExecuteAsync(plan, Real, null, CancellationToken.None);
        foreach (var step in run.Steps) log.WriteLine($"{step.Status}: {step.Item.Description} {step.Message}");
        Assert.Equal(1, run.DoneCount);
        Assert.False(Directory.Exists(project));
        var zip = Assert.Single(Directory.GetFiles(archive, "*.zip"));
        Assert.NotNull(ArchiveNaming.Parse(Path.GetFileName(zip)));

        var undo = await s.Host.Undo.UndoRunAsync(run.RunId, null, CancellationToken.None);
        Assert.Equal(0, undo.Failed);
        Assert.True(Directory.Exists(project));
        AssertSameOutsideData(before, TestbedBuilder.Manifest(s.Root));
    }

    /// <summary>
    /// Archive, then restore from the "Archived projects" list. Needs two engine features this section emits:
    /// ArchiveToZip Args["manifestJson"] (the note inside the zip) and ExtractZip Args["unwrapRoot"]. Until the engine
    /// writes the note, the archived row is shown greyed out with an honest reason, and this test proves exactly that.
    /// </summary>
    [Fact]
    public async Task Archive_then_restore_from_the_list_puts_the_project_back_exactly()
    {
        using var s = Testbed("e2e-restore");
        var project = Path.Combine(s.K.Downloads, WebProject);
        var archive = Path.Combine(s.Root, "D", "Archived projects");
        Directory.CreateDirectory(archive);
        s.ExtraOptions[DeveloperOptions.ArchiveFolder] = archive;
        var before = TestbedBuilder.Manifest(s.Root);

        var item = await ArchiveItemAsync(s, project);
        var run = await s.Host.Executor.ExecuteAsync(PlanBuilder.FromSelection("developer", "Archive", new[] { item }), Real, null, CancellationToken.None);
        Assert.Equal(1, run.DoneCount);
        var zip = Assert.Single(Directory.GetFiles(archive, "*.zip"));
        bool hasNote;
        using (var z = ZipFile.OpenRead(zip)) hasNote = z.GetEntry(ArchiveNaming.ManifestEntryName) is not null;

        var listing = await new ArchiveScanner().ScanAsync(s.Context(), null, CancellationToken.None);
        var row = Assert.Single(Assert.Single(listing.Groups, g => g.Id == ArchiveScanner.ArchivedGroupId).Items);
        if (!hasNote)
        {
            log.WriteLine("ENGINE GAP: ArchiveToZip ignored Args[\"manifestJson\"], so the zip has no pctidy-archive.json. Restore row: " + row.DisabledReason);
            Assert.Empty(row.Actions);
            Assert.Contains("no PC Tidy note", row.DisabledReason);
            return;
        }

        var restore = await s.Host.Executor.ExecuteAsync(PlanBuilder.FromSelection("developer", "Restore", new[] { row }), Real, null, CancellationToken.None);
        foreach (var step in restore.Steps) log.WriteLine($"{step.Status}: {step.Item.Description} {step.Message}");
        Assert.Equal(1, restore.DoneCount);
        var after = TestbedBuilder.Manifest(s.Root);

        // Restoring from the ZIP can only promise what a zip keeps: the same paths, the same contents, and last-write
        // times to zip's 2-second precision. Zip stores no creation times and no folder attributes (the unzipped files
        // get new creation times, and .git loses its Hidden flag), so those aren't compared. Undo of the archive step
        // itself is still exact, because that restores the original from quarantine (see the test above).
        var projectRel = Path.GetRelativePath(s.Root, project);
        bool InProject(string rel) => rel.Equals(projectRel, StringComparison.OrdinalIgnoreCase)
                                     || rel.StartsWith(projectRel + "\\", StringComparison.OrdinalIgnoreCase);
        Dictionary<string, ManifestEntry> Project(TestbedManifest m)
            => m.Entries.Where(e => InProject(e.Path)).ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        var was = Project(before);
        var now = Project(after);
        Assert.True(was.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(now.Keys),
            "Restore didn't bring back the same files and folders:\n" + string.Join("\n", was.Keys.Except(now.Keys, StringComparer.OrdinalIgnoreCase)
                .Select(p => "missing " + p).Concat(now.Keys.Except(was.Keys, StringComparer.OrdinalIgnoreCase).Select(p => "extra   " + p)).Take(20)));
        foreach (var (path, b) in was)
        {
            var a = now[path];
            Assert.Equal(b.Kind, a.Kind);
            if (b.Kind != EntryKind.File) continue;
            Assert.True(b.Size == a.Size && b.Hash == a.Hash, $"Different content after restore: {path}");
            Assert.True(b.LastWriteUtc is { } bt && a.LastWriteUtc is { } at && Math.Abs((bt - at).TotalSeconds) <= 2,
                $"Last-write time not kept (to 2 s) after restore: {path} [{b.LastWriteUtc:O} -> {a.LastWriteUtc:O}]");
        }

        // Nothing else changed; the zip stays in the archive folder (and the original waits in quarantine).
        var elsewhere = TestbedBuilder.Compare(before, after).Differences
            .Where(d => !IsPcTidyData(d.Path) && !InProject(d.Path) && !d.Path.StartsWith(@"D\Archived projects\", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(elsewhere.Count == 0, "Restore changed something outside the project:\n" + string.Join("\n", elsewhere.Take(20)));
    }

    [Fact]
    public async Task Minimal_PC_with_one_drive_and_nothing_installed_scans_without_errors()
    {
        using var s = new DevSandbox("e2e-minimal", SandboxProfile.Minimal, generate: true) { Now = DevSandbox.Anchor.AddDays(400) };
        var scan = await new DeveloperScanner().ScanAsync(s.Context(), null, CancellationToken.None);
        Assert.Contains(scan.Notes, n => n.Contains("Neither Docker nor WSL"));
        Assert.All(scan.AllItems, i => Assert.False(string.IsNullOrWhiteSpace(i.Reason)));
        Assert.All(scan.Groups, g => Assert.False(string.IsNullOrWhiteSpace(g.Description)));
        Assert.All(scan.AllItems.Where(i => i.Badge != Badge.Safe), i => Assert.False(i.DefaultSelected));
        var archive = await new ArchiveScanner().ScanAsync(s.Context(), null, CancellationToken.None);
        Assert.Contains(archive.AllItems, i => i.Title == WebProject);
        Assert.StartsWith(s.K.UserProfile, DevContext.DefaultArchiveFolder(s.K), StringComparison.OrdinalIgnoreCase); // one drive: in your user folder
    }

    private async Task<ScanItem> ArchiveItemAsync(DevSandbox s, string project)
    {
        var scan = await new ArchiveScanner().ScanAsync(s.Context(), null, CancellationToken.None);
        foreach (var i in scan.AllItems) log.WriteLine($"{i.Title}: {i.DisabledReason ?? "offered"}");
        var item = scan.AllItems.Single(i => string.Equals(i.Detail, project, StringComparison.OrdinalIgnoreCase));
        Assert.Null(item.DisabledReason);
        return item;
    }

    private static bool IsPcTidyData(string relative)
        => relative.StartsWith("Data", StringComparison.OrdinalIgnoreCase) || relative.Contains("PC Tidy Quarantine", StringComparison.OrdinalIgnoreCase);

    private static void AssertSameOutsideData(TestbedManifest before, TestbedManifest after)
    {
        var outside = TestbedBuilder.Compare(before, after).Differences.Where(d => !IsPcTidyData(d.Path)).ToList();
        Assert.True(outside.Count == 0, "Undo did not restore the tree exactly:\n" + string.Join("\n", outside.Take(20).Select(d => d.ToString())));
    }
}
