using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Organize.Planning;
using PcTidy.Features.Organize.Rules;
using PcTidy.Safety;
using Xunit;

namespace PcTidy.Features.Organize.Tests;

/// <summary>
/// 3.3 "Sort by type" (option A) and its safety rules, with the real SafetyGuard on a practice PC. Only the decisions that
/// keep files safe or put them in the right folder are pinned here (see docs/reviews/v3.3-organize-design-review.md).
/// </summary>
public class SortByTypeTests
{
    private static OrganizeSettings ByType() => new() { Rules = RulePresets.Find(RulePresets.ByTypeId)!.Create() };

    private static Task<OrganizePreview> PlanAsync(Sandbox sb, string root, OrganizeSettings? settings = null)
        => new OrganizePlanner(new SafetyGuard(sb.Locations, sb.AppSettings), sb.InUse, new ManagedFileWalker(), sb.AppSettings, sb.Clock, sb.Locations)
            .PlanAsync(new OrganizeRequest
            {
                Roots = new[] { root },
                Settings = settings ?? ByType(),
                Culture = System.Globalization.CultureInfo.InvariantCulture,
            }, null, CancellationToken.None);

    [Fact]
    public async Task Files_go_into_type_folders_inside_the_folder_being_sorted()
    {
        using var sb = new Sandbox();
        sb.File("_clientids.gsc");
        sb.File("mp_custom.ff");
        sb.File("zm_origins_bo2_pack.zip", "PK");
        sb.File("rapidfire.gpc");
        sb.Bytes("Screenshot 2026-11-02 191544.png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D });
        sb.File("2026-10-28 21-14-03.mkv");
        sb.File("ubuntu.iso");
        sb.File("linux.torrent", "d8:announce");
        sb.File("vlc-3.0.21-win64.msix");

        var p = await PlanAsync(sb, sb.Downloads);

        Assert.Equal(Path.Combine("Games", "Call of Duty"), p.ActionFor("_clientids.gsc").RelativeDestFolder());
        Assert.Equal(Path.Combine("Games", "Call of Duty"), p.ActionFor("mp_custom.ff").RelativeDestFolder());
        Assert.Equal(Path.Combine("Games", "Call of Duty"), p.ActionFor("zm_origins_bo2_pack.zip").RelativeDestFolder());
        Assert.Equal(Path.Combine("Games", "Cronus scripts"), p.ActionFor("rapidfire.gpc").RelativeDestFolder());
        Assert.Equal(Path.Combine("Pictures", "Screenshots"), p.ActionFor("Screenshot 2026-11-02 191544.png").RelativeDestFolder());
        Assert.Equal(Path.Combine("Videos", "Clips and recordings"), p.ActionFor("2026-10-28 21-14-03.mkv").RelativeDestFolder());
        Assert.Equal("Disk images", p.ActionFor("ubuntu.iso").RelativeDestFolder());
        Assert.Equal("Torrents", p.ActionFor("linux.torrent").RelativeDestFolder());
        Assert.Equal("Program installers", p.ActionFor("vlc-3.0.21-win64.msix").RelativeDestFolder());
        Assert.True(p.ActionFor("_clientids.gsc").DefaultSelected); // game files start ticked in Downloads
        Assert.All(p.Actions, a => Assert.Equal(PlannedActionKind.Move, a.Kind)); // moves only
    }

    [Fact]
    public async Task Configs_msi_fonts_and_game_files_outside_Downloads_need_a_look_and_scripts_stay_put()
    {
        using var sb = new Sandbox();
        sb.File("plutonium_zm.cfg");
        sb.File("app-setup.msi");
        sb.File("Inter-Regular.ttf");
        sb.File("start-server.bat");
        sb.File("settings.json", "{}");

        var p = await PlanAsync(sb, sb.Downloads);
        foreach (var name in new[] { "plutonium_zm.cfg", "app-setup.msi", "Inter-Regular.ttf" })
        {
            Assert.Equal(Badge.Check, p.ActionFor(name).Badge);
            Assert.False(p.ActionFor(name).DefaultSelected);
            Assert.False(p.ActionFor(name).Automatic);
        }
        Assert.DoesNotContain(p.Actions, a => a.SourcePath.EndsWith("start-server.bat", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(p.Actions, a => a.SourcePath.EndsWith("settings.json", StringComparison.OrdinalIgnoreCase));

        // Another of the user's folders (Videos is local in the practice PC's profile).
        sb.File(Path.Combine(sb.Locations.Videos, "_clientids.gsc"));
        var d = await PlanAsync(sb, sb.Locations.Videos);
        Assert.False(d.ActionFor("_clientids.gsc").DefaultSelected); // only Downloads ticks game files
    }

    [Fact]
    public async Task Disguised_programs_and_a_portable_apps_own_files_are_never_sorted()
    {
        using var sb = new Sandbox();
        sb.Bytes("invoice.pdf.exe", "MZ"u8.ToArray());
        sb.Bytes("holiday.jpg", "MZ\u0090\0"u8.ToArray());
        sb.Bytes("Tool.exe", "MZ"u8.ToArray());
        sb.File("Tool.ini");

        var p = await PlanAsync(sb, sb.Downloads);
        Assert.Equal(SkipKind.Disguised, p.SkipFor("invoice.pdf.exe").Kind);
        Assert.Equal(SkipKind.Disguised, p.SkipFor("holiday.jpg").Kind);
        Assert.Equal(SkipKind.PortableApp, p.SkipFor("Tool.ini").Kind);
        Assert.Empty(p.Actions);
    }

    [Fact]
    public async Task AppData_and_app_owned_folders_are_never_sorted_even_when_picked_by_hand()
    {
        using var sb = new Sandbox();
        var storage = Path.Combine(sb.Locations.LocalAppData, "Plutonium", "storage", "t6", "scripts", "zm");
        sb.File(Path.Combine(storage, "_clientids.gsc"));
        var myGames = Path.Combine(sb.Locations.Documents, "My Games", "Call of Duty");
        sb.File(Path.Combine(myGames, "plutonium_bo2.cfg")); // typed as a game file anywhere else

        foreach (var root in new[] { storage, myGames })
        {
            var p = await PlanAsync(sb, root);
            Assert.Empty(p.Actions);
            Assert.NotNull(Assert.Single(p.Roots).SkipReason);
        }
        var planner = new OrganizePlanner(new SafetyGuard(sb.Locations, sb.AppSettings), sb.InUse, new ManagedFileWalker(), sb.AppSettings, sb.Clock, sb.Locations);
        Assert.NotNull(planner.NeverSortReason(storage)); // the folder picker refuses it too
        Assert.NotNull(planner.NeverSortReason(myGames));
        Assert.Null(planner.NeverSortReason(sb.Downloads));
    }

    [Fact]
    public async Task An_identical_copy_already_sorted_stays_put_instead_of_going_to_quarantine()
    {
        using var sb = new Sandbox();
        sb.File(Path.Combine(sb.Downloads, "Music", "song.mp3"), "same");
        sb.File("song.mp3", "same");

        var p = await PlanAsync(sb, sb.Downloads);
        Assert.DoesNotContain(p.Actions, a => a.Kind is PlannedActionKind.DuplicateQuarantine or PlannedActionKind.Quarantine);
        Assert.Equal(SkipKind.AlreadyThere, p.SkipFor("song.mp3").Kind);
    }

    [Fact]
    public async Task Contents_decide_ambiguous_files()
    {
        using var sb = new Sandbox();
        sb.Bytes("holiday.jpg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10 });
        sb.Bytes("photo.jpg", "PK\u0003\u0004"u8.ToArray()); // really a zip: stays put
        var ts = new byte[188 * 3];
        ts[0] = ts[188] = ts[376] = 0x47;                   // MPEG transport stream packets
        sb.Bytes("capture.ts", ts);
        sb.File("app.ts", "export const answer = 42;");      // TypeScript

        var p = await PlanAsync(sb, sb.Downloads);
        Assert.Equal("Pictures", p.ActionFor("holiday.jpg").RelativeDestFolder());
        Assert.DoesNotContain(p.Actions, a => a.SourcePath.EndsWith("photo.jpg", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Videos", p.ActionFor("capture.ts").RelativeDestFolder());
        Assert.Equal("Code", p.ActionFor("app.ts").RelativeDestFolder());
    }

    [Fact]
    public async Task Things_that_belong_together_stay_put()
    {
        using var sb = new Sandbox();
        sb.File("page.html", "<html>");
        sb.File(Path.Combine("page_files", "style.css"));
        sb.File("bot.py", "print(1)");
        sb.File("run.sh");
        sb.File("app.ico");
        sb.File("Game.cue", "FILE \"Game (Track 1).bin\" BINARY");
        sb.File("Game (Track 1).bin");
        sb.File("Game (Track 2).bin");

        var p = await PlanAsync(sb, sb.Downloads);
        Assert.Empty(p.Actions);
    }

    [Fact]
    public async Task Folders_stay_put_with_Sort_by_type_even_with_Move_whole_folders_on()
    {
        using var sb = new Sandbox();
        sb.File(Path.Combine("holiday photos", "a.jpg"));
        sb.File(Path.Combine("holiday photos", "b.jpg"));
        sb.File("song.mp3");
        var settings = ByType();
        settings.MoveWholeFolders = true;

        var p = await PlanAsync(sb, sb.Downloads, settings);
        Assert.Equal(SkipKind.Folder, p.SkipFor("holiday photos").Kind);
        Assert.All(p.Actions, a => Assert.Equal(PlannedActionKind.Move, a.Kind));
        Assert.All(p.Actions.SelectMany(a => a.Items), i => Assert.Equal(ActionKind.MovePath, i.Kind)); // no folder steps
    }

    [Fact]
    public async Task Other_changed_to_quarantine_never_runs_in_a_Sort_by_type_list()
    {
        using var sb = new Sandbox();
        sb.File("notes.dat");
        var settings = ByType();
        var other = settings.Rules.Single(r => r.Id == DefaultRules.OtherId);
        other.Action.Kind = RuleActionKind.Quarantine;

        var p = await PlanAsync(sb, sb.Downloads, settings);
        Assert.Empty(p.Actions);
        Assert.Equal(SkipKind.RuleBlocked, p.SkipFor("notes.dat").Kind);
    }

    [Fact]
    public async Task A_CloneCD_image_stays_whole()
    {
        using var sb = new Sandbox();
        sb.File("game.img");
        sb.File("game.ccd");
        sb.File("game.sub");
        sb.File("ubuntu.img");

        var p = await PlanAsync(sb, sb.Downloads);
        Assert.DoesNotContain(p.Actions, a => a.SourcePath.EndsWith("game.img", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Disk images", p.ActionFor("ubuntu.img").RelativeDestFolder());
    }

    [Fact]
    public async Task A_Sort_by_type_rule_changed_to_quarantine_never_runs()
    {
        using var sb = new Sandbox();
        sb.File("song.mp3");
        var settings = ByType();
        var music = settings.Rules.First(r => RulePresets.IsByTypeRule(r) && r.Name.Contains("Music", StringComparison.OrdinalIgnoreCase));
        music.Action.Kind = RuleActionKind.Quarantine;

        var p = await PlanAsync(sb, sb.Downloads, settings);
        Assert.Empty(p.Actions);
        Assert.Equal(SkipKind.RuleBlocked, p.SkipFor("song.mp3").Kind);
    }

    [Fact]
    public async Task A_type_folder_that_is_a_link_elsewhere_is_never_filled_and_a_linked_root_is_checked_where_it_leads()
    {
        using var sb = new Sandbox();
        var elsewhere = Path.Combine(sb.Root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        var games = Path.Combine(sb.Downloads, "Games");
        Assert.True(Junction(games, elsewhere), "mklink /J failed");
        sb.File("_clientids.gsc");

        var p = await PlanAsync(sb, sb.Downloads);
        Assert.DoesNotContain(p.Actions, a => a.SourcePath.EndsWith("_clientids.gsc", StringComparison.OrdinalIgnoreCase));
        // The rule's folder is refused before any file is looked at; the rule says why (the file just stays put).
        Assert.Contains(p.Problems, x => x.RuleId == RulePresets.ByTypeRulePrefix + "cod" && x.Blocking);
        Assert.Equal(PathUtil.Normalize(elsewhere), SafetyFolders.ThroughLinks(Path.Combine(games, "x")) is { } real ? Path.GetDirectoryName(real) : null, ignoreCase: true);
        Assert.Null(SafetyFolders.ThroughLinks(sb.Downloads)); // an ordinary folder leads where it says
        Assert.Empty(Directory.EnumerateFileSystemEntries(elsewhere));
    }

    private static bool Junction(string link, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        proc.WaitForExit(10_000);
        return proc.ExitCode == 0 && Directory.Exists(link);
    }

    [Theory]
    [InlineData("ChromeSetup", true)]
    [InlineData("npp.8.6.Installer.x64", true)]
    [InlineData("unins000", false)]
    [InlineData("Uninstall Tool", false)]
    [InlineData("FirefoxPortable", false)]
    [InlineData("Sunset", false)] // "set" inside a word is no installer
    public void Installer_names_are_read_as_whole_words(string name, bool installer)
        => Assert.Equal(installer, PcTidy.Features.Organize.Detection.InstallerDetector.Inspect(name + ".exe", mayReadContent: false).IsInstaller);
}
