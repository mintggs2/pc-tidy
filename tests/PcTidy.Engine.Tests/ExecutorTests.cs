using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Engine.IO;
using Xunit;
using static PcTidy.Engine.Tests.TreeTools;

namespace PcTidy.Engine.Tests;

public class ExecutorTests
{
    [Fact]
    public async Task Quarantine_run_completes_journals_and_undo_restores_byte_for_byte()
    {
        using var sb = new TestSandbox();
        var work = MakeTree(Path.Combine(sb.Work(), "stuff"));
        var before = Snapshot(work);
        var items = new DirectoryInfo(LongPath.Io(work)).EnumerateFileSystemInfos()
            .Select(i => ScannedItem(ActionKind.QuarantinePath, LongPath.Display(i.FullName))).ToArray();
        var engine = sb.Engine();

        var run = await engine.Executor.ExecuteAsync(Plan(items), Real, null, default);
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.All(run.Steps, s => Assert.Equal(StepStatus.Done, s.Status));
        Assert.Empty(Snapshot(work));
        Assert.True(run.BytesFreed > 2_500_000);
        var loaded = (await engine.Journal.LoadAsync(run.RunId, default))!;
        Assert.Equal(RunStatus.Completed, loaded.Status);
        Assert.All(loaded.Steps, s => Assert.NotNull(s.UndoData!["quarantineId"]));

        var report = await engine.Undo.UndoRunAsync(run.RunId, null, default);
        Assert.True(report.Complete, string.Join("; ", report.Messages));
        Assert.Equal(items.Length, report.Restored);
        Assert.Equal(before, Snapshot(work)); // whole items moved, so even their folder times are intact
        Assert.Equal(RunStatus.Undone, (await engine.Journal.LoadAsync(run.RunId, default))!.Status);
    }

    [Fact]
    public async Task Safety_blocked_items_are_skipped_with_the_reason()
    {
        using var sb = new TestSandbox();
        var keep = WriteFile(Path.Combine(sb.Work(), "system.dll"), 10, 1);
        var go = WriteFile(Path.Combine(sb.Work(), "junk.tmp"), 10, 2);
        sb.Safety.Blocked.Add(keep);

        var run = await sb.Engine().Executor.ExecuteAsync(Plan(ScannedItem(ActionKind.QuarantinePath, keep), ScannedItem(ActionKind.QuarantinePath, go)), Real, null, default);
        Assert.Equal(StepStatus.Skipped, run.Steps[0].Status);
        Assert.Equal("It's in a protected folder.", run.Steps[0].Message);
        Assert.Equal(StepStatus.Done, run.Steps[1].Status);
        Assert.Equal(RunStatus.CompletedWithSkips, run.Status);
        Assert.True(Exists(keep));
        Assert.False(Exists(go));
    }

    [Fact]
    public async Task Changed_since_scan_is_skipped()
    {
        using var sb = new TestSandbox();
        var a = WriteFile(Path.Combine(sb.Work(), "a.log"), 100, 1);
        var b = WriteFile(Path.Combine(sb.Work(), "b.log"), 100, 2);
        var itemA = ScannedItem(ActionKind.QuarantinePath, a);
        var itemB = ScannedItem(ActionKind.QuarantinePath, b);
        File.SetLastWriteTimeUtc(LongPath.Io(a), DateTime.UtcNow);        // touched after the scan
        File.AppendAllText(LongPath.Io(b), "more");                      // grew after the scan
        File.SetLastWriteTimeUtc(LongPath.Io(b), itemB.ExpectedLastWriteUtc!.Value);

        var run = await sb.Engine().Executor.ExecuteAsync(Plan(itemA, itemB), Real, null, default);
        Assert.All(run.Steps, s => Assert.Equal("It changed since the scan, so it was left alone.", s.Message));
        Assert.True(Exists(a) && Exists(b));
    }

    [Fact]
    public async Task Required_app_running_and_file_in_use_are_skipped()
    {
        using var sb = new TestSandbox();
        var cache = WriteFile(Path.Combine(sb.Work(), "cache.db"), 10, 1);
        var locked = WriteFile(Path.Combine(sb.Work(), "open.docx"), 10, 2);
        sb.InUse.InUse[locked] = new[] { "Word" };
        var engine = sb.Engine();
        engine.Executor.RunningProcesses = () => new HashSet<string>(new[] { "chrome", "explorer" });

        var run = await engine.Executor.ExecuteAsync(Plan(
            ScannedItem(ActionKind.QuarantinePath, cache) with { RequiresClosedApps = new[] { "chrome", "steam" } },
            ScannedItem(ActionKind.QuarantinePath, locked)), Real, null, default);
        Assert.Equal("Close Chrome first.", run.Steps[0].Message);
        Assert.Equal("In use by Word.", run.Steps[1].Message);
        Assert.True(Exists(cache) && Exists(locked));
    }

    [Fact]
    public async Task Simulate_changes_nothing_and_saves_a_report()
    {
        using var sb = new TestSandbox();
        var root = sb.Work("tree");
        MakeTree(Path.Combine(root, "t"));
        var rootBefore = Snapshot(root);
        var t = Path.Combine(root, "t");
        var plan = Plan(
            ScannedItem(ActionKind.QuarantinePath, Path.Combine(t, "big.dat")),
            ScannedItem(ActionKind.MovePath, Path.Combine(t, "readme.txt"), Path.Combine(root, "moved", "readme.txt")),
            ScannedItem(ActionKind.RenamePath, Path.Combine(t, "empty.bin"), Path.Combine(t, "EMPTY.bin")),
            Item(ActionKind.CreateDirectory, dest: Path.Combine(root, "new")),
            Item(ActionKind.WriteTextFile, dest: Path.Combine(root, "r.md")) with { Args = new() { ["content"] = "hi" } },
            ScannedItem(ActionKind.ArchiveToZip, Path.Combine(t, "sub"), Path.Combine(root, "sub.zip")),
            ScannedItem(ActionKind.MoveFolderWithJunction, Path.Combine(t, "empty folder"), Path.Combine(sb.SecondDrive(), "ef")),
            Item(ActionKind.QuarantinePath, Path.Combine(t, "gone.txt")));
        var engine = sb.Engine();

        var run = await engine.Executor.ExecuteAsync(plan, Simulate, null, default);
        Assert.Equal(RunStatus.Simulated, run.Status);
        Assert.Equal(rootBefore, Snapshot(root));
        Assert.All(run.Steps.Take(7), s => Assert.Equal(StepStatus.Simulated, s.Status));
        Assert.Equal(StepStatus.Skipped, run.Steps[7].Status);
        Assert.Contains("Would move 'big.dat'", run.Steps[0].Message);

        var loaded = (await engine.Journal.LoadAsync(run.RunId, default))!;
        Assert.Equal(RunStatus.Simulated, loaded.Status);
        var lines = File.ReadAllText(Path.Combine(DataPaths.Runs(sb.Settings.DataRoot), run.RunId, "steps.jsonl"));
        Assert.DoesNotContain("\"Started\"", lines);
        Assert.Empty(await engine.Journal.FindUnfinishedAsync(default));
        Assert.Empty(await engine.Quarantine.ListAsync(null, default));
        Assert.Contains("Simulation only", RunSummary.Summarize(loaded));
        Assert.False(Directory.Exists(DataPaths.Quarantine(sb.Settings.DataRoot)));
        Assert.False(Directory.Exists(Path.Combine(sb.Locations.FixedDrives[1], QuarantineStore.VolumeFolderName)));
    }

    [Fact]
    public async Task One_failing_step_never_stops_the_run()
    {
        using var sb = new TestSandbox();
        var work = sb.Work();
        var a = WriteFile(Path.Combine(work, "a.txt"), 10, 1);
        var b = WriteFile(Path.Combine(work, "b.txt"), 10, 2);
        var plan = Plan(
            ScannedItem(ActionKind.QuarantinePath, a),
            Item(ActionKind.RegistrySetValue) with { Description = "no handler for this here" },
            Item(ActionKind.CreateDirectory, dest: Path.Combine(work, "b.txt")),   // a file has that name → Failed
            ScannedItem(ActionKind.QuarantinePath, b));

        var run = await sb.Engine().Executor.ExecuteAsync(plan, Real, null, default);
        Assert.Equal(new[] { StepStatus.Done, StepStatus.Skipped, StepStatus.Failed, StepStatus.Done }, run.Steps.Select(s => s.Status));
        Assert.Equal(RunStatus.CompletedWithSkips, run.Status);
    }

    [Fact]
    public async Task Cancelling_between_steps_leaves_a_consistent_journal()
    {
        using var sb = new TestSandbox();
        var files = Enumerable.Range(0, 5).Select(i => WriteFile(Path.Combine(sb.Work(), $"f{i}.txt"), 10, i)).ToArray();
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p => { if (p.StepIndex == 2) cts.Cancel(); });
        var engine = sb.Engine();

        var run = await engine.Executor.ExecuteAsync(Plan(files.Select(f => ScannedItem(ActionKind.QuarantinePath, f)).ToArray()), Real, progress, cts.Token);
        var loaded = (await engine.Journal.LoadAsync(run.RunId, default))!;
        Assert.Equal(RunStatus.CompletedWithSkips, loaded.Status);
        Assert.Equal(new[] { StepStatus.Done, StepStatus.Done }, loaded.Steps.Take(2).Select(s => s.Status));
        Assert.All(loaded.Steps.Skip(2), s => Assert.True(s.Status is StepStatus.Pending or StepStatus.Skipped));
        Assert.DoesNotContain(loaded.Steps, s => s.Status == StepStatus.Started);
        Assert.Equal(loaded.Steps.Count(s => s.Status == StepStatus.Done), files.Count(f => !Exists(f)));
        Assert.Empty(await engine.Journal.FindUnfinishedAsync(default));
    }

    [Fact]
    public async Task Restore_point_failure_stops_unless_the_user_continues()
    {
        using var sb = new TestSandbox();
        var file = WriteFile(Path.Combine(sb.Work(), "a.txt"), 10, 1);
        var failing = new FakeRestorePoints { Result = new RestorePointInfo(false, false, null, null, "System Protection is off.") };
        var plan = () => Plan(ScannedItem(ActionKind.QuarantinePath, file) with { IsSystemChange = true });
        var real = new NonSandboxLocations(sb.Locations);

        var stopped = await sb.Engine(restore: failing, locations: real).Executor.ExecuteAsync(plan(), Real, null, default);
        Assert.Equal(RunStatus.Failed, stopped.Status);
        Assert.Contains("System Protection is off.", stopped.Steps[0].Message);
        Assert.True(Exists(file));

        string? asked = null;
        var go = await sb.Engine(restore: failing, locations: real).Executor.ExecuteAsync(plan(),
            new ExecuteOptions { ContinueWithoutRestorePoint = p => { asked = p; return Task.FromResult(true); } }, null, default);
        Assert.Equal("System Protection is off.", asked);
        Assert.Equal(RunStatus.Completed, go.Status);
        Assert.False(Exists(file));
    }

    [Fact]
    public async Task Restore_point_is_made_on_a_real_pc_but_never_in_the_sandbox()
    {
        using var sb = new TestSandbox();
        var rp = new FakeRestorePoints();
        var item = () => Item(ActionKind.CreateDirectory, dest: Path.Combine(sb.Work(), Guid.NewGuid().ToString("N"))) with { IsSystemChange = true };

        var run = await sb.Engine(restore: rp, locations: new NonSandboxLocations(sb.Locations)).Executor.ExecuteAsync(Plan(item()), Real, null, default);
        Assert.Equal(1, rp.Calls);
        Assert.True(run.RestorePoint!.Created);

        await sb.Engine(restore: rp).Executor.ExecuteAsync(Plan(item()), Real, null, default);
        Assert.Equal(1, rp.Calls);
    }

    [Fact]
    public async Task Admin_steps_go_to_the_elevated_helper_in_one_batch()
    {
        using var sb = new TestSandbox();
        var elevation = new FakeElevation { IsElevated = false };
        var work = sb.Work();
        var plan = Plan(
            Item(ActionKind.CreateDirectory, dest: Path.Combine(work, "admin1")) with { NeedsAdmin = true },
            Item(ActionKind.CreateDirectory, dest: Path.Combine(work, "user")),
            Item(ActionKind.CreateDirectory, dest: Path.Combine(work, "admin2")) with { NeedsAdmin = true });
        var engine = sb.Engine(elevation: elevation, locations: new NonSandboxLocations(sb.Locations));

        var run = await engine.Executor.ExecuteAsync(plan, Real, null, default);
        var batch = Assert.Single(elevation.Batches);
        Assert.Equal(new[] { 0, 2 }, batch.Select(s => s.Index));
        Assert.All(batch, s => Assert.Equal(StepStatus.Started, s.Status));
        Assert.All(run.Steps, s => Assert.Equal(StepStatus.Done, s.Status));
        Assert.True(Directory.Exists(Path.Combine(work, "user")));
        Assert.False(Directory.Exists(Path.Combine(work, "admin1"))); // the fake helper didn't really do it

        var undo = await engine.Undo.UndoRunAsync(run.RunId, null, default);
        Assert.Equal(2, elevation.Batches.Count);
        Assert.Equal(new[] { 2, 0 }, elevation.Batches[1].Select(s => s.Index));
        Assert.Equal(3, undo.Restored);
    }

    [Fact]
    public async Task Admin_steps_without_any_way_to_elevate_are_skipped()
    {
        if (new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)) return; // test runner is elevated: not applicable
        using var sb = new TestSandbox();
        var dir = Path.Combine(sb.Work(), "admin");
        var run = await sb.Engine(locations: new NonSandboxLocations(sb.Locations)).Executor
            .ExecuteAsync(Plan(Item(ActionKind.CreateDirectory, dest: dir) with { NeedsAdmin = true }), Real, null, default);
        Assert.Equal("Needs admin rights, which aren't available here.", run.Steps[0].Message);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public async Task Admin_steps_in_the_sandbox_run_locally_and_never_ask_for_elevation()
    {
        using var sb = new TestSandbox();
        var elevation = new FakeElevation { IsElevated = false };
        var dir = Path.Combine(sb.Work(), "admin");
        var run = await sb.Engine(elevation: elevation).Executor
            .ExecuteAsync(Plan(Item(ActionKind.CreateDirectory, dest: dir) with { NeedsAdmin = true }), Real, null, default);
        Assert.Equal(StepStatus.Done, run.Steps[0].Status);
        Assert.Empty(elevation.Batches);
        Assert.True(Directory.Exists(dir));
    }

    [Fact]
    public async Task Delete_cap_refuses_huge_plans_unless_overridden()
    {
        using var sb = new TestSandbox();
        new EngineSettings { MaxRemoveItemsPerRun = 2, MaxRemoveBytesPerRun = 1000 }.SaveTo(sb.Settings);
        var files = Enumerable.Range(0, 3).Select(i => WriteFile(Path.Combine(sb.Work(), $"f{i}.txt"), 10, i)).ToArray();
        PlanItem[] Items() => files.Select(f => ScannedItem(ActionKind.QuarantinePath, f)).ToArray();

        var tooMany = await sb.Engine().Executor.ExecuteAsync(Plan(Items()), Real, null, default);
        Assert.Equal(RunStatus.Failed, tooMany.Status);
        Assert.Contains("safety limit", tooMany.Steps[0].Message);
        Assert.All(files, f => Assert.True(Exists(f)));

        var big = WriteFile(Path.Combine(sb.Work(), "big.bin"), 2000, 9);
        var tooBig = await sb.Engine().Executor.ExecuteAsync(Plan(ScannedItem(ActionKind.QuarantinePath, big)), Real, null, default);
        Assert.Contains("safety limit", tooBig.Steps[0].Message);

        var simulated = await sb.Engine().Executor.ExecuteAsync(Plan(Items()), Simulate, null, default);
        Assert.Equal(RunStatus.Simulated, simulated.Status); // simulations are never capped

        // The override is per plan: it must be on EVERY capped item (the UI sets it on all after the user confirmed).
        var items = Items();
        items[0] = items[0] with { Args = new() { [EngineSettings.CapOverrideArg] = "true" } };
        var partly = await sb.Engine().Executor.ExecuteAsync(Plan(items), Real, null, default);
        Assert.Equal(RunStatus.Failed, partly.Status);
        Assert.All(files, f => Assert.True(Exists(f)));

        var all = Items().Select(i => i with { Args = new() { [EngineSettings.CapOverrideArg] = "true" } }).ToArray();
        var overridden = await sb.Engine().Executor.ExecuteAsync(Plan(all), Real, null, default);
        Assert.Equal(RunStatus.Completed, overridden.Status);
    }

    /// <summary>IProgress that reports synchronously (Progress&lt;T&gt; posts to the thread pool).</summary>
    private sealed class SyncProgress : IProgress<RunProgress>
    {
        private readonly Action<RunProgress> _a;
        public SyncProgress(Action<RunProgress> a) => _a = a;
        public void Report(RunProgress value) => _a(value);
    }
}
