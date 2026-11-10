using System.IO.Compression;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Data.Sqlite;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Engine.Handlers;
using PcTidy.Engine.IO;
using Xunit;
using static PcTidy.Engine.Tests.TreeTools;

namespace PcTidy.Engine.Tests;

/// <summary>v2/v3 engine handlers: every one is run → undone → checked byte for byte (content, times, attributes).</summary>
public class V2HandlerTests
{
    private static PlanItem Edit(string path, string content, Dictionary<string, string>? extra = null)
    {
        var args = new Dictionary<string, string> { ["content"] = content };
        foreach (var (k, v) in extra ?? new()) args[k] = v;
        return Item(ActionKind.EditTextFile, path) with { Args = args, ExpectedLastWriteUtc = FsOps.Stat(path)?.LastWriteTimeUtc };
    }

    private static string WriteBytes(string path, byte[] bytes, FileAttributes extra = 0)
    {
        var io = LongPath.Io(path);
        Directory.CreateDirectory(Path.GetDirectoryName(io)!);
        File.WriteAllBytes(io, bytes);
        File.SetCreationTimeUtc(io, OldCreation);
        File.SetLastWriteTimeUtc(io, OldTime);
        if (extra != 0) File.SetAttributes(io, File.GetAttributes(io) | extra);
        return LongPath.Display(io);
    }

    private static byte[] Bytes(string path) => File.ReadAllBytes(LongPath.Io(path));

    private static async Task<List<string>> NoLeftovers(string folder) => await Task.FromResult(
        Directory.EnumerateFileSystemEntries(LongPath.Io(folder)).Select(Path.GetFileName).Where(n => n!.Contains(".pctidy-")).ToList()!);

    // ------------------------------------------------------------------ EditTextFile

    [Fact]
    public async Task Edit_keeps_utf8_bom_and_crlf_backs_up_and_undo_is_byte_identical()
    {
        using var sb = new TestSandbox();
        var work = sb.Work("My Games");
        var ini = WriteBytes(Path.Combine(work, "settings.ini"),
            new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("[Video]\r\nVSync=1\r\nName=Ünïcødé\r\n")).ToArray(), FileAttributes.ReadOnly);
        var before = FileSnapshot(ini);
        var engine = sb.Engine();

        var run = await engine.Executor.ExecuteAsync(Plan(Edit(ini, "[Video]\nVSync=0\nName=Ünïcødé\n")), Real, null, default);

        Assert.True(run.Steps[0].Status == StepStatus.Done, run.Steps[0].Status + ": " + run.Steps[0].Message);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("[Video]\r\nVSync=0\r\nName=Ünïcødé\r\n")), Bytes(ini));
        Assert.True((File.GetAttributes(LongPath.Io(ini)) & FileAttributes.ReadOnly) != 0); // stays read-only
        var backup = Assert.Single(await engine.Quarantine.ListAsync(run.RunId, default));
        Assert.EndsWith(".pctidy-backup", backup.OriginalPath);
        Assert.Empty(await NoLeftovers(work));

        var undo = await engine.Undo.UndoRunAsync(run.RunId, null, default);
        Assert.True(undo.Complete, string.Join("; ", undo.Messages));
        Assert.Equal(before, FileSnapshot(ini));
        Assert.Empty(await NoLeftovers(work));
    }

    [Fact]
    public async Task Edit_keeps_utf16_and_lf_files_as_they_are()
    {
        using var sb = new TestSandbox();
        var work = sb.Work();
        var utf16 = WriteBytes(Path.Combine(work, "a.cfg"), new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("a=1\r\nb=2\r\n")).ToArray());
        var lf = WriteBytes(Path.Combine(work, "b.json"), Encoding.UTF8.GetBytes("{\n  \"x\": 1\n}\n"));
        var snapshots = new[] { FileSnapshot(utf16), FileSnapshot(lf) };
        var engine = sb.Engine();

        var run = await engine.Executor.ExecuteAsync(Plan(Edit(utf16, "a=1\r\nb=3\r\n"), Edit(lf, "{\r\n  \"x\": 2\r\n}\r\n")), Real, null, default);

        Assert.Equal(2, run.DoneCount);
        Assert.Equal(new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("a=1\r\nb=3\r\n")), Bytes(utf16));
        Assert.Equal(Encoding.UTF8.GetBytes("{\n  \"x\": 2\n}\n"), Bytes(lf)); // LF file stays LF, no BOM added
        await engine.Undo.UndoRunAsync(run.RunId, null, default);
        Assert.Equal(snapshots, new[] { FileSnapshot(utf16), FileSnapshot(lf) });
    }

    [Fact]
    public async Task Edit_keeps_an_ansi_file_ansi_and_refuses_text_it_cant_hold()
    {
        using var sb = new TestSandbox();
        var ansi = CodePagesEncodingProvider.Instance.GetEncoding((int)GetACP());
        if (ansi is null || GetACP() == 65001) return; // this PC has no single ANSI code page to test with
        var file = WriteBytes(Path.Combine(sb.Work(), "old.ini"), ansi.GetBytes("name=") .Concat(new byte[] { 0xFF, 0xFE, 0xFD }).ToArray());
        Assert.StartsWith("ansi", TextFormat.Detect(Bytes(file)).Name);
        var engine = sb.Engine();

        var run = await engine.Executor.ExecuteAsync(Plan(Edit(file, "name=x 😀")), Real, null, default);

        Assert.Equal(StepStatus.Skipped, run.Steps[0].Status);
        Assert.Contains("can't store", run.Steps[0].Message);
        run = await engine.Executor.ExecuteAsync(Plan(Edit(file, "name=plain")), Real, null, default);
        Assert.True(run.Steps[0].Status == StepStatus.Done, run.Steps[0].Status + ": " + run.Steps[0].Message);
        Assert.Equal(ansi.GetBytes("name=plain"), Bytes(file));
    }

    [Fact]
    public async Task Edit_is_skipped_when_the_file_changed_since_the_scan_or_is_open_for_writing()
    {
        using var sb = new TestSandbox();
        var file = WriteText(Path.Combine(sb.Work(), "c.ini"), "a=1");
        var stale = Edit(file, "a=2") with { ExpectedLastWriteUtc = OldTime.AddDays(-3) };
        var engine = sb.Engine();

        var run = await engine.Executor.ExecuteAsync(Plan(stale), Real, null, default);
        Assert.Equal(StepStatus.Skipped, run.Steps[0].Status);
        Assert.Equal("a=1", File.ReadAllText(file));

        using (new FileStream(LongPath.Io(file), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            run = await engine.Executor.ExecuteAsync(Plan(Edit(file, "a=2")), Real, null, default);
            Assert.Equal(StepStatus.Skipped, run.Steps[0].Status);
            Assert.Contains("in use", run.Steps[0].Message);
        }
        Assert.Equal("a=1", File.ReadAllText(file));
    }

    [Fact]
    public async Task Edit_creates_a_missing_config_and_undo_removes_it_and_its_folders()
    {
        using var sb = new TestSandbox();
        var work = sb.Work();
        var file = Path.Combine(work, "pip", "pip.ini");
        var item = Item(ActionKind.EditTextFile, file) with { Args = new() { ["content"] = "[global]\ncache-dir = D:\\cache\n", ["createIfMissing"] = "true" } };
        var engine = sb.Engine();

        var run = await engine.Executor.ExecuteAsync(Plan(item), Real, null, default);

        Assert.True(run.Steps[0].Status == StepStatus.Done, run.Steps[0].Status + ": " + run.Steps[0].Message);
        Assert.Equal("[global]\ncache-dir = D:\\cache\n", File.ReadAllText(file));
        await engine.Undo.UndoRunAsync(run.RunId, null, default);
        Assert.False(Exists(Path.Combine(work, "pip")));
    }

    [Fact]
    public async Task Undo_leaves_a_file_the_app_rewrote_after_the_edit()
    {
        using var sb = new TestSandbox();
        var file = WriteText(Path.Combine(sb.Work(), "localconfig.vdf"), "\"LaunchOptions\" \"\"");
        var engine = sb.Engine();
        var run = await engine.Executor.ExecuteAsync(Plan(Edit(file, "\"LaunchOptions\" \"-novid\"")), Real, null, default);
        File.WriteAllText(LongPath.Io(file), "rewritten by the app");

        var undo = await engine.Undo.UndoRunAsync(run.RunId, null, default);

        Assert.Equal(1, undo.Skipped);
        Assert.Equal("rewritten by the app", File.ReadAllText(file));
        Assert.Single(await engine.Quarantine.ListAsync(run.RunId, default)); // the original is still safe there
    }

    [Fact]
    public async Task Edit_crash_recovery_knows_whether_the_swap_happened()
    {
        using var sb = new TestSandbox();
        var work = sb.Work();
        var file = WriteText(Path.Combine(work, "d.ini"), "x=1");
        var engine = sb.Engine();
        var ctx = engine.Executor.Context("20260101-000000-crashe", ExecutionMode.Real);
        var handler = new EditTextFileHandler();
        var prepared = handler.Prepare(new StepRecord { Index = 0, Item = Edit(file, "x=2") }, ctx);

        // Power cut before anything happened (only our temp file was written).
        File.WriteAllText(LongPath.Io(prepared.UndoData![UndoKeys.TempPath]), "partial");
        Assert.False(await handler.WasCompletedAsync(prepared, ctx, default));
        Assert.Empty(await NoLeftovers(work));

        // Power cut after the swap, before the result was journaled.
        Assert.Equal(StepStatus.Done, (await handler.ExecuteAsync(prepared, ctx, default)).Status);
        Assert.True(await handler.WasCompletedAsync(prepared, ctx, default));
        var undone = await handler.UndoAsync(prepared with { Status = StepStatus.Done }, ctx, default);
        Assert.Equal(StepStatus.Undone, undone.Status);
        Assert.Equal("x=1", File.ReadAllText(file));
    }

    [Fact]
    public async Task Edit_with_the_real_safety_guard_on_a_game_config()
    {
        var root = Path.Combine(TestSandbox.TestbedRootPath, "host-edit-" + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            using var host = PcTidy.Hosting.PcTidyHost.CreateSandbox(root, SandboxProfile.Minimal); // Documents not in OneDrive
            var ini = WriteText(Path.Combine(host.Locations.Documents, "My Games", "Some Game", "GameUserSettings.ini"), "[Display]\r\nFrameRateLimit=60\r\n");
            var before = FileSnapshot(ini);
            var item = Edit(ini, "[Display]\r\nFrameRateLimit=0\r\n") with { FeatureId = "games" };

            var run = await host.Executor.ExecuteAsync(Plan(item), Real, null, default);
            Assert.True(run.Steps[0].Status == StepStatus.Done, run.Steps[0].Status + ": " + run.Steps[0].Message);
            Assert.Contains("FrameRateLimit=0", File.ReadAllText(ini));

            var undo = await host.Undo.UndoRunAsync(run.RunId, null, default);
            Assert.True(undo.Complete, string.Join("; ", undo.Messages));
            Assert.Equal(before, FileSnapshot(ini));
        }
        finally { FsOps.DeleteTree(root); }
    }

    // ------------------------------------------------------------------ ShredFile

    private static PlanItem Shred(string path) => ScannedItem(ActionKind.ShredFile, path) with { Reversible = false };

    [Fact]
    public async Task Shred_overwrites_and_deletes_a_file_with_streams_and_read_only_and_says_the_drive_type()
    {
        using var sb = new TestSandbox();
        var work = sb.Work("secret");
        var file = WriteFile(Path.Combine(work, "tax return.pdf"), 300_000, 3, extra: FileAttributes.ReadOnly);
        File.SetAttributes(LongPath.Io(file), FileAttributes.Normal);
        File.WriteAllText(LongPath.Io(file) + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        File.SetAttributes(LongPath.Io(file), FileAttributes.ReadOnly);
        var engine = sb.Engine();

        var run = await engine.Executor.ExecuteAsync(Plan(Shred(file)), Real, null, default);

        Assert.True(run.Steps[0].Status == StepStatus.Done, run.Steps[0].Status + ": " + run.Steps[0].Message);
        Assert.Contains("deleted for good", run.Steps[0].Message);
        Assert.Contains(run.Steps[0].UndoData![UndoKeys.Media], new[] { "hdd", "ssd", "unknown" });
        Assert.Empty(Directory.EnumerateFileSystemEntries(LongPath.Io(work)));
        Assert.Empty(await engine.Quarantine.ListAsync(run.RunId, default)); // nothing kept: it's permanent

        var undo = await engine.Undo.UndoRunAsync(run.RunId, null, default);
        Assert.Equal(0, undo.Restored);
        Assert.Contains("can't be undone", Assert.Single(undo.Messages));
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string newFile, string existing, IntPtr security);

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();

    [Fact]
    public async Task Shred_refuses_hard_links_folders_and_items_not_marked_permanent()
    {
        using var sb = new TestSandbox();
        var work = sb.Work();
        var file = WriteFile(Path.Combine(work, "a.bin"), 1000, 1);
        Assert.True(CreateHardLink(LongPath.Io(Path.Combine(work, "b.bin")), LongPath.Io(file), IntPtr.Zero));
        var folder = sb.Work("folder");
        var plain = WriteFile(Path.Combine(work, "c.bin"), 1000, 2);
        var before = Snapshot(work);
        var engine = sb.Engine();

        var run = await engine.Executor.ExecuteAsync(Plan(
            Shred(file),
            Item(ActionKind.ShredFile, folder, isDir: true) with { Reversible = false },
            Shred(plain) with { Reversible = true }), Real, null, default);

        Assert.All(run.Steps, s => Assert.Equal(StepStatus.Skipped, s.Status));
        Assert.Contains("hard link", run.Steps[0].Message);
        Assert.Equal(before, Snapshot(work));
    }

    // ------------------------------------------------------------------ Duplicates: verify before remove

    private static PlanItem Duplicate(string path, string keep, string hash, bool folder = false)
        => ScannedItem(ActionKind.QuarantinePath, path) with
        {
            Args = new() { ["verifyHash"] = hash, ["keepPath"] = keep, ["verifyMode"] = folder ? "folder" : "file" },
        };

    [Fact]
    public async Task Duplicate_file_goes_only_while_both_copies_still_match()
    {
        using var sb = new TestSandbox();
        var work = sb.Work();
        var keep = WriteFile(Path.Combine(work, "keep", "photo.jpg"), 50_000, 7);
        var dup1 = WriteFile(Path.Combine(work, "dup1", "photo.jpg"), 50_000, 7);
        var dup2 = WriteFile(Path.Combine(work, "dup2", "photo.jpg"), 50_000, 7);
        var hash = FileHash.Full(LongPath.Io(keep));
        var before = Snapshot(work, includeFolderTimes: false);
        var engine = sb.Engine();

        var run = await engine.Executor.ExecuteAsync(Plan(Duplicate(dup1, keep, hash)), Real, null, default);
        Assert.True(run.Steps[0].Status == StepStatus.Done, run.Steps[0].Status + ": " + run.Steps[0].Message);
        await engine.Undo.UndoRunAsync(run.RunId, null, default);
        Assert.Equal(before, Snapshot(work, includeFolderTimes: false));

        // The kept copy was edited after the scan: the duplicate must stay.
        var item = Duplicate(dup2, keep, hash);
        using (var fs = new FileStream(LongPath.Io(keep), FileMode.Open, FileAccess.Write)) { fs.WriteByte(0x42); }
        run = await engine.Executor.ExecuteAsync(Plan(item), Real, null, default);
        Assert.Equal(StepStatus.Skipped, run.Steps[0].Status);
        Assert.Contains("copy that stays changed", run.Steps[0].Message);
        Assert.True(Exists(dup2));
    }

    [Fact]
    public async Task Duplicate_file_is_refused_when_the_kept_copy_is_the_same_file_or_gone()
    {
        using var sb = new TestSandbox();
        var work = sb.Work();
        var file = WriteFile(Path.Combine(work, "a.bin"), 5000, 3);
        var hash = FileHash.Full(LongPath.Io(file));
        Assert.True(CreateHardLink(LongPath.Io(Path.Combine(work, "link.bin")), LongPath.Io(file), IntPtr.Zero));
        var engine = sb.Engine();

        var run = await engine.Executor.ExecuteAsync(Plan(
            Duplicate(file, Path.Combine(work, "link.bin"), hash),
            Duplicate(file, Path.Combine(work, "missing.bin"), hash)), Real, null, default);

        Assert.All(run.Steps, s => Assert.Equal(StepStatus.Skipped, s.Status));
        Assert.True(Exists(file));
    }

    /// <summary>The Duplicates section's folder signature, written independently of the engine's.</summary>
    private static string? FolderSignature(string dir)
    {
        var lines = new List<string>();
        int files = 0;
        foreach (var f in Directory.EnumerateFiles(LongPath.Io(dir)))
        {
            var len = new FileInfo(f).Length;
            lines.Add($"F|{Path.GetFileName(f).ToUpperInvariant()}|{len}|{(len == 0 ? "EMPTY" : FileHash.Full(f))}");
            files++;
        }
        foreach (var d in Directory.EnumerateDirectories(LongPath.Io(dir)))
        {
            var s = FolderSignature(d);
            if (s is null) return null;
            lines.Add($"D|{Path.GetFileName(d).ToUpperInvariant()}|{s}");
            files++;
        }
        if (files == 0) return null;
        lines.Sort(StringComparer.Ordinal);
        return Convert.ToHexString(XxHash3.Hash(Encoding.UTF8.GetBytes(string.Join("\n", lines))));
    }

    [Fact]
    public async Task Duplicate_folder_goes_only_while_names_sizes_and_hashes_of_both_trees_match()
    {
        using var sb = new TestSandbox();
        var work = sb.Work();
        foreach (var name in new[] { "Album", "Album - Copy", "Album (old)" })
        {
            WriteFile(Path.Combine(work, name, "a.jpg"), 20_000, 1);
            WriteFile(Path.Combine(work, name, "raw", "b.cr2"), 30_000, 2);
            WriteFile(Path.Combine(work, name, "empty.txt"), 0, 3);
        }
        var keep = Path.Combine(work, "Album");
        var sig = FolderSignature(keep)!;
        Assert.Equal(sig, DuplicateVerifier.Signature(keep, default));
        var before = Snapshot(work, includeFolderTimes: false);
        var engine = sb.Engine();

        var run = await engine.Executor.ExecuteAsync(Plan(Duplicate(Path.Combine(work, "Album - Copy"), keep, sig, folder: true)), Real, null, default);
        Assert.True(run.Steps[0].Status == StepStatus.Done, run.Steps[0].Status + ": " + run.Steps[0].Message);
        await engine.Undo.UndoRunAsync(run.RunId, null, default);
        Assert.Equal(before, Snapshot(work, includeFolderTimes: false));

        WriteFile(Path.Combine(keep, "raw", "b.cr2"), 30_000, 99); // same name and size, different content
        run = await engine.Executor.ExecuteAsync(Plan(Duplicate(Path.Combine(work, "Album (old)"), keep, sig, folder: true)), Real, null, default);
        Assert.Equal(StepStatus.Skipped, run.Steps[0].Status);
        Assert.True(Exists(Path.Combine(work, "Album (old)")));
    }

    [Fact]
    public async Task Duplicate_folder_is_kept_when_the_folder_that_stays_is_the_same_folder_through_a_junction()
    {
        // DUP-V3-1: "Music\Archive" is a junction to "Real\Archive" (as Move to drive leaves it). The two spellings of
        // Album have identical signatures, but they are one folder: quarantining one would take the only copy.
        using var sb = new TestSandbox();
        var work = sb.Work();
        var real = Path.Combine(work, "Real", "Archive");
        WriteFile(Path.Combine(real, "Album", "a.jpg"), 20_000, 1);
        WriteFile(Path.Combine(real, "Album", "raw", "b.cr2"), 30_000, 2);
        Directory.CreateDirectory(Path.Combine(work, "Music"));
        var link = Path.Combine(work, "Music", "Archive");
        Junction.Create(link, real);
        try
        {
            var source = Path.Combine(real, "Album");
            var keep = Path.Combine(link, "Album");
            var sig = FolderSignature(source)!;
            Assert.Equal(sig, DuplicateVerifier.Signature(keep, default));
            Assert.True(DuplicateVerifier.SameOrNestedFolder(source, keep));
            Assert.True(DuplicateVerifier.SameOrNestedFolder(Path.Combine(real, "Album", "raw"), keep)); // inside it
            Assert.False(DuplicateVerifier.SameOrNestedFolder(source, Path.Combine(work, "Music")));

            var engine = sb.Engine();
            var run = await engine.Executor.ExecuteAsync(Plan(Duplicate(source, keep, sig, folder: true)), Real, null, default);
            Assert.Equal(StepStatus.Skipped, run.Steps[0].Status);
            Assert.Contains("same folder", run.Steps[0].Message);
            Assert.True(Exists(Path.Combine(source, "a.jpg")));
        }
        finally { Junction.Remove(link); }
    }

    // ------------------------------------------------------------------ ArchiveToZip manifest + ExtractZip unwrap

    /// <summary>Name, size and content hash of every file (a zip keeps no attributes or creation times).</summary>
    private static List<string> Contents(IEnumerable<string> snapshot)
        => snapshot.Where(l => l.StartsWith("F|")).Select(l => string.Join("|", l.Split('|').Take(4))).ToList();

    [Fact]
    public async Task Archive_writes_its_manifest_and_extract_unwraps_into_the_original_place()
    {
        using var sb = new TestSandbox();
        var work = sb.Work();
        var folder = MakeTree(Path.Combine(work, "Old project"), oddNames: false);
        var before = Snapshot(folder, includeFolderTimes: false);
        var zip = Path.Combine(work, "Archive", "2026", "Old project.zip");
        const string manifest = "{\"source\":\"Old project\",\"by\":\"PC Tidy\"}";
        var engine = sb.Engine();

        var run = await engine.Executor.ExecuteAsync(Plan(ScannedItem(ActionKind.ArchiveToZip, folder, zip) with { Args = new() { ["manifestJson"] = manifest } }), Real, null, default);
        Assert.True(run.Steps[0].Status == StepStatus.Done, run.Steps[0].Status + ": " + run.Steps[0].Message);
        using (var archive = ZipFile.OpenRead(LongPath.Io(zip)))
        {
            using var reader = new StreamReader(archive.GetEntry(ArchiveToZipHandler.ManifestEntryName)!.Open());
            Assert.Equal(manifest, reader.ReadToEnd());
        }

        // Restore: the zip's "Old project" folder is unwrapped straight into the original place.
        var restore = Item(ActionKind.ExtractZip, zip, folder) with { Args = new() { [ExtractZipHandler.UnwrapRootArg] = "Old project" } };
        var extracted = await engine.Executor.ExecuteAsync(Plan(restore), Real, null, default);
        Assert.Equal(StepStatus.Done, extracted.Steps[0].Status);
        Assert.Equal(Contents(before), Contents(Snapshot(folder, includeFolderTimes: false))); // same files, same bytes
        Assert.False(Exists(Path.Combine(folder, ArchiveToZipHandler.ManifestEntryName)));
        Assert.False(Exists(Path.Combine(folder, "Old project")));

        // Undo the archive (after undoing the restore): the original is back and the folders made for the zip are gone.
        await engine.Undo.UndoRunAsync(extracted.RunId, null, default);
        var undo = await engine.Undo.UndoRunAsync(run.RunId, null, default);
        Assert.True(undo.Complete, string.Join("; ", undo.Messages));
        Assert.Equal(before, Snapshot(folder, includeFolderTimes: false));
        Assert.False(Exists(Path.Combine(work, "Archive")));
    }

    // ------------------------------------------------------------------ CookiesDeleteExcept

    private static string MakeCookieDb(string path, bool firefox, params string[] hosts)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LongPath.Io(path))!);
        var cs = new SqliteConnectionStringBuilder { DataSource = LongPath.Io(path), Pooling = false }.ToString();
        using (var c = new SqliteConnection(cs))
        {
            c.Open();
            using var create = c.CreateCommand();
            create.CommandText = firefox
                ? "CREATE TABLE moz_cookies (id INTEGER PRIMARY KEY, host TEXT, name TEXT, value TEXT)"
                : "CREATE TABLE meta (key TEXT, value TEXT); CREATE TABLE cookies (host_key TEXT NOT NULL, name TEXT NOT NULL, value TEXT, path TEXT)";
            create.ExecuteNonQuery();
            foreach (var h in hosts)
            {
                using var ins = c.CreateCommand();
                ins.CommandText = firefox ? "INSERT INTO moz_cookies (host, name, value) VALUES ($h, 'n', 'v')" : "INSERT INTO cookies VALUES ($h, 'n', 'v', '/')";
                ins.Parameters.AddWithValue("$h", h);
                ins.ExecuteNonQuery();
            }
        }
        File.SetLastWriteTimeUtc(LongPath.Io(path), OldTime);
        return LongPath.Display(LongPath.Io(path));
    }

    private static List<string> CookieHostsIn(string db, bool firefox)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = LongPath.Io(db), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        c.Open();
        using var q = c.CreateCommand();
        q.CommandText = firefox ? "SELECT host FROM moz_cookies ORDER BY host" : "SELECT host_key FROM cookies ORDER BY host_key";
        using var r = q.ExecuteReader();
        var l = new List<string>();
        while (r.Read()) l.Add(r.GetString(0));
        return l;
    }

    private static PlanItem Cookies(string browser, string profile, string keep) => new()
    {
        Kind = ActionKind.CookiesDeleteExcept, FeatureId = "privacy", Description = "Delete cookies",
        Args = new() { ["browser"] = browser, ["profileDir"] = profile, ["keepHosts"] = keep },
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cookies_are_deleted_except_kept_sites_and_undo_restores_the_exact_database(bool firefox)
    {
        using var sb = new TestSandbox();
        var profile = sb.Work(Path.Combine("Browser", "Profile 1"));
        var db = MakeCookieDb(firefox ? Path.Combine(profile, "cookies.sqlite") : Path.Combine(profile, "Network", "Cookies"), firefox,
            ".google.com", "accounts.google.com", "notgoogle.com", "example.com", ".ads.example.net", "mail.proton.me");
        var other = WriteText(Path.Combine(profile, "History"), "never touched");
        var before = Snapshot(profile, includeFolderTimes: false);
        var engine = sb.Engine();
        // Firefox needs browser = "firefox", whose process must be closed: that variant can't run while Firefox is open here.
        if (firefox && System.Diagnostics.Process.GetProcessesByName("firefox").Length > 0) return;

        // Chromium variant: a browser name PC Tidy doesn't know, so the test never depends on which apps run on this PC.
        var run = await engine.Executor.ExecuteAsync(Plan(Cookies(firefox ? "firefox" : "testbrowser", profile, "google.com\n.proton.me\n")), Real, null, default);

        Assert.True(run.Steps[0].Status == StepStatus.Done, run.Steps[0].Status + ": " + run.Steps[0].Message);
        Assert.Contains("Removed 3 cookie(s)", run.Steps[0].Message);
        Assert.Equal(new[] { ".google.com", "accounts.google.com", "mail.proton.me" }, CookieHostsIn(db, firefox));
        Assert.Equal("never touched", File.ReadAllText(other));

        var undo = await engine.Undo.UndoRunAsync(run.RunId, null, default);
        Assert.True(undo.Complete, string.Join("; ", undo.Messages));
        Assert.Equal(before, Snapshot(profile, includeFolderTimes: false));
    }

    [Fact]
    public async Task Cookies_are_left_alone_while_the_browser_has_the_database_open()
    {
        using var sb = new TestSandbox();
        var profile = sb.Work("Profile");
        var db = MakeCookieDb(Path.Combine(profile, "Network", "Cookies"), false, "example.com");
        var before = Snapshot(profile, includeFolderTimes: false);
        var engine = sb.Engine();

        using (new FileStream(LongPath.Io(db), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            var run = await engine.Executor.ExecuteAsync(Plan(Cookies("testbrowser", profile, "")), Real, null, default);
            Assert.Equal(StepStatus.Skipped, run.Steps[0].Status);
            Assert.Contains("Close", run.Steps[0].Message);
        }
        Assert.Equal(before, Snapshot(profile, includeFolderTimes: false));
    }

    [Theory]
    [InlineData(".example.com", "example.com", true)]
    [InlineData("a.b.example.com", "example.com", true)]
    [InlineData("badexample.com", "example.com", false)]
    [InlineData("example.com", "https://Example.com:443/login", true)]
    [InlineData("example.org", "example.com", false)]
    public void Cookie_hosts_keep_the_site_and_its_subdomains_only(string cookieHost, string keep, bool kept)
        => Assert.Equal(kept, CookieHosts.IsKept(cookieHost, CookieHosts.Parse(keep)));

    // ------------------------------------------------------------------ HostsCommentOut

    private static string MakeHosts(IKnownLocations locations, string text)
        => WriteBytes(HostsCommentOutHandler.HostsPath(locations), Encoding.ASCII.GetBytes(text));

    private const string HostsText = "# Copyright (c) Microsoft Corp.\r\n127.0.0.1 localhost\r\n0.0.0.0 telemetry.example\r\n0.0.0.0 ads.example\r\n";

    private static PlanItem Hosts(string path, params string[] lines) => new()
    {
        Kind = ActionKind.HostsCommentOut, FeatureId = "tweaks", Description = "Turn off hosts lines", SourcePath = path,
        NeedsAdmin = true, IsSystemChange = true, Args = new() { ["lines"] = string.Join("\n", lines) },
    };

    [Fact]
    public async Task Hosts_lines_are_commented_out_exactly_and_undo_restores_the_file()
    {
        using var sb = new TestSandbox();
        var hosts = MakeHosts(sb.Locations, HostsText);
        var before = FileSnapshot(hosts);
        var engine = sb.Engine();

        var run = await engine.Executor.ExecuteAsync(Plan(Hosts(hosts, "0.0.0.0 telemetry.example", "0.0.0.0 not-there")), Real, null, default);

        Assert.True(run.Steps[0].Status == StepStatus.Done, run.Steps[0].Status + ": " + run.Steps[0].Message);
        Assert.Equal(HostsText.Replace("0.0.0.0 telemetry.example", "# PC Tidy: 0.0.0.0 telemetry.example"), File.ReadAllText(hosts));
        var undo = await engine.Undo.UndoRunAsync(run.RunId, null, default);
        Assert.True(undo.Complete, string.Join("; ", undo.Messages));
        Assert.Equal(before, FileSnapshot(hosts));
        Assert.Empty(await NoLeftovers(Path.GetDirectoryName(hosts)!));
    }

    [Fact]
    public async Task Hosts_handler_changes_only_the_windows_hosts_file()
    {
        using var sb = new TestSandbox();
        var fake = WriteText(Path.Combine(sb.Work(), "hosts"), HostsText);
        var engine = sb.Engine();

        var run = await engine.Executor.ExecuteAsync(Plan(Hosts(fake, "0.0.0.0 ads.example")), Real, null, default);

        Assert.Equal(StepStatus.Skipped, run.Steps[0].Status);
        Assert.Equal(HostsText, File.ReadAllText(fake));
    }

    /// <summary>The app as a standard user; admin steps go to an in-process copy of the real elevated helper.</summary>
    private sealed class InProcessHelper(PcTidy.Platform.ElevatedBatchExecutor helper) : IElevationService
    {
        public bool IsElevated => false;
        public Task<IReadOnlyList<StepRecord>> ExecuteElevatedAsync(string runId, IReadOnlyList<StepRecord> steps, ExecutionMode mode, bool undo, CancellationToken ct)
            => helper.RunAsync(runId, steps, mode, undo, ct);
    }

    [Fact]
    public async Task Hosts_edit_runs_through_the_elevated_helper_with_its_admin_quarantine_and_ledger()
    {
        using var sb = new TestSandbox();
        var hosts = MakeHosts(sb.Locations, HostsText);
        var before = FileSnapshot(hosts);
        var adminRoot = Path.Combine(sb.Root, "ProgramData-Admin");
        Directory.CreateDirectory(LongPath.Io(adminRoot));
        var realLike = new NonSandboxLocations(sb.Locations);
        var adminStore = QuarantineStore.CreateAdminStore(adminRoot, realLike, sb.Settings, () => { });
        var helper = new PcTidy.Platform.ElevatedBatchExecutor(EngineFactory.CreateFileHandlers(), () => new HandlerContext
        {
            RunId = "", Locations = realLike, Safety = sb.Safety, InUse = sb.InUse, Quarantine = adminStore,
            Settings = sb.Settings, Mode = ExecutionMode.Real, IsElevated = true,
        }, policy: new PcTidy.Platform.ElevatedHelperPolicy { Ledger = new PcTidy.Platform.HelperLedger(adminRoot, () => { }) });
        var engine = sb.Engine(elevation: new InProcessHelper(helper), restore: new FakeRestorePoints(), locations: realLike);

        var run = await engine.Executor.ExecuteAsync(Plan(Hosts(hosts, "0.0.0.0 ads.example")), Real, null, default);

        Assert.True(run.Steps[0].Status == StepStatus.Done, run.Steps[0].Status + ": " + run.Steps[0].Message);
        Assert.Contains("# PC Tidy: 0.0.0.0 ads.example", File.ReadAllText(hosts));
        Assert.Empty(await engine.Quarantine.ListAsync(run.RunId, default)); // the backup is in the admin quarantine
        Assert.Single(await adminStore.ListAsync(run.RunId, default));

        var undo = await engine.Undo.UndoRunAsync(run.RunId, null, default);
        Assert.True(undo.Complete, string.Join("; ", undo.Messages));
        Assert.Equal(before, FileSnapshot(hosts));
    }

    // ------------------------------------------------------------------ CopyPath

    [Fact]
    public async Task Copy_leaves_the_original_proves_the_copy_and_undo_quarantines_only_the_copy()
    {
        using var sb = new TestSandbox();
        var work = sb.Work();
        var folder = MakeTree(Path.Combine(work, "transcripts"));
        var before = Snapshot(folder);
        var archive = sb.SecondDrive("Archive");
        Directory.CreateDirectory(LongPath.Io(Path.Combine(archive, "transcripts"))); // taken: AutoNumber
        var engine = sb.Engine();

        var run = await engine.Executor.ExecuteAsync(Plan(ScannedItem(ActionKind.CopyPath, folder, archive + "\\")), Real, null, default);

        Assert.True(run.Steps[0].Status == StepStatus.Done, run.Steps[0].Status + ": " + run.Steps[0].Message);
        Assert.Equal(before, Snapshot(folder));
        Assert.Equal(before, Snapshot(Path.Combine(archive, "transcripts (2)")));
        var undo = await engine.Undo.UndoRunAsync(run.RunId, null, default);
        Assert.True(undo.Complete, string.Join("; ", undo.Messages));
        Assert.False(Exists(Path.Combine(archive, "transcripts (2)")));
        Assert.Equal(before, Snapshot(folder));
    }

    // ------------------------------------------------------------------ OneDriveFreeUpSpace

    [Fact]
    public async Task Free_up_space_only_changes_pin_attributes_inside_onedrive_and_undo_restores_them()
    {
        using var sb = new TestSandbox();
        Assert.NotEmpty(sb.Locations.CloudRoots);
        var inCloud = WriteFile(Path.Combine(sb.Locations.CloudRoots[0], "Videos", "trip.mp4"), 4000, 1);
        var outside = WriteFile(Path.Combine(sb.Work(), "local.mp4"), 4000, 2);
        var contentBefore = FileHash.Full(LongPath.Io(inCloud));
        var attributesBefore = File.GetAttributes(LongPath.Io(inCloud));
        var engine = sb.Engine();

        var run = await engine.Executor.ExecuteAsync(Plan(
            ScannedItem(ActionKind.OneDriveFreeUpSpace, inCloud),
            ScannedItem(ActionKind.OneDriveFreeUpSpace, outside)), Real, null, default);

        Assert.True(run.Steps[0].Status == StepStatus.Done, run.Steps[0].Status + ": " + run.Steps[0].Message);
        Assert.Equal((FileAttributes)0x00100000, File.GetAttributes(LongPath.Io(inCloud)) & (FileAttributes)0x00180000);
        Assert.Equal(StepStatus.Skipped, run.Steps[1].Status);
        Assert.Equal(contentBefore, FileHash.Full(LongPath.Io(inCloud)));

        var undo = await engine.Undo.UndoRunAsync(run.RunId, null, default);
        Assert.True(undo.Complete, string.Join("; ", undo.Messages));
        Assert.Equal(attributesBefore, File.GetAttributes(LongPath.Io(inCloud)));
    }
}
