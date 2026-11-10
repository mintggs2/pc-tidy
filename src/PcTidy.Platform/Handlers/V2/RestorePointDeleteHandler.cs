using System.Globalization;
using System.Runtime.InteropServices;
using PcTidy.Core.Model;

namespace PcTidy.Platform;

/// <summary>Lists and removes restore points. Real: WMI listing + SRRemoveRestorePoint. Tests: a fake.</summary>
public interface IRestorePointRemover
{
    bool TouchesRealSystem { get; }
    RestorePointListing List();
    /// <summary>Windows' result code (0 = removed).</summary>
    uint Remove(uint sequenceNumber);
}

public sealed class WindowsRestorePointRemover : IRestorePointRemover
{
    private readonly WmiRestorePointBackend _wmi = new();
    public bool TouchesRealSystem => true;
    public RestorePointListing List() => _wmi.List();
    public uint Remove(uint sequenceNumber) => SRRemoveRestorePoint(sequenceNumber);

    [DllImport("srclient.dll")] private static extern uint SRRemoveRestorePoint(uint dwRPNum);
}

/// <summary>
/// RestorePointDelete. Args: "sequenceNumber". NOT reversible (Windows can't bring a restore point back).
/// The newest restore point is always kept, so there is still a way back after PC Tidy's own system changes.
/// Success is checked by listing again, never by the return code alone.
/// </summary>
public sealed class RestorePointDeleteHandler(IRestorePointRemover remover) : SystemActionHandler
{
    public RestorePointDeleteHandler() : this(new WindowsRestorePointRemover()) { }

    protected override ActionKind Kind => ActionKind.RestorePointDelete;
    protected override bool TouchesRealSystem => remover.TouchesRealSystem;
    protected override bool NeedsElevation(PlanItem item) => true;

    private static bool TryNumber(PlanItem item, out uint n)
        => uint.TryParse(item.Arg("sequenceNumber").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out n) && n > 0;

    protected override string? Validate(PlanItem item)
    {
        if (!TryNumber(item, out _)) return "This step doesn't name a valid restore point.";
        return item.Reversible ? "Deleting a restore point can't be undone, so the step must say so." : null;
    }

    protected override string Describe(PlanItem item) => $"Would delete restore point #{item.Arg("sequenceNumber").Trim()} (SRRemoveRestorePoint)";

    protected override Task<StepRecord> RunAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        TryNumber(step.Item, out var number);
        var before = remover.List();
        if (before.Problem is { } problem) return Task.FromResult(step.Skipped(problem));
        if (before.Points.All(p => p.SequenceNumber != number)) return Task.FromResult(step.Skipped("This restore point is already gone."));
        if (before.Points.Max(p => p.SequenceNumber) == number) return Task.FromResult(step.Skipped("PC Tidy always keeps your newest restore point."));

        var rc = remover.Remove(number);
        var after = remover.List();
        if (after.Problem is null && after.Points.All(p => p.SequenceNumber != number))
            return Task.FromResult(step.Done($"Deleted restore point #{number}."));
        return Task.FromResult(step.Failed(rc == 0 ? "Windows said the restore point was deleted, but it's still listed." : $"Windows couldn't delete this restore point (code {rc})."));
    }

    protected override Task<bool> CheckCompletedAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        TryNumber(step.Item, out var number);
        var now = remover.List();
        return Task.FromResult(now.Problem is null && now.Points.All(p => p.SequenceNumber != number));
    }
}
