using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Search.Structure;
using PcTidy.TestbedGen;
using Xunit;
using Xunit.Abstractions;

namespace PcTidy.Features.Search.Tests;

/// <summary>Folder-structure suggestions: plan shapes with the REAL SafetyGuard, and run → undo → identical.</summary>
[Collection("search-testbed")]
public class StructureTests(ITestOutputHelper log)
{
    private static readonly ManualClock Clock = new(new DateTimeOffset(SearchSandbox.Now));

    /// <summary>A second drive with one folder of each kind, plus things that must never move.</summary>
    private static SearchSandbox Layout()
    {
        var s = new SearchSandbox("structure", SandboxProfile.Default);
        var d = s.D;
        var now = SearchSandbox.Now;
        s.Put(Path.Combine(d, "Camera", "IMG_0001.jpg"), new byte[5000], now.AddDays(-10));
        s.Put(Path.Combine(d, "Camera", "IMG_0002.jpg"), new byte[6000], now.AddDays(-12));
        s.Put(Path.Combine(d, "Movies", "Trip.mp4"), new byte[9000], now.AddDays(-800));
        s.Put(Path.Combine(d, "Old Projects", "plan.txt"), "plan", now.AddDays(-700));
        s.Put(Path.Combine(d, "Taxes 2024", "return.pdf"), new byte[3000], now.AddDays(-20));
        s.Put(Path.Combine(d, "Installers", "setup.exe"), new byte[4000], now.AddDays(-30));
        s.Put(Path.Combine(d, "Mixed", "a.docx"), new byte[100], now.AddDays(-5));
        s.Put(Path.Combine(d, "Mixed", "b.zip"), new byte[100], now.AddDays(-5));
        s.Put(Path.Combine(d, "Mixed", "c.bin"), new byte[100], now.AddDays(-5));
        s.Put(Path.Combine(d, "repo", ".git", "HEAD"), "ref: refs/heads/main", now.AddDays(-3));
        s.Put(Path.Combine(d, "repo", "main.c"), "int main(){}", now.AddDays(-3));
        s.Put(Path.Combine(d, "My Games", "Save", "slot1.sav"), new byte[100], now.AddDays(-3));
        s.Put(Path.Combine(d, "SteamLibrary", "steamapps", "common", "Game", "game.exe"), new byte[100], now.AddDays(-3));
        s.Put(Path.Combine(d, "Archive", "old.zip"), new byte[100], now.AddDays(-900)); // already part of the structure
        Directory.CreateDirectory(Path.Combine(d, "Empty"));
        // A junction to the Camera folder: listed, never entered, never moved.
        Junction(Path.Combine(d, "Camera link"), Path.Combine(d, "Camera"));
        return s;
    }

    /// <summary>mklink /J needs no admin rights (a test helper; features never create links).</summary>
    private static void Junction(string link, string target)
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        p.WaitForExit();
        Assert.True(Directory.Exists(link) && File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint), "junction not created");
    }

    private static StructureProposal ProposeD(SearchSandbox s, StructurePreset preset)
    {
        var planner = new StructurePlanner(s.K, s.Host.Safety, s.Host.Walker, Clock);
        var root = planner.DefaultRoots().Single(r => !r.IsDocuments);
        return planner.Propose(root, preset, CancellationToken.None);
    }

    private static string? DestOfOrNull(StructureProposal p, string name)
        => p.Moves.Any(m => m.Folder.Name == name) ? DestOf(p, name) : null;

    private static string? DestOf(StructureProposal p, string name)
        => p.Moves.Single(m => m.Folder.Name == name) is { DisabledReason: null, Destination: { } dest } ? Path.GetRelativePath(p.Root.Path, dest) : null;

    [Fact]
    public void Roots_are_Documents_on_the_Windows_drive_and_each_other_drive()
    {
        using var s = new SearchSandbox("structure-roots", SandboxProfile.Default);
        var roots = new StructurePlanner(s.K, s.Host.Safety, s.Host.Walker).DefaultRoots();
        Assert.Equal(2, roots.Count);
        Assert.True(roots[0].IsDocuments);
        Assert.Equal(PathUtil.Normalize(s.K.Documents), roots[0].Path);
        Assert.Equal(s.D, roots[1].Path);
        using var m = new SearchSandbox("structure-roots-min", SandboxProfile.Minimal);
        Assert.Single(new StructurePlanner(m.K, m.Host.Safety, m.Host.Walker).DefaultRoots());
    }

    [Fact]
    public void Simple_preset_places_folders_by_what_they_hold()
    {
        using var s = Layout();
        var p = ProposeD(s, StructurePreset.Simple);
        foreach (var m in p.Moves) log.WriteLine($"{m.Folder.Name}: {m.Destination ?? "-"} | {m.DisabledReason ?? m.Reason}");
        Assert.Equal(@"Media\Photos\Camera", DestOf(p, "Camera"));
        Assert.Equal(@"Media\Videos\Movies", DestOf(p, "Movies"));
        Assert.Equal(@"Documents\Old Projects", DestOf(p, "Old Projects"));
        Assert.Equal(@"Documents\Taxes 2024", DestOf(p, "Taxes 2024"));
        Assert.Equal(@"Software\Installers", DestOf(p, "Installers"));
        Assert.Null(DestOf(p, "Mixed")); // recently used mix: stays
        Assert.Null(DestOf(p, "Empty"));
        // Never moved: a code project, an app's own folder, a game library, a link.
        Assert.Contains("project", p.Moves.Single(m => m.Folder.Name == "repo").DisabledReason, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(p.Moves.Single(m => m.Folder.Name == "My Games").DisabledReason);
        Assert.Null(DestOfOrNull(p, "SteamLibrary")); // a game library is never offered (skipped or greyed)
        Assert.Contains("link", p.Moves.Single(m => m.Folder.Name == "Camera link").DisabledReason);
        Assert.DoesNotContain(p.Moves, m => m.Folder.Name == "Archive"); // already the structure
    }

    [Fact]
    public void A_folder_named_like_its_category_becomes_it()
    {
        using var s = Layout();
        s.Put(Path.Combine(s.D, "Photos", "x.jpg"), new byte[100], SearchSandbox.Now.AddDays(-2));
        var p = ProposeD(s, StructurePreset.Simple);
        var photos = p.Moves.Single(m => m.Folder.Name == "Photos");
        Assert.True(photos.BecomesCategory);
        Assert.Equal(@"Media\Photos", DestOf(p, "Photos"));
        Assert.Equal(@"Media\Photos\Camera", DestOf(p, "Camera"));
        Assert.Equal("Photos", p.Moves[0].Folder.Name); // moves first
        Assert.DoesNotContain(p.Tree, t => t.RelativePath == @"Media\Photos"); // never pre-created
    }

    [Fact]
    public void Name_clashes_are_auto_numbered_and_said()
    {
        using var s = Layout();
        Directory.CreateDirectory(Path.Combine(s.D, "Software", "Installers"));
        var p = ProposeD(s, StructurePreset.Simple);
        Assert.Equal(@"Software\Installers (2)", DestOf(p, "Installers"));
        Assert.Contains("Installers (2)", p.Moves.Single(m => m.Folder.Name == "Installers").Reason);
    }

    [Fact]
    public void By_date_uses_ISO_years()
    {
        using var s = Layout();
        var p = ProposeD(s, StructurePreset.ByDate);
        Assert.Equal(@"Photos\2026\Camera", DestOf(p, "Camera"));
        Assert.Equal(@"Photos\2024\Movies", DestOf(p, "Movies"));
        Assert.Equal(@"Archive\2024\Old Projects", DestOf(p, "Old Projects"));
        Assert.Null(DestOf(p, "Taxes 2024")); // used this year: stays
    }

    [Fact]
    public void Projects_and_areas()
    {
        using var s = Layout();
        var p = ProposeD(s, StructurePreset.ProjectsAndAreas);
        Assert.Equal(@"4 Archive\2024\Old Projects", DestOf(p, "Old Projects"));
        Assert.Equal(@"2 Areas\Taxes 2024", DestOf(p, "Taxes 2024"));
        Assert.Equal(@"3 Resources\Camera", DestOf(p, "Camera"));
        Assert.Equal(@"1 Projects\Mixed", DestOf(p, "Mixed"));
        Assert.Contains(p.Tree, t => t.RelativePath == "0 Inbox" && !t.Exists);
    }

    [Fact]
    public void Numbered_johnny_decimal_areas_and_categories()
    {
        using var s = Layout();
        var p = ProposeD(s, StructurePreset.Numbered);
        Assert.Equal(@"10-19 Life admin\11 Money\Taxes 2024", DestOf(p, "Taxes 2024"));
        Assert.Equal(@"30-39 Media\31 Photos\Camera", DestOf(p, "Camera"));
        Assert.Equal(@"30-39 Media\32 Videos\Movies", DestOf(p, "Movies"));
        Assert.Equal(@"40-49 Software & games\41 Installers\Installers", DestOf(p, "Installers"));
        Assert.Equal(@"90-99 Archive\91 Old files\Old Projects", DestOf(p, "Old Projects"));
        Assert.All(p.Tree, t => Assert.True(t.RelativePath.Count(c => c == '\\') <= 1)); // depth ≤ 2 (+ the folder = 3)
    }

    [Theory]
    [InlineData(StructurePreset.Simple)]
    [InlineData(StructurePreset.ByDate)]
    [InlineData(StructurePreset.ProjectsAndAreas)]
    [InlineData(StructurePreset.Numbered)]
    public async Task Plan_shapes_are_unticked_check_items_of_create_and_move_that_the_real_guard_allows(StructurePreset preset)
    {
        using var s = Layout();
        var ctx = s.Host.CreateScanContext(ScanDepth.Quick);
        ctx = new ScanContext
        {
            Locations = ctx.Locations, Safety = ctx.Safety, InUse = ctx.InUse, Settings = ctx.Settings, Walker = ctx.Walker,
            Options = new Dictionary<string, object> { [SearchScanner.PresetOption] = preset },
        };
        var result = await new SearchScanner(Clock).ScanAsync(ctx, null, CancellationToken.None);
        Assert.Equal(SearchIds.FeatureId, result.FeatureId);
        Assert.NotEmpty(result.Groups);
        foreach (var g in result.Groups)
        {
            Assert.False(string.IsNullOrWhiteSpace(g.Description));
            Assert.Equal(Badge.Check, g.Badge);
        }
        foreach (var item in result.AllItems)
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Reason));
            Assert.Equal(Badge.Check, item.Badge);
            Assert.False(item.DefaultSelected);
            Assert.All(item.Actions, a =>
            {
                Assert.Contains(a.Kind, new[] { ActionKind.CreateDirectory, ActionKind.MovePath });
                Assert.Equal(SearchIds.FeatureId, a.FeatureId);
                Assert.True(a.Reversible);
                Assert.False(string.IsNullOrWhiteSpace(a.Description));
                if (a.Kind == ActionKind.MovePath) Assert.True(a.IsDirectory); // whole folders only (checked per root below)
            });
            if (item.DisabledReason is null)
                Assert.All(item.Actions, a => Assert.True(s.Host.Safety.Check(a).Allowed, $"{a.Description}: {s.Host.Safety.Check(a).Reason}"));
        }
        // On the second drive the moves are allowed; a ticked plan puts the folders that create dirs first.
        var dMoves = result.Groups.Where(g => g.Id.EndsWith("-moves")).SelectMany(g => g.Items).Where(i => i.DisabledReason is null).ToList();
        Assert.NotEmpty(dMoves);
        Assert.All(dMoves.SelectMany(i => i.Actions).Where(a => a.Kind == ActionKind.MovePath),
            a => Assert.Equal(s.D, PathUtil.Normalize(Path.GetDirectoryName(a.SourcePath!)!)));
        var plan = PlanBuilder.FromSelection(SearchIds.FeatureId, "Folder structure", result.AllItems);
        int firstMove = plan.Items.ToList().FindIndex(a => a.Kind == ActionKind.MovePath);
        Assert.All(plan.Items.Skip(firstMove), a => Assert.NotEqual(ActionKind.CreateDirectory, a.Kind));
        Assert.Equal(plan.Items.Count(a => a.Kind == ActionKind.CreateDirectory),
            plan.Items.Where(a => a.Kind == ActionKind.CreateDirectory).Select(a => a.DestinationPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.False(new SearchScanner().IncludedInOptimizeAll);
    }

    [Fact]
    public async Task Windows_drive_Documents_in_OneDrive_or_a_library_is_never_reorganized_without_permission()
    {
        using var s = new SearchSandbox("structure-docs", SandboxProfile.Minimal);
        s.Put(Path.Combine(s.K.Documents, "Taxes", "2025.pdf"), new byte[2000], SearchSandbox.Now.AddDays(-30));
        s.Put(Path.Combine(s.K.Documents, "My Games", "x", "save.dat"), new byte[10], SearchSandbox.Now.AddDays(-3));
        var result = await new SearchScanner(Clock).ScanAsync(s.Host.CreateScanContext(ScanDepth.Quick), null, CancellationToken.None);
        foreach (var i in result.AllItems) log.WriteLine($"{i.Title}: {i.DisabledReason ?? "offered"} | {i.Reason}");
        var games = result.AllItems.Single(i => i.Title == "My Games");
        Assert.NotNull(games.DisabledReason);
        Assert.Empty(games.Actions);
        // Documents is a Windows library: the real guard decides (today it asks for a per-folder permission for "search").
        foreach (var i in result.AllItems.Where(i => i.Actions.Count > 0))
        {
            bool allowed = i.Actions.All(a => s.Host.Safety.Check(a).Allowed);
            Assert.Equal(allowed, i.DisabledReason is null);
        }

        using var od = new SearchSandbox("structure-onedrive", SandboxProfile.Default);
        od.Put(Path.Combine(od.K.Documents, "Taxes", "2025.pdf"), new byte[2000], SearchSandbox.Now.AddDays(-30));
        var r2 = await new SearchScanner(Clock).ScanAsync(od.Host.CreateScanContext(ScanDepth.Quick), null, CancellationToken.None);
        var taxes = r2.AllItems.Single(i => i.Title == "Taxes");
        Assert.Contains("OneDrive", taxes.DisabledReason);
    }

    [Fact]
    public async Task Run_then_undo_leaves_the_generated_testbed_identical()
    {
        using var s = new SearchSandbox("structure-e2e", SandboxProfile.Default, generate: true);
        var before = TestbedBuilder.Manifest(s.Root);
        var planner = new StructurePlanner(s.K, s.Host.Safety, s.Host.Walker, Clock);
        var result = planner.Plan(StructurePreset.Simple, null, null, CancellationToken.None);
        foreach (var i in result.AllItems) log.WriteLine($"{i.Title}: {i.Detail} | {i.DisabledReason ?? "offered"}");
        var offered = result.AllItems.Where(i => i.DisabledReason is null && i.Actions.Count > 0).ToList();
        Assert.Contains(offered, i => i.Title == "Photos");
        Assert.Contains(offered, i => i.Title == "Backups");

        var plan = PlanBuilder.FromSelection(SearchIds.FeatureId, "Folder structure", offered);
        var run = await s.Host.Executor.ExecuteAsync(plan, new ExecuteOptions(), null, CancellationToken.None);
        foreach (var step in run.Steps) log.WriteLine($"{step.Status}: {step.Item.Description} {step.Message}");
        Assert.All(run.Steps, st => Assert.Equal(StepStatus.Done, st.Status));
        // D:\Photos became D:\Media\Photos (whole folder, contents untouched); Backups went into the Archive.
        Assert.True(File.Exists(Path.Combine(s.D, "Media", "Photos", "Holiday 2025", "IMG_20250614_101500.jpg")));
        Assert.False(Directory.Exists(Path.Combine(s.D, "Photos")));
        Assert.True(File.Exists(Path.Combine(s.D, "Archive", "Backups", "old-backup-2024.zip")));
        Assert.True(Directory.Exists(Path.Combine(s.D, "SteamLibrary", "steamapps")));

        var undo = await s.Host.Undo.UndoRunAsync(run.RunId, null, CancellationToken.None);
        foreach (var m in undo.Messages) log.WriteLine(m);
        Assert.Equal(0, undo.Failed);
        var diff = TestbedBuilder.Compare(before, TestbedBuilder.Manifest(s.Root));
        var outside = diff.Differences.Where(d => !d.Path.StartsWith(@"Data\", StringComparison.OrdinalIgnoreCase) && d.Path != "Data").ToList();
        Assert.True(outside.Count == 0, "Undo did not restore the tree exactly:\n" + string.Join("\n", outside.Take(20).Select(d => d.ToString())));
    }
}
