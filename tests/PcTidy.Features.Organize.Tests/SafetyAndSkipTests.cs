using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Organize.Planning;
using PcTidy.Features.Organize.Rules;
using Xunit;

namespace PcTidy.Features.Organize.Tests;

public class SafetyAndSkipTests
{
    [Fact]
    public async Task Never_moved_files_are_listed_with_their_reason()
    {
        using var sb = new Sandbox();
        foreach (var ext in new[] { "crdownload", "part", "partial", "opdownload", "download", "!ut", "!qB", "bc!", "tmp" })
            sb.File($"movie.{ext}");
        sb.File("~$report.docx");
        sb.File(".~lock.budget.ods#");
        foreach (var ext in new[] { "lnk", "url", "website", "appref-ms" }) sb.File($"app.{ext}");
        sb.File("desktop.ini");
        sb.File("Thumbs.db");
        var hidden = sb.File("secret.jpg");
        System.IO.File.SetAttributes(hidden, FileAttributes.Hidden);
        var system = sb.File("sys.pdf");
        System.IO.File.SetAttributes(system, FileAttributes.System);
        sb.File("fresh.jpg", ageDays: 0.001); // ~1.5 minutes old
        var locked = sb.File("thesis.docx");
        sb.InUse.Locked[locked] = "Word";
        sb.File("ok.jpg");

        var p = await sb.PlanAsync();

        Assert.Equal("ok.jpg", Assert.Single(p.Actions).Name);
        foreach (var ext in new[] { "crdownload", "part", "partial", "opdownload", "download", "!ut", "!qB", "bc!", "tmp" })
            Assert.Equal(SkipKind.StillDownloading, p.SkipFor($"movie.{ext}").Kind);
        Assert.Equal(SkipKind.StillDownloading, p.SkipFor("~$report.docx").Kind);
        Assert.Equal(SkipKind.StillDownloading, p.SkipFor(".~lock.budget.ods#").Kind);
        foreach (var ext in new[] { "lnk", "url", "website", "appref-ms" }) Assert.Equal(SkipKind.Shortcut, p.SkipFor($"app.{ext}").Kind);
        Assert.Equal(SkipKind.SystemFile, p.SkipFor("desktop.ini").Kind);
        Assert.Equal(SkipKind.SystemFile, p.SkipFor("Thumbs.db").Kind);
        Assert.Equal(SkipKind.Hidden, p.SkipFor("secret.jpg").Kind);
        Assert.Equal(SkipKind.SystemFile, p.SkipFor("sys.pdf").Kind);
        Assert.Equal(SkipKind.RecentlyChanged, p.SkipFor("fresh.jpg").Kind);
        var inUse = p.SkipFor("thesis.docx");
        Assert.Equal(SkipKind.InUse, inUse.Kind);
        Assert.Contains("Word", inUse.Reason);
        Assert.All(p.Skipped, s => Assert.False(string.IsNullOrWhiteSpace(s.Reason)));
    }

    [Fact]
    public async Task Folders_stay_put_and_category_folders_are_recognised()
    {
        using var sb = new Sandbox();
        sb.File(@"Holiday\a.jpg");
        sb.File(@"Pictures\already.jpg");
        var p = await sb.PlanAsync();
        Assert.Empty(p.Actions);
        Assert.Equal(SkipKind.Folder, p.SkipFor("Holiday").Kind);
        Assert.Equal(SkipKind.AlreadySorted, p.SkipFor("Pictures").Kind);
    }

    [Fact]
    public async Task Sorting_a_category_folder_itself_is_refused()
    {
        using var sb = new Sandbox();
        sb.File(@"Pictures\a.jpg");
        var p = await sb.PlanAsync(null, ScanDepth.Quick, sb.Downloads, Path.Combine(sb.Downloads, "Pictures"));
        Assert.Empty(p.Actions);
        Assert.Equal(SkipKind.AlreadySorted, p.SkipFor("a.jpg").Kind);
    }

    [Fact]
    public async Task Move_whole_folders_moves_as_one_unit_but_never_projects()
    {
        using var sb = new Sandbox();
        sb.File(@"Holiday\a.jpg"); sb.File(@"Holiday\b.png"); sb.File(@"Holiday\notes.txt");
        sb.File(@"MyApp\.git\HEAD"); sb.File(@"MyApp\main.cs");
        sb.File(@"Mixed\a.jpg"); sb.File(@"Mixed\b.mp3");
        Sandbox.AgeDir(Path.Combine(sb.Downloads, "Holiday"));
        Sandbox.AgeDir(Path.Combine(sb.Downloads, "MyApp"));
        Sandbox.AgeDir(Path.Combine(sb.Downloads, "Mixed"));
        Sandbox.AgeDir(Path.Combine(sb.Downloads, "MyApp", ".git"));

        var p = await sb.PlanAsync(new OrganizeSettings { MoveWholeFolders = true });

        var holiday = p.ActionFor("Holiday");
        Assert.Equal(PlannedActionKind.MoveFolder, holiday.Kind);
        Assert.True(holiday.IsDirectory);
        Assert.Equal(Path.Combine(sb.Downloads, "Pictures", "Holiday"), holiday.DestinationPath);
        Assert.Equal(SkipKind.ProjectOrApp, p.SkipFor("MyApp").Kind);
        Assert.Equal(SkipKind.Folder, p.SkipFor("Mixed").Kind);
    }

    [Fact]
    public async Task A_root_that_is_a_code_project_is_never_sorted_inside()
    {
        using var sb = new Sandbox();
        var project = Path.Combine(sb.Downloads, "repo");
        sb.File(@"repo\package.json");
        sb.File(@"repo\logo.png");
        var p = await sb.PlanAsync(null, ScanDepth.Quick, project);
        Assert.Empty(p.Actions);
        Assert.Equal(SkipKind.ProjectOrApp, p.SkipFor("logo.png").Kind);
    }

    [Fact]
    public async Task OneDrive_desktop_is_skipped_unless_allowed()
    {
        using var sb = new Sandbox(); // Default profile: Desktop is inside OneDrive
        sb.File("photo.jpg", folder: sb.Locations.Desktop);
        Assert.True(sb.Safety.IsInCloudRoot(sb.Locations.Desktop));

        var blocked = await sb.PlanAsync(null, ScanDepth.Quick, sb.Locations.Desktop);
        Assert.Empty(blocked.Actions);
        Assert.Equal(SkipKind.CloudFolder, blocked.SkipFor("photo.jpg").Kind);

        sb.AppSettings.AllowedAskFirstFolders.Add(sb.Locations.Desktop);
        var allowed = await sb.PlanAsync(null, ScanDepth.Quick, sb.Locations.Desktop);
        var move = allowed.ActionFor("photo.jpg");
        Assert.Equal(Path.Combine(sb.Locations.Desktop, "Pictures", "photo.jpg"), move.DestinationPath);
        Assert.Equal(Badge.Safe, move.Badge); // stays inside OneDrive
    }

    [Fact]
    public async Task Desktop_outside_OneDrive_is_sorted_directly_on_a_minimal_pc()
    {
        using var sb = new Sandbox(SandboxProfile.Minimal);
        Assert.False(sb.Safety.IsInCloudRoot(sb.Locations.Desktop));
        sb.File("notes.txt", folder: sb.Locations.Desktop);
        var p = await sb.PlanAsync(null, ScanDepth.Quick, sb.Locations.Desktop);
        Assert.Equal(Path.Combine(sb.Locations.Desktop, "Documents", "notes.txt"), p.ActionFor("notes.txt").DestinationPath);
    }

    [Fact]
    public void Optimize_all_roots_detect_onedrive_instead_of_assuming()
    {
        using (var sb = new Sandbox())
        {
            Assert.Equal(new[] { sb.Downloads }, OrganizeScanner.DefaultRoots(sb.Locations, sb.Safety, sb.AppSettings));
            sb.AppSettings.AllowedAskFirstFolders.Add(sb.Locations.Desktop);
            Assert.Equal(new[] { sb.Downloads, sb.Locations.Desktop }, OrganizeScanner.DefaultRoots(sb.Locations, sb.Safety, sb.AppSettings));
        }
        using (var minimal = new Sandbox(SandboxProfile.Minimal))
            Assert.Equal(new[] { minimal.Downloads, minimal.Locations.Desktop }, OrganizeScanner.DefaultRoots(minimal.Locations, minimal.Safety, minimal.AppSettings));
    }

    [Fact]
    public async Task Moving_out_of_OneDrive_is_a_check_item_with_a_warning()
    {
        using var sb = new Sandbox();
        sb.AppSettings.AllowedAskFirstFolders.Add(sb.Locations.Desktop);
        sb.File("report.pdf", folder: sb.Locations.Desktop);
        var outside = Path.Combine(sb.Locations.UserProfile, "Archive");
        var settings = new OrganizeSettings();
        settings.Rules.Insert(0, new OrganizeRule { Name = "To archive", Conditions = ConditionGroup.AllOf(RuleCondition.Ext("pdf")), Action = new RuleAction { Destination = outside } });

        var p = await sb.PlanAsync(settings, ScanDepth.Quick, sb.Locations.Desktop);

        var move = p.ActionFor("report.pdf");
        Assert.Equal(Badge.Check, move.Badge);
        Assert.False(move.DefaultSelected);
        Assert.False(move.Automatic);
        Assert.Contains("out of OneDrive", move.Reason);
        Assert.False(OrganizeResultBuilder.ToItem(move, new[] { sb.Locations.Desktop }).DefaultSelected);
    }

    [Fact]
    public async Task Clashes_get_a_number_and_identical_files_become_duplicate_checks()
    {
        using var sb = new Sandbox();
        sb.File(@"Pictures\cat.jpg", "old cat");
        sb.File("cat.jpg", "new cat");
        sb.File(@"Pictures\dog.jpg", "same dog");
        sb.File("dog.jpg", "same dog");
        sb.File(@"Pictures\cow.jpg", "aaaa");
        sb.File("cow.jpg", "bbbb"); // same size, different content

        var p = await sb.PlanAsync();

        var cat = p.ActionFor("cat.jpg");
        Assert.Equal(Path.Combine(sb.Downloads, "Pictures", "cat (2).jpg"), cat.DestinationPath);
        Assert.True(cat.Renumbered);
        Assert.Equal(NameClashPolicy.AutoNumber, cat.Items.Last().ClashPolicy);
        Assert.Equal(Path.Combine(sb.Downloads, "Pictures", "cow (2).jpg"), p.ActionFor("cow.jpg").DestinationPath);

        var dog = p.ActionFor("dog.jpg");
        Assert.Equal(PlannedActionKind.DuplicateQuarantine, dog.Kind);
        Assert.Equal(Badge.Check, dog.Badge);
        Assert.False(dog.DefaultSelected);
        Assert.Equal(ActionKind.QuarantinePath, Assert.Single(dog.Items).Kind);
        Assert.Equal(Path.Combine(sb.Downloads, "Pictures", "dog.jpg"), dog.DuplicateOf);
    }

    [Fact]
    public async Task Skip_clash_policy_leaves_the_file_and_overwrite_is_refused()
    {
        using var sb = new Sandbox();
        sb.File(@"Documents\a.txt", "one");
        sb.File("a.txt", "two!");
        var settings = new OrganizeSettings();
        settings.Rules.Single(r => r.Name == "Documents").ClashPolicy = NameClashPolicy.Skip;
        var p = await sb.PlanAsync(settings);
        Assert.Equal(SkipKind.NameClash, p.SkipFor("a.txt").Kind);

        settings.Rules.Single(r => r.Name == "Documents").ClashPolicy = NameClashPolicy.Overwrite;
        var p2 = await sb.PlanAsync(settings);
        Assert.Contains(p2.Problems, x => x.Blocking && x.Message.Contains("never overwrites"));
        Assert.DoesNotContain(p2.Actions, a => a.RuleName == "Documents");
    }

    [Fact]
    public async Task Missing_folders_are_created_parents_first_and_deduplicated_in_the_plan()
    {
        using var sb = new Sandbox();
        sb.File("a.png", ageDays: 1);
        sb.File("b.png", ageDays: 1);
        var p = await sb.PlanAsync(new OrganizeSettings { UseDateFolders = true });
        Assert.All(p.Actions, a => Assert.Equal(ActionKind.CreateDirectory, a.Items[0].Kind));

        var plan = PlanAssembler.Build("t", p.Actions);
        var dirs = plan.Items.TakeWhile(i => i.Kind == ActionKind.CreateDirectory).Select(i => i.DestinationPath!).ToList();
        Assert.Equal(dirs.Distinct(StringComparer.OrdinalIgnoreCase).Count(), dirs.Count);
        Assert.Equal(dirs.OrderBy(d => d.Length), dirs);
        Assert.Equal(Path.Combine(sb.Downloads, "Pictures"), dirs[0]);
        Assert.Equal(2, plan.Items.Count(i => i.Kind == ActionKind.MovePath));
        Assert.All(plan.Items, i => Assert.Equal(OrganizeModule.FeatureId, i.FeatureId));
    }

    [Fact]
    public async Task Exclusions_match_whole_path_segments_only()
    {
        Assert.True(PathExclusions.IsExcluded(@"C:\x\Projects\a.txt", "Projects"));
        Assert.False(PathExclusions.IsExcluded(@"C:\x\MyProjects2\a.txt", "Projects"));
        Assert.True(PathExclusions.IsExcluded(@"C:\x\Work\Projects\a", @"Work\Projects"));
        Assert.False(PathExclusions.IsExcluded(@"C:\x\Work\ProjectsOld\a", @"Work\Projects"));
        Assert.True(PathExclusions.IsExcluded(@"C:\Data\Keep\a.txt", @"C:\Data\Keep"));
        Assert.False(PathExclusions.IsExcluded(@"C:\Data\Keeper\a.txt", @"C:\Data\Keep"));

        using var sb = new Sandbox();
        var projects = Path.Combine(sb.Root, "Projects");
        var myProjects = Path.Combine(sb.Root, "MyProjects2");
        sb.File("a.txt", folder: projects);
        sb.File("b.txt", folder: myProjects);
        sb.AppSettings.ScanExclusions.Add("Projects");
        var p = await sb.PlanAsync(null, ScanDepth.Quick, projects, myProjects);
        Assert.Equal(SkipKind.Protected, p.SkipFor("a.txt").Kind);
        Assert.Equal("b.txt", Assert.Single(p.Actions).Name);
    }

    [Fact]
    public async Task Protected_files_and_destinations_are_never_used()
    {
        using var sb = new Sandbox();
        sb.File("a.pdf");
        var settings = new OrganizeSettings();
        settings.Rules.Insert(0, new OrganizeRule { Name = "Into Windows", Conditions = ConditionGroup.AllOf(RuleCondition.Ext("pdf")), Action = new RuleAction { Destination = sb.Locations.WindowsDir } });
        var p = await sb.PlanAsync(settings);
        Assert.Contains(p.Problems, x => x.Blocking && x.Message.Contains("protected"));
        Assert.Equal("Documents", p.ActionFor("a.pdf").RelativeDestFolder()); // the bad rule is paused, the next one applies
    }
}
