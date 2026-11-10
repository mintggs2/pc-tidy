using Microsoft.Data.Sqlite;
using PcTidy.Core.Model;
using PcTidy.Engine.Handlers;
using PcTidy.Engine.IO;
using Xunit;
using static PcTidy.Engine.Tests.TreeTools;

namespace PcTidy.Engine.Tests;

/// <summary>Crash recovery (WasCompleted) of the v2 handlers: a power cut after the intent never leaves a half-done change.</summary>
public class V2RecoveryTests
{
    private const string RunId = "20260101-000000-v2recv";

    private static string MakeDb(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LongPath.Io(path))!);
        using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = LongPath.Io(path), Pooling = false }.ToString()))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE cookies (host_key TEXT NOT NULL, name TEXT); INSERT INTO cookies VALUES ('a.com','x'),('b.com','y');";
            cmd.ExecuteNonQuery();
        }
        return LongPath.Display(LongPath.Io(path));
    }

    [Fact]
    public async Task Cookies_stopped_between_parking_the_companions_and_the_swap_are_put_back()
    {
        using var sb = new TestSandbox();
        var profile = sb.Work("Profile");
        var db = MakeDb(Path.Combine(profile, "Network", "Cookies"));
        WriteText(db + "-wal", "wal bytes");
        var before = Snapshot(profile, includeFolderTimes: false);
        var engine = sb.Engine();
        var ctx = engine.Executor.Context(RunId, ExecutionMode.Real);
        var handler = new CookiesDeleteExceptHandler();
        var item = new PlanItem
        {
            Kind = ActionKind.CookiesDeleteExcept, FeatureId = "privacy", Description = "cookies",
            Args = new() { ["browser"] = "testbrowser", ["profileDir"] = profile, ["keepHosts"] = "a.com" },
        };
        var step = handler.Prepare(new StepRecord { Index = 0, Item = item }, ctx);

        // The crash: the companion was moved into the backup folder, the database swap never happened.
        var backup = step.UndoData![UndoKeys.BackupPath];
        Directory.CreateDirectory(LongPath.Io(backup));
        FsOps.Rename(db + "-wal", Path.Combine(backup, "Cookies-wal"));
        WriteText(step.UndoData[UndoKeys.TempPath], "temp copy");

        Assert.False(await handler.WasCompletedAsync(step, ctx, default));
        Assert.Equal(before, Snapshot(profile, includeFolderTimes: false));
    }

    [Fact]
    public async Task Cookies_swapped_before_the_crash_count_as_done_and_undo_brings_the_original_back()
    {
        using var sb = new TestSandbox();
        var profile = sb.Work("Profile");
        var db = MakeDb(Path.Combine(profile, "Network", "Cookies"));
        var original = FileSnapshot(db);
        var engine = sb.Engine();
        var ctx = engine.Executor.Context(RunId, ExecutionMode.Real);
        var handler = new CookiesDeleteExceptHandler();
        var item = new PlanItem
        {
            Kind = ActionKind.CookiesDeleteExcept, FeatureId = "privacy", Description = "cookies",
            Args = new() { ["browser"] = "testbrowser", ["profileDir"] = profile, ["keepHosts"] = "a.com" },
        };
        var step = handler.Prepare(new StepRecord { Index = 0, Item = item }, ctx);
        Assert.Equal(StepStatus.Done, (await handler.ExecuteAsync(step, ctx, default)).Status);

        // Only the intent reached the disk: recovery must find the change, and undo must work from the intent alone.
        Assert.True(await handler.WasCompletedAsync(step, ctx, default));
        var undone = await handler.UndoAsync(step with { Status = StepStatus.Done }, ctx, default);
        Assert.Equal(StepStatus.Undone, undone.Status);
        Assert.Equal(original, FileSnapshot(db));
    }

    [Fact]
    public async Task Shred_recovery_finishes_a_renamed_leftover_and_never_reports_an_untouched_file_done()
    {
        using var sb = new TestSandbox();
        var work = sb.Work();
        var file = WriteFile(Path.Combine(work, "a.bin"), 2000, 1);
        var engine = sb.Engine();
        var ctx = engine.Executor.Context(RunId, ExecutionMode.Real);
        var handler = new ShredFileHandler();
        var step = handler.Prepare(new StepRecord { Index = 0, Item = ScannedItem(ActionKind.ShredFile, file) with { Reversible = false } }, ctx);

        Assert.False(await handler.WasCompletedAsync(step, ctx, default)); // nothing happened yet
        Assert.True(Exists(file));

        // The crash: overwritten and renamed, not yet deleted.
        FsOps.Rename(file, step.UndoData![UndoKeys.ShredName]);
        Assert.True(await handler.WasCompletedAsync(step, ctx, default));
        Assert.Empty(Directory.EnumerateFileSystemEntries(LongPath.Io(work)));
    }
}
