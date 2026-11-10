using PcTidy.Core;
using PcTidy.Core.Model;
using Xunit;

namespace PcTidy.Platform.Tests;

/// <summary>
/// The elevated helper trusts nothing the user can write (review findings C1 / H3 / M3 / M5). All in-process with fake
/// handlers: nothing elevates, nothing real changes.
/// </summary>
public class HelperHardeningTests
{
    private static PlanItem Service(string name = "ContosoSync") => new()
    {
        Kind = ActionKind.ServiceSetStartType, FeatureId = "test", Description = "test", NeedsAdmin = true, ServiceName = name, ServiceStartType = ServiceStart.Manual,
    };

    private static (ElevatedBatchExecutor Executor, RecordingHandler Handler) WithLedger(TestSandbox sandbox, Func<IReadOnlyList<PlanItem>, CancellationToken, string?>? cap = null)
    {
        var handler = new RecordingHandler(ActionKind.ServiceSetStartType, ActionKind.QuarantinePath);
        var ledger = new HelperLedger(Path.Combine(sandbox.Root, "Admin"), () => { });
        return (new ElevatedBatchExecutor(new[] { handler }, () => sandbox.Context(), policy: new ElevatedHelperPolicy { Ledger = ledger, DeleteCap = cap }), handler);
    }

    [Fact]
    public async Task The_helper_undoes_only_steps_it_ran_itself()
    {
        using var sandbox = new TestSandbox();
        var (executor, handler) = WithLedger(sandbox);

        // A "Done" step the helper never ran (e.g. written into the app's journal by hand) is refused.
        var forged = await executor.RunAsync("run-a", new[] { Steps.Of(Service(), 0, StepStatus.Done) }, ExecutionMode.Real, undo: true, default);
        Assert.Equal(StepStatus.UndoFailed, forged[0].Status);
        Assert.Contains("didn't make this change", forged[0].Message);
        Assert.Empty(handler.Calls);

        var item = Service();
        var ran = await executor.RunAsync("run-a", new[] { Steps.Of(item, 0) }, ExecutionMode.Real, undo: false, default);
        Assert.Equal(StepStatus.Done, ran[0].Status);
        var again = await executor.RunAsync("run-a", new[] { Steps.Of(item, 0) }, ExecutionMode.Real, undo: false, default);
        Assert.Equal("This step has already run.", again[0].Message);

        // The same index but a different item (edited journal) is refused too.
        var swapped = await executor.RunAsync("run-a", new[] { ran[0] with { Item = Service("SomethingElse") } }, ExecutionMode.Real, undo: true, default);
        Assert.Equal(StepStatus.UndoFailed, swapped[0].Status);

        var undone = await executor.RunAsync("run-a", new[] { ran[0] }, ExecutionMode.Real, undo: true, default);
        Assert.Equal(StepStatus.Undone, undone[0].Status);
        var twice = await executor.RunAsync("run-a", new[] { ran[0] }, ExecutionMode.Real, undo: true, default);
        Assert.Equal("It was already undone.", twice[0].Message);
    }

    [Fact]
    public async Task Undo_uses_the_helpers_own_record_not_the_undo_data_in_the_request()
    {
        using var sandbox = new TestSandbox();
        var (executor, handler) = WithLedger(sandbox);
        var ran = await executor.RunAsync("run-b", new[] { Steps.Of(Service(), 0) }, ExecutionMode.Real, undo: false, default);
        var tampered = ran[0] with { UndoData = new() { ["k"] = "tampered" }, RegistryPrior = new RegistryPriorState { RegExportPath = @"C:\Users\Public\evil.reg" } };

        await executor.RunAsync("run-b", new[] { tampered }, ExecutionMode.Real, undo: true, default);

        var undoCall = handler.Calls.Single(c => c.Op == "undo");
        Assert.Equal("v", undoCall.Step.UndoData!["k"]);
        Assert.Null(undoCall.Step.RegistryPrior);
    }

    [Fact]
    public async Task Admin_quarantine_only_works_inside_admin_cleanup_folders()
    {
        using var sandbox = new TestSandbox();
        var (executor, handler) = WithLedger(sandbox);
        var inTemp = Path.Combine(sandbox.Locations.WindowsTemp, "old.tmp");
        Directory.CreateDirectory(sandbox.Locations.WindowsTemp);
        File.WriteAllText(inTemp, "x");
        PlanItem Q(string path, string? carveOut) => new() { Kind = ActionKind.QuarantinePath, FeatureId = "cleanup", Description = "q", NeedsAdmin = true, SourcePath = path, CarveOutId = carveOut };

        var results = await executor.RunAsync("run-c", new[]
        {
            Steps.Of(Q(inTemp, CarveOuts.WindowsTemp), 0),                                     // allowed
            Steps.Of(Q(inTemp, null), 1),                                                      // no carve-out
            Steps.Of(Q(inTemp, CarveOuts.UserTemp), 2),                                        // not an admin carve-out
            Steps.Of(Q(Path.Combine(sandbox.Locations.WindowsDir, "System32", "x.dll"), CarveOuts.WindowsTemp), 3), // outside its roots
            Steps.Of(Q(sandbox.Locations.WindowsTemp, CarveOuts.WindowsTemp), 4),              // the folder itself
            Steps.Of(Q(Path.Combine(sandbox.Locations.WindowsTemp, "..", "System32"), CarveOuts.WindowsTemp), 5), // sneaky ".."
        }, ExecutionMode.Real, undo: false, default);

        Assert.Equal(StepStatus.Done, results[0].Status);
        Assert.All(results.Skip(1), r => Assert.Contains("only quarantines items in the cleanup folders", r.Message));
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task The_helper_rechecks_files_itself_still_there_and_unchanged()
    {
        using var sandbox = new TestSandbox();
        var (executor, handler) = WithLedger(sandbox);
        Directory.CreateDirectory(sandbox.Locations.WindowsTemp);
        var changed = Path.Combine(sandbox.Locations.WindowsTemp, "changed.tmp");
        File.WriteAllText(changed, "now longer than at scan time");
        PlanItem Q(string path, long size) => new() { Kind = ActionKind.QuarantinePath, FeatureId = "cleanup", Description = "q", NeedsAdmin = true, SourcePath = path, CarveOutId = CarveOuts.WindowsTemp, ExpectedSize = size };

        var results = await executor.RunAsync("run-d", new[]
        {
            Steps.Of(Q(Path.Combine(sandbox.Locations.WindowsTemp, "gone.tmp"), 1), 0),
            Steps.Of(Q(changed, 3), 1),
        }, ExecutionMode.Real, undo: false, default);

        Assert.Equal("It's no longer there.", results[0].Message);
        Assert.Equal("It changed since the scan, so it was left alone.", results[1].Message);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task The_delete_cap_is_enforced_in_the_helper_too()
    {
        using var sandbox = new TestSandbox();
        var (executor, handler) = WithLedger(sandbox, cap: (items, _) => items.Count > 1 ? "Stopped before any change: over the safety limit." : null);

        var results = await executor.RunAsync("run-e", new[] { Steps.Of(Service(), 0), Steps.Of(Service(), 1) }, ExecutionMode.Real, undo: false, default);

        Assert.All(results, r => Assert.Equal("Stopped before any change: over the safety limit.", r.Message));
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public void Only_a_parent_running_PC_Tidys_own_program_is_trusted()
    {
        var own = Environment.ProcessPath!;
        Assert.Equal(Path.GetFullPath(own), Path.GetFullPath(ProcessIdentity.ImagePath(Environment.ProcessId)!), ignoreCase: true);
        Assert.True(ElevatedHost.IsTrustedParent(Environment.ProcessId, own));
        Assert.False(ElevatedHost.IsTrustedParent(Environment.ProcessId, Path.Combine(Environment.SystemDirectory, "notepad.exe")));
        Assert.False(ElevatedHost.IsTrustedParent(Environment.ProcessId, null));
    }

    [Fact]
    public async Task Dispose_never_waits_for_a_batch_still_in_flight()
    {
        using var sandbox = new TestSandbox();
        var started = new TaskCompletionSource();
        var handler = new RecordingHandler(ActionKind.ServiceSetStartType)
        {
            BeforeExecute = async (_, ct) =>
            {
                started.TrySetResult();
                try { await Task.Delay(TimeSpan.FromSeconds(20), ct); } catch (OperationCanceledException) { }
            },
        };
        var launcher = new InProcessLauncher(new[] { handler }, () => sandbox.Context());
        var service = new ElevationService(sandbox.Locations, null, launcher, allowLaunchInSandbox: true, TimeSpan.FromSeconds(20));
        var batch = service.ExecuteElevatedAsync("r", new[] { Steps.Of(Service(), 0) }, ExecutionMode.Real, false, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var clock = System.Diagnostics.Stopwatch.StartNew();
        await service.DisposeAsync();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"Dispose took {clock.Elapsed.TotalSeconds:0.0} s");

        var results = await batch.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Single(results); // the batch still gets an answer (here: unknown / not run), it isn't left hanging
    }

    private sealed class InProcessLauncher(IEnumerable<IActionHandler> handlers, Func<HandlerContext> ctx) : IHelperLauncher
    {
        public HelperProcess Launch(string pipeName, int parentPid)
        {
            var task = Task.Run(() => ElevatedHost.RunAsync(new[] { ElevationProtocol.ElevatedSwitch, pipeName, parentPid.ToString() }, handlers, ctx, null, ElevatedHost.StartupChecks.None));
            return new HelperProcess(Environment.ProcessId, () => task.IsCompleted);
        }
    }

    [Fact]
    public async Task A_registry_backup_is_checked_and_imported_only_as_a_private_copy()
    {
        using var sandbox = new TestSandbox();
        var backup = Path.Combine(sandbox.Root, "user-writable.reg");
        // A tampered backup that would delete another key: refused before reg.exe is ever started.
        File.WriteAllText(backup, "Windows Registry Editor Version 5.00\r\n\r\n[-HKEY_LOCAL_MACHINE\\SOFTWARE\\Other]\r\n");
        var privateFolder = Path.Combine(sandbox.Root, "Admin", "backups", "reg", "import");

        var problem = await RegFileBackup.ImportVerifiedCopyAsync(backup, privateFolder, RegistryHive.LocalMachine, @"SOFTWARE\Contoso\Thing", view64: true, default);

        Assert.Contains("deletions", problem);
        Assert.Empty(Directory.EnumerateFiles(privateFolder)); // the private copy is removed again
    }

    [Fact]
    public void Restart_Manager_probes_use_the_literal_name()
    {
        using var sandbox = new TestSandbox();
        var odd = Path.Combine(sandbox.Root, "trailing dot.");
        File.WriteAllText(RestartManagerInUseChecker.Literal(odd), "x");
        var checker = new RestartManagerInUseChecker();
        Assert.False(checker.IsLocked(odd));
        using (new FileStream(RestartManagerInUseChecker.Literal(odd), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.True(checker.IsLocked(odd));
        File.Delete(RestartManagerInUseChecker.Literal(odd));
    }
}
