using RegistryHive = Microsoft.Win32.RegistryHive;
using PcTidy.App.Composition;
using PcTidy.App.Composition.Demo;
using PcTidy.App.Shell;
using PcTidy.App.Updates;
using PcTidy.Core.Model;
using PcTidy.UI.Flow;
using Xunit;

namespace PcTidy.UI.Tests;

/// <summary>Fixes from the v2 UI review: pages to open, note colours, the demo rail, the install folder and the uninstaller lookup.</summary>
public class SharedUiFixTests
{
    private static PlanItem Open(string description, string target = "steam://uninstall/1") => new()
    { Kind = ActionKind.OpenExternal, FeatureId = "games", Description = description, Args = new Dictionary<string, string> { ["target"] = target } };
    private static PlanItem Q(long size) => new() { Kind = ActionKind.QuarantinePath, FeatureId = "games", Description = "clip", ExpectedSize = size };
    private static PlanItem Bin() => new() { Kind = ActionKind.EmptyRecycleBin, FeatureId = "cleanup", Description = "bin", ExpectedSize = 10, Reversible = false };
    private static ActionPlan Plan(params PlanItem[] items) => new() { FeatureId = "games", Title = "Game library", Items = items };

    // ---------------- launcher-uninstall items (Games › Library) ----------------

    [Fact]
    public void Launcher_uninstall_has_a_verb_on_the_bar_and_in_the_confirm_dialog()
    {
        var items = new[] { Open("Open Steam to uninstall \"Another Game\"") };
        Assert.Equal("Open Steam to uninstall (1)", PlanFacts.PrimaryAction(items));

        var model = new ConfirmModel(new[] { Plan(items) }, null, 7);
        Assert.Equal("Open Steam to uninstall (1)", model.PrimaryText(false));
        var line = Assert.Single(model.Groups, g => g.Kind == ConfirmModel.KindOpen);
        Assert.Equal("Open Steam to uninstall \"Another Game\"", line.Title);
        Assert.True(model.CanRun(false));
        Assert.Single(model.PlansToRun(false));
        Assert.Equal(0, model.Counts.Total); // opening a page is not a change to the PC
        Assert.True(model.Counts.HasWork);
        // The dialog widens so the long button text isn't cut off; short ones keep the usual width.
        Assert.Equal(640, ConfirmDialog.DialogWidthFor("Clean 6.3 GB"));
        Assert.True(ConfirmDialog.DialogWidthFor("Open Steam to uninstall (1)") >= 780);
    }

    [Fact]
    public void Several_pages_to_open_are_counted_and_named()
    {
        var two = new[] { Open("Open Steam to uninstall \"A\""), Open("Open Steam to uninstall \"B\"") };
        Assert.Equal("Open Steam to uninstall (2)", PlanFacts.PrimaryAction(two));
        var launchers = new[] { Open("Open Steam to uninstall \"A\""), Open("Open Epic Games to uninstall \"B\""), Open("Open Steam to uninstall \"C\"") };
        Assert.Equal("Open 2 apps to uninstall (3)", PlanFacts.PrimaryAction(launchers));
        Assert.Equal("Open Windows Update settings", PlanFacts.PrimaryAction(new[] { Open("Open Windows Update settings", "ms-settings:windowsupdate") }));
        Assert.Equal("Open 2 pages", PlanFacts.PrimaryAction(new[] { Open("Open Windows Update settings"), Open("Read the help page") }));
        Assert.Equal("Open 2 pages", PlanFacts.TotalsLine(PlanCounts.Of(two)));
        Assert.Equal("Nothing selected", PlanFacts.PrimaryAction(Array.Empty<PlanItem>()));
    }

    [Fact]
    public void Pages_to_open_never_change_the_verb_of_a_real_clean_and_survive_an_unticked_permanent_box()
    {
        Assert.Equal("Clean 2 KB", PlanFacts.PrimaryAction(new[] { Q(2048), Open("Open Steam to uninstall \"A\"") }));
        // An open marked non-reversible is still just an open, never "permanent".
        Assert.Equal(PlanCategory.Housekeeping, PlanFacts.Categorize(Open("x") with { Reversible = false }));
        var model = new ConfirmModel(new[] { Plan(Open("Open Steam to uninstall \"A\"")), Plan(Bin()) }, null, 7);
        Assert.True(model.CanRun(false));
        var run = model.PlansToRun(false);
        Assert.Single(run);
        Assert.True(PlanFacts.IsOpen(run[0].Items[0]));
    }

    [Fact]
    public void Done_card_says_a_page_was_opened_not_nothing()
    {
        var item = Open("Open Steam to uninstall \"A\"");
        var run = new RunRecord { RunId = "r", FeatureId = "games", Title = "Game library", Status = RunStatus.Completed,
            Steps = new[] { new StepRecord { Index = 0, Item = item, Status = StepStatus.Done } } };
        Assert.Equal("1 page opened", new RunSummary(new[] { run }).Headline);
    }

    // ---------------- note colour ----------------

    [Fact]
    public void Only_a_close_this_app_first_note_is_a_blocker()
    {
        var group = new GroupRow("games", "Games", new ScanGroup
        {
            Id = "g", Title = "G", Badge = Badge.Check,
            Items = new[]
            {
                new ScanItem { Title = "Never played", Reason = "r", Badge = Badge.Check, CurrentState = "Never played" },
                new ScanItem { Title = "Chrome cache", Reason = "r", Badge = Badge.Safe, CurrentState = "Running", RequiresClosedApps = new[] { "Chrome" } },
            },
        });
        Assert.False(group.Items[0].NoteIsBlocker);
        Assert.Equal("Never played", group.Items[0].Note);
        Assert.True(group.Items[1].NoteIsBlocker);
        Assert.Equal("Close Chrome first", group.Items[1].Note);
    }

    // ---------------- demo rail ----------------

    [Fact]
    public void Demo_rail_is_the_real_rail()
    {
        var real = FeatureCatalog.All();
        var demo = DemoModules.All().ToList();
        Assert.Equal(real.Select(m => (m.Id, m.Title, m.Group, m.Icon, m.Order, m.Release)),
                     demo.Select(m => (m.Id, m.Title, m.Group, m.Icon, m.Order, m.Release)));
        Assert.Contains(demo, m => m.Id == "photos");
        Assert.Contains(demo, m => m.Id == "desktoptidy");
        Assert.True(demo.Single(m => m.Id == "games").Scanners.Count > 0); // sample data where the demo has it
    }

    [Fact]
    public void Demo_sections_without_sample_data_show_no_developer_notes()
    {
        string text = "";
        var t = new Thread(() =>
        {
            var page = new DemoPlaceholderPage("Drivers", "See every driver.", "DeveloperBoard24");
            text = string.Join(" ", Texts(page));
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        Assert.DoesNotContain("plugs in", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("built separately", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No sample data", text);
    }

    private static IEnumerable<string> Texts(object o)
    {
        switch (o)
        {
            case System.Windows.Controls.TextBlock tb: yield return tb.Text; break;
            case PcTidy.UI.Controls.EmptyState es: yield return es.Title + " " + es.Message; break;
        }
        if (o is System.Windows.DependencyObject d)
            foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(d))
                foreach (var s in Texts(child)) yield return s;
    }

    // ---------------- Services: presets, run order, health report ----------------

    [Fact]
    public void Service_profiles_belong_to_the_Gamer_and_Privacy_presets_and_an_empty_profile_ticks_nothing()
    {
        Assert.True(OptimizePresets.IncludesGroup(OptimizePresets.Gamer, "services", "services.profile-gaming"));
        Assert.True(OptimizePresets.IncludesGroup(OptimizePresets.Privacy, "services", "services.profile-privacy"));
        Assert.False(OptimizePresets.IncludesGroup(OptimizePresets.Recommended, "services", "services.profile-privacy"));
        Assert.False(OptimizePresets.IncludesGroup(OptimizePresets.Gamer, "services", "services.profile-privacy"));

        // Gaming has no proven changes: one information-only row. It shows as "Info only", nothing ticked, nothing counted.
        var empty = new ScanGroup
        {
            Id = "services.profile-gaming", Title = "Gaming profile", Badge = Badge.Safe,
            Items = new[] { new ScanItem { Id = "services.profile-gaming.none", Title = "Gaming: nothing to do", Reason = "No proven service changes.", Badge = Badge.Safe, DefaultSelected = true } },
        };
        var row = new GroupRow("services", "Services", empty);
        Assert.True(row.IsInfoOnly);
        Assert.False(OptimizePresets.ShouldTick(OptimizePresets.Gamer, "services", empty, empty.Items[0]));
        row.SetAll(true);
        Assert.Equal(false, row.IsChecked);
    }

    [Fact]
    public void Services_run_after_user_files_and_startup_and_before_tweaks_and_drivers()
    {
        int services = SafeOrder.Rank("services");
        foreach (var before in new[] { "cleanup", "duplicates", "developer", "games", "organize", "photos", "desktoptidy", "speed" })
            Assert.True(SafeOrder.Rank(before) < services, before);
        foreach (var after in new[] { "tweaks", "drivers", "pccare" })
            Assert.True(SafeOrder.Rank(after) > services, after);
    }

    [Fact]
    public void PC_cares_health_report_gets_its_Services_heading_from_the_Services_section()
    {
        // The shell hands PC care a section source (no patching of the saved file before the confirm any more).
        var modules = PcTidy.App.Composition.FeatureCatalog.All();
        var services = modules.OfType<PcTidy.Features.Services.ServicesModule>().Single();
        var source = new PcTidy.App.Composition.ServicesReportSection(services);
        Assert.Equal("Services", source.Heading);
        var lines = PcTidy.App.Composition.ServicesReportSection.ToReportLines(new[] { ("Services", "212 (180 from Microsoft, 32 from other companies)"), ("Not signed", "1") });
        Assert.Equal(new[] { "Services", "Not signed" }, lines.Select(l => l.Label));
        Assert.Equal("1", lines[1].Value);
    }

    // ---------------- per-machine install ----------------

    [Fact]
    public void Program_Files_and_the_install_folder_are_never_the_data_folder()
    {
        const string pf = @"C:\Program Files", pf86 = @"C:\Program Files (x86)", install = @"C:\Program Files\PC Tidy";
        Assert.True(InstallFolder.IsProtected(@"C:\Program Files\PC Tidy\Data", install, pf, pf86));
        Assert.True(InstallFolder.IsProtected(@"C:\Program Files (x86)\Stuff", install, pf, pf86));
        Assert.False(InstallFolder.IsProtected(@"D:\PC Tidy data", install, pf, pf86));
        Assert.False(InstallFolder.IsProtected(@"C:\Program Files Backup", install, pf, pf86));
    }

    [Fact]
    public void The_machine_wide_uninstaller_is_found_first_then_an_old_per_user_one()
    {
        const string machine = @"C:\Program Files\PC Tidy", user = @"C:\Users\tester\AppData\Local\Programs\PC Tidy";
        (string?, string?) Entry(string dir) => ($"\"{dir}\\uninstall.exe\" --uninstall", dir);
        var both = OwnProcesses.FindUninstaller(h => h == RegistryHive.LocalMachine ? Entry(machine) : Entry(user), _ => true);
        Assert.Equal(machine, both!.InstallLocation);
        var oldOnly = OwnProcesses.FindUninstaller(h => h == RegistryHive.LocalMachine ? (null, null) : Entry(user), _ => true);
        Assert.Equal(user, oldOnly!.InstallLocation);
        // A bad machine entry doesn't hide a good per-user one.
        var bad = OwnProcesses.FindUninstaller(h => h == RegistryHive.LocalMachine ? ("\"C:\\Windows\\System32\\cmd.exe\" --uninstall", machine) : Entry(user), _ => true);
        Assert.Equal(user, bad!.InstallLocation);
        Assert.Null(OwnProcesses.FindUninstaller(_ => (null, null), _ => true));
    }
}
