using PcTidy.Core.Model;

namespace PcTidy.Platform;

/// <summary>
/// A handler whose steps, when settled as finished after the helper stopped mid-step, may lack the before-state undo
/// needs. It says so in the step's message instead of leaving the user to find out when they try to undo.
/// </summary>
internal interface ISettledStepNote
{
    /// <summary>Text added to the "it finished" message, or null when the step can be undone as usual.</summary>
    string? NoteWhenSettledDone(StepRecord step);
}

/// <summary>Extra rules the real elevated helper applies (the composition root supplies them; tests may leave them out).</summary>
public sealed record ElevatedHelperPolicy
{
    /// <summary>The helper's own record of what it ran: undo only what's in it, with its recorded undo data.</summary>
    public HelperLedger? Ledger { get; init; }

    /// <summary>
    /// The delete cap, measured on disk (null = within limits). Applied to every request that runs steps. The helper
    /// strips <c>Args["capOverride"]</c> before calling it: the user's "go ahead anyway" is given in the app, and the
    /// helper keeps its own limits (admin removals only ever touch admin cleanup folders).
    /// </summary>
    public Func<IReadOnlyList<PlanItem>, CancellationToken, string?>? DeleteCap { get; init; }

    /// <summary>
    /// Per-run limit across all requests (needs the <see cref="Ledger"/>): what this helper already quarantined in the
    /// run plus what the request would remove. Null = no per-run limit.
    /// </summary>
    public (int MaxItems, long MaxBytes)? RunCap { get; init; }
}

/// <summary>
/// The admin helper's gatekeeper and runner. It trusts nothing it receives: each step must be an admin-capable kind,
/// marked NeedsAdmin, handled by a known handler, and pass the helper's OWN SafetyGuard check plus its own
/// "still the same as scanned / not in use" checks (a standard user often can't even read where admin steps work, so
/// the app leaves those checks to the helper). Admin quarantine works only inside admin carve-out folders.
/// When the helper runs under a different account than the app (a standard user typed an admin's credentials),
/// per-user steps are refused, because they would change the other account's settings and files.
/// With a <see cref="HelperLedger"/>, a step is undone only if this helper ran it, using the helper's recorded result.
/// Also used in-process (sandbox, or when PC Tidy already runs elevated) so both paths apply the same rules.
/// </summary>
public sealed class ElevatedBatchExecutor
{
    /// <summary>The only kinds the helper will run. Everything else is done by the normal app.</summary>
    public static readonly IReadOnlySet<ActionKind> AdminCapableKinds = new HashSet<ActionKind>
    {
        ActionKind.QuarantinePath,
        ActionKind.RegistrySetValue,
        ActionKind.RegistryDeleteValue,
        ActionKind.RegistryDeleteKey,
        ActionKind.ServiceSetStartType,
        ActionKind.ScheduledTaskSetEnabled,
        ActionKind.StartupItemSetEnabled,
        ActionKind.RunSystemTool,
        ActionKind.PowerPlanSet,
        ActionKind.HostsCommentOut,
        ActionKind.RunUninstaller,
        ActionKind.RestorePointDelete,
        ActionKind.WlanProfileDelete,
        ActionKind.UserProfileDelete,
        ActionKind.OptionalFeatureRemove,
        ActionKind.VolumeOptimize,
        ActionKind.ShellExtensionSetEnabled,
        // v3.1: turning a disabled device back on (and, only as its undo, off again).
        ActionKind.DeviceSetEnabled,
    };

    /// <summary>Kinds that are always machine-wide (safe to run from another admin account).</summary>
    private static readonly IReadOnlySet<ActionKind> MachineWideKinds = new HashSet<ActionKind>
    {
        ActionKind.ServiceSetStartType, ActionKind.ScheduledTaskSetEnabled, ActionKind.RunSystemTool,
        ActionKind.PowerPlanSet, ActionKind.HostsCommentOut, ActionKind.RestorePointDelete, ActionKind.UserProfileDelete,
        ActionKind.OptionalFeatureRemove, ActionKind.VolumeOptimize, ActionKind.DeviceSetEnabled,
        // Machine-wide when they need admin: all-user Wi-Fi profiles (per-user ones are refused by the handler) and
        // apps installed for everyone (the handler only runs HKLM-listed uninstallers in the helper).
        ActionKind.WlanProfileDelete, ActionKind.RunUninstaller,
    };

    /// <summary>The Engine's cap override argument (Args["capOverride"]); never honoured by the helper.</summary>
    internal const string CapOverrideArg = "capOverride";

    /// <summary>A request with more steps is refused whole (the app sends admin steps in smaller requests).</summary>
    public const int MaxStepsPerRequest = 10_000;

    private readonly IReadOnlyList<IActionHandler> _handlers;
    private readonly Func<HandlerContext> _contextFactory;
    private readonly bool _sameAccountAsApp;
    private readonly ElevatedHelperPolicy _policy;

    /// <param name="handlers">All handlers (Engine file handlers + <see cref="PlatformHandlers.CreateAll"/>).</param>
    /// <param name="contextFactory">Builds a context with this process's own Locations, SafetyGuard, InUse, Quarantine, Settings.</param>
    /// <param name="sameAccountAsApp">False when the helper runs as a different user than the app that asked.</param>
    /// <param name="policy">Ledger and delete cap of the real helper.</param>
    public ElevatedBatchExecutor(IEnumerable<IActionHandler> handlers, Func<HandlerContext> contextFactory, bool sameAccountAsApp = true,
        ElevatedHelperPolicy? policy = null)
    {
        _handlers = handlers.ToList();
        _contextFactory = contextFactory;
        _sameAccountAsApp = sameAccountAsApp;
        _policy = policy ?? new ElevatedHelperPolicy();
    }

    /// <summary>Why a step is refused, or null if it may run. Pure apart from the SafetyGuard call.</summary>
    public string? Validate(StepRecord step, bool undo, HandlerContext ctx)
    {
        var item = step.Item;
        if (item is null) return "This step is empty.";
        if (!AdminCapableKinds.Contains(item.Kind)) return "PC Tidy's admin helper doesn't do this kind of change.";
        if (!item.NeedsAdmin) return "This step doesn't need admin rights, so the admin helper won't run it.";
        if (undo && step.Status != StepStatus.Done) return "Only finished steps can be undone.";
        if (!undo && step.Status is not (StepStatus.Pending or StepStatus.Started)) return "This step has already run.";
        if (!_sameAccountAsApp && !IsMachineWide(item))
            return "The admin sign-in was a different account, so PC Tidy didn't change anything that belongs to your account.";
        if (item.Kind == ActionKind.QuarantinePath && AdminCarveOutProblem(item, ctx) is { } outside) return outside;
        if (_handlers.FirstOrDefault(h => h.CanHandle(item)) is null) return "No handler for this kind of change.";

        SafetyVerdict verdict;
        try
        {
            verdict = ctx.Safety.Check(item);
        }
        catch (Exception)
        {
            return "The safety check couldn't run, so this step was skipped.";
        }
        return verdict.Allowed ? null : verdict.Reason ?? "PC Tidy's safety rules don't allow this.";
    }

    /// <summary>
    /// Admin quarantine only ever acts strictly inside the roots of an admin carve-out (Windows temp, crash dumps...)
    /// or on its listed single files. The SafetyGuard enforces this too; the helper checks it on its own as well.
    /// </summary>
    internal static string? AdminCarveOutProblem(PlanItem item, HandlerContext ctx)
    {
        const string refusal = "PC Tidy's admin helper only quarantines items in the cleanup folders that need admin rights.";
        if (CarveOuts.Find(item.CarveOutId) is not { NeedsAdmin: true } carveOut) return refusal;
        if (item.SourcePath is not { Length: > 3 } source || !IsPlainFullPath(source)) return refusal;
        IEnumerable<string> roots;
        try { roots = carveOut.Roots(ctx.Locations).ToList(); }
        catch (Exception) { return refusal; }
        foreach (var root in roots.Where(r => r.Length > 3 && IsPlainFullPath(r)))
            if (source.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) && source.Length > root.TrimEnd('\\').Length + 1)
                return carveOut.IsDenied(source[root.TrimEnd('\\').Length..]) ? refusal : null;
        foreach (var (id, file) in CarveOuts.AllowedSingleFiles(ctx.Locations))
            if (id == carveOut.Id && string.Equals(file, source, StringComparison.OrdinalIgnoreCase)) return null;
        return refusal;
    }

    private static bool IsPlainFullPath(string p)
        => Path.IsPathFullyQualified(p) && !p.StartsWith(@"\\", StringComparison.Ordinal) && p.IndexOf(':', 2) < 0 && !p.Contains('/')
           && !p.Contains(@"\.\") && !p.Contains(@"\..\") && !p.EndsWith(@"\..") && !p.EndsWith(@"\.") && p.IndexOf(@"\\", 1, StringComparison.Ordinal) < 0;

    private static bool IsMachineWide(PlanItem item) => item.Kind switch
    {
        _ when MachineWideKinds.Contains(item.Kind) => true,
        ActionKind.RegistrySetValue or ActionKind.RegistryDeleteValue or ActionKind.RegistryDeleteKey => item.Registry?.Hive == RegistryHive.LocalMachine,
        ActionKind.StartupItemSetEnabled or ActionKind.ShellExtensionSetEnabled => item.Arg("hive").Equals("HKLM", StringComparison.OrdinalIgnoreCase),
        _ => false, // file steps use the app's own data folder and quarantine
    };

    /// <summary>Validates and runs (or simulates / undoes) each step in order. One result per step, same order.</summary>
    public async Task<IReadOnlyList<StepRecord>> RunAsync(string runId, IReadOnlyList<StepRecord> steps, ExecutionMode mode, bool undo, CancellationToken ct)
    {
        if (steps.Count > MaxStepsPerRequest)
            return steps.Select(s => Refuse(s, undo, "Too many steps in one request.")).ToList();

        var ctx = WithRun(_contextFactory(), runId, mode);
        var real = mode == ExecutionMode.Real;
        if (real && !undo && CapProblem(runId, steps, ct) is { } problem)
            return steps.Select(s => s.Skipped(problem)).ToList();

        // Pass 1: decide what may run (the ledger supplies the trusted version of each step to undo).
        var effective = new StepRecord[steps.Count];
        var refusals = new string?[steps.Count];
        for (int i = 0; i < steps.Count; i++)
        {
            (effective[i], refusals[i]) = real && _policy.Ledger is { } ledger ? await FromLedgerAsync(ledger, runId, steps[i], undo, ctx).ConfigureAwait(false) : (steps[i], null);
            refusals[i] ??= Validate(effective[i], undo, ctx);
        }
        if (real && !undo) await PreCheckFilesAsync(effective, refusals, ctx, ct).ConfigureAwait(false);

        // Intents first (one flush), then the work, then the results (one flush) before answering.
        var runnable = Enumerable.Range(0, steps.Count).Where(i => refusals[i] is null).ToList();
        if (real && !undo) _policy.Ledger?.Append(runId, runnable.Select(i => (HelperLedger.Started, effective[i] with { Status = StepStatus.Started })));

        var results = new List<StepRecord>(steps.Count);
        for (int i = 0; i < steps.Count; i++)
        {
            if (refusals[i] is { } refusal) results.Add(Refuse(steps[i], undo, refusal));
            // Stays in "runnable", so the ledger records it as finished (skipped), not left Started.
            else if (!undo && NeedsNotDone(effective[i], results, steps, i) is { } needs) results.Add(steps[i].Skipped(needs));
            else if (ct.IsCancellationRequested) results.Add(undo ? steps[i] with { Message = "Stopped before this step." } : steps[i].Skipped("Stopped before this step."));
            else results.Add(await RunOneAsync(effective[i], undo, ctx, ct).ConfigureAwait(false));
        }

        if (real && _policy.Ledger is { } done)
            done.Append(runId, runnable.Select(i => results[i])
                .Where(r => undo ? r.Status == StepStatus.Undone : r.Status is not (StepStatus.Started or StepStatus.Pending))
                .Select(r => (undo ? HelperLedger.Undone : HelperLedger.Finished, r)));
        return results;
    }

    /// <summary>
    /// A step whose <see cref="PlanItem.DependsOn"/> names an earlier step of THIS request that didn't finish (Done, or
    /// Simulated in a simulation) is skipped. Needs outside the request were checked by the app before sending.
    /// </summary>
    private static string? NeedsNotDone(StepRecord step, IReadOnlyList<StepRecord> earlier, IReadOnlyList<StepRecord> request, int position)
    {
        if (step.Item?.DependsOn is not { Count: > 0 } needs) return null;
        foreach (var id in needs)
        {
            var dep = earlier.LastOrDefault(r => r.Item?.Id == id);
            if (dep is not null && dep.Status is not (StepStatus.Done or StepStatus.Simulated))
                return $"Not done, because an earlier step it needs didn't finish: {dep.Item!.Description}";
            // A need that sits at or after this step in the same request can't have run yet: never treat it as met.
            if (dep is null && request.Skip(position).Any(r => r.Item?.Id == id))
                return "Not done, because a step it needs comes after it.";
        }
        return null;
    }

    /// <summary>
    /// The helper's delete caps. capOverride is removed from every item first (the helper never takes the app's word for
    /// "the user confirmed"), then the on-disk cap runs, then the per-run total from the ledger.
    /// </summary>
    private string? CapProblem(string runId, IReadOnlyList<StepRecord> steps, CancellationToken ct)
    {
        var items = steps.Select(s => s.Item).Where(i => i is not null).Select(WithoutCapOverride).ToList();
        if (_policy.DeleteCap is { } cap)
        {
            string? problem;
            try { problem = cap(items, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { problem = "PC Tidy couldn't measure what these steps would remove, so nothing was changed."; }
            if (problem is not null) return problem;
        }
        if (_policy.RunCap is { } limit && _policy.Ledger is { } ledger)
        {
            var removing = items.Where(i => i.Kind == ActionKind.QuarantinePath).ToList();
            if (removing.Count == 0) return null;
            (int Items, long Bytes) before;
            try { before = ledger.RemovedSoFar(runId); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return "PC Tidy's admin helper couldn't read its own records, so nothing was changed."; }
            if (before.Items + removing.Count > limit.MaxItems || before.Bytes + removing.Sum(i => Math.Max(0, i.ExpectedSize)) > limit.MaxBytes)
                return "Stopped before any change: together with what this run already removed with admin rights, this goes over the safety limit.";
        }
        return null;
    }

    internal static PlanItem WithoutCapOverride(PlanItem item)
        => item.Args is { } a && a.ContainsKey(CapOverrideArg)
            ? item with { Args = a.Where(kv => !kv.Key.Equals(CapOverrideArg, StringComparison.OrdinalIgnoreCase)).ToDictionary(kv => kv.Key, kv => kv.Value) }
            : item;

    /// <summary>
    /// Settle steps the app only knows as "Started" (the helper stopped before answering). Read-only. With a ledger, the
    /// helper answers from its own records: a finished step comes back with its real result (and undo data); a step it
    /// only started is checked with the handler's WasCompletedAsync and recorded as finished either way; a step it never
    /// saw is "not done". Without a ledger (in-process) the handler's check decides.
    /// </summary>
    public async Task<IReadOnlyList<StepRecord>> SettleAsync(string runId, IReadOnlyList<StepRecord> steps, CancellationToken ct)
    {
        var ctx = WithRun(_contextFactory(), runId, ExecutionMode.Real);
        var results = new List<StepRecord>(steps.Count);
        var settled = new List<(string, StepRecord)>();
        foreach (var step in steps)
        {
            ct.ThrowIfCancellationRequested();
            if (step.Item is null) { results.Add(step.Skipped("This step is empty.")); continue; }
            var handler = _handlers.FirstOrDefault(h => h.CanHandle(step.Item));
            if (_policy.Ledger is not { } ledger)
            {
                results.Add(await CheckAsync(handler, step, ctx).ConfigureAwait(false));
                continue;
            }

            HelperLedger.Record? record;
            try { record = ledger.Find(runId, step.Index); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                results.Add(step with { Status = StepStatus.Started, Message = "PC Tidy's admin helper couldn't read its own records. Check this step yourself." });
                continue;
            }
            if (record is null) { results.Add(step.Skipped("PC Tidy's admin helper never started this step, so nothing changed.")); continue; }
            if (record.ItemHash != HelperLedger.HashOf(step.Item)) { results.Add(step.Skipped("This step doesn't match what PC Tidy's admin helper was asked, so it can't say.")); continue; }
            if (record.State != HelperLedger.Started) { results.Add(record.Step); continue; }

            var checkedStep = await CheckAsync(handler, record.Step, ctx).ConfigureAwait(false);
            results.Add(checkedStep);
            settled.Add((HelperLedger.Finished, checkedStep));
        }
        if (settled.Count > 0) _policy.Ledger?.Append(runId, settled);
        return results;
    }

    private static async Task<StepRecord> CheckAsync(IActionHandler? handler, StepRecord step, HandlerContext ctx)
    {
        try
        {
            if (handler is not null && await handler.WasCompletedAsync(step, ctx, CancellationToken.None).ConfigureAwait(false))
            {
                var note = handler is ISettledStepNote n && n.NoteWhenSettledDone(step) is { Length: > 0 } text ? " " + text : "";
                return step.Finish(StepStatus.Done, "It finished before PC Tidy's admin helper stopped." + note);
            }
        }
        catch (Exception) { /* can't tell: reported as not done */ }
        return step.Skipped("It didn't happen before PC Tidy's admin helper stopped, so nothing changed.");
    }

    /// <summary>The admin-only quarantine (empty when this isn't the real helper: then admin items are in the app's own quarantine).</summary>
    public async Task<IReadOnlyList<QuarantineEntry>> ListAdminQuarantineAsync(CancellationToken ct)
    {
        if (_policy.Ledger is null) return Array.Empty<QuarantineEntry>();
        return await _contextFactory().Quarantine.ListAsync(null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Permanently removes a run's admin quarantine. Only for a run this helper's own ledger shows it quarantined for
    /// (the app asks only after the user confirmed). Null = done; otherwise why not.
    /// </summary>
    public async Task<string?> PurgeAdminQuarantineAsync(string runId, CancellationToken ct)
    {
        if (_policy.Ledger is not { } ledger) return "There's no separate admin quarantine here.";
        try
        {
            if (!ledger.QuarantinedIn(runId)) return "PC Tidy's admin helper didn't quarantine anything in this run.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return "PC Tidy's admin helper couldn't read its own records, so nothing was removed.";
        }
        await _contextFactory().Quarantine.PurgeRunAsync(runId, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// Execute: a step this helper already finished is refused; one it only started (it stopped mid-way) may be retried
    /// for the very same item. Undo: only a step this helper ran, for the very same item, with its recorded result.
    /// </summary>
    private async Task<(StepRecord Step, string? Refusal)> FromLedgerAsync(HelperLedger ledger, string runId, StepRecord requested, bool undo, HandlerContext ctx)
    {
        if (requested.Item is null) return (requested, "This step is empty.");
        HelperLedger.Record? record;
        try { record = ledger.Find(runId, requested.Index); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (requested, "PC Tidy's admin helper couldn't read its own records, so nothing was changed.");
        }
        var sameItem = record is not null && record.ItemHash == HelperLedger.HashOf(requested.Item);
        if (!undo)
        {
            if (record is null) return (requested, null);
            if (!sameItem) return (requested, "This step doesn't match what PC Tidy's admin helper was asked before, so it wasn't run.");
            return record.State == HelperLedger.Started ? (requested, null) : (requested, "This step has already run.");
        }

        const string notMine = "PC Tidy's admin helper didn't make this change, so it won't undo it.";
        if (record is null || !sameItem) return (requested, notMine);
        if (record.State == HelperLedger.Undone) return (requested, "It was already undone.");
        if (record.State == HelperLedger.Finished)
            return record.Step.Status == StepStatus.Done ? (record.Step, null) : (requested, "Only finished steps can be undone.");

        // Started but never finished (the helper stopped): undo only if the change really happened.
        var handler = _handlers.FirstOrDefault(h => h.CanHandle(record.Step.Item));
        try
        {
            if (handler is not null && await handler.WasCompletedAsync(record.Step, ctx, CancellationToken.None).ConfigureAwait(false))
                return (record.Step with { Status = StepStatus.Done }, null);
        }
        catch (Exception) { /* can't tell: not undone */ }
        return (requested, "This change never happened, so there's nothing to undo.");
    }

    /// <summary>
    /// For file steps the helper checks what the app couldn't (it can read admin-only folders): still there, still
    /// the same as when scanned, not an online-only file, not in use.
    /// </summary>
    private static async Task PreCheckFilesAsync(StepRecord[] steps, string?[] refusals, HandlerContext ctx, CancellationToken ct)
    {
        var files = new List<int>();
        for (int i = 0; i < steps.Length; i++)
        {
            if (refusals[i] is not null || steps[i].Item.Kind != ActionKind.QuarantinePath || steps[i].Item.SourcePath is not { } source) continue;
            refusals[i] = SourceProblem(steps[i].Item, LiteralIo(source));
            if (refusals[i] is null) files.Add(i);
        }
        if (files.Count == 0) return;
        IReadOnlyList<InUseInfo> inUse;
        try { inUse = await ctx.InUse.CheckAsync(files.Select(i => steps[i].Item.SourcePath!).ToList(), ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return; } // the move itself still fails safely on an open file
        foreach (var i in files)
            if (inUse.FirstOrDefault(u => u.InUse && string.Equals(u.Path, steps[i].Item.SourcePath, StringComparison.OrdinalIgnoreCase)) is { } busy)
                refusals[i] = busy.Apps.Count > 0 ? $"In use by {string.Join(", ", busy.Apps)}." : "It's in use right now.";
    }

    private static string? SourceProblem(PlanItem item, string io)
    {
        FileSystemInfo info;
        try
        {
            var attributes = File.GetAttributes(io);
            info = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(io) : new FileInfo(io);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return "It's no longer there."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "PC Tidy couldn't read it, so it was left alone."; }
        if (CloudFile.IsPlaceholder(info.Attributes)) return "It's an online-only OneDrive file. PC Tidy never downloads it.";
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) return "It's a link to somewhere else, so PC Tidy left it alone.";
        if (item.IsDirectory != info is DirectoryInfo && (item.ExpectedLastWriteUtc.HasValue || item.ExpectedSize > 0)) return Changed;
        if (item.ExpectedLastWriteUtc is { } expected)
        {
            var want = expected.Kind == DateTimeKind.Local ? expected.ToUniversalTime() : DateTime.SpecifyKind(expected, DateTimeKind.Utc);
            if (info.LastWriteTimeUtc.Ticks != want.Ticks) return Changed;
        }
        if (info is FileInfo f && item.ExpectedSize > 0 && f.Length != item.ExpectedSize) return Changed;
        return null;
    }

    private const string Changed = "It changed since the scan, so it was left alone.";

    /// <summary>The literal extended form of a plain full path (no normalisation, like the SafetyGuard).</summary>
    private static string LiteralIo(string path)
        => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path : @"\\?\" + (path.Length > 3 ? path.TrimEnd('\\') : path);

    private async Task<StepRecord> RunOneAsync(StepRecord step, bool undo, HandlerContext ctx, CancellationToken ct)
    {
        var handler = _handlers.First(h => h.CanHandle(step.Item));
        var started = step.StartedUtc ?? DateTime.UtcNow;
        try
        {
            var result = undo ? await handler.UndoAsync(step, ctx, ct).ConfigureAwait(false)
                : ctx.Mode == ExecutionMode.Simulate ? await handler.SimulateAsync(step, ctx, ct).ConfigureAwait(false)
                : await handler.ExecuteAsync(step with { Status = StepStatus.Started, StartedUtc = started }, ctx, ct).ConfigureAwait(false);
            return result with { StartedUtc = result.StartedUtc ?? started, FinishedUtc = result.FinishedUtc ?? DateTime.UtcNow };
        }
        catch (OperationCanceledException)
        {
            // It may have got part-way: report it as Started so the engine asks WasCompletedAsync.
            return step with { Status = StepStatus.Started, StartedUtc = started, Message = "Stopped part-way. PC Tidy will check whether it finished." };
        }
        catch (Exception ex)
        {
            return undo ? step.UndoFailed("Something went wrong: " + ex.Message) : step.Failed("Something went wrong: " + ex.Message);
        }
    }

    private static StepRecord Refuse(StepRecord step, bool undo, string reason) => undo ? step.UndoFailed(reason) : step.Skipped(reason);

    private static HandlerContext WithRun(HandlerContext c, string runId, ExecutionMode mode) => new()
    {
        RunId = runId,
        Locations = c.Locations,
        Safety = c.Safety,
        InUse = c.InUse,
        Quarantine = c.Quarantine,
        Settings = c.Settings,
        Mode = mode,
        IsElevated = c.IsElevated || ProcessElevation.IsElevated,
    };
}
