using System.Diagnostics;
using System.Net.Http;
using PcTidy.Features.PcCare.Analysis;

namespace PcTidy.Features.PcCare.Source;

/// <summary>
/// The lag check (build list B2): latency while idle, then while the download is full (~10 s), then while the upload is
/// full (~10 s). Opt-in only: the page runs it when the user presses Run test. It measures and returns numbers; it never
/// produces PlanItems and never changes anything. Each request has a timeout and everything stops on cancel.
/// </summary>
public sealed class LagCheckRunner : IDisposable
{
    private readonly ILagCheckTransport _transport;
    private readonly LagCheckPlan _plan;
    private readonly string _host;
    private readonly TimeProvider _clock;

    public LagCheckRunner(ILagCheckTransport transport, string host, LagCheckPlan? plan = null, TimeProvider? clock = null)
    {
        _transport = transport;
        _host = host;
        _plan = plan ?? LagCheckPlan.Default;
        _clock = clock ?? TimeProvider.System;
    }

    public LagCheckPlan Plan => _plan;

    /// <summary>Disposes the transport when it owns a connection pool.</summary>
    public void Dispose() => (_transport as IDisposable)?.Dispose();

    public async Task<LagCheckResult> RunAsync(IProgress<LagCheckProgress>? progress, CancellationToken ct)
    {
        var total = _plan.ApproxDuration.TotalMilliseconds;
        var clock = Stopwatch.StartNew();
        void Report(LagCheckStage stage, string message)
            => progress?.Report(new LagCheckProgress(stage, total <= 0 ? 0 : Math.Clamp(clock.Elapsed.TotalMilliseconds / total, 0, 0.99), message));

        // 1. Idle: one warm-up request (opens the connection; not counted), then the idle samples.
        Report(LagCheckStage.Idle, "Measuring delay while the line is quiet…");
        await _transport.PingAsync(_plan.PingTimeout, ct).ConfigureAwait(false);
        var idleSamples = new List<double?>();
        for (int i = 0; i < _plan.IdleSamples; i++)
        {
            ct.ThrowIfCancellationRequested();
            idleSamples.Add(Ms(await _transport.PingAsync(_plan.PingTimeout, ct).ConfigureAwait(false)));
            Report(LagCheckStage.Idle, $"Measuring delay while the line is quiet… {i + 1} of {_plan.IdleSamples}");
            if (i < _plan.IdleSamples - 1) await Delay(_plan.PingInterval, ct).ConfigureAwait(false);
        }
        var idle = LagCheckMath.Stats(idleSamples);
        if (idle is null)
        {
            return new LagCheckResult
            {
                Outcome = LagCheckOutcome.NoAnswer, Host = _host, FinishedUtc = _clock.GetUtcNow().UtcDateTime,
                Problem = $"{_host} didn't answer. Check that you're online, then try again.",
            };
        }

        // 2. Download full, 3. a short quiet gap, 4. upload full.
        Report(LagCheckStage.Download, "Measuring delay while downloading…");
        var down = await LoadedPhaseAsync(upload: false, m => Report(LagCheckStage.Download, m), ct).ConfigureAwait(false);
        Report(LagCheckStage.Settle, "Letting the line go quiet…");
        await Delay(_plan.Settle, ct).ConfigureAwait(false);
        Report(LagCheckStage.Upload, "Measuring delay while uploading…");
        var up = await LoadedPhaseAsync(upload: true, m => Report(LagCheckStage.Upload, m), ct).ConfigureAwait(false);
        progress?.Report(new LagCheckProgress(LagCheckStage.Done, 1, "Done"));

        var problems = new[] { down.Problem, up.Problem }.Where(p => p is not null).ToList();
        return new LagCheckResult
        {
            Outcome = down.Stats is not null && up.Stats is not null ? LagCheckOutcome.Done : LagCheckOutcome.Partial,
            Host = _host,
            FinishedUtc = _clock.GetUtcNow().UtcDateTime,
            Idle = idle,
            UnderDownload = down.Stats,
            UnderUpload = up.Stats,
            BytesDownloaded = down.Bytes,
            BytesUploaded = up.Bytes,
            DownloadMbps = down.Mbps,
            UploadMbps = up.Mbps,
            Problem = problems.Count > 0 ? string.Join(" ", problems) : null,
        };
    }

    private sealed record PhaseResult(LatencyStats? Stats, long Bytes, double? Mbps, string? Problem);

    /// <summary>
    /// Fills the line in one direction with <see cref="LagCheckPlan.Streams"/> parallel transfers (until the time or the byte
    /// budget runs out) and takes a timing sample every interval after the ramp-up.
    /// </summary>
    private async Task<PhaseResult> LoadedPhaseAsync(bool upload, Action<string> report, CancellationToken ct)
    {
        using var phase = CancellationTokenSource.CreateLinkedTokenSource(ct);
        phase.CancelAfter(_plan.LoadDuration);
        long budget = upload ? _plan.UploadBudgetBytes : _plan.DownloadBudgetBytes;
        long chunk = upload ? _plan.UploadChunkBytes : _plan.DownloadChunkBytes;
        long moved = 0;
        int failures = 0;
        var sw = Stopwatch.StartNew();

        long Reserve()
        {
            while (true)
            {
                long left = Interlocked.Read(ref budget);
                if (left <= 0) return 0;
                long take = Math.Min(chunk, left);
                if (Interlocked.CompareExchange(ref budget, left - take, left) == left) return take;
            }
        }

        async Task Worker()
        {
            while (!phase.IsCancellationRequested)
            {
                long take = Reserve();
                if (take <= 0) return;
                using var one = CancellationTokenSource.CreateLinkedTokenSource(phase.Token);
                one.CancelAfter(_plan.ChunkTimeout);
                try
                {
                    void Count(int n) => Interlocked.Add(ref moved, n);
                    if (upload) await _transport.UploadAsync(take, Count, one.Token).ConfigureAwait(false);
                    else await _transport.DownloadAsync(take, Count, one.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    if (!phase.IsCancellationRequested) { Interlocked.Increment(ref failures); return; } // this transfer timed out
                    return; // the phase is over
                }
                catch (HttpRequestException) { Interlocked.Increment(ref failures); return; }
                catch (IOException) { Interlocked.Increment(ref failures); return; }
            }
        }

        // Started directly (not Task.Run) so each transfer is already under way before the first sample.
        var workers = Enumerable.Range(0, Math.Max(1, _plan.Streams)).Select(_ => Worker()).ToList();
        var allDone = Task.WhenAll(workers);

        var samples = new List<double?>();
        try
        {
            await Task.WhenAny(allDone, Task.Delay(_plan.RampUp, phase.Token)).ConfigureAwait(false);
            while (!phase.IsCancellationRequested && !allDone.IsCompleted)
            {
                var rtt = await _transport.PingAsync(_plan.PingTimeout, ct).ConfigureAwait(false);
                // A sample is "under load" only if the line was still busy when it came back.
                if (!allDone.IsCompleted) samples.Add(Ms(rtt));
                report($"{(upload ? "Uploading" : "Downloading")}… {samples.Count(s => s is not null)} samples");
                await Task.WhenAny(allDone, Task.Delay(_plan.PingInterval, phase.Token)).ConfigureAwait(false);
            }
        }
        finally
        {
            phase.Cancel(); // stop the transfers
            try { await allDone.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        ct.ThrowIfCancellationRequested();
        sw.Stop();

        var stats = LagCheckMath.Stats(samples);
        long bytes = Interlocked.Read(ref moved);
        double? mbps = bytes > 0 && sw.Elapsed.TotalSeconds > 0 ? bytes * 8 / sw.Elapsed.TotalSeconds / 1_000_000 : null;
        string? problem = stats is not null ? null
            : bytes == 0 || failures > 0
                ? $"The {(upload ? "upload" : "download")} to {_host} didn't work, so delay under {(upload ? "upload" : "download")} wasn't measured."
                : $"The {(upload ? "upload" : "download")} finished too fast to measure delay under load.";
        return new PhaseResult(stats, bytes, mbps, problem);
    }

    private static double? Ms(TimeSpan? t) => t?.TotalMilliseconds;

    private static async Task Delay(TimeSpan t, CancellationToken ct)
    {
        if (t > TimeSpan.Zero) await Task.Delay(t, ct).ConfigureAwait(false);
    }
}
