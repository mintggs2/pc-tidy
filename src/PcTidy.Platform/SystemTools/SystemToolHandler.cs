using System.Globalization;
using System.Text.Json;
using PcTidy.Core.Model;

namespace PcTidy.Platform;

/// <summary>
/// RunSystemTool: runs one whitelisted Windows tool (<see cref="SystemToolCatalog"/>). Machine-wide tools run in the
/// elevated helper; per-user tools (winget, WSL, Docker, developer caches, OneDrive, Storage settings) never do.
/// Success = exit code plus a follow-up registry/API check where one exists; output only goes to a log under
/// DataRoot\logs\tools (in the helper: its admin-only folder). In the sandbox (real backend) it returns Simulated with
/// the exact command that would run. Tests pass a fake <see cref="ISystemToolBackend"/> to exercise the logic.
/// </summary>
public sealed class SystemToolHandler : IActionHandler
{
    internal const string PriorHibernateEnabledData = "priorHibernateEnabled";
    internal const string PriorHiberFileTypeData = "priorHiberFileType";
    internal const string PriorBcdData = "priorBcd";
    internal const string PriorAutotuningData = "priorAutotuning";
    internal const string GradleQuarantineData = "gradleQuarantine";
    internal const string DriverAdminCopyData = "adminCopy";
    internal const string LogData = "log";

    private const string NotUndoableBcd = "unreadable";
    internal static readonly TimeSpan GradleInUseAge = TimeSpan.FromDays(30);

    private readonly ISystemToolBackend _backend;

    public SystemToolHandler() : this(WindowsSystemToolBackend.Instance) { }
    public SystemToolHandler(ISystemToolBackend backend) => _backend = backend;

    public bool CanHandle(PlanItem item) => item.Kind == ActionKind.RunSystemTool;

    private bool Resolve(PlanItem item, out SystemToolPlan plan, out string problem) => SystemToolCatalog.TryResolve(item, _backend, out plan, out problem);

    public async Task<StepRecord> ExecuteAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        if (ctx.Mode == ExecutionMode.Simulate) return await SimulateAsync(step, ctx, ct).ConfigureAwait(false);
        if (!Resolve(step.Item, out var plan, out var problem)) return step.Skipped(problem);
        if (ctx.Locations.IsSandbox && _backend.TouchesRealSystem) return step.SandboxSimulated($"Would run: {plan.Display}");
        if (plan.PerUser && ctx.IsElevated) return step.Skipped("This works on your own account's apps and files, so PC Tidy's admin helper doesn't run it.");
        if (plan.NeedsAdmin && _backend.TouchesRealSystem && !ctx.IsElevated()) return step.Skipped("This Windows tool needs admin rights. PC Tidy runs it in its admin helper.");
        if (plan.MissingProgram is { } missing) return step.Skipped(missing);

        ct.ThrowIfCancellationRequested();
        var log = LogPath(ctx, step, plan.ToolId);
        try
        {
            return plan.ToolId switch
            {
                ToolIds.CleanmgrHandler => await RunCleanmgrAsync(step, plan, log, ct).ConfigureAwait(false),
                ToolIds.HibernateType => await RunHibernateAsync(step, plan, log, ct).ConfigureAwait(false),
                ToolIds.DismAnalyzeComponentStore => await RunDismAnalyzeAsync(step, plan, log, ct).ConfigureAwait(false),
                ToolIds.FlushDns => _backend.FlushDnsCache() ? step.Done("Cleared the DNS cache.") : step.Failed("Windows couldn't clear the DNS cache."),
                ToolIds.WindowsUpdateInstallDrivers => await RunWindowsUpdateAsync(step, plan, ct).ConfigureAwait(false),
                ToolIds.DriverRollback => RunRollback(step, plan),
                ToolIds.RestorePointCreate => await RunRestorePointAsync(step, plan, ctx, ct).ConfigureAwait(false),
                ToolIds.BcdDeleteValue => await RunBcdDeleteAsync(step, plan, log, ct).ConfigureAwait(false),
                ToolIds.NetshAutotuningNormal => await RunAutotuningAsync(step, plan, log, ct).ConfigureAwait(false),
                ToolIds.CipherWipeFreeSpace => await RunCipherAsync(step, plan, log, ct).ConfigureAwait(false),
                ToolIds.PnpExportDrivers => await RunDriverExportAsync(step, plan, log, ctx, ct).ConfigureAwait(false),
                ToolIds.PnpAddDrivers => await RunDriverAddAsync(step, plan, log, ctx, ct).ConfigureAwait(false),
                ToolIds.PnpRemoveDevice => await RunRemoveDeviceAsync(step, plan, log, ct).ConfigureAwait(false),
                ToolIds.ShadowStorageResize => await RunShadowResizeAsync(step, plan, log, ct).ConfigureAwait(false),
                ToolIds.CompactLzx when CompactFolderProblem(plan.Folder!, ctx.Locations) is { } refused => step.Skipped(refused),
                ToolIds.OneDriveShutdown => await RunOneDriveShutdownAsync(step, plan, log, ct).ConfigureAwait(false),
                ToolIds.CleanmgrStorageSense => OpenStorageSettings(step),
                ToolIds.DevCacheClean when plan.GradleCachesFolder is not null => await RunGradleAsync(step, plan, log, ctx, ct).ConfigureAwait(false),
                _ when plan.Launch => Launch(step, plan),
                _ => await RunPlainAsync(step, plan, log, ct).ConfigureAwait(false),
            };
        }
        catch (FileNotFoundException)
        {
            return step.Skipped("This program isn't on this PC.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return step.Skipped("Windows didn't allow this (access denied). Nothing was changed.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException
                                       or System.Management.ManagementException or InvalidOperationException or IOException)
        {
            return step.Failed("The Windows tool couldn't run: " + ex.Message);
        }
    }

    public Task<StepRecord> SimulateAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        if (!Resolve(step.Item, out var plan, out var problem)) return Task.FromResult(step.Skipped(problem));
        var permanent = plan.Reversible ? "" : " This can't be undone.";
        return Task.FromResult(step.Simulated($"Would run: {plan.Display}.{permanent}"));
    }

    public async Task<StepRecord> UndoAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        if (step.Status != StepStatus.Done) return step.Skipped("Nothing to undo.");
        if (!Resolve(step.Item, out var plan, out var problem)) return step.UndoFailed(problem);
        switch (plan.ToolId)
        {
            case ToolIds.DismAnalyzeComponentStore: return step.Skipped("Measuring changes nothing, so there's nothing to undo.");
            case ToolIds.CleanmgrStorageSense: return step.Skipped("Opening a page changes nothing, so there's nothing to undo.");
            case ToolIds.PnpExportDrivers: return step.Skipped("A driver backup changes nothing on the system. The backup was kept.");
            case ToolIds.DevCacheClean when plan.GradleCachesFolder is not null: return await UndoGradleAsync(step, ctx, ct).ConfigureAwait(false);
        }

        IReadOnlyList<ToolCommand>? commands;
        string done;
        switch (plan.ToolId)
        {
            case ToolIds.HibernateType:
                if (HibernateUndoTarget(step) is not { } target) return step.UndoFailed("The saved hibernation setting is missing, so PC Tidy can't undo this.");
                commands = SystemToolCatalog.HibernateCommands(target);
                done = $"Set hibernation back to {target}.";
                break;
            case ToolIds.BcdDeleteValue:
                var prior = step.Undo(PriorBcdData);
                if (prior is null or NotUndoableBcd || BcdUndoValue(plan.BcdName!, prior) is not { } text)
                    return step.Skipped("PC Tidy couldn't read this boot setting's old value, so it can't put it back. Nothing was changed.");
                commands = new[] { SystemToolCatalog.BcdSetCommand(plan.BcdName!, text) };
                done = $"Put the boot setting '{plan.BcdName}' back to {text}.";
                break;
            case ToolIds.NetshAutotuningNormal:
                if (step.Undo(PriorAutotuningData) is not { } level || !SystemToolCatalog.AutotuningLevels.Contains(level))
                    return step.Skipped("PC Tidy couldn't read the old auto-tuning level, so it can't put it back. Nothing was changed.");
                commands = new[] { SystemToolCatalog.AutotuningCommand(level) };
                done = $"Set network auto-tuning back to {level}.";
                break;
            case ToolIds.OneDriveShutdown:
            case PcTidy.Core.Model.ToolIds.CompactLzx:
                commands = plan.UndoCommands;
                done = plan.ToolId == ToolIds.OneDriveShutdown ? "Started OneDrive again." : $"Uncompressed {plan.Folder}.";
                break;
            default:
                return step.CannotUndo();
        }

        if (ctx.Locations.IsSandbox && _backend.TouchesRealSystem) return step.SandboxSimulated($"Would run: {string.Join(" then ", commands!)}");
        if (plan.PerUser && ctx.IsElevated) return step.UndoFailed("This works on your own account, so PC Tidy's admin helper doesn't undo it.");
        if (plan.NeedsAdmin && _backend.TouchesRealSystem && !ctx.IsElevated()) return step.UndoFailed("Undoing this needs admin rights. PC Tidy runs it in its admin helper.");

        try
        {
            if (plan.ToolId == ToolIds.OneDriveShutdown)
            {
                _backend.Launch(commands![0]);
                return step.Undone(done);
            }
            foreach (var command in commands!)
            {
                var result = await _backend.RunAsync(command, plan.Timeout, LogPath(ctx, step, plan.ToolId + "-undo"), ct).ConfigureAwait(false);
                if (result.TimedOut || (result.ExitCode != 0 && !plan.RestartCodes.Contains(result.ExitCode)))
                    return step.UndoFailed($"Windows couldn't put this back (code {result.ExitCode}).");
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            return step.UndoFailed("Windows couldn't put this back: " + ex.Message);
        }

        if (plan.ToolId == ToolIds.HibernateType)
            return _backend.ReadHibernation() is { } now && HibernationMatches(now, HibernateUndoTarget(step)!)
                ? step.Undone(done) : step.UndoFailed("Windows didn't keep the original hibernation setting.");
        return step.Undone(done);
    }

    /// <summary>
    /// Where hibernation undo goes back to: the state read from the registry right before the change (this PC's real
    /// value), else the feature's Args["undoType"] (validated by the catalog). Null if neither is known.
    /// </summary>
    internal static string? HibernateUndoTarget(StepRecord step)
    {
        if (step.UndoData is { } u && u.TryGetValue(PriorHibernateEnabledData, out var enabledText) && enabledText is "0" or "1")
        {
            if (enabledText == "0") return "off";
            var priorType = u.TryGetValue(PriorHiberFileTypeData, out var t) && int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var raw) ? raw : (int?)null;
            return WindowsToolState.HiberTypeName(priorType) ?? "on";
        }
        return SystemToolCatalog.TryResolve(step.Item, out var plan, out _) ? plan.HibernateUndoTarget : null;
    }

    /// <summary>"bool:1" / "int:3" → bcdedit's input keyword for that value.</summary>
    internal static string? BcdUndoValue(string name, string prior)
    {
        var parts = prior.Split(':');
        if (parts.Length != 2 || !ulong.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var v)) return null;
        return parts[0] switch
        {
            "bool" => SystemToolCatalog.BcdValueText(name, new BcdElementValue(true, v)),
            "int" => SystemToolCatalog.BcdValueText(name, new BcdElementValue(false, v)),
            _ => null,
        };
    }

    public Task<bool> WasCompletedAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        if (!Resolve(step.Item, out var plan, out _) || (ctx.Locations.IsSandbox && _backend.TouchesRealSystem)) return Task.FromResult(false);
        try
        {
            return Task.FromResult(plan.ToolId switch
            {
                ToolIds.HibernateType => _backend.ReadHibernation() is { } now && HibernationMatches(now, plan.HibernateTarget!),
                ToolIds.BcdDeleteValue when plan.BcdElementType is { } type => _backend.ReadBcdElement(type, out var readable) is null && readable,
                ToolIds.NetshAutotuningNormal => _backend.ReadAutotuningLevel() == "normal",
                ToolIds.OneDriveShutdown => !_backend.IsProcessRunning("OneDrive"),
                // Re-running a cleanup or repair tool after a crash is harmless; the rest is reported as not done.
                _ => false,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Task.FromResult(false);
        }
    }

    // ------------------------------------------------------------------ tools

    private async Task<StepRecord> RunPlainAsync(StepRecord step, SystemToolPlan plan, string log, CancellationToken ct)
    {
        var result = await RunAllAsync(plan, plan.Commands, log, ct).ConfigureAwait(false);
        if (result.TimedOut) return WithLog(step.Failed($"{plan.Title} is still running. It was left to finish on its own."), log);
        var restart = plan.RestartCodes.Contains(result.ExitCode) || step.Item.Arg("needsRestart") == "true";
        if (plan.ToolId == ToolIds.DefenderQuickScan && result.ExitCode == 2)
            return WithLog(step.Done("The Defender scan finished and found something. Open Windows Security to see what it did."), log);
        if (plan.SuccessCodes.Contains(result.ExitCode) || plan.RestartCodes.Contains(result.ExitCode))
            return WithLog(step.Done($"Done: {Lower(plan.Title)}.{(restart ? " Restart your PC to finish." : "")}"), log);
        return WithLog(step.Failed($"Windows reported a problem (code {result.ExitCode}). Details are in the log."), log);
    }

    private StepRecord Launch(StepRecord step, SystemToolPlan plan)
    {
        _backend.Launch(plan.Commands[0]);
        return step.Done($"Done: {Lower(plan.Title)}.");
    }

    private StepRecord OpenStorageSettings(StepRecord step)
    {
        _backend.OpenUri("ms-settings:storagesense");
        return step.Done("Opened Storage settings.");
    }

    private async Task<StepRecord> RunDismAnalyzeAsync(StepRecord step, SystemToolPlan plan, string log, CancellationToken ct)
    {
        var result = await RunAllAsync(plan, plan.Commands, log, ct).ConfigureAwait(false);
        if (result.ExitCode != 0 || result.TimedOut)
            return WithLog(step.Failed($"Windows couldn't measure the component store (code {result.ExitCode})."), log);

        // DISM ran with /English, so these labels are the same on every PC.
        var facts = DismAnalysis.Parse(result.Output);
        var summary = facts.TryGetValue(DismAnalysis.Reclaimable, out var r) && facts.TryGetValue(DismAnalysis.CleanupRecommended, out var rec)
            ? $"Measured the component store: {r} reclaimable package(s), cleanup recommended: {rec}."
            : "Measured the component store. Details are in the log.";
        return WithLog(step.Done(summary).WithUndo(facts.Select(f => ("result." + f.Key, f.Value)).ToArray()), log);
    }

    private async Task<StepRecord> RunCleanmgrAsync(StepRecord step, SystemToolPlan plan, string log, CancellationToken ct)
    {
        var handler = plan.CleanmgrHandler!;
        if (!_backend.CleanmgrHandlerExists(handler)) return step.Skipped($"Windows' '{handler}' cleaner isn't on this PC.");

        var windowsOld = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "Windows.old");
        ProcessResult result;
        // Every StateFlags value is put back exactly as it was afterwards, so other Disk Cleanup presets are unchanged.
        using (_backend.SelectCleanmgrHandler(handler))
            result = await RunAllAsync(plan, plan.Commands, log, ct).ConfigureAwait(false);

        if (result.TimedOut) return WithLog(step.Failed("Disk Cleanup is still running. It was left to finish on its own."), log);
        if (result.ExitCode != 0) return WithLog(step.Failed($"Disk Cleanup reported a problem (code {result.ExitCode})."), log);
        if (handler == "Previous Installations" && _backend.DirectoryExists(windowsOld))
            return WithLog(step.Done("Disk Cleanup ran, but Windows kept part of the previous installation."), log);
        return WithLog(step.Done($"Windows' '{handler}' cleaner finished."), log);
    }

    private async Task<StepRecord> RunHibernateAsync(StepRecord step, SystemToolPlan plan, string log, CancellationToken ct)
    {
        var before = _backend.ReadHibernation();
        if (before is not null && HibernationMatches(before, plan.HibernateTarget!))
            return step.Skipped("Hibernation is already set this way. Nothing to change.");

        var result = await RunAllAsync(plan, plan.Commands, log, ct).ConfigureAwait(false);
        var after = _backend.ReadHibernation();
        if (result.ExitCode != 0 || after is null || !HibernationMatches(after, plan.HibernateTarget!))
            return WithLog(step.Failed("Windows didn't change the hibernation setting. Some PCs don't support it."), log);

        var done = step.Done($"{plan.Title}.");
        if (before is not null)
        {
            done = done.WithUndo((PriorHibernateEnabledData, before.Enabled ? "1" : "0"));
            if (before.HiberFileType is { } type) done = done.WithUndo((PriorHiberFileTypeData, type.ToString(CultureInfo.InvariantCulture)));
        }
        return WithLog(done, log);
    }

    private async Task<StepRecord> RunWindowsUpdateAsync(StepRecord step, SystemToolPlan plan, CancellationToken ct)
    {
        var r = await _backend.InstallDriverUpdatesAsync(plan.UpdateIds!, ct).ConfigureAwait(false);
        if (r.Found == 0) return step.Skipped("Windows Update no longer offers these driver updates.");
        var restart = r.RebootRequired ? " Restart your PC to finish." : "";
        var partly = r.Found < r.Requested ? $" ({r.Found} of {r.Requested}; the others are no longer offered)" : "";
        return r.ResultCode switch
        {
            2 => step.Done($"Installed the driver updates{partly}.{restart}"),
            3 => step.Done($"Installed some of the driver updates; Windows Update shows which ones failed.{restart}"),
            _ => step.Failed($"Windows Update couldn't install the driver updates (result {r.ResultCode})."),
        };
    }

    private StepRecord RunRollback(StepRecord step, SystemToolPlan plan)
    {
        var r = _backend.RollbackDriver(plan.InstanceId!);
        if (!r.Ok) return step.Failed($"Windows couldn't roll back this driver (code {r.Error}). The previous driver may no longer be on this PC.");
        return step.Done(r.RebootRequired ? "Rolled back the driver. Restart your PC to finish." : "Rolled back the driver.");
    }

    private async Task<StepRecord> RunRestorePointAsync(StepRecord step, SystemToolPlan plan, HandlerContext ctx, CancellationToken ct)
    {
        var info = await _backend.CreateRestorePointAsync(ctx.Locations, plan.RestorePointDescription!, ct).ConfigureAwait(false);
        if (info.Created) return step.Done($"Made a restore point (\"{info.Description}\").");
        if (info.Reused) return step.Done($"A restore point from the last 24 hours already exists (\"{info.Description}\"), so Windows didn't make another.");
        return step.Failed(info.Problem ?? "Windows couldn't make a restore point.");
    }

    private async Task<StepRecord> RunBcdDeleteAsync(StepRecord step, SystemToolPlan plan, string log, CancellationToken ct)
    {
        string prior = NotUndoableBcd;
        var readable = false;
        if (plan.BcdElementType is { } type)
        {
            var before = _backend.ReadBcdElement(type, out readable);
            if (readable && before is null) return step.Skipped($"The boot setting '{plan.BcdName}' isn't set. Nothing to remove.");
            if (readable && before is not null) prior = (before.IsBoolean ? "bool:" : "int:") + before.Value.ToString(CultureInfo.InvariantCulture);
        }

        var result = await RunAllAsync(plan, plan.Commands, log, ct).ConfigureAwait(false);
        if (result.ExitCode != 0) return WithLog(step.Failed($"Windows couldn't remove the boot setting (code {result.ExitCode})."), log);
        if (readable && _backend.ReadBcdElement(plan.BcdElementType!.Value, out _) is not null)
            return WithLog(step.Failed("Windows still has this boot setting."), log);
        var note = prior == NotUndoableBcd ? " PC Tidy couldn't read its old value, so this can't be undone from History." : "";
        return WithLog(step.Done($"Removed the boot setting '{plan.BcdName}'. Restart your PC to finish.{note}").WithUndo((PriorBcdData, prior)), log);
    }

    private async Task<StepRecord> RunAutotuningAsync(StepRecord step, SystemToolPlan plan, string log, CancellationToken ct)
    {
        var before = _backend.ReadAutotuningLevel();
        if (before == "normal") return step.Skipped("Network auto-tuning is already normal. Nothing to change.");
        var result = await RunAllAsync(plan, plan.Commands, log, ct).ConfigureAwait(false);
        if (result.ExitCode != 0) return WithLog(step.Failed($"Windows couldn't change network auto-tuning (code {result.ExitCode})."), log);
        if (before is not null && _backend.ReadAutotuningLevel() is { } after && after != "normal") return WithLog(step.Failed("Windows didn't keep the new auto-tuning level."), log);
        var done = step.Done(before is null
            ? "Set network auto-tuning back to normal. PC Tidy couldn't read the old level, so this can't be undone from History."
            : "Set network auto-tuning back to normal.");
        return WithLog(before is null ? done : done.WithUndo((PriorAutotuningData, before)), log);
    }

    private async Task<StepRecord> RunCipherAsync(StepRecord step, SystemToolPlan plan, string log, CancellationToken ct)
    {
        // Overwriting free space is pointless on an SSD (TRIM already discards it) and only wears it out.
        if (_backend.MediaOf(plan.Drive!.Value) != DriveMedia.Hdd)
            return step.Skipped("Free-space wiping is only for hard drives. SSDs already discard deleted data, and this would only wear them out.");
        return await RunPlainAsync(step, plan, log, ct).ConfigureAwait(false);
    }

    private async Task<StepRecord> RunOneDriveShutdownAsync(StepRecord step, SystemToolPlan plan, string log, CancellationToken ct)
    {
        if (!_backend.IsProcessRunning("OneDrive")) return step.Skipped("OneDrive isn't running. Nothing to pause.");
        var result = await RunAllAsync(plan, plan.Commands, log, ct).ConfigureAwait(false);
        for (var i = 0; i < 20 && _backend.IsProcessRunning("OneDrive"); i++) await Task.Delay(500, ct).ConfigureAwait(false);
        if (_backend.IsProcessRunning("OneDrive")) return WithLog(step.Failed($"OneDrive didn't close (code {result.ExitCode})."), log);
        return WithLog(step.Done("Closed OneDrive. Undo (or the next sign-in) starts it again."), log);
    }

    /// <summary>
    /// Driver backup. In the admin helper pnputil writes into the helper's admin-only store first (nothing the user can
    /// redirect), the helper records a manifest of every file (SHA-256) there, and the files are then copied to the
    /// user's backup folder handle by handle (links refused, nothing overwritten). Elsewhere it writes directly.
    /// </summary>
    private async Task<StepRecord> RunDriverExportAsync(StepRecord step, SystemToolPlan plan, string log, HandlerContext ctx, CancellationToken ct)
    {
        var userFolder = plan.Folder!;
        if (!PathArgs.HasNoLinks(userFolder)) return step.Skipped("The backup folder goes through a link to somewhere else, so PC Tidy won't use it.");
        if (!ctx.IsElevated)
        {
            Directory.CreateDirectory(userFolder);
            return await RunPlainAsync(step, plan, log, ct).ConfigureAwait(false);
        }

        var id = $"{SafeRun(ctx)}-{step.Index}";
        var adminCopy = Path.Combine(DataPaths.Backups(ctx.Locations.DataRoot), "drivers", id);
        Directory.CreateDirectory(adminCopy);
        var export = new ToolCommand(plan.Commands[0].ExePath, new[] { "/export-driver", "*", adminCopy });
        var result = await _backend.RunAsync(export, plan.Timeout, log, ct).ConfigureAwait(false);
        if (result.TimedOut || result.ExitCode != 0) return WithLog(step.Failed($"Windows couldn't back up the drivers (code {result.ExitCode})."), log);

        var manifest = DriverBackups.Record(adminCopy, DriverBackups.ManifestFolder(ctx.Locations.DataRoot), id, ct);
        int copied;
        try { copied = DriverBackups.CopyToUserFolder(adminCopy, userFolder, ct); }
        catch (IOException ex)
        {
            return WithLog(step.Failed($"PC Tidy kept its protected copy of the drivers, but couldn't copy them to {userFolder}: {ex.Message}")
                .WithUndo((DriverAdminCopyData, adminCopy)), log);
        }
        var kept = copied < manifest.Files.Count ? $" {manifest.Files.Count - copied} file(s) already there were left as they were." : "";
        return WithLog(step.Done($"Backed up the drivers to {userFolder} ({copied} files). PC Tidy also keeps its own protected copy.{kept}")
            .WithUndo((DriverAdminCopyData, adminCopy)), log);
    }

    /// <summary>
    /// Driver restore. In the admin helper the drivers are never installed straight from a folder the user can change:
    /// the folder is copied into the admin-only store (links refused), and only driver packages whose files exactly
    /// match a backup the helper itself recorded (names, sizes, SHA-256) are installed from that copy. Anything else,
    /// and any package with a driver on the vulnerable-driver list, is left out.
    /// </summary>
    private async Task<StepRecord> RunDriverAddAsync(StepRecord step, SystemToolPlan plan, string log, HandlerContext ctx, CancellationToken ct)
    {
        var source = plan.Folder!;
        if (!Directory.Exists(source)) return step.Skipped("The driver backup folder isn't there any more.");
        if (!PathArgs.HasNoLinks(source)) return step.Skipped("The driver backup folder goes through a link to somewhere else, so PC Tidy won't use it.");
        if (!ctx.IsElevated) return await RunPlainAsync(step, plan, log, ct).ConfigureAwait(false);

        var staging = Path.Combine(ctx.Locations.DataRoot, "staging", "drivers", $"{SafeRun(ctx)}-{step.Index}");
        try
        {
            var manifests = DriverBackups.LoadAll(DriverBackups.ManifestFolder(ctx.Locations.DataRoot));
            var (decision, problem) = DriverBackups.StageAndCheck(source, staging, manifests, ct);
            if (decision is null) return step.Skipped(problem!);
            var left = LeftOut(decision);
            if (decision.Approved.Count == 0)
                return step.Skipped(manifests.Count == 0
                    ? "PC Tidy only puts back drivers from a backup it made itself on this PC, and it hasn't made one yet. Nothing was installed."
                    : "Nothing in this folder matches a driver backup PC Tidy made on this PC, so nothing was installed." + left);

            var install = new ToolCommand(plan.Commands[0].ExePath, new[] { "/add-driver", Path.Combine(staging, "*.inf"), "/subdirs", "/install" });
            var done = await RunPlainAsync(step, plan with { Commands = new[] { install } }, log, ct).ConfigureAwait(false);
            return left.Length == 0 || done.Status != StepStatus.Done ? done : done with { Message = done.Message + left };
        }
        finally
        {
            DriverBackups.TryDelete(staging);
        }
    }

    private static string LeftOut(DriverBackups.RestoreDecision d)
    {
        var parts = new List<string>();
        if (d.NotFromABackup.Count > 0) parts.Add($" Left out (not exactly as PC Tidy backed them up): {string.Join(", ", d.NotFromABackup.Take(5))}{(d.NotFromABackup.Count > 5 ? "…" : "")}.");
        if (d.Vulnerable.Count > 0) parts.Add($" Left out (a driver with a known security hole): {string.Join(", ", d.Vulnerable.Take(5))}.");
        return string.Concat(parts);
    }

    /// <summary>
    /// Removing a device entry is only for devices that aren't connected any more (ghost entries). A device that is
    /// present right now is never removed; one PC Tidy can't find, or can't check, isn't either.
    /// </summary>
    private async Task<StepRecord> RunRemoveDeviceAsync(StepRecord step, SystemToolPlan plan, string log, CancellationToken ct)
    {
        switch (_backend.DeviceState(plan.InstanceId!))
        {
            case DevicePresence.Present: return step.Skipped("This device is connected right now, so PC Tidy won't remove it.");
            case DevicePresence.Missing: return step.Skipped("Windows doesn't know this device any more. Nothing to remove.");
            case DevicePresence.Unknown: return step.Skipped("PC Tidy couldn't check whether this device is connected, so it left it alone.");
        }
        return await RunPlainAsync(step, plan, log, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Restore point space: never below what restore points use now plus room for one more (the newest one's size,
    /// estimated as the average, since Windows doesn't report a size per point). Shrinking further would make Windows
    /// delete restore points, which can't be brought back.
    /// </summary>
    private async Task<StepRecord> RunShadowResizeAsync(StepRecord step, SystemToolPlan plan, string log, CancellationToken ct)
    {
        if (_backend.ReadShadowStorage(plan.Drive!.Value) is not { VolumeBytes: > 0 } storage)
            return step.Skipped("PC Tidy couldn't check how much space restore points use now, so it didn't change it.");
        var minimum = MinimumShadowBytes(storage);
        var requested = storage.VolumeBytes / 100 * plan.Percent!.Value;
        if (requested < minimum)
        {
            var minPercent = (int)Math.Min(100, Math.Ceiling(minimum * 100.0 / storage.VolumeBytes));
            return step.Skipped($"Restore points on {plan.Drive}: use {Sizes.Format(storage.UsedBytes)} now. {plan.Percent}% would make Windows delete some, " +
                                $"so PC Tidy won't go below {minPercent}%.");
        }
        return await RunPlainAsync(step, plan, log, ct).ConfigureAwait(false);
    }

    /// <summary>Used space plus the newest snapshot's size (estimated as the average snapshot).</summary>
    internal static long MinimumShadowBytes(ShadowStorageFacts s) => s.UsedBytes + (s.Snapshots > 0 ? s.UsedBytes / s.Snapshots : 0);

    /// <summary>Anti-cheat and store folders (by folder name) LZX compression must never touch.</summary>
    private static readonly string[] CompactNeverFolders =
    {
        "steamapps", "EasyAntiCheat", "EasyAntiCheat_EOS", "BattlEye", "Riot Vanguard", "Vanguard", "FACEIT", "PunkBuster", "nProtect",
        "XboxGames", "WindowsApps", "ModifiableWindowsApps", "Epic Games", "GOG Galaxy", "Ubisoft Game Launcher", "EA Games", "Battle.net",
    };

    /// <summary>
    /// Folders LZX compression may work on: the user's own folders. Refused: a drive root; Windows, Program Files and
    /// ProgramData (or a folder holding one); cloud-synced folders (compressing would re-upload or break placeholders);
    /// game and launcher folders (anti-cheat checks files, updates rewrite them); and anti-cheat folders by name.
    /// </summary>
    internal static string? CompactFolderProblem(string folder, IKnownLocations k)
    {
        const string refusal = "PC Tidy only compresses your own folders, not Windows, programs, games or cloud folders.";
        if (Path.GetPathRoot(folder) is { } root && string.Equals(Path.TrimEndingDirectorySeparator(folder), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
            return refusal;
        bool Overlaps(string? other) => other is { Length: > 3 } o && (PathUtil.IsUnder(folder, o) || PathUtil.IsUnder(o, folder)
                                                                         || string.Equals(Path.TrimEndingDirectorySeparator(o), Path.TrimEndingDirectorySeparator(folder), StringComparison.OrdinalIgnoreCase));
        var system = new[] { k.WindowsDir, k.ProgramFiles, k.ProgramFilesX86, k.ProgramData };
        if (system.Any(Overlaps)) return refusal;
        if (k.CloudRoots.Any(Overlaps)) return "This folder is synced to the cloud (or holds one that is), so PC Tidy won't compress it.";
        var games = k.SteamLibraries.Concat(k.GameInstallDirs).Append(k.SteamRoot).Append(k.EpicManifestsDir);
        if (games.Any(Overlaps)) return "This folder holds games. Their anti-cheat and updates expect the files as they are, so PC Tidy won't compress it.";
        var parts = folder.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => CompactNeverFolders.Contains(p, StringComparer.OrdinalIgnoreCase)))
            return "This looks like a game, store or anti-cheat folder, so PC Tidy won't compress it.";
        return null;
    }

    /// <summary>
    /// Gradle: "gradle --stop" when a gradle.exe exists, then caches of older Gradle versions (not the newest, not used in
    /// the last 30 days, not in use right now) go to quarantine, so they can still be put back.
    /// </summary>
    private async Task<StepRecord> RunGradleAsync(StepRecord step, SystemToolPlan plan, string log, HandlerContext ctx, CancellationToken ct)
    {
        if (plan.Commands.Count > 0) await RunAllAsync(plan, plan.Commands, log, ct).ConfigureAwait(false);
        var caches = plan.GradleCachesFolder!;
        if (!Directory.Exists(caches)) return step.Skipped("Gradle has no caches on this PC.");
        if (!PathArgs.HasNoLinks(caches)) return step.Skipped("Gradle's cache folder goes through a link to somewhere else, so PC Tidy left it alone.");

        var old = OldGradleVersions(caches, DateTime.UtcNow);
        var entries = new List<QuarantineEntry>();
        if (old.Count > 0)
        {
            var busy = (await ctx.InUse.CheckAsync(old, ct).ConfigureAwait(false)).Where(u => u.InUse).Select(u => u.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in old.Where(d => !busy.Contains(d)))
            {
                ct.ThrowIfCancellationRequested();
                if (ctx.Safety.ClassifyPath(dir).Protection == PathProtection.Never) continue;
                entries.Add(await ctx.Quarantine.QuarantineAsync(dir, ctx.RunId, ct).ConfigureAwait(false));
            }
        }
        var bytes = entries.Sum(e => e.SizeBytes);
        var stopped = plan.Commands.Count > 0 ? "Stopped Gradle. " : "";
        var done = entries.Count == 0
            ? step.Done($"{stopped}No caches of old Gradle versions to tidy.")
            : step.Done($"{stopped}Moved caches of {entries.Count} old Gradle version(s) ({Sizes.Format(bytes)}) to quarantine.") with { BytesAffected = bytes };
        return WithLog(done.WithUndo((GradleQuarantineData, JsonSerializer.Serialize(entries, ElevationProtocol.Json))), log);
    }

    private static async Task<StepRecord> UndoGradleAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        List<QuarantineEntry>? entries;
        try { entries = step.Undo(GradleQuarantineData) is { } json ? JsonSerializer.Deserialize<List<QuarantineEntry>>(json, ElevationProtocol.Json) : null; }
        catch (JsonException) { entries = null; }
        if (entries is null || entries.Count == 0) return step.Skipped("The tool's own cache cleaning can't be undone. Nothing else was moved.");
        foreach (var e in entries) await ctx.Quarantine.RestoreAsync(e, ct).ConfigureAwait(false);
        return step.Undone($"Put back {entries.Count} Gradle cache folder(s) from quarantine.");
    }

    /// <summary>Version-named cache folders that are not the newest version and weren't used in the last 30 days.</summary>
    internal static IReadOnlyList<string> OldGradleVersions(string caches, DateTime nowUtc)
    {
        var versions = new DirectoryInfo(caches).EnumerateDirectories()
            .Where(d => (d.Attributes & FileAttributes.ReparsePoint) == 0)
            .Select(d => (Dir: d, Version: Version.TryParse(d.Name.Split('-')[0], out var v) && d.Name.Count(c => c == '.') >= 1 ? v : null))
            .Where(x => x.Version is not null)
            .ToList();
        if (versions.Count < 2) return Array.Empty<string>();
        var newest = versions.Max(x => x.Version)!;
        return versions.Where(x => x.Version! < newest && nowUtc - x.Dir.LastWriteTimeUtc > GradleInUseAge).Select(x => x.Dir.FullName).ToList();
    }

    // ------------------------------------------------------------------ helpers

    private static bool HibernationMatches(HibernationState state, string target) => target switch
    {
        "off" => !state.Enabled,
        "on" => state.Enabled,
        _ => state.Enabled && WindowsToolState.HiberTypeName(state.HiberFileType) == target,
    };

    /// <summary>Runs the commands in order; stops at the first one that doesn't succeed.</summary>
    private async Task<ProcessResult> RunAllAsync(SystemToolPlan plan, IReadOnlyList<ToolCommand> commands, string log, CancellationToken ct)
    {
        ProcessResult last = new(0, false, "");
        foreach (var command in commands)
        {
            last = await _backend.RunAsync(command, plan.Timeout, log, ct).ConfigureAwait(false);
            if (last.TimedOut || !(plan.SuccessCodes.Contains(last.ExitCode) || plan.RestartCodes.Contains(last.ExitCode))) break;
        }
        return last;
    }

    private static string Lower(string title) => title.Length == 0 ? title : char.ToLowerInvariant(title[0]) + title[1..];

    private static string SafeRun(HandlerContext ctx) => string.Concat(ctx.RunId.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));

    private static string LogPath(HandlerContext ctx, StepRecord step, string toolId)
        => Path.Combine(DataPaths.Logs(ctx.Locations.DataRoot), "tools", $"{SafeRun(ctx)}-{step.Index}-{toolId}.log");

    private static StepRecord WithLog(StepRecord step, string log) => step.WithUndo((LogData, log));
}

/// <summary>Reads the few facts PC Tidy needs from <c>DISM /English /AnalyzeComponentStore</c>.</summary>
internal static class DismAnalysis
{
    public const string ActualSize = "actualSize";
    public const string Reclaimable = "reclaimablePackages";
    public const string CleanupRecommended = "cleanupRecommended";

    private static readonly (string Label, string Key)[] Labels =
    {
        ("Actual Size of Component Store", ActualSize),
        ("Number of Reclaimable Packages", Reclaimable),
        ("Component Store Cleanup Recommended", CleanupRecommended),
    };

    public static Dictionary<string, string> Parse(string output)
    {
        var facts = new Dictionary<string, string>();
        foreach (var line in output.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var label = line[..colon].Trim();
            foreach (var (l, key) in Labels)
                if (label.Equals(l, StringComparison.OrdinalIgnoreCase)) facts[key] = line[(colon + 1)..].Trim();
        }
        return facts;
    }
}
