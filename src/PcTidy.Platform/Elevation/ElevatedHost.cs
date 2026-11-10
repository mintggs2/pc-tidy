using System.Globalization;
using System.IO.Pipes;
using System.Security.Principal;
using PcTidy.Core.Model;

namespace PcTidy.Platform;

/// <summary>
/// The elevated side of PC Tidy: <c>PcTidy.exe --elevated &lt;pipeName&gt; &lt;parentPid&gt;</c>.
/// Call from the app's Main before any UI starts:
/// <code>
/// if (ElevatedHost.IsElevatedInvocation(args))
///     return ElevatedHost.Run(args, handlers, () => new HandlerContext { ... });
/// </code>
/// It checks the parent is alive and really owns the pipe, answers requests (run / undo / simulate admin steps,
/// make a restore point) until the app says bye, closes the pipe, dies, or stays idle; then it exits.
/// It has no UI and never touches anything outside the validated steps.
/// </summary>
public static class ElevatedHost
{
    public const int ExitOk = 0;
    public const int ExitBadArguments = 2;
    public const int ExitParentGone = 3;
    public const int ExitPipeRefused = 4;
    public const int ExitError = 5;
    /// <summary>A variable that loads other code into .NET (startup hook, profiler...) is set: the helper refuses to work.</summary>
    public const int ExitUnsafeEnvironment = 6;
    /// <summary>The helper's own program folder can be changed without admin rights (portable copy, developer build).</summary>
    public const int ExitUntrustedLocation = 7;

    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);

    /// <summary>What the helper checks about itself before doing anything (tests switch these off: they run in-process).</summary>
    internal sealed record StartupChecks(Func<IReadOnlyList<string>> UnsafeVariables, Func<string?> OwnFolderProblem)
    {
        public static StartupChecks Real { get; } = new(
            () => HelperEnvironment.UnsafeVariables(Environment.GetEnvironmentVariables()),
            InstallIntegrity.OwnFolderProblem);

        public static StartupChecks None { get; } = new(() => Array.Empty<string>(), () => null);
    }

    public static bool IsElevatedInvocation(string[] args)
        => args.Length >= 1 && args[0].Equals(ElevationProtocol.ElevatedSwitch, StringComparison.OrdinalIgnoreCase);

    /// <summary>Synchronous wrapper for an STA Main.</summary>
    public static int Run(string[] args, IEnumerable<IActionHandler> handlers, Func<HandlerContext> contextFactory, ElevatedHelperPolicy? policy = null)
        => Task.Run(() => RunAsync(args, handlers, contextFactory, policy)).GetAwaiter().GetResult();

    /// <param name="policy">The real helper's ledger and delete cap (see <see cref="ElevatedHelperPolicy"/>).</param>
    public static Task<int> RunAsync(string[] args, IEnumerable<IActionHandler> handlers, Func<HandlerContext> contextFactory, ElevatedHelperPolicy? policy = null)
        => RunAsync(args, handlers, contextFactory, policy, StartupChecks.Real);

    internal static async Task<int> RunAsync(string[] args, IEnumerable<IActionHandler> handlers, Func<HandlerContext> contextFactory, ElevatedHelperPolicy? policy, StartupChecks checks)
    {
        if (!TryParseArgs(args, out var pipeName, out var parentPid)) return ExitBadArguments;

        // Defence in depth (the app checks the same before showing the admin prompt). The .NET runtime acts on these
        // variables before this code runs, so this can't undo what a startup hook or profiler already did; it stops
        // the helper from doing any admin work for it.
        if (checks.UnsafeVariables() is { Count: > 0 } unsafeVariables)
        {
            HelperLog.Write(contextFactory, $"Refused to start: unsafe environment variable(s) set: {string.Join(", ", unsafeVariables)}.");
            return ExitUnsafeEnvironment;
        }
        if (checks.OwnFolderProblem() is { } folderProblem)
        {
            HelperLog.Write(contextFactory, $"Refused to start: its program folder isn't admin-only ({folderProblem})");
            return ExitUntrustedLocation;
        }

        var parentStarted = ProcessIdentity.StartTimeUtc(parentPid);
        if (!ProcessIdentity.IsAlive(parentPid, null)) return ExitParentGone;
        // A real parent started before this helper; a recycled PID would be newer.
        if (parentStarted is { } ps && ProcessIdentity.StartTimeUtc(Environment.ProcessId) is { } mine && ps > mine) return ExitParentGone;

        await using var pipe = ElevatedPipe.CreateClient(pipeName);
        try
        {
            using var connectCts = new CancellationTokenSource(ConnectTimeout);
            await pipe.ConnectAsync(connectCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException or TimeoutException)
        {
            return ExitPipeRefused;
        }
        // Only talk to the process that launched us, and only if it is PC Tidy itself (the same program file as this
        // helper): another program that guessed or stole the pipe name gets nothing.
        if (ElevatedPipe.ServerProcessId(pipe) != parentPid) return ExitPipeRefused;
        if (!IsTrustedParent(parentPid, Environment.ProcessPath)) return ExitPipeRefused;

        // Programs the helper starts get their own temp folder in the admin-only store (see HelperEnvironment.CleanBlock).
        if (checks == StartupChecks.Real) ConfigureChildTemp(contextFactory);

        var executor = new ElevatedBatchExecutor(handlers, contextFactory, SameAccount(parentPid), policy);
        using var parentGone = new CancellationTokenSource();
        using var watcher = WatchParent(parentPid, parentStarted, parentGone);
        try
        {
            await ServeAsync(pipe, executor, contextFactory, parentGone.Token).ConfigureAwait(false);
            return ExitOk;
        }
        catch (OperationCanceledException) when (parentGone.IsCancellationRequested)
        {
            return ExitParentGone;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            return ExitError;
        }
    }

    private static void ConfigureChildTemp(Func<HandlerContext> contextFactory)
    {
        try { HelperEnvironment.ChildTempFolder = Path.Combine(contextFactory().Locations.DataRoot, "temp"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { /* Windows' own temp folder is used */ }
    }

    /// <summary>The parent must run exactly this helper's own program file.</summary>
    internal static bool IsTrustedParent(int parentPid, string? ownImage) => ProcessIdentity.RunsImage(parentPid, ownImage);

    internal static bool TryParseArgs(string[] args, out string pipeName, out int parentPid)
    {
        pipeName = "";
        parentPid = 0;
        if (args.Length != 3 || !IsElevatedInvocation(args)) return false;
        if (!ElevationProtocol.IsValidPipeName(args[1])) return false;
        if (!int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out parentPid) || parentPid <= 0) return false;
        pipeName = args[1];
        return true;
    }

    /// <summary>Request loop. While a batch runs, the next message is read too, so a Cancel stops it between steps.</summary>
    internal static async Task ServeAsync(Stream pipe, ElevatedBatchExecutor executor, Func<HandlerContext> contextFactory, CancellationToken parentGone)
    {
        Task<ElevatedRequest?>? pendingRead = null;
        while (true)
        {
            pendingRead ??= ElevationProtocol.ReadAsync<ElevatedRequest>(pipe, parentGone);
            using (var idleCts = CancellationTokenSource.CreateLinkedTokenSource(parentGone))
            {
                var idle = Task.Delay(IdleTimeout, idleCts.Token);
                var first = await Task.WhenAny(pendingRead, idle).ConfigureAwait(false);
                idleCts.Cancel();
                if (first != pendingRead)
                {
                    parentGone.ThrowIfCancellationRequested();
                    return; // idle for too long: the app will start a new helper if it needs one
                }
            }

            var request = await pendingRead.ConfigureAwait(false);
            pendingRead = null;
            if (request is null || request.Kind == ElevatedRequestKind.Bye) return;
            if (request.Kind == ElevatedRequestKind.Cancel) continue; // nothing running

            using var batchCts = CancellationTokenSource.CreateLinkedTokenSource(parentGone);
            pendingRead = ElevationProtocol.ReadAsync<ElevatedRequest>(pipe, parentGone);
            var work = HandleAsync(request, executor, contextFactory, batchCts.Token);

            // Watch for Cancel (or the pipe closing) while the batch runs.
            while (!work.IsCompleted)
            {
                if (await Task.WhenAny(work, pendingRead).ConfigureAwait(false) == work) break;
                var next = await pendingRead.ConfigureAwait(false);
                if (next is null) { batchCts.Cancel(); pendingRead = null; await work.ConfigureAwait(false); return; }
                if (next.Kind == ElevatedRequestKind.Cancel) { batchCts.Cancel(); pendingRead = null; break; }
                // Anything else mid-batch is a protocol error: finish the batch, then stop.
                batchCts.Cancel();
                await work.ConfigureAwait(false);
                return;
            }

            var response = await work.ConfigureAwait(false);
            await ElevationProtocol.WriteAsync(pipe, response, parentGone).ConfigureAwait(false);
        }
    }

    internal static async Task<ElevatedResponse> HandleAsync(ElevatedRequest request, ElevatedBatchExecutor executor, Func<HandlerContext> contextFactory, CancellationToken ct)
    {
        switch (request.Kind)
        {
            case ElevatedRequestKind.Execute:
                if (request.Steps is not { } steps || string.IsNullOrWhiteSpace(request.RunId))
                    return new ElevatedResponse { Error = "The request was incomplete." };
                return new ElevatedResponse { Steps = await executor.RunAsync(request.RunId, steps, request.Mode, request.Undo, ct).ConfigureAwait(false) };

            case ElevatedRequestKind.RestorePoint:
                var service = new RestorePointService(contextFactory().Locations);
                return new ElevatedResponse { RestorePoint = await service.EnsureAsync(request.Description ?? "PC Tidy", ct).ConfigureAwait(false) };

            case ElevatedRequestKind.Settle:
                if (request.Steps is not { } started || string.IsNullOrWhiteSpace(request.RunId))
                    return new ElevatedResponse { Error = "The request was incomplete." };
                return new ElevatedResponse { Steps = await executor.SettleAsync(request.RunId, started, ct).ConfigureAwait(false) };

            case ElevatedRequestKind.ListAdminQuarantine:
                return new ElevatedResponse { Quarantine = await executor.ListAdminQuarantineAsync(ct).ConfigureAwait(false) };

            case ElevatedRequestKind.PurgeAdminQuarantine:
                if (string.IsNullOrWhiteSpace(request.RunId)) return new ElevatedResponse { Error = "The request was incomplete." };
                var refusal = await executor.PurgeAdminQuarantineAsync(request.RunId, ct).ConfigureAwait(false);
                return refusal is null ? new ElevatedResponse { Purged = true } : new ElevatedResponse { Error = refusal };

            default:
                return new ElevatedResponse { Error = "Unknown request." };
        }
    }

    /// <summary>True when the helper runs as the same Windows account as the app (the usual case).</summary>
    private static bool SameAccount(int parentPid)
    {
        using var me = WindowsIdentity.GetCurrent();
        var parent = ProcessIdentity.UserOf(parentPid);
        return parent is not null && parent == me.User;
    }

    private static Timer WatchParent(int parentPid, DateTime? parentStarted, CancellationTokenSource onGone)
        => new(_ =>
        {
            if (!ProcessIdentity.IsAlive(parentPid, parentStarted))
            {
                try { onGone.Cancel(); } catch (ObjectDisposedException) { }
            }
        }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
}
