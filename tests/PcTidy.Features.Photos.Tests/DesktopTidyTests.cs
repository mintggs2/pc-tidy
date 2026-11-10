using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Organize.Planning;
using PcTidy.Features.Photos.Desktop;
using PcTidy.Features.Photos.Shared;
using PcTidy.TestbedGen;
using Xunit;

namespace PcTidy.Features.Photos.Tests;

/// <summary>Desktop tidy: the never-moved list, both modes, and OneDrive "ask first" vs. a plain local Desktop.</summary>
public class DesktopTidyTests
{
    private static DesktopTidyRequest Old(string desktop, bool shortcuts = false) => new() { Desktop = desktop, Mode = DesktopTidyMode.MoveOldClutter, IncludeShortcuts = shortcuts };

    [Fact]
    public async Task Never_moves_shortcuts_shell_files_hidden_partial_recent_or_in_use_files()
    {
        using var pc = TestPc.HandMade("never");
        var d = pc.K.Desktop;
        var lnk = TestPc.Put(Path.Combine(d, "App.lnk"), "lnk", 90);
        var url = TestPc.Put(Path.Combine(d, "Site.url"), "[InternetShortcut]", 90);
        var appref = TestPc.Put(Path.Combine(d, "Tool.appref-ms"), "x", 90);
        var ini = TestPc.Put(Path.Combine(d, "desktop.ini"), "[.ShellClassInfo]", 90, FileAttributes.Hidden | FileAttributes.System);
        var hidden = TestPc.Put(Path.Combine(d, "secret.txt"), "x", 90, FileAttributes.Hidden);
        var system = TestPc.Put(Path.Combine(d, "sys.dat"), "x", 90, FileAttributes.System);
        var partial = TestPc.Put(Path.Combine(d, "movie.mp4.crdownload"), "x", 90);
        var office = TestPc.Put(Path.Combine(d, "~$report.docx"), "x", 90);
        var recent = TestPc.Put(Path.Combine(d, "just-saved.txt"), "x", 0);
        var young = TestPc.Put(Path.Combine(d, "last-week.txt"), "x", 7);
        var locked = TestPc.Put(Path.Combine(d, "open.docx"), "x", 90);
        var program = TestPc.Put(Path.Combine(d, "tool.exe"), new byte[] { (byte)'M', (byte)'Z', 0, 0 }, 90);
        var old = TestPc.Put(Path.Combine(d, "old notes.txt"), "x", 90);
        Directory.CreateDirectory(Path.Combine(d, "Project stuff"));

        PcTidy.Features.Organize.Planning.OrganizePreview p;
        using (TestbedBuilder.LockFile(locked))
            p = (await pc.DesktopPlanner().PlanAsync(Old(d), null, CancellationToken.None)).Preview;

        var moved = Assert.Single(p.Actions);
        Assert.True(TestPc.Same(old, moved.SourcePath));
        var month = File.GetLastWriteTime(old).ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(TestPc.Same(Path.Combine(d, "Old desktop", month, "old notes.txt"), moved.DestinationPath!), moved.DestinationPath);

        SkipKind KindOf(string path) => p.Skipped.Single(s => TestPc.Same(s.Path, path)).Kind;
        foreach (var s in new[] { lnk, url, appref }) Assert.Equal(SkipKind.Shortcut, KindOf(s));
        Assert.Equal(SkipKind.SystemFile, KindOf(ini));
        Assert.Equal(SkipKind.Hidden, KindOf(hidden));
        Assert.Equal(SkipKind.SystemFile, KindOf(system));
        Assert.Equal(SkipKind.StillDownloading, KindOf(partial));
        Assert.Equal(SkipKind.StillDownloading, KindOf(office));
        Assert.Equal(SkipKind.RecentlyChanged, KindOf(recent));
        Assert.Equal(SkipKind.LeaveInPlace, KindOf(young));
        Assert.Equal(SkipKind.InUse, KindOf(locked));
        Assert.Equal(SkipKind.PortableApp, KindOf(program));
        Assert.Equal(SkipKind.Folder, KindOf(Path.Combine(d, "Project stuff")));
        Assert.All(p.Skipped, s => Assert.False(string.IsNullOrWhiteSpace(s.Reason)));

        // "Include shortcuts" lets shortcuts move; desktop.ini and the rest still stay.
        var with = (await pc.DesktopPlanner().PlanAsync(Old(d, shortcuts: true), null, CancellationToken.None)).Preview;
        Assert.Contains(with.Actions, a => TestPc.Same(a.SourcePath, lnk));
        Assert.Contains(with.Actions, a => TestPc.Same(a.SourcePath, url));
        Assert.Contains(with.Actions, a => TestPc.Same(a.SourcePath, appref));
        Assert.Contains(with.Skipped, s => TestPc.Same(s.Path, ini));
        Assert.Contains("shortcut still works", with.Actions.First(a => TestPc.Same(a.SourcePath, lnk)).Reason);
    }

    [Fact]
    public async Task Group_by_type_uses_organize_categories_inside_the_desktop()
    {
        using var pc = TestPc.HandMade("group");
        var d = pc.K.Desktop;
        TestPc.Put(Path.Combine(d, "cat.jpg"), "x", 3);
        TestPc.Put(Path.Combine(d, "letter.docx"), "x", 3);
        TestPc.Put(Path.Combine(d, "backup.zip"), "x", 3);
        TestPc.Put(Path.Combine(d, "song.mp3"), "x", 3);
        TestPc.Put(Path.Combine(d, "setup.msi"), "x", 3);
        TestPc.Put(Path.Combine(d, "weird.xyz"), "x", 3);
        TestPc.Put(Path.Combine(d, "App.lnk"), "x", 3);
        Directory.CreateDirectory(Path.Combine(d, "Pictures"));

        var preview = await pc.DesktopPlanner().PlanAsync(new DesktopTidyRequest { Desktop = d, Mode = DesktopTidyMode.GroupByType }, null, CancellationToken.None);
        var p = preview.Preview;

        string Folder(string name) => Path.GetFileName(Path.GetDirectoryName(p.Actions.Single(a => a.Name == name).DestinationPath!)!);
        Assert.Equal("Pictures", Folder("cat.jpg"));
        Assert.Equal("Documents", Folder("letter.docx"));
        Assert.Equal("Archives", Folder("backup.zip"));
        Assert.Equal("Music", Folder("song.mp3"));
        Assert.Equal("Installers", Folder("setup.msi"));
        Assert.All(p.Actions, a => Assert.True(PathUtil.IsUnder(a.DestinationPath!, d)));
        Assert.Contains(p.Skipped, s => s.Name == "weird.xyz" && s.Kind == SkipKind.LeaveInPlace);
        Assert.Contains(p.Skipped, s => s.Name == "App.lnk" && s.Kind == SkipKind.Shortcut);
        Assert.Contains(p.Skipped, s => s.Name == "Pictures" && s.Kind == SkipKind.AlreadySorted);
        // Pictures already exists: only the missing folders are created.
        var cat = p.Actions.Single(a => a.Name == "cat.jpg");
        Assert.DoesNotContain(cat.Items, i => i.Kind == ActionKind.CreateDirectory);
        Assert.Contains(p.Actions.Single(a => a.Name == "letter.docx").Items, i => i.Kind == ActionKind.CreateDirectory);

        // Before → after tree comes from Organize's shared preview.
        var after = PreviewTree.After(p);
        Assert.Contains(after, r => r.Name == "Documents" && r.IsNew);
    }

    [Fact]
    public async Task OneDrive_desktop_asks_first_and_moves_stay_inside_it()
    {
        using var pc = TestPc.Generated("onedrive-desktop", SandboxProfile.Default);
        var desktop = pc.K.Desktop;
        Assert.True(pc.Host.Safety.IsInCloudRoot(Path.Combine(desktop, "x.txt")));

        var asking = await pc.DesktopPlanner().PlanAsync(Old(desktop), null, CancellationToken.None);
        Assert.True(asking.NeedsPermission);
        Assert.NotEmpty(asking.Preview.Actions);
        var result = DesktopTidyResults.Build(asking);
        var moves = result.AllItems.Where(i => i.Actions.Count > 0).ToList();
        Assert.All(moves, i =>
        {
            Assert.False(i.DefaultSelected);
            Assert.Equal(DesktopTidyPlanner.PermissionReason, i.DisabledReason);
        });
        // Said once (in the page's OneDrive bar): no "asks first" note, and the rows don't repeat it either.
        Assert.DoesNotContain(asking.Preview.Notes, n => n.Contains("OneDrive", StringComparison.Ordinal));
        Assert.All(moves, i => Assert.DoesNotContain("Allow PC Tidy", new PcTidy.Features.Photos.UI.ChangeRow(i, _ => { }).Reason));
        // The guard agrees: not before the user says yes.
        var firstMove = moves[0].Actions.Last();
        Assert.Equal(PathProtection.AskFirst, pc.Host.Safety.Check(firstMove).Protection);
        Assert.False(pc.Host.Safety.Check(firstMove).Allowed);
        Assert.Empty(PlanBuilder.FromSelection(DesktopTidyModule.FeatureId, "Desktop", moves).Items);

        CloudFolders.Allow(desktop, pc.Host.Settings);
        var allowed = await pc.DesktopPlanner().PlanAsync(Old(desktop), null, CancellationToken.None);
        Assert.Equal(FolderAccess.AllowedCloud, allowed.Access);
        var items = DesktopTidyResults.Build(allowed).AllItems.Where(i => i.Actions.Count > 0).ToList();
        Assert.Equal(asking.Preview.Actions.Count, items.Count);
        Assert.All(items, i =>
        {
            Assert.Null(i.DisabledReason);
            Assert.True(i.DefaultSelected);
            Assert.Contains("OneDrive only renames it", i.Reason);
            Assert.All(i.Actions, a => Assert.True(pc.Host.Safety.Check(a).Allowed, a.Description + ": " + pc.Host.Safety.Check(a).Reason));
            Assert.True(PathUtil.IsUnder(i.Actions.Last().DestinationPath!, desktop));
        });
        // The testbed's 45-day-old "Untitled.txt" is old clutter; the 1-day-old "todo.txt" isn't.
        Assert.Contains(allowed.Preview.Actions, a => a.Name == "Untitled.txt");
        Assert.Contains(allowed.Preview.Skipped, s => s.Name == "todo.txt" && s.Kind == SkipKind.LeaveInPlace);
        Assert.Contains(allowed.Preview.Skipped, s => s.Name == "desktop.ini");
        Assert.All(allowed.Preview.Skipped.Where(s => s.Name.EndsWith(".lnk") || s.Name.EndsWith(".url")), s => Assert.Equal(SkipKind.Shortcut, s.Kind));
    }

    [Fact]
    public async Task Local_desktop_on_a_minimal_pc_is_tidied_directly()
    {
        using var pc = TestPc.Generated("minimal-desktop", SandboxProfile.Minimal);
        var preview = await pc.DesktopPlanner().PlanAsync(Old(pc.K.Desktop), null, CancellationToken.None);
        Assert.Equal(FolderAccess.Local, preview.Access);
        var items = DesktopTidyResults.Build(preview).AllItems.Where(i => i.Actions.Count > 0).ToList();
        Assert.NotEmpty(items);
        Assert.All(items, i =>
        {
            Assert.Null(i.DisabledReason);
            Assert.True(i.DefaultSelected);
            Assert.All(i.Actions, a => Assert.True(pc.Host.Safety.Check(a).Allowed, a.Description));
        });
    }
}
