using PcTidy.App.Shell;
using PcTidy.Core.Model;
using PcTidy.Features.Schedule;

namespace PcTidy.App.Composition;

/// <summary>
/// Before/after proof around every REAL run (Plan "Across the whole app"): Schedule &amp; reports samples free space,
/// startup count, idle RAM/CPU… before and after, saves the run report, and may plan the optional report copy.
/// Each step is time-boxed and can never stop or fail a run. Not used in demo mode (it would write into the demo's
/// pretend data folder) and never for simulations.
/// </summary>
public sealed class RunProofBridge
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);
    private readonly RunProof? _proof;

    public RunProofBridge(AppServices services) => _proof = services.IsDemo ? null : SafeCreate(services);

    private static RunProof? SafeCreate(AppServices services)
    {
        try { return RunProof.From(services); }
        catch (Exception ex) { CrashHandler.Write(ex, "Before/after proof", terminating: false); return null; }
    }

    public bool IsAvailable => _proof is not null;

    /// <summary>"before.*" metrics for <see cref="PcTidy.Core.ExecuteOptions.MetricsBefore"/>, or null.</summary>
    public async Task<Dictionary<string, double>?> BeforeAsync(string runId)
    {
        if (_proof is null) return null;
        using var cts = new CancellationTokenSource(Limit);
        try { return await Task.Run(() => _proof.SampleBeforeAsync(runId, cts.Token), cts.Token); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { if (ex is not OperationCanceledException) CrashHandler.Write(ex, "Before/after proof", terminating: false); return null; }
    }

    /// <summary>The Done card's before/after lines and the optional report-copy plan (run it with RunApprovedAsync).</summary>
    public async Task<(IReadOnlyList<string> Lines, ActionPlan? CopyPlan)> AfterAsync(IReadOnlyList<RunRecord> runs)
    {
        if (_proof is null || runs.Count == 0) return (Array.Empty<string>(), null);
        using var cts = new CancellationTokenSource(Limit);
        try
        {
            var result = await Task.Run(() => _proof.AfterRunAsync(runs, cts.Token), cts.Token);
            return (result.Changes.Select(c => c.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList(), result.CopyPlan);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex is not OperationCanceledException) CrashHandler.Write(ex, "Before/after proof", terminating: false);
            return (Array.Empty<string>(), null);
        }
    }
}
