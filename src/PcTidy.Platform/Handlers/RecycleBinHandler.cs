using System.Runtime.InteropServices;
using PcTidy.Core.Model;
using static PcTidy.Platform.NativeMethods;

namespace PcTidy.Platform;

/// <summary>Size of the current user's Recycle Bin on all drives.</summary>
public sealed record RecycleBinInfo(long Bytes, long Items);

/// <summary>Read-only Recycle Bin query through Windows' own API (never by opening $Recycle.Bin).</summary>
public static class RecycleBin
{
    /// <summary>Current size, or null if Windows couldn't tell.</summary>
    public static RecycleBinInfo? Query()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBinW(null, ref info) == 0 ? new RecycleBinInfo(info.i64Size, info.i64NumItems) : null;
    }
}

/// <summary>
/// EmptyRecycleBin: empties the current user's bin with SHEmptyRecycleBin (no dialog, no sound). Permanent: can't be undone.
/// In the sandbox it only reports what it would do and never empties the real bin.
/// </summary>
public sealed class RecycleBinHandler : IActionHandler
{
    public bool CanHandle(PlanItem item) => item.Kind == ActionKind.EmptyRecycleBin;

    public Task<StepRecord> ExecuteAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        if (ctx.Mode == ExecutionMode.Simulate) return SimulateAsync(step, ctx, ct);
        if (ctx.Locations.IsSandbox) return Task.FromResult(step.SandboxSimulated(WouldEmpty()));
        // The elevated helper may run as another account; the bin to empty is the signed-in user's.
        if (ctx.IsElevated) return Task.FromResult(step.Skipped("The Recycle Bin is emptied by PC Tidy itself, not its admin helper."));

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var before = RecycleBin.Query();
            if (before is { Items: 0 }) return step.Skipped("The Recycle Bin is already empty.");

            var hr = SHEmptyRecycleBinW(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
            var after = RecycleBin.Query();
            if (hr != 0 && hr != E_UNEXPECTED && after is not { Items: 0 })
                return step.Failed("Windows couldn't empty the Recycle Bin. Some files may be in use.");

            var freed = Math.Max(0, (before?.Bytes ?? 0) - (after?.Bytes ?? 0));
            return after is { Items: > 0 }
                ? step.Done($"Emptied most of the Recycle Bin ({Sizes.Format(freed)}). A few items couldn't be removed.") with { BytesAffected = freed }
                : step.Done($"Emptied the Recycle Bin ({Sizes.Format(freed)}).") with { BytesAffected = freed };
        }, ct);
    }

    public Task<StepRecord> SimulateAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
        => Task.FromResult(step.Simulated(WouldEmpty() + ". This can't be undone."));

    public Task<StepRecord> UndoAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
        => Task.FromResult(step.CannotUndo());

    public Task<bool> WasCompletedAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
        => Task.FromResult(!ctx.Locations.IsSandbox && RecycleBin.Query() is { Items: 0 });

    private static string WouldEmpty()
        => RecycleBin.Query() is { } info
            ? $"Would empty the Recycle Bin ({Sizes.Format(info.Bytes)} in {info.Items} item{(info.Items == 1 ? "" : "s")})"
            : "Would empty the Recycle Bin";
}
