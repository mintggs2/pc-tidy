using System.IO.Pipes;
using PcTidy.Core.Model;

namespace PcTidy.Platform;

/// <summary>
/// Runs admin-only steps in PC Tidy's elevated helper with one admin prompt per session: the first request starts
/// the helper (Verb=runas), later requests (restore point, steps, undo) reuse it until <see cref="DisposeAsync"/>
/// or the helper's idle timeout. Requests go over a per-user named pipe as JSON; the helper re-checks everything.
/// If the prompt is cancelled or refused, the steps come back Skipped with a friendly reason.
/// In the sandbox it never starts anything: steps run in-process through the given executor (same validation),
/// or come back Simulated if there is none.
/// </summary>
public sealed class ElevationService : IElevationService, IElevationSettlement, IAsyncDisposable, IDisposable
{
    private readonly IKnownLocations _locations;
    private readonly ElevatedBatchExecutor? _inProcess;
    private readonly IHelperLauncher _launcher;
    private readonly bool _allowLaunchInSandbox;
    private readonly TimeSpan _connectTimeout;
    private readonly Func<string?> _preLaunchCheck;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Session? _session;

    /// <param name="locations">The app's locations (sandbox = never elevate).</param>
    /// <param name="inProcessExecutor">Used when no helper is needed: in the sandbox, or when PC Tidy already runs elevated.</param>
    public ElevationService(IKnownLocations locations, ElevatedBatchExecutor? inProcessExecutor = null)
        : this(locations, inProcessExecutor, new RunAsLauncher(), allowLaunchInSandbox: false, TimeSpan.FromSeconds(60), InstallIntegrity.PreLaunchCheck) { }

    /// <summary>Tests: a fake launcher that starts <see cref="ElevatedHost"/> in-process, never runas.</summary>
    /// <param name="preLaunchCheck">
    /// Asked before every admin prompt: null = go ahead, otherwise the reason PC Tidy won't ask (the steps are Skipped
    /// with it). Real: <see cref="InstallIntegrity.PreLaunchCheck"/> (installed copy only, no unsafe settings).
    /// </param>
    internal ElevationService(IKnownLocations locations, ElevatedBatchExecutor? inProcessExecutor, IHelperLauncher launcher, bool allowLaunchInSandbox, TimeSpan connectTimeout,
        Func<string?>? preLaunchCheck = null)
    {
        _locations = locations;
        _inProcess = inProcessExecutor;
        _launcher = launcher;
        _allowLaunchInSandbox = allowLaunchInSandbox;
        _connectTimeout = connectTimeout;
        _preLaunchCheck = preLaunchCheck ?? (() => null);
    }

    public bool IsElevated => ProcessElevation.IsElevated;

    private bool RunsInProcess => IsElevated || (_locations.IsSandbox && !_allowLaunchInSandbox);

    public async Task<IReadOnlyList<StepRecord>> ExecuteElevatedAsync(string runId, IReadOnlyList<StepRecord> steps, ExecutionMode mode, bool undo, CancellationToken ct)
    {
        if (steps.Count == 0) return Array.Empty<StepRecord>();

        if (RunsInProcess)
        {
            if (_inProcess is not null) return await _inProcess.RunAsync(runId, steps, mode, undo, ct).ConfigureAwait(false);
            return steps.Select(s => _locations.IsSandbox
                ? s.SandboxSimulated(undo ? "Would undo this in the admin helper" : "Would run this in the admin helper")
                : Refuse(s, undo, "PC Tidy is already running as admin but has no way to run this step.")).ToList();
        }

        var request = new ElevatedRequest { Kind = ElevatedRequestKind.Execute, RunId = runId, Mode = mode, Undo = undo, Steps = steps };
        try
        {
            var response = await SendAsync(request, ct).ConfigureAwait(false);
            if (response.Error is { } error) return steps.Select(s => Refuse(s, undo, error)).ToList();
            if (response.Steps is not { } results || results.Count != steps.Count)
                return steps.Select(s => Refuse(s, undo, "PC Tidy's admin helper gave an unexpected answer.")).ToList();
            return results;
        }
        catch (ElevationRefusedException ex)
        {
            return steps.Select(s => Refuse(s, undo, ex.Message)).ToList();
        }
        catch (Exception ex) when (ex is ElevationDeclinedException or ElevationUnavailableException)
        {
            var reason = $"{ex.Message}, so {(undo ? "this wasn't undone" : AdminSteps.SkippedMarker)}.";
            return steps.Select(s => Refuse(s, undo, reason)).ToList();
        }
        catch (HelperLostException)
        {
            // The request reached the helper, so some steps may have run: report them as Started (unknown).
            return steps.Select(s => s with { Status = StepStatus.Started, Message = "PC Tidy's admin helper stopped before answering. PC Tidy will check what happened." }).ToList();
        }
    }

    /// <summary>Makes (or reuses) a restore point in the helper. Used by <see cref="AdminRestorePointService"/>.</summary>
    public async Task<RestorePointInfo> EnsureRestorePointAsync(string description, CancellationToken ct)
    {
        if (RunsInProcess) return await new RestorePointService(_locations).EnsureAsync(description, ct).ConfigureAwait(false);
        try
        {
            var response = await SendAsync(new ElevatedRequest { Kind = ElevatedRequestKind.RestorePoint, Description = description }, ct).ConfigureAwait(false);
            return response.RestorePoint ?? new RestorePointInfo(false, false, description, null, response.Error ?? "PC Tidy's admin helper gave an unexpected answer.");
        }
        catch (ElevationRefusedException ex)
        {
            return new RestorePointInfo(false, false, description, null, ex.Message + " No restore point was made.");
        }
        catch (Exception ex) when (ex is ElevationDeclinedException or ElevationUnavailableException)
        {
            return new RestorePointInfo(false, false, description, null, $"{ex.Message}, so no restore point was made.");
        }
        catch (HelperLostException)
        {
            return new RestorePointInfo(false, false, description, null, "PC Tidy's admin helper stopped before making the restore point.");
        }
    }

    /// <summary>
    /// For admin steps the journal shows as Started (the helper stopped before answering): asks the helper, which answers
    /// from its own ledger (Done with its recorded undo data, or not done). Read-only; one result per step, same order.
    /// If the helper can't be reached the steps come back unchanged (still Started) so nothing is guessed.
    /// </summary>
    public async Task<IReadOnlyList<StepRecord>> SettleElevatedAsync(string runId, IReadOnlyList<StepRecord> steps, CancellationToken ct)
    {
        if (steps.Count == 0) return Array.Empty<StepRecord>();
        if (RunsInProcess)
            return _inProcess is not null ? await _inProcess.SettleAsync(runId, steps, ct).ConfigureAwait(false) : steps;
        try
        {
            var response = await SendAsync(new ElevatedRequest { Kind = ElevatedRequestKind.Settle, RunId = runId, Steps = steps }, ct).ConfigureAwait(false);
            return response.Steps is { } results && results.Count == steps.Count ? results : steps;
        }
        catch (Exception ex) when (ex is ElevationDeclinedException or ElevationUnavailableException or ElevationRefusedException or HelperLostException)
        {
            return steps;
        }
    }

    /// <summary>The admin-only quarantine, for the Quarantine page. Empty when PC Tidy runs in-process (sandbox / already elevated).</summary>
    public async Task<AdminQuarantineListing> ListAdminQuarantineAsync(CancellationToken ct)
    {
        if (RunsInProcess)
            return new AdminQuarantineListing(_inProcess is null ? Array.Empty<QuarantineEntry>() : await _inProcess.ListAdminQuarantineAsync(ct).ConfigureAwait(false), null);
        try
        {
            var response = await SendAsync(new ElevatedRequest { Kind = ElevatedRequestKind.ListAdminQuarantine }, ct).ConfigureAwait(false);
            return new AdminQuarantineListing(response.Quarantine ?? Array.Empty<QuarantineEntry>(), response.Error);
        }
        catch (Exception ex) when (ex is ElevationDeclinedException or ElevationUnavailableException or ElevationRefusedException or HelperLostException)
        {
            return new AdminQuarantineListing(Array.Empty<QuarantineEntry>(), Explain(ex));
        }
    }

    /// <summary>
    /// Permanently deletes a run's admin quarantine (call only after the user confirmed). The helper refuses runs it
    /// didn't quarantine for. Returns null when done, otherwise a plain-English reason.
    /// </summary>
    public async Task<string?> PurgeAdminQuarantineAsync(string runId, CancellationToken ct)
    {
        if (RunsInProcess)
            return _inProcess is null ? "There's no separate admin quarantine here." : await _inProcess.PurgeAdminQuarantineAsync(runId, ct).ConfigureAwait(false);
        try
        {
            var response = await SendAsync(new ElevatedRequest { Kind = ElevatedRequestKind.PurgeAdminQuarantine, RunId = runId }, ct).ConfigureAwait(false);
            return response.Purged ? null : response.Error ?? "PC Tidy's admin helper gave an unexpected answer.";
        }
        catch (Exception ex) when (ex is ElevationDeclinedException or ElevationUnavailableException or ElevationRefusedException or HelperLostException)
        {
            return Explain(ex);
        }
    }

    private static string Explain(Exception ex) => ex switch
    {
        HelperLostException => "PC Tidy's admin helper stopped before answering.",
        ElevationRefusedException => ex.Message,
        _ => ex.Message + ".",
    };

    /// <summary>
    /// Tells the helper to exit (it also exits on its own when idle or when PC Tidy closes). Never waits long: if a
    /// batch is still in flight the pipe is simply closed, which the helper treats as "stop after the current step"
    /// (the run's journal then shows what happened). Use <c>Executor.IsRunning</c> to ask the user before closing.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        var haveGate = await _gate.WaitAsync(GateTimeout).ConfigureAwait(false);
        try
        {
            if (_session is { } s && haveGate)
            {
                using var bye = new CancellationTokenSource(ByeTimeout);
                try
                {
                    if (s.IsAlive) await ElevationProtocol.WriteAsync(s.Pipe, new ElevatedRequest { Kind = ElevatedRequestKind.Bye }, bye.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
                {
                    // Already gone, or not listening: closing the pipe below ends it anyway.
                }
            }
            DropSession();
        }
        finally
        {
            if (haveGate) _gate.Release();
        }
    }

    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ByeTimeout = TimeSpan.FromSeconds(2);
    private int _disposed;

    /// <summary>Synchronous form (bounded: never blocks for longer than the timeouts above, never on the caller's context).</summary>
    public void Dispose() => Task.Run(() => DisposeAsync().AsTask()).Wait(GateTimeout + ByeTimeout + TimeSpan.FromSeconds(1));

    // ------------------------------------------------------------------ session

    private sealed class HelperLostException : Exception;

    private sealed record Session(NamedPipeServerStream Pipe, HelperProcess Helper)
    {
        public bool IsAlive => Pipe.IsConnected && !Helper.HasExited();
    }

    private async Task<ElevatedResponse> SendAsync(ElevatedRequest request, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var session = await EnsureSessionAsync(ct).ConfigureAwait(false);
                try
                {
                    await ElevationProtocol.WriteAsync(session.Pipe, request, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException && attempt == 0)
                {
                    // The helper left (idle timeout) before we wrote anything: start a fresh one once.
                    DropSession();
                    continue;
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    DropSession();
                    throw new ElevationUnavailableException("PC Tidy lost contact with its admin helper");
                }
                return await ReadResponseAsync(session, ct).ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); }
    }

    private async Task<ElevatedResponse> ReadResponseAsync(Session session, CancellationToken ct)
    {
        // The read itself is not cancelled: on cancel the helper is asked to stop between steps, and its answer
        // (what really happened) is still collected so the journal stays true.
        var read = ElevationProtocol.ReadAsync<ElevatedResponse>(session.Pipe, CancellationToken.None);
        if (ct.CanBeCanceled)
        {
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (ct.Register(() => cancelled.TrySetResult()))
            {
                if (await Task.WhenAny(read, cancelled.Task).ConfigureAwait(false) != read)
                {
                    try { await ElevationProtocol.WriteAsync(session.Pipe, new ElevatedRequest { Kind = ElevatedRequestKind.Cancel }, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
                }
            }
        }

        try
        {
            return await read.ConfigureAwait(false) ?? throw new HelperLostException();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or ObjectDisposedException)
        {
            DropSession();
            throw new HelperLostException();
        }
        catch (HelperLostException)
        {
            DropSession();
            throw;
        }
    }

    private async Task<Session> EnsureSessionAsync(CancellationToken ct)
    {
        if (_session is { IsAlive: true } live) return live;
        DropSession();

        // Never show an admin prompt for a copy whose files (or settings) other programs could change: approving it
        // would run whatever they put there as admin.
        if (_preLaunchCheck() is { } refusal) throw new ElevationRefusedException(refusal);

        var pipeName = ElevationProtocol.NewPipeName();
        var server = ElevatedPipe.CreateServer(pipeName);
        HelperProcess? helper = null;
        try
        {
            // Launch blocks while Windows shows the admin prompt, so keep it off the caller's thread.
            helper = await Task.Run(() => _launcher.Launch(pipeName, Environment.ProcessId), ct).ConfigureAwait(false);

            using var timeout = new CancellationTokenSource(_connectTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            var connect = server.WaitForConnectionAsync(linked.Token);
            while (await Task.WhenAny(connect, Task.Delay(250, CancellationToken.None)).ConfigureAwait(false) != connect)
            {
                if (helper.HasExited())
                {
                    linked.Cancel();
                    // A helper that refused to start for a reason trying again can't fix is "refused" (no Try again).
                    var code = helper.ExitCode?.Invoke();
                    if (code is ElevatedHost.ExitUntrustedLocation or ElevatedHost.ExitUnsafeEnvironment)
                        throw new ElevationRefusedException(ClosedEarly(code) + ", so the steps that need admin rights weren't run.");
                    throw new ElevationUnavailableException(ClosedEarly(code));
                }
            }
            try
            {
                await connect.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ElevationUnavailableException("PC Tidy's admin helper didn't connect in time");
            }

            // Make sure the process on the other end is the one we started, running PC Tidy's own program file.
            if (helper.ProcessId is { } expected
                && (ElevatedPipe.ClientProcessId(server) != expected || !ProcessIdentity.RunsImage(expected, Environment.ProcessPath)))
                throw new ElevationRefusedException("An unexpected program answered instead of PC Tidy's admin helper, so nothing was sent to it.");

            _session = new Session(server, helper);
            return _session;
        }
        catch
        {
            helper?.Dispose();
            await server.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Why the helper stopped before it connected, from its exit code (see <see cref="ElevatedHost"/>).</summary>
    internal static string ClosedEarly(int? exitCode) => exitCode switch
    {
        ElevatedHost.ExitUntrustedLocation => "PC Tidy's admin helper refused to start: PC Tidy's program folder can be changed without admin rights (install PC Tidy with Setup to use admin steps)",
        ElevatedHost.ExitUnsafeEnvironment => "PC Tidy's admin helper refused to start: a setting on this PC makes .NET load extra code into programs",
        ElevatedHost.ExitError => "PC Tidy's admin helper couldn't set up its admin-only folder",
        ElevatedHost.ExitParentGone => "PC Tidy's admin helper couldn't find this PC Tidy window",
        null => "PC Tidy's admin helper closed before it started",
        { } code => $"PC Tidy's admin helper closed before it started (code {code})",
    };

    private void DropSession()
    {
        try { _session?.Pipe.Dispose(); }
        catch (IOException) { }
        _session?.Helper.Dispose();
        _session = null;
    }

    private static StepRecord Refuse(StepRecord step, bool undo, string reason) => undo ? step.UndoFailed(reason) : step.Skipped(reason);
}

/// <summary>
/// The app's <see cref="IRestorePointService"/>: makes the restore point in the admin helper (sharing the run's single
/// admin prompt), or directly when PC Tidy already runs elevated. Sandbox: fake "created", never WMI.
/// </summary>
public sealed class AdminRestorePointService(ElevationService elevation) : IRestorePointService
{
    public Task<RestorePointInfo> EnsureAsync(string description, CancellationToken ct) => elevation.EnsureRestorePointAsync(description, ct);
}

/// <summary>What the admin-only quarantine holds; <see cref="Problem"/> is set when it couldn't be listed.</summary>
public sealed record AdminQuarantineListing(IReadOnlyList<QuarantineEntry> Entries, string? Problem);
