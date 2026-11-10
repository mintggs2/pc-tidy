using System.Text.Json;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Engine.Handlers;
using PcTidy.Engine.IO;
using Xunit;
using static PcTidy.Engine.Tests.TreeTools;

namespace PcTidy.Engine.Tests;

/// <summary>Regression tests for the independent review's findings (each fails on the old engine).</summary>
public class ReviewFixTests
{
    // ------------------------------------------------------------------ C1: restores go only to the planned place

    [Fact]
    public async Task Undo_never_restores_to_a_place_a_tampered_quarantine_record_names()
    {
        using var sb = new TestSandbox();
        var file = WriteFile(Path.Combine(sb.Work(), "victim.txt"), 100, 1);
        var engine = sb.Engine();
        var run = await engine.Executor.ExecuteAsync(Plan(ScannedItem(ActionKind.QuarantinePath, file)), Real, null, default);
        Assert.Equal(StepStatus.Done, run.Steps[0].Status);

        // The user-writable entry.json is edited to point the restore at a system folder.
        var evil = Path.Combine(sb.Locations.WindowsDir, "System32", "victim.txt");
        var entry = Assert.Single(await engine.Quarantine.ListAsync(run.RunId, default));
        var manifestPath = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(entry.StoredPath)!)!, "entry.json");
        var json = File.ReadAllText(LongPath.Io(manifestPath)).Replace(JsonEncodedText.Encode(file).ToString(), JsonEncodedText.Encode(evil).ToString());
        File.WriteAllText(LongPath.Io(manifestPath), json);

        var undo = await engine.Undo.UndoRunAsync(run.RunId, null, default);

        Assert.Equal(0, undo.Restored);
        Assert.False(Exists(evil));
        Assert.False(Exists(file));
        Assert.True(Exists(entry.StoredPath)); // still safe in quarantine
        Assert.Contains("doesn't match", Assert.Single(undo.Messages));
    }

    [Fact]
    public async Task Restore_to_a_place_other_than_the_record_is_refused_by_the_store()
    {
        using var sb = new TestSandbox();
        var file = WriteFile(Path.Combine(sb.Work(), "a.txt"), 10, 1);
        var store = new QuarantineStore(sb.Settings.DataRoot, sb.Locations, sb.Settings);
        var entry = await store.QuarantineAsync(file, "20260101-000000-c1test", default);

        await Assert.ThrowsAsync<QuarantineIntegrityException>(() => store.RestoreToAsync(entry, Path.Combine(sb.Work(), "elsewhere.txt"), default));
        Assert.Equal(file, await store.RestoreToAsync(entry, file, default));
    }

    [Fact]
    public async Task Undo_rechecks_the_original_place_with_the_safety_guard()
    {
        using var sb = new TestSandbox();
        var file = WriteFile(Path.Combine(sb.Work(), "now-protected.txt"), 10, 1);
        var engine = sb.Engine();
        var run = await engine.Executor.ExecuteAsync(Plan(ScannedItem(ActionKind.QuarantinePath, file)), Real, null, default);
        sb.Safety.Blocked.Add(file);

        var undo = await engine.Undo.UndoRunAsync(run.RunId, null, default);

        Assert.Equal(0, undo.Restored);
        Assert.False(Exists(file));
        Assert.Contains("safety rules", Assert.Single(undo.Messages));
    }

    // ------------------------------------------------------------------ H3: admin store moves by handle

    [Fact]
    public async Task Admin_store_refuses_items_reached_through_a_link_and_restores_only_by_undo()
    {
        using var sb = new TestSandbox();
        var verified = 0;
        var store = QuarantineStore.CreateAdminStore(Path.Combine(sb.Root, "Admin"), sb.Locations, sb.Settings, () => verified++);
        var real = sb.Work("real");
        var file = WriteFile(Path.Combine(real, "f.txt"), 10, 1);
        var link = Path.Combine(sb.Work(), "link");
        Junction.Create(link, real);

        await Assert.ThrowsAsync<SafetyRefusalException>(() => store.QuarantineAsync(Path.Combine(link, "f.txt"), "20260101-000000-h3test", default));
        Assert.True(Exists(file));

        var entry = await store.QuarantineAsync(file, "20260101-000000-h3test", default);
        Assert.False(Exists(file));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RestoreAsync(entry, default));
        Assert.Equal(file, await store.RestoreToAsync(entry, file, default));
        Assert.True(verified >= 3); // the admin folder is re-checked before every use
        Junction.Remove(link);
    }

    [Fact]
    public async Task Admin_store_does_nothing_when_its_folder_check_fails()
    {
        using var sb = new TestSandbox();
        var store = QuarantineStore.CreateAdminStore(Path.Combine(sb.Root, "Admin"), sb.Locations, sb.Settings,
            () => throw new UnauthorizedAccessException("not private"));
        var file = WriteFile(Path.Combine(sb.Work(), "f.txt"), 10, 1);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.QuarantineAsync(file, "20260101-000000-h3test", default));
        Assert.True(Exists(file));
    }

    [Fact]
    public void Handle_move_refuses_when_a_folder_on_the_way_was_swapped_for_a_junction()
    {
        using var sb = new TestSandbox();
        var work = sb.Work();
        var checkedPath = Path.Combine(work, "parent", "f.txt");
        WriteFile(checkedPath, 10, 1);
        // After the check, "parent" is replaced by a junction to a folder holding a different f.txt.
        var elsewhere = WriteFile(Path.Combine(work, "elsewhere", "f.txt"), 20, 2);
        Directory.Move(LongPath.Io(Path.Combine(work, "parent")), LongPath.Io(Path.Combine(work, "parent-old")));
        Junction.Create(Path.Combine(work, "parent"), Path.Combine(work, "elsewhere"));
        var dest = Path.Combine(work, "dest");
        Directory.CreateDirectory(LongPath.Io(dest));

        Assert.Throws<SafetyRefusalException>(() => HandleFs.MoveVerified(checkedPath, Path.Combine(dest, "f.txt")));
        Assert.True(Exists(elsewhere));
        Assert.False(Exists(Path.Combine(dest, "f.txt")));
        Junction.Remove(Path.Combine(work, "parent"));
    }

    [Fact]
    public void A_folder_the_user_made_is_never_trusted_as_the_admin_folder()
    {
        using var sb = new TestSandbox();
        var root = Path.Combine(sb.Work("pd"), "PC Tidy", "Admin");
        Directory.CreateDirectory(LongPath.Io(root));
        Assert.NotNull(AdminDataFolder.WhyNotTrusted(root));
    }

    // ------------------------------------------------------------------ H1: helper Started results

    [Fact]
    public async Task Admin_steps_the_helper_left_Started_are_settled_by_the_helper_and_recorded_Done_when_they_happened()
    {
        using var sb = new TestSandbox();
        var work = sb.Work();
        var happened = Path.Combine(work, "happened");
        var notYet = Path.Combine(work, "not-yet");
        var elevation = new FakeElevation
        {
            Run = s =>
            {
                // The helper stopped part-way after creating the first folder: every step comes back Started.
                if (s.Item.DestinationPath == happened) Directory.CreateDirectory(happened);
                return s with { Status = StepStatus.Started, Message = "Stopped part-way." };
            },
            // Its settle request answers from its own ledger.
            Settle = s => s.Item.DestinationPath == happened
                ? s with { Status = StepStatus.Done, Message = "Done (confirmed by the helper)." }
                : s with { Status = StepStatus.Skipped, Message = "It didn't happen." },
        };
        var engine = sb.Engine(elevation: elevation, locations: new NonSandboxLocations(sb.Locations));
        var run = await engine.Executor.ExecuteAsync(Plan(
            Item(ActionKind.CreateDirectory, dest: happened) with { NeedsAdmin = true },
            Item(ActionKind.CreateDirectory, dest: notYet) with { NeedsAdmin = true }), Real, null, default);

        Assert.Equal(StepStatus.Done, run.Steps[0].Status);
        Assert.Equal(StepStatus.Skipped, run.Steps[1].Status);
        Assert.NotNull(run.Steps[0].UndoData); // so Undo can reverse it
    }

    [Fact]
    public async Task Admin_steps_go_to_the_helper_in_requests_under_its_limit()
    {
        using var sb = new TestSandbox();
        var elevation = new FakeElevation();
        var work = sb.Work();
        var items = Enumerable.Range(0, 2500).Select(i => Item(ActionKind.CreateDirectory, dest: Path.Combine(work, $"d{i}")) with { NeedsAdmin = true }).ToArray();
        var run = await sb.Engine(elevation: elevation, locations: new NonSandboxLocations(sb.Locations)).Executor.ExecuteAsync(Plan(items), Real, null, default);

        Assert.Equal(3, elevation.Batches.Count);
        Assert.All(elevation.Batches, b => Assert.True(b.Count <= Executor.AdminRequestSteps));
        Assert.Equal(2500, run.DoneCount);
    }

    [Fact]
    public async Task Admin_steps_are_checked_by_the_helper_not_by_the_app_that_cant_read_them()
    {
        using var sb = new TestSandbox();
        var adminFile = Path.Combine(sb.Locations.WindowsTemp, "locked-down", "x.tmp");
        // The app's own guard can't read inside the admin folder, so it would refuse the item...
        sb.Safety.Blocked.Add(adminFile);
        var helperChecked = new List<string>();
        var elevation = new FakeElevation
        {
            // ...but the helper's guard (which can read it) allows it, and the helper runs it.
            Run = s => { helperChecked.Add(s.Item.SourcePath!); return s with { Status = StepStatus.Done, Message = "ok" }; },
        };
        var engine = sb.Engine(elevation: elevation, locations: new NonSandboxLocations(sb.Locations));
        var adminItem = Item(ActionKind.QuarantinePath, adminFile) with { NeedsAdmin = true, CarveOutId = CarveOuts.WindowsTemp };
        var userItem = Item(ActionKind.QuarantinePath, adminFile);

        var run = await engine.Executor.ExecuteAsync(Plan(adminItem, userItem), Real, null, default);

        Assert.Equal(StepStatus.Done, run.Steps[0].Status);
        Assert.Equal(new[] { adminFile }, helperChecked);
        Assert.Equal(StepStatus.Skipped, run.Steps[1].Status); // the app's own steps are still checked by the app
    }

    // ------------------------------------------------------------------ H2: the run is always finalised

    private sealed class ThrowingInUse : IInUseChecker
    {
        public Func<Exception>? OnCheck { get; init; }
        public Task<IReadOnlyList<InUseInfo>> CheckAsync(IReadOnlyList<string> paths, CancellationToken ct) => throw OnCheck!();
        public bool IsLocked(string path) => false;
    }

    [Fact]
    public async Task Errors_in_the_in_use_check_skip_the_step_with_a_reason_and_the_run_still_finishes()
    {
        using var sb = new TestSandbox();
        var file = WriteFile(Path.Combine(sb.Work(), "a.txt"), 10, 1);
        var services = EngineFactory.Create(sb.Locations, sb.Safety, new ThrowingInUse { OnCheck = () => new IOException("disk said no") }, sb.Settings, null, null, null, false);

        var run = await services.Executor.ExecuteAsync(Plan(ScannedItem(ActionKind.QuarantinePath, file)), Real, null, default);

        Assert.Equal(StepStatus.Skipped, run.Steps[0].Status);
        Assert.Contains("couldn't check", run.Steps[0].Message);
        Assert.NotEqual(RunStatus.Running, (await services.Journal.LoadAsync(run.RunId, default))!.Status);
        Assert.Empty(await services.Journal.FindUnfinishedAsync(default));
        Assert.False(services.Executor.IsRunning);
    }

    [Fact]
    public async Task Cancelling_while_the_in_use_batch_is_built_stops_cleanly_and_releases_the_run()
    {
        using var sb = new TestSandbox();
        var file = WriteFile(Path.Combine(sb.Work(), "a.txt"), 10, 1);
        using var cts = new CancellationTokenSource();
        var inUse = new ThrowingInUse { OnCheck = () => { cts.Cancel(); return new OperationCanceledException(cts.Token); } };
        var services = EngineFactory.Create(sb.Locations, sb.Safety, inUse, sb.Settings, null, null, null, false);

        var run = await services.Executor.ExecuteAsync(Plan(ScannedItem(ActionKind.QuarantinePath, file)), Real, null, cts.Token);

        Assert.Equal(StepStatus.Pending, run.Steps[0].Status);
        Assert.NotEqual(RunStatus.Running, run.Status);
        Assert.NotEqual(RunStatus.Running, (await services.Journal.LoadAsync(run.RunId, default))!.Status);
        Assert.True(Exists(file));
    }

    [Fact]
    public async Task A_cancelled_simulation_is_finished_as_Simulated_not_left_Planned()
    {
        using var sb = new TestSandbox();
        var file = WriteFile(Path.Combine(sb.Work(), "a.txt"), 10, 1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var services = sb.Engine();
        var run = await services.Executor.ExecuteAsync(Plan(ScannedItem(ActionKind.QuarantinePath, file)), Simulate, null, cts.Token);
        Assert.Equal(RunStatus.Simulated, (await services.Journal.LoadAsync(run.RunId, default))!.Status);
    }

    [Fact]
    public async Task IsRunning_is_true_only_while_a_run_is_in_progress()
    {
        using var sb = new TestSandbox();
        var file = WriteFile(Path.Combine(sb.Work(), "a.txt"), 10, 1);
        var engine = sb.Engine();
        var seen = false;
        var progress = new InlineProgress(_ => seen |= engine.Executor.IsRunning);
        await engine.Executor.ExecuteAsync(Plan(ScannedItem(ActionKind.QuarantinePath, file)), Real, progress, default);
        Assert.True(seen);
        Assert.False(engine.Executor.IsRunning);
    }

    private sealed class InlineProgress(Action<RunProgress> report) : IProgress<RunProgress>
    {
        public void Report(RunProgress value) => report(value);
    }

    // ------------------------------------------------------------------ verified delete

    [Fact]
    public async Task Verified_delete_removes_only_what_was_copied_and_leaves_new_or_changed_items()
    {
        using var sb = new TestSandbox();
        var src = Path.Combine(sb.Work(), "tree");
        var same = WriteFile(Path.Combine(src, "same.txt"), 100, 1);
        var changed = WriteFile(Path.Combine(src, "sub", "changed.txt"), 100, 2);
        var copy = await VerifiedCopier.CopyAsync(src, Path.Combine(sb.SecondDrive(), "tree"), default);

        // Between the verified copy and the delete: one file changes, one appears.
        File.AppendAllText(LongPath.Io(changed), "more");
        var added = WriteText(Path.Combine(src, "sub", "new.txt"), "new");

        var report = VerifiedCopier.RemoveVerified(src, copy.Source);

        Assert.False(Exists(same));
        Assert.True(Exists(changed));
        Assert.True(Exists(added));
        Assert.False(report.Complete);
        Assert.False(report.NothingRemoved);
        Assert.Contains("changed or were added", report.Note());
    }

    [Fact]
    public async Task Cross_drive_quarantine_with_a_file_in_use_keeps_it_and_reports_a_note()
    {
        using var sb = new TestSandbox();
        var src = Path.Combine(sb.Work(), "tree");
        WriteFile(Path.Combine(src, "free.txt"), 100, 1);
        var busy = WriteFile(Path.Combine(src, "busy.txt"), 100, 2);
        var store = new QuarantineStore(sb.Settings.DataRoot, sb.Locations, sb.Settings) { ForceCopyIntoDataRoot = true };

        PartialQuarantineException partial;
        using (new FileStream(LongPath.Io(busy), FileMode.Open, FileAccess.Read, FileShare.Read))
            partial = await Assert.ThrowsAsync<PartialQuarantineException>(() => store.QuarantineAsync(src, "20260101-000000-vdtest", default));

        Assert.True(Exists(busy));
        Assert.False(Exists(Path.Combine(src, "free.txt")));
        Assert.Contains("in use", partial.Note);
        Assert.True(Exists(Path.Combine(partial.Entry.StoredPath, "busy.txt"))); // the verified copy has it too
    }

    [Fact]
    public async Task Folders_are_checked_for_files_in_use_before_they_are_moved()
    {
        using var sb = new TestSandbox();
        var folder = Path.Combine(sb.Work(), "folder");
        WriteFile(Path.Combine(folder, "x.txt"), 10, 1);
        sb.InUse.InUse[folder] = new[] { "Contoso" };
        var run = await sb.Engine().Executor.ExecuteAsync(Plan(ScannedItem(ActionKind.QuarantinePath, folder)), Real, null, default);
        Assert.Equal("In use by Contoso.", run.Steps[0].Message);
        Assert.True(Exists(folder));
    }

    // ------------------------------------------------------------------ M2: FAT / exFAT time precision

    [Fact]
    public void Copied_trees_compare_names_sizes_and_times_within_the_destination_precision()
    {
        using var sb = new TestSandbox();
        var src = Path.Combine(sb.Work(), "t");
        WriteFile(Path.Combine(src, "a.bin"), 10, 1, lastWrite: OldTime.AddTicks(12_345));
        var original = TreeManifest.Build(src);
        var fat = Path.Combine(sb.Work(), "t-fat");
        WriteFile(Path.Combine(fat, "a.bin"), 10, 1, lastWrite: OldTime); // FAT rounds to 2 s steps
        var rounded = TreeManifest.Build(fat);

        Assert.NotEqual(original.MetadataHash, rounded.MetadataHash); // why the old exact check always failed
        Assert.True(VerifiedCopier.SameTree(original, rounded, TimeSpan.FromSeconds(2)));
        Assert.False(VerifiedCopier.SameTree(original, rounded, TimeSpan.Zero));
        Assert.Equal(TimeSpan.Zero, VolumeInfo.TimeTolerance(sb.Root)); // the testbed drive is NTFS
    }

    // ------------------------------------------------------------------ M4: rename + overwrite undo

    [Fact]
    public async Task Undoing_an_overwriting_rename_brings_back_the_item_it_replaced()
    {
        using var sb = new TestSandbox();
        var work = sb.Work();
        var a = WriteText(Path.Combine(work, "a.txt"), "the renamed one");
        var b = WriteText(Path.Combine(work, "b.txt"), "the one that was replaced");
        var engine = sb.Engine();
        var run = await engine.Executor.ExecuteAsync(Plan(ScannedItem(ActionKind.RenamePath, a, b) with { ClashPolicy = NameClashPolicy.Overwrite }), Real, null, default);
        Assert.Equal(StepStatus.Done, run.Steps[0].Status);
        Assert.Equal("the renamed one", File.ReadAllText(LongPath.Io(b)));

        var undo = await engine.Undo.UndoRunAsync(run.RunId, null, default);

        Assert.Equal(1, undo.Restored);
        Assert.Equal("the renamed one", File.ReadAllText(LongPath.Io(a)));
        Assert.Equal("the one that was replaced", File.ReadAllText(LongPath.Io(b)));
    }

    // ------------------------------------------------------------------ planned names are used exactly

    [Theory]
    [InlineData(ActionKind.ArchiveToZip)]
    [InlineData(ActionKind.ExtractZip)]
    [InlineData(ActionKind.MoveFolderWithJunction)]
    public async Task A_planned_name_taken_after_the_intent_was_journaled_skips_instead_of_picking_another(ActionKind kind)
    {
        using var sb = new TestSandbox();
        var work = sb.Work();
        var folder = Path.Combine(work, "data");
        WriteFile(Path.Combine(folder, "x.txt"), 10, 1);
        var zip = Path.Combine(work, "data.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(LongPath.Io(folder), LongPath.Io(zip));
        var item = kind switch
        {
            ActionKind.ArchiveToZip => Item(kind, folder, Path.Combine(work, "archive.zip"), isDir: true),
            ActionKind.ExtractZip => Item(kind, zip, Path.Combine(work, "extracted")),
            _ => Item(kind, folder, Path.Combine(sb.SecondDrive(), "moved"), isDir: true),
        };
        var handler = (IActionHandler)EngineFactory.CreateFileHandlers().First(h => h.CanHandle(item));
        var ctx = sb.Engine().Executor.Context("20260101-000000-names", ExecutionMode.Real);
        var prepared = ((IPreparingHandler)handler).Prepare(new StepRecord { Index = 0, Item = item }, ctx);
        var planned = prepared.UndoData![kind == ActionKind.ArchiveToZip ? UndoKeys.ZipPath : UndoKeys.FinalPath];
        Directory.CreateDirectory(LongPath.Io(planned)); // someone takes the name in the meantime

        var result = await handler.ExecuteAsync(prepared with { Status = StepStatus.Started }, ctx, default);

        Assert.Equal(StepStatus.Skipped, result.Status);
        Assert.Contains("appeared just now", result.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(LongPath.Io(planned)));
    }

    [Fact]
    public async Task Two_moves_to_the_same_name_in_one_batch_are_planned_with_different_names()
    {
        using var sb = new TestSandbox();
        var work = sb.Work();
        var dest = Path.Combine(work, "out", "same.txt");
        var a = WriteText(Path.Combine(work, "a", "same.txt"), "A");
        var b = WriteText(Path.Combine(work, "b", "same.txt"), "B");
        var run = await sb.Engine().Executor.ExecuteAsync(Plan(ScannedItem(ActionKind.MovePath, a, dest), ScannedItem(ActionKind.MovePath, b, dest)), Real, null, default);
        Assert.All(run.Steps, s => Assert.Equal(StepStatus.Done, s.Status));
        Assert.Equal("A", File.ReadAllText(LongPath.Io(dest)));
        Assert.Equal("B", File.ReadAllText(LongPath.Io(Path.Combine(work, "out", "same (2).txt"))));
    }

    // ------------------------------------------------------------------ M3: delete cap measured on disk

    [Fact]
    public async Task Delete_cap_counts_real_folder_contents_and_every_kind_that_moves_originals()
    {
        using var sb = new TestSandbox();
        new EngineSettings { MaxRemoveItemsPerRun = 3 }.SaveTo(sb.Settings);
        var folder = Path.Combine(sb.Work(), "big");
        for (int i = 0; i < 5; i++) WriteFile(Path.Combine(folder, $"f{i}.bin"), 10, i);
        // The plan says "1 item, 0 bytes"; on disk it's 5 files.
        var run = await sb.Engine().Executor.ExecuteAsync(Plan(Item(ActionKind.QuarantinePath, folder, isDir: true)), Real, null, default);
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains("safety limit", run.Steps[0].Message);

        var renames = Enumerable.Range(0, 4).Select(i => Item(ActionKind.RenamePath, WriteFile(Path.Combine(sb.Work(), $"r{i}.txt"), 1, i), Path.Combine(sb.Work(), $"r{i}-new.txt"))).ToArray();
        var renameRun = await sb.Engine().Executor.ExecuteAsync(Plan(renames), Real, null, default);
        Assert.Contains("safety limit", renameRun.Steps[0].Message);
        Assert.True(Exists(Path.Combine(folder, "f0.bin")));
    }

    // ------------------------------------------------------------------ M4 (safety): per-drive quarantine folder

    [Fact]
    public async Task A_per_drive_quarantine_folder_others_can_open_is_not_used_and_the_reason_is_logged()
    {
        using var sb = new TestSandbox();
        var file = WriteFile(Path.Combine(sb.SecondDrive(), "a.txt"), 10, 1);
        var planted = Path.Combine(sb.Locations.FixedDrives[^1], QuarantineStore.VolumeFolderName);
        Directory.CreateDirectory(LongPath.Io(planted)); // inherits the drive's open permissions
        var store = new QuarantineStore(sb.Settings.DataRoot, sb.Locations, sb.Settings);

        var entry = await store.QuarantineAsync(file, "20260101-000000-pvtest", default);

        Assert.StartsWith(store.DataStoreRoot, entry.StoredPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("other accounts", store.LastVolumeStoreProblem);
        Assert.Contains("Not using", File.ReadAllText(Path.Combine(DataPaths.Logs(sb.Settings.DataRoot), "engine.log")));
        Assert.DoesNotContain(planted, store.AllStoreRoots(), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_per_drive_quarantine_folder_that_is_a_link_is_not_used()
    {
        using var sb = new TestSandbox();
        var file = WriteFile(Path.Combine(sb.SecondDrive(), "a.txt"), 10, 1);
        var target = sb.SecondDrive("somewhere");
        Junction.Create(Path.Combine(sb.Locations.FixedDrives[^1], QuarantineStore.VolumeFolderName), target);
        var store = new QuarantineStore(sb.Settings.DataRoot, sb.Locations, sb.Settings);

        var entry = await store.QuarantineAsync(file, "20260101-000000-pvtest", default);

        Assert.StartsWith(store.DataStoreRoot, entry.StoredPath, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFileSystemEntries(LongPath.Io(target)));
        Junction.Remove(Path.Combine(sb.Locations.FixedDrives[^1], QuarantineStore.VolumeFolderName));
    }

    // ------------------------------------------------------------------ batched intent journaling

    /// <summary>The real journal, but the power goes off when the first result is written (after the batch's durable intent).</summary>
    private sealed class PowerCutJournal(RunJournal inner) : IRunJournal, IStepBatchJournal
    {
        public Task<RunRecord> CreateAsync(ActionPlan plan, ExecutionMode mode, CancellationToken ct) => inner.CreateAsync(plan, mode, ct);
        public Task SaveStepAsync(string runId, StepRecord step, CancellationToken ct) => inner.SaveStepAsync(runId, step, ct);
        public Task SaveStepsAsync(string runId, IReadOnlyList<StepRecord> steps, bool durable, CancellationToken ct)
            => steps.Any(s => s.Status == StepStatus.Done) ? throw new InvalidProgramException("power cut") : inner.SaveStepsAsync(runId, steps, durable, ct);
        public Task SaveRunAsync(RunRecord run, CancellationToken ct) => run.Status == RunStatus.Running ? inner.SaveRunAsync(run, ct) : throw new InvalidProgramException("power cut");
        public Task<RunRecord?> LoadAsync(string runId, CancellationToken ct) => inner.LoadAsync(runId, ct);
        public Task<IReadOnlyList<RunRecord>> ListAsync(CancellationToken ct) => inner.ListAsync(ct);
        public Task<IReadOnlyList<RunRecord>> FindUnfinishedAsync(CancellationToken ct) => inner.FindUnfinishedAsync(ct);
    }

    [Fact]
    public async Task One_durable_intent_covers_a_whole_batch_and_recovery_resolves_every_step_of_it()
    {
        using var sb = new TestSandbox();
        var files = Enumerable.Range(0, 10).Select(i => WriteFile(Path.Combine(sb.Work(), $"f{i}.txt"), 10, i)).ToArray();
        var inner = new RunJournal(sb.Settings.DataRoot);
        var quarantine = new QuarantineStore(sb.Settings.DataRoot, sb.Locations, sb.Settings);
        var executor = new Executor(EngineFactory.CreateFileHandlers(), new PowerCutJournal(inner), sb.Safety, sb.InUse, quarantine, sb.Locations, sb.Settings);
        var plan = Plan(files.Select(f => ScannedItem(ActionKind.QuarantinePath, f)).ToArray());

        // The first file is quarantined, then the power goes before its result is journaled.
        await Assert.ThrowsAsync<InvalidProgramException>(() => executor.ExecuteAsync(plan, Real, null, default));
        inner.SimulateProcessDeath();
        Assert.False(Exists(files[0]));

        var engine = sb.Engine();
        var interrupted = await engine.Journal.LoadAsync(plan.RunId, default);
        Assert.Equal(RunStatus.Interrupted, interrupted!.Status);
        Assert.All(interrupted.Steps, s => Assert.Equal(StepStatus.Started, s.Status)); // the whole batch's intent was durable
        Assert.All(interrupted.Steps, s => Assert.NotNull(s.UndoData![UndoKeys.QuarantineId])); // with each planned entry

        var resumed = await engine.Undo.ResumeAsync(plan.RunId, null, default);
        Assert.Equal(RunStatus.Completed, resumed.Status);
        Assert.Equal("Finished before the interruption.", resumed.Steps[0].Message);
        Assert.All(files, f => Assert.False(Exists(f)));

        var undo = await engine.Undo.UndoRunAsync(plan.RunId, null, default);
        Assert.Equal(10, undo.Restored);
        Assert.All(files, f => Assert.True(Exists(f)));
    }

    [Fact]
    public async Task A_quarantine_manifest_lost_in_a_power_cut_is_rebuilt_from_the_journaled_intent()
    {
        using var sb = new TestSandbox();
        var file = WriteFile(Path.Combine(sb.Work(), "a.txt"), 100, 1);
        var store = new QuarantineStore(sb.Settings.DataRoot, sb.Locations, sb.Settings);
        var id = QuarantineStore.NewEntryId();
        const string runId = "20260101-000000-intent";
        var entry = await store.QuarantineAsync(file, runId, id, default);
        File.Delete(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(entry.StoredPath)!)!, "entry.json")); // never reached the disk

        Assert.Empty(await store.ListAsync(runId, default));
        var recovered = await store.RecoverAsync(runId, id, file, default);

        Assert.NotNull(recovered);
        Assert.Equal(file, recovered!.OriginalPath);
        Assert.Equal(file, await store.RestoreToAsync(recovered, file, default));
        Assert.Equal(100, new FileInfo(LongPath.Io(file)).Length);
    }

    [Fact]
    public async Task Paths_that_would_need_normalising_are_refused_not_rewritten()
    {
        using var sb = new TestSandbox();
        var file = WriteFile(Path.Combine(sb.Work(), "trailing dot."), 10, 1);
        Assert.Throws<ArgumentException>(() => LongPath.Io(Path.Combine(sb.Work(), "x", "..", "trailing dot.")));
        Assert.Throws<ArgumentException>(() => LongPath.Io(file + ":stream"));
        // The literal name with its trailing dot is the one acted on.
        var run = await sb.Engine().Executor.ExecuteAsync(Plan(ScannedItem(ActionKind.QuarantinePath, file)), Real, null, default);
        Assert.Equal(StepStatus.Done, run.Steps[0].Status);
        Assert.False(Exists(file));
    }
}
