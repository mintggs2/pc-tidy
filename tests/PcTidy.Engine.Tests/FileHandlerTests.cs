using System.IO.Compression;
using PcTidy.Core.Model;
using PcTidy.Engine.IO;
using Xunit;
using static PcTidy.Engine.Tests.TreeTools;

namespace PcTidy.Engine.Tests;

public class FileHandlerTests
{
    // -------------------------------------------------------------- MovePath

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Move_folder_into_new_nested_folders_and_undo_restores_everything(bool crossVolume)
    {
        using var sb = new TestSandbox();
        var work = sb.Work();
        var folder = MakeTree(Path.Combine(work, "Holiday 2025"));
        var file = WriteFile(Path.Combine(work, "cat.jpg"), 80_000, 5, extra: FileAttributes.ReadOnly);
        var workBefore = Snapshot(work, includeFolderTimes: false);
        var treeBefore = Snapshot(folder);
        var dest = Path.Combine(sb.Locations.Downloads, "Sorted", "Pictures");
        var engine = sb.Engine(forceCopy: crossVolume);

        var run = await engine.Executor.ExecuteAsync(Plan(
            ScannedItem(ActionKind.MovePath, folder, dest + "\\"),
            ScannedItem(ActionKind.MovePath, file, Path.Combine(dest, "cat.jpg"))), Real, null, default);
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal(treeBefore, Snapshot(Path.Combine(dest, "Holiday 2025")));
        Assert.True(Exists(Path.Combine(dest, "cat.jpg")));
        Assert.False(Exists(folder));

        var report = await engine.Undo.UndoRunAsync(run.RunId, null, default);
        Assert.True(report.Complete, string.Join("; ", report.Messages));
        Assert.Equal(workBefore, Snapshot(work, includeFolderTimes: false));
        Assert.Equal(treeBefore, Snapshot(folder));
        Assert.False(Exists(Path.Combine(sb.Locations.Downloads, "Sorted"))); // folders it created are gone again
    }

    [Fact]
    public async Task Move_clash_policies_autonumber_skip_and_overwrite_by_quarantine()
    {
        using var sb = new TestSandbox();
        var src = sb.Work("src");
        var dst = sb.Work("dst");
        var a = WriteText(Path.Combine(src, "a.txt"), "new a");
        var b = WriteText(Path.Combine(src, "b.txt"), "new b");
        var c = WriteText(Path.Combine(src, "c.txt"), "new c");
        foreach (var n in new[] { "a.txt", "b.txt", "c.txt" }) WriteText(Path.Combine(dst, n), "old " + n);
        var engine = sb.Engine();

        var run = await engine.Executor.ExecuteAsync(Plan(
            ScannedItem(ActionKind.MovePath, a, Path.Combine(dst, "a.txt")) with { ClashPolicy = NameClashPolicy.AutoNumber },
            ScannedItem(ActionKind.MovePath, b, Path.Combine(dst, "b.txt")) with { ClashPolicy = NameClashPolicy.Skip },
            ScannedItem(ActionKind.MovePath, c, Path.Combine(dst, "c.txt")) with { ClashPolicy = NameClashPolicy.Overwrite }), Real, null, default);

        Assert.Equal("new a", File.ReadAllText(Path.Combine(dst, "a (2).txt")));
        Assert.Equal("old a.txt", File.ReadAllText(Path.Combine(dst, "a.txt")));
        Assert.Equal(StepStatus.Skipped, run.Steps[1].Status);
        Assert.True(Exists(b));
        Assert.Equal("new c", File.ReadAllText(Path.Combine(dst, "c.txt")));
        Assert.Single(await engine.Quarantine.ListAsync(run.RunId, default)); // the replaced c.txt, restorable

        await engine.Undo.UndoRunAsync(run.RunId, null, default);
        Assert.Equal("new a", File.ReadAllText(a));
        Assert.Equal("new c", File.ReadAllText(c));
        Assert.Equal("old c.txt", File.ReadAllText(Path.Combine(dst, "c.txt")));
        Assert.False(Exists(Path.Combine(dst, "a (2).txt")));
    }

    [Fact]
    public async Task Undo_leaves_an_item_that_changed_after_the_move_and_never_overwrites()
    {
        using var sb = new TestSandbox();
        var work = sb.Work();
        var a = WriteText(Path.Combine(work, "a.txt"), "a");
        var b = WriteText(Path.Combine(work, "b.txt"), "b");
        var dest = sb.Work("dest");
        var engine = sb.Engine();
        var run = await engine.Executor.ExecuteAsync(Plan(
            ScannedItem(ActionKind.MovePath, a, Path.Combine(dest, "a.txt")),
            ScannedItem(ActionKind.MovePath, b, Path.Combine(dest, "b.txt"))), Real, null, default);

        File.AppendAllText(Path.Combine(dest, "a.txt"), " edited");   // user changed it after the move
        WriteText(b, "a new b in the old place");                       // something new where b was

        var report = await engine.Undo.UndoRunAsync(run.RunId, null, default);
        Assert.Equal(1, report.Restored);
        Assert.Equal(1, report.Skipped);
        Assert.Equal("a edited", File.ReadAllText(Path.Combine(dest, "a.txt")));
        Assert.Equal("a new b in the old place", File.ReadAllText(b));
        Assert.Equal("b", File.ReadAllText(Path.Combine(work, "b (restored).txt")));
        var loaded = (await engine.Journal.LoadAsync(run.RunId, default))!;
        Assert.Equal(RunStatus.PartiallyUndone, loaded.Status);
        Assert.Equal(StepStatus.UndoFailed, loaded.Steps[0].Status);
    }

    [Fact]
    public async Task Moving_a_folder_into_itself_is_refused()
    {
        using var sb = new TestSandbox();
        var folder = MakeTree(Path.Combine(sb.Work(), "Photos"), oddNames: false);
        var before = Snapshot(folder);
        var run = await sb.Engine().Executor.ExecuteAsync(Plan(
            ScannedItem(ActionKind.MovePath, folder, Path.Combine(folder, "sub", "Photos")),
            ScannedItem(ActionKind.MoveFolderWithJunction, folder, Path.Combine(folder, "inside"))), Real, null, default);
        Assert.All(run.Steps, s => Assert.Equal("A folder can't be moved into itself.", s.Message));
        Assert.Equal(before, Snapshot(folder));
    }

    // -------------------------------------------------------------- RenamePath

    [Fact]
    public async Task Rename_swap_and_case_only_rename_work_and_undo()
    {
        using var sb = new TestSandbox();
        var work = sb.Work();
        var a = WriteText(Path.Combine(work, "a.txt"), "content of a");
        var b = WriteText(Path.Combine(work, "b.txt"), "content of b");
        var lower = WriteText(Path.Combine(work, "photo.jpg"), "photo");
        var folder = MakeTree(Path.Combine(work, "my folder"), oddNames: false);
        var before = Snapshot(work, includeFolderTimes: false);
        var engine = sb.Engine();

        var run = await engine.Executor.ExecuteAsync(Plan(
            ScannedItem(ActionKind.RenamePath, a, b),
            ScannedItem(ActionKind.RenamePath, b, a),
            ScannedItem(ActionKind.RenamePath, lower, Path.Combine(work, "Photo.JPG")),
            ScannedItem(ActionKind.RenamePath, folder, Path.Combine(work, "My Folder"))), Real, null, default);

        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal("content of b", File.ReadAllText(a));
        Assert.Equal("content of a", File.ReadAllText(b));
        var names = Directory.EnumerateFileSystemEntries(LongPath.Io(work)).Select(Path.GetFileName).ToList();
        Assert.Contains("Photo.JPG", names);
        Assert.Contains("My Folder", names);
        Assert.DoesNotContain(names, n => n!.StartsWith(".pctidy-rename"));

        var report = await engine.Undo.UndoRunAsync(run.RunId, null, default);
        Assert.True(report.Complete, string.Join("; ", report.Messages));
        Assert.Equal(before, Snapshot(work, includeFolderTimes: false));
        Assert.Contains("photo.jpg", Directory.EnumerateFileSystemEntries(LongPath.Io(work)).Select(Path.GetFileName));
    }

    // -------------------------------------------------------------- CreateDirectory / WriteTextFile

    [Fact]
    public async Task Create_directory_undo_removes_only_empty_folders()
    {
        using var sb = new TestSandbox();
        var work = sb.Work();
        var engine = sb.Engine();
        var run = await engine.Executor.ExecuteAsync(Plan(
            Item(ActionKind.CreateDirectory, dest: Path.Combine(work, "A", "B", "C")),
            Item(ActionKind.CreateDirectory, dest: Path.Combine(work, "Keep")),
            Item(ActionKind.CreateDirectory, dest: work)), Real, null, default);
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Contains("already there", run.Steps[2].Message);
        WriteText(Path.Combine(work, "Keep", "user file.txt"), "mine");

        await engine.Undo.UndoRunAsync(run.RunId, null, default);
        Assert.False(Exists(Path.Combine(work, "A")));
        Assert.True(Exists(Path.Combine(work, "Keep", "user file.txt")));
        Assert.True(Exists(work));
    }

    [Fact]
    public async Task Write_text_file_never_overwrites_and_undo_quarantines_it()
    {
        using var sb = new TestSandbox();
        var work = sb.Work();
        var existing = WriteText(Path.Combine(work, "report.md"), "user's own");
        var engine = sb.Engine();
        var run = await engine.Executor.ExecuteAsync(Plan(
            Item(ActionKind.WriteTextFile, dest: existing) with { Args = new() { ["content"] = "# Report ✓" }, ClashPolicy = NameClashPolicy.Overwrite }), Real, null, default);
        Assert.Equal("user's own", File.ReadAllText(existing));
        Assert.True(run.Steps[0].Status == StepStatus.Done, run.Steps[0].Message);
        var written = Path.Combine(work, "report (2).md");
        Assert.Equal("# Report ✓", File.ReadAllText(written));

        await engine.Undo.UndoRunAsync(run.RunId, null, default);
        Assert.False(Exists(written));
        Assert.Single(await engine.Quarantine.ListAsync(run.RunId, default));
        Assert.Equal("user's own", File.ReadAllText(existing));
    }

    // -------------------------------------------------------------- zip

    [Fact]
    public async Task Archive_to_zip_verifies_quarantines_original_and_undo_restores_exactly()
    {
        using var sb = new TestSandbox();
        var work = sb.Work();
        var folder = MakeTree(Path.Combine(work, "Old Project"));
        var before = Snapshot(folder);
        var zip = Path.Combine(work, "Old Project.zip");
        var engine = sb.Engine();

        var run = await engine.Executor.ExecuteAsync(Plan(ScannedItem(ActionKind.ArchiveToZip, folder, zip)), Real, null, default);
        Assert.Equal(StepStatus.Done, run.Steps[0].Status);
        Assert.False(Exists(folder));
        using (var archive = ZipFile.OpenRead(LongPath.Io(zip)))
        {
            Assert.Equal(before.Count(l => l.StartsWith("F|")), archive.Entries.Count(e => !e.FullName.EndsWith('/')));
            Assert.Contains(archive.Entries, e => e.FullName == "Old Project/ünïcødé ✓ 日本語.txt");
            Assert.Contains(archive.Entries, e => e.FullName == "Old Project/empty folder/");
        }

        var report = await engine.Undo.UndoRunAsync(run.RunId, null, default);
        Assert.True(report.Complete, string.Join("; ", report.Messages));
        Assert.Equal(before, Snapshot(folder));
        Assert.False(Exists(zip));
        Assert.Equal(2, (await engine.Quarantine.ListAsync(run.RunId, default)).Count); // original (restored) + zip
    }

    [Fact]
    public async Task Extract_zip_into_new_folder_and_undo_quarantines_it_keeping_the_zip()
    {
        using var sb = new TestSandbox();
        var work = sb.Work();
        var source = MakeTree(Path.Combine(work, "pack"), oddNames: false);
        var zip = Path.Combine(work, "pack.zip");
        ZipFile.CreateFromDirectory(LongPath.Io(source), LongPath.Io(zip));
        Directory.CreateDirectory(LongPath.Io(Path.Combine(work, "pack-out"))); // name taken → AutoNumber
        var engine = sb.Engine();

        var run = await engine.Executor.ExecuteAsync(Plan(ScannedItem(ActionKind.ExtractZip, zip, Path.Combine(work, "pack-out"))), Real, null, default);
        Assert.Equal(StepStatus.Done, run.Steps[0].Status);
        var outDir = Path.Combine(work, "pack-out (2)");
        Assert.Equal(
            Snapshot(source, false).Where(l => l.StartsWith("F|")).Select(l => string.Join('|', l.Split('|').Take(4))),
            Snapshot(outDir, false).Where(l => l.StartsWith("F|")).Select(l => string.Join('|', l.Split('|').Take(4))));

        await engine.Undo.UndoRunAsync(run.RunId, null, default);
        Assert.False(Exists(outDir));
        Assert.True(Exists(zip));
        Assert.True(Exists(Path.Combine(work, "pack-out")));
    }

    [Fact]
    public async Task Extract_refuses_zip_entries_that_escape_the_folder()
    {
        using var sb = new TestSandbox();
        var work = sb.Work();
        var zip = Path.Combine(work, "evil.zip");
        using (var fs = new FileStream(LongPath.Io(zip), FileMode.CreateNew))
        using (var a = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(a.CreateEntry("ok.txt").Open())) w.Write("ok");
            using (var w = new StreamWriter(a.CreateEntry("../escaped.txt").Open())) w.Write("bad");
        }
        var run = await sb.Engine().Executor.ExecuteAsync(Plan(ScannedItem(ActionKind.ExtractZip, zip, Path.Combine(work, "out"))), Real, null, default);
        Assert.Equal(StepStatus.Failed, run.Steps[0].Status);
        Assert.Contains("unsafe path", run.Steps[0].Message);
        Assert.False(Exists(Path.Combine(work, "escaped.txt")));
        Assert.False(Exists(Path.Combine(work, "out")));
        Assert.False(Exists(Path.Combine(work, "out.pctidy-partial")));
    }

    // -------------------------------------------------------------- MoveFolderWithJunction

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Junction_move_keeps_the_old_path_working_and_undo_restores_exactly(bool crossVolume)
    {
        using var sb = new TestSandbox();
        var games = sb.Work("Games");
        var folder = MakeTree(Path.Combine(games, "Big Game"));
        var before = Snapshot(folder);
        var dest = Path.Combine(sb.SecondDrive("Moved"), "Big Game");
        var engine = sb.Engine(forceCopy: crossVolume);

        var run = await engine.Executor.ExecuteAsync(Plan(ScannedItem(ActionKind.MoveFolderWithJunction, folder, dest)), Real, null, default);
        Assert.Equal(StepStatus.Done, run.Steps[0].Status);
        Assert.True(Junction.PointsTo(folder, dest));
        Assert.Equal(before, Snapshot(dest));
        Assert.Equal(File.ReadAllBytes(LongPath.Io(Path.Combine(dest, "big.dat"))), File.ReadAllBytes(LongPath.Io(Path.Combine(folder, "big.dat"))));
        Assert.False(Exists(folder + ".pctidy-old"));
        Assert.Equal(crossVolume ? 1 : 0, (await engine.Quarantine.ListAsync(run.RunId, default)).Count);

        var report = await engine.Undo.UndoRunAsync(run.RunId, null, default);
        Assert.True(report.Complete, string.Join("; ", report.Messages));
        Assert.False((File.GetAttributes(LongPath.Io(folder)) & FileAttributes.ReparsePoint) != 0);
        Assert.Equal(before, Snapshot(folder));
        Assert.False(Exists(dest));
    }

    [Fact]
    public async Task Junction_undo_moves_back_changes_made_through_the_link()
    {
        using var sb = new TestSandbox();
        var folder = MakeTree(Path.Combine(sb.Work(), "AppData Thing"), oddNames: false);
        var dest = Path.Combine(sb.SecondDrive(), "AppData Thing");
        var engine = sb.Engine(forceCopy: true);
        var run = await engine.Executor.ExecuteAsync(Plan(ScannedItem(ActionKind.MoveFolderWithJunction, folder, dest)), Real, null, default);

        WriteText(Path.Combine(folder, "saved through link.txt"), "new data"); // an app writes via the junction
        await engine.Undo.UndoRunAsync(run.RunId, null, default);
        Assert.Equal("new data", File.ReadAllText(LongPath.Io(Path.Combine(folder, "saved through link.txt"))));
        Assert.False(Exists(dest));
    }
}
