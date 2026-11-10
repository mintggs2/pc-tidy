using PcTidy.Core;
using PcTidy.Core.Model;

namespace PcTidy.Engine;

/// <summary>
/// The elevated helper's read-only "settle" request (<see cref="IElevationSettlement"/>). An elevation service without
/// it can't settle: its unfinished steps simply stay Started (they are never re-run to find out).
/// </summary>
internal static class HelperSettlement
{
    public delegate Task<IReadOnlyList<StepRecord>> Settle(string runId, IReadOnlyList<StepRecord> steps, CancellationToken ct);

    public const string MethodName = nameof(IElevationSettlement.SettleElevatedAsync);

    public static Settle? For(IElevationService? elevation)
        => elevation is IElevationSettlement s ? s.SettleElevatedAsync : null;
}
