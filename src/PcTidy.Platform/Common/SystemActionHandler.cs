using System.ComponentModel;
using System.Runtime.InteropServices;
using PcTidy.Core.Model;

namespace PcTidy.Platform;

/// <summary>
/// Shared shape of the v2 system handlers. Each one talks to Windows only through a small backend interface
/// (real: Windows APIs; tests: an in-memory fake with <see cref="TouchesRealSystem"/> = false), so argument checks,
/// plan → API mapping and prior-state / undo logic can all be tested without changing the PC.
/// Order of checks: simulate mode → arguments → sandbox (real backend: Simulated, nothing runs) → admin helper rules → run.
/// </summary>
public abstract class SystemActionHandler : IActionHandler
{
    protected const string NeedsAdminMessage = "This needs admin rights. PC Tidy runs it in its admin helper.";
    protected const string PerUserMessage = "This changes something for your own account, so PC Tidy's admin helper doesn't do it.";
    protected const string AccessDeniedMessage = "Windows didn't allow this (access denied). Nothing was changed.";

    protected abstract ActionKind Kind { get; }

    /// <summary>False for test fakes: then the handler logic runs even in the sandbox.</summary>
    protected abstract bool TouchesRealSystem { get; }

    public virtual bool CanHandle(PlanItem item) => item.Kind == Kind;

    /// <summary>Null when the item's arguments are acceptable, otherwise a plain-English reason (the step is skipped).</summary>
    protected abstract string? Validate(PlanItem item);

    /// <summary>What would happen, starting with "Would ..." and without a final period.</summary>
    protected abstract string Describe(PlanItem item);

    /// <summary>Needs the elevated helper (the step is skipped when run without admin rights).</summary>
    protected virtual bool NeedsElevation(PlanItem item) => false;

    /// <summary>Per-user work (windows, Store apps, the user's own settings) is never done by the admin helper.</summary>
    protected virtual bool PerUserOnly(PlanItem item) => false;

    /// <summary>Whether a finished step can be put back (decided from what was recorded when it ran).</summary>
    protected virtual bool CanUndo(StepRecord step) => false;

    /// <summary>What undo would do, for the sandbox ("Would ...", no final period).</summary>
    protected virtual string DescribeUndo(StepRecord step) => "Would put this back";

    protected abstract Task<StepRecord> RunAsync(StepRecord step, HandlerContext ctx, CancellationToken ct);

    protected virtual Task<StepRecord> RevertAsync(StepRecord step, HandlerContext ctx, CancellationToken ct) => Task.FromResult(step.CannotUndo());

    /// <summary>Resume: did a step left as Started really happen? Default: no (it is then shown as skipped).</summary>
    protected virtual Task<bool> CheckCompletedAsync(StepRecord step, HandlerContext ctx, CancellationToken ct) => Task.FromResult(false);

    public async Task<StepRecord> ExecuteAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        if (ctx.Mode == ExecutionMode.Simulate) return await SimulateAsync(step, ctx, ct).ConfigureAwait(false);
        if (Validate(step.Item) is { } problem) return step.Skipped(problem);
        if (ctx.Locations.IsSandbox && TouchesRealSystem) return step.SandboxSimulated(Describe(step.Item));
        if (PerUserOnly(step.Item) && ctx.IsElevated) return step.Skipped(PerUserMessage);
        if (TouchesRealSystem && NeedsElevation(step.Item) && !ctx.IsElevated()) return step.Skipped(NeedsAdminMessage);
        ct.ThrowIfCancellationRequested();
        try
        {
            return await RunAsync(step, ctx, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return step.Skipped(AccessDeniedMessage);
        }
        catch (Exception ex) when (IsWindowsError(ex))
        {
            return step.Failed("Windows reported a problem: " + ex.Message);
        }
    }

    public Task<StepRecord> SimulateAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
        => Task.FromResult(Validate(step.Item) is { } problem ? step.Skipped(problem) : step.Simulated(Describe(step.Item) + "." + PermanentNote(step.Item)));

    public async Task<StepRecord> UndoAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        if (step.Status != StepStatus.Done) return step.Skipped("Nothing to undo.");
        if (!CanUndo(step)) return await RevertAsync(step, ctx, ct).ConfigureAwait(false);
        if (ctx.Locations.IsSandbox && TouchesRealSystem) return step.SandboxSimulated(DescribeUndo(step));
        if (PerUserOnly(step.Item) && ctx.IsElevated) return step.UndoFailed(PerUserMessage);
        if (TouchesRealSystem && NeedsElevation(step.Item) && !ctx.IsElevated()) return step.UndoFailed("Undoing this needs admin rights. PC Tidy runs it in its admin helper.");
        try
        {
            return await RevertAsync(step, ctx, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return step.UndoFailed("Windows didn't allow putting this back (access denied).");
        }
        catch (Exception ex) when (IsWindowsError(ex))
        {
            return step.UndoFailed("Windows couldn't put this back: " + ex.Message);
        }
    }

    public async Task<bool> WasCompletedAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        if (Validate(step.Item) is not null) return false;
        if (ctx.Locations.IsSandbox && TouchesRealSystem) return false;
        try { return await CheckCompletedAsync(step, ctx, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return false; }
    }

    private static string PermanentNote(PlanItem item) => item.Reversible ? "" : " This can't be undone.";

    protected static bool IsWindowsError(Exception ex)
        => ex is Win32Exception or COMException or IOException or InvalidOperationException or System.Management.ManagementException
            or ExternalException or ArgumentException;

    /// <summary>Folder for a step's backups: &lt;DataRoot&gt;\backups\&lt;area&gt; (in the admin helper: its admin-only folder).</summary>
    protected static string BackupFolder(HandlerContext ctx, string area) => Path.Combine(DataPaths.Backups(ctx.Locations.DataRoot), area);

    protected static string SafeRunId(HandlerContext ctx) => string.Concat(ctx.RunId.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
}
