using System.Runtime.InteropServices;
using PcTidy.Core.Model;

namespace PcTidy.Platform;

/// <summary>Reads and switches the active power plan. Real: powrprof.dll. Tests: a fake.</summary>
public interface IPowerSchemeControl
{
    bool TouchesRealSystem { get; }
    Guid? GetActive();
    bool Exists(Guid scheme);
    void SetActive(Guid scheme);
}

public sealed class WindowsPowerSchemeControl : IPowerSchemeControl
{
    public bool TouchesRealSystem => true;

    public Guid? GetActive()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var ptr) != 0 || ptr == IntPtr.Zero) return null;
        try { return Marshal.PtrToStructure<Guid>(ptr); }
        finally { LocalFree(ptr); }
    }

    public bool Exists(Guid scheme)
    {
        // Asking for the plan's name fails with ERROR_FILE_NOT_FOUND when the plan doesn't exist.
        uint size = 0;
        return PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size) == 0;
    }

    public void SetActive(Guid scheme)
    {
        var rc = PowerSetActiveScheme(IntPtr.Zero, ref scheme);
        if (rc != 0) throw new System.ComponentModel.Win32Exception((int)rc);
    }

    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr rootPowerKey, out IntPtr activePolicyGuid);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(IntPtr rootPowerKey, ref Guid schemeGuid);
    [DllImport("powrprof.dll")]
    private static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid, IntPtr subGroup, IntPtr setting, IntPtr buffer, ref uint bufferSize);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr mem);
}

/// <summary>
/// PowerPlanSet. Args: "schemeGuid" (an existing plan on this PC). Records the plan that was active (priorScheme)
/// and undo switches back to it. No admin needed (the per-user active plan).
/// </summary>
public sealed class PowerPlanHandler(IPowerSchemeControl control) : SystemActionHandler
{
    internal const string PriorSchemeData = "priorScheme";

    public PowerPlanHandler() : this(new WindowsPowerSchemeControl()) { }

    protected override ActionKind Kind => ActionKind.PowerPlanSet;
    protected override bool TouchesRealSystem => control.TouchesRealSystem;

    protected override string? Validate(PlanItem item)
        => Guid.TryParse(item.Arg("schemeGuid").Trim(), out var g) && g != Guid.Empty ? null : "This step doesn't name a valid power plan.";

    protected override string Describe(PlanItem item) => $"Would switch the power plan to {Scheme(item):D} (PowerSetActiveScheme)";

    protected override bool CanUndo(StepRecord step) => Guid.TryParse(step.Undo(PriorSchemeData), out _);
    protected override string DescribeUndo(StepRecord step) => $"Would switch the power plan back to {step.Undo(PriorSchemeData)}";

    private static Guid Scheme(PlanItem item) => Guid.Parse(item.Arg("schemeGuid").Trim());

    protected override Task<StepRecord> RunAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var target = Scheme(step.Item);
        if (!control.Exists(target)) return Task.FromResult(step.Skipped("This power plan isn't on this PC."));
        var before = control.GetActive();
        if (before == target) return Task.FromResult(step.Skipped("This power plan is already active. Nothing to change."));
        control.SetActive(target);
        if (control.GetActive() != target)
        {
            if (before is { } b) control.SetActive(b);
            return Task.FromResult(step.Failed("Windows didn't keep the new power plan. The old one is still active."));
        }
        var done = step.Done("Switched the power plan.");
        return Task.FromResult(before is { } prior ? done.WithUndo((PriorSchemeData, prior.ToString("D"))) : done);
    }

    protected override Task<StepRecord> RevertAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        if (!Guid.TryParse(step.Undo(PriorSchemeData), out var prior)) return Task.FromResult(step.CannotUndo());
        if (!control.Exists(prior)) return Task.FromResult(step.UndoFailed("The power plan that was active before isn't on this PC any more."));
        control.SetActive(prior);
        return Task.FromResult(control.GetActive() == prior ? step.Undone("Switched back to the previous power plan.") : step.UndoFailed("Windows didn't keep the previous power plan."));
    }

    protected override Task<bool> CheckCompletedAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
        => Task.FromResult(control.GetActive() == Scheme(step.Item));
}
