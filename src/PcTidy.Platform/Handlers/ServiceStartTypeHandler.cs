using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PcTidy.Catalog.Services;
using PcTidy.Core.Model;
using static PcTidy.Platform.NativeMethods;

namespace PcTidy.Platform;

/// <summary>A service's start settings as stored by Windows (registry <c>Start</c>, <c>DelayedAutostart</c>, <c>Type</c>).</summary>
public sealed record ServiceStartConfig(int Start, bool DelayedAutostart, int ServiceType)
{
    /// <summary>Kernel (1) and file-system (2) drivers are never changed.</summary>
    public bool IsDriver => (ServiceType & 0x3) != 0;
}

/// <summary>One recovery step ("restart the service after 60 s"): SC_ACTION type (0 none, 1 restart, 2 reboot, 3 run a command) and delay.</summary>
public sealed record ServiceRecoveryAction(int Type, uint DelayMs);

/// <summary>
/// A service's recovery settings (Services console → Recovery tab): what Windows does when it fails, as stored by the
/// Service Control Manager. Saved before a start-type change so undo can put back exactly what was there.
/// </summary>
/// <param name="ResetPeriodSeconds">Seconds without failure before the failure count resets (0xFFFFFFFF = never).</param>
/// <param name="FailureActionsOnNonCrash">"Enable actions for stops with errors".</param>
public sealed record ServiceRecoveryConfig(
    uint ResetPeriodSeconds,
    string? RebootMessage,
    string? Command,
    IReadOnlyList<ServiceRecoveryAction> Actions,
    bool FailureActionsOnNonCrash)
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>Value equality (records compare lists by reference). Null and "" text count as the same.</summary>
    public bool SameAs(ServiceRecoveryConfig? other)
        => other is not null
           && ResetPeriodSeconds == other.ResetPeriodSeconds
           && string.Equals(RebootMessage ?? "", other.RebootMessage ?? "", StringComparison.Ordinal)
           && string.Equals(Command ?? "", other.Command ?? "", StringComparison.Ordinal)
           && FailureActionsOnNonCrash == other.FailureActionsOnNonCrash
           && Actions.SequenceEqual(other.Actions);

    /// <summary>"Run a program" is part of these settings (a command, or an action of type 3). PC Tidy never writes those.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool RunsCommand => !string.IsNullOrWhiteSpace(Command) || Actions.Any(a => a.Type == 3);

    /// <summary>Compact JSON for the run journal's undo data.</summary>
    public string Serialize() => System.Text.Json.JsonSerializer.Serialize(this, Json);

    /// <summary>Reads what <see cref="Serialize"/> wrote; null when it's missing or damaged.</summary>
    public static ServiceRecoveryConfig? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var r = System.Text.Json.JsonSerializer.Deserialize<ServiceRecoveryConfig>(json, Json);
            // Windows allows at most a handful of actions; anything odd is treated as damaged rather than written back.
            if (r is null || r.Actions is null || r.Actions.Count > 32 || r.Actions.Any(a => a is null || a.Type is < 0 or > 3)) return null;
            return r;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

/// <summary>Reads and changes service start types. Real: Service Control Manager. Tests: a fake.</summary>
public interface IServiceControl
{
    /// <summary>False for fakes, so handler logic can be tested in the sandbox.</summary>
    bool TouchesRealSystem { get; }
    /// <summary>Null when the service isn't installed.</summary>
    ServiceStartConfig? Query(string serviceName);
    void SetStart(string serviceName, int start, bool delayedAutostart);

    /// <summary>The service's recovery (failure actions) settings; null when they can't be read (the start-type backup still works).</summary>
    ServiceRecoveryConfig? QueryRecovery(string serviceName) => null;

    /// <summary>Puts recovery settings back exactly (only called on undo, and only when they differ from what's there now).</summary>
    void SetRecovery(string serviceName, ServiceRecoveryConfig recovery)
        => throw new NotSupportedException("This service control can't change recovery settings.");
}

/// <summary>Maps <see cref="ServiceStart"/> to Windows' raw values and back.</summary>
public static class ServiceStartMapping
{
    public static (int Start, bool Delayed) ToRaw(ServiceStart s) => s switch
    {
        ServiceStart.Boot => (0, false),
        ServiceStart.System => (1, false),
        ServiceStart.Automatic => (2, false),
        ServiceStart.AutomaticDelayed => (2, true),
        ServiceStart.Manual => (3, false),
        ServiceStart.Disabled => (4, false),
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };

    public static ServiceStart FromRaw(int start, bool delayed) => start switch
    {
        0 => ServiceStart.Boot,
        1 => ServiceStart.System,
        2 => delayed ? ServiceStart.AutomaticDelayed : ServiceStart.Automatic,
        3 => ServiceStart.Manual,
        _ => ServiceStart.Disabled,
    };

    public static string Friendly(ServiceStart s) => s switch
    {
        ServiceStart.Automatic => "Automatic",
        ServiceStart.AutomaticDelayed => "Automatic (delayed start)",
        ServiceStart.Manual => "Manual",
        ServiceStart.Disabled => "Disabled",
        _ => s.ToString(),
    };
}

/// <summary>Real service control: reads the service's registry key, changes it through the Service Control Manager.</summary>
public sealed class WindowsServiceControl : IServiceControl
{
    public bool TouchesRealSystem => true;

    public ServiceStartConfig? Query(string serviceName)
    {
        using var lm = RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = lm.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
        if (key?.GetValue("Start") is not int start) return null;
        var delayed = key.GetValue("DelayedAutostart") is int d && d != 0;
        var type = key.GetValue("Type") is int t ? t : 0;
        return new ServiceStartConfig(start, delayed, type);
    }

    public void SetStart(string serviceName, int start, bool delayedAutostart)
    {
        var scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) throw Error();
        try
        {
            var svc = OpenServiceW(scm, serviceName, SERVICE_QUERY_CONFIG | SERVICE_CHANGE_CONFIG);
            if (svc == IntPtr.Zero) throw Error();
            try
            {
                if (!ChangeServiceConfigW(svc, SERVICE_NO_CHANGE, (uint)start, SERVICE_NO_CHANGE, null, null, IntPtr.Zero, null, null, null, null))
                    throw Error();
                // The delayed flag only means something for automatic services; Windows rejects it otherwise.
                if (start == 2)
                {
                    var info = new SERVICE_DELAYED_AUTO_START_INFO { fDelayedAutostart = delayedAutostart };
                    if (!ChangeServiceConfig2W(svc, SERVICE_CONFIG_DELAYED_AUTO_START_INFO, ref info)) throw Error();
                }
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    /// <summary>Read-only: QueryServiceConfig2 (failure actions + the "non-crash failures" flag).</summary>
    public ServiceRecoveryConfig? QueryRecovery(string serviceName)
    {
        var scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) return null;
        try
        {
            var svc = OpenServiceW(scm, serviceName, SERVICE_QUERY_CONFIG);
            if (svc == IntPtr.Zero) return null;
            try
            {
                var actions = QueryConfig2(svc, SERVICE_CONFIG_FAILURE_ACTIONS, buffer =>
                {
                    var fa = System.Runtime.InteropServices.Marshal.PtrToStructure<SERVICE_FAILURE_ACTIONSW>(buffer);
                    var list = new List<ServiceRecoveryAction>();
                    var size = System.Runtime.InteropServices.Marshal.SizeOf<SC_ACTION>();
                    for (var i = 0; i < Math.Min(fa.cActions, 32u) && fa.lpsaActions != IntPtr.Zero; i++)
                    {
                        var a = System.Runtime.InteropServices.Marshal.PtrToStructure<SC_ACTION>(fa.lpsaActions + i * size);
                        list.Add(new ServiceRecoveryAction(a.Type, a.Delay));
                    }
                    return (Reset: fa.dwResetPeriod, Reboot: Text(fa.lpRebootMsg), Command: Text(fa.lpCommand), Actions: list);
                });
                if (actions is not { } a) return null;
                var flag = QueryConfig2(svc, SERVICE_CONFIG_FAILURE_ACTIONS_FLAG,
                    buffer => System.Runtime.InteropServices.Marshal.PtrToStructure<SERVICE_FAILURE_ACTIONS_FLAG>(buffer).fFailureActionsOnNonCrashFailures);
                return new ServiceRecoveryConfig(a.Reset, a.Reboot, a.Command, a.Actions, flag ?? false);
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    /// <summary>
    /// Writes the saved recovery settings back (undo only). Empty text clears a reboot message or command, and an empty
    /// action list with a non-null array clears the actions, which is how Windows stores "take no action".
    /// </summary>
    public void SetRecovery(string serviceName, ServiceRecoveryConfig recovery)
    {
        // Second wall (ELV-1): a recovery action that runs a program is never written, whoever asks.
        if (recovery.RunsCommand) throw new NotSupportedException("PC Tidy never sets a recovery action that runs a program.");
        var scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) throw Error();
        try
        {
            // A "restart the service" action may only be configured on a handle that could start it (no service is started here).
            var access = SERVICE_QUERY_CONFIG | SERVICE_CHANGE_CONFIG | (recovery.Actions.Any(a => a.Type == SC_ACTION_RESTART) ? SERVICE_START : 0);
            var svc = OpenServiceW(scm, serviceName, access);
            if (svc == IntPtr.Zero) throw Error();
            var size = System.Runtime.InteropServices.Marshal.SizeOf<SC_ACTION>();
            var actions = System.Runtime.InteropServices.Marshal.AllocHGlobal(Math.Max(1, recovery.Actions.Count) * size);
            var reboot = System.Runtime.InteropServices.Marshal.StringToHGlobalUni(recovery.RebootMessage ?? "");
            var command = System.Runtime.InteropServices.Marshal.StringToHGlobalUni(recovery.Command ?? "");
            var info = System.Runtime.InteropServices.Marshal.AllocHGlobal(System.Runtime.InteropServices.Marshal.SizeOf<SERVICE_FAILURE_ACTIONSW>());
            var flag = System.Runtime.InteropServices.Marshal.AllocHGlobal(System.Runtime.InteropServices.Marshal.SizeOf<SERVICE_FAILURE_ACTIONS_FLAG>());
            try
            {
                for (var i = 0; i < recovery.Actions.Count; i++)
                    System.Runtime.InteropServices.Marshal.StructureToPtr(new SC_ACTION { Type = recovery.Actions[i].Type, Delay = recovery.Actions[i].DelayMs }, actions + i * size, false);
                System.Runtime.InteropServices.Marshal.StructureToPtr(new SERVICE_FAILURE_ACTIONSW
                {
                    dwResetPeriod = recovery.ResetPeriodSeconds, lpRebootMsg = reboot, lpCommand = command,
                    cActions = (uint)recovery.Actions.Count, lpsaActions = actions,
                }, info, false);
                if (!ChangeServiceConfig2Ptr(svc, SERVICE_CONFIG_FAILURE_ACTIONS, info)) throw Error();
                System.Runtime.InteropServices.Marshal.StructureToPtr(new SERVICE_FAILURE_ACTIONS_FLAG { fFailureActionsOnNonCrashFailures = recovery.FailureActionsOnNonCrash }, flag, false);
                if (!ChangeServiceConfig2Ptr(svc, SERVICE_CONFIG_FAILURE_ACTIONS_FLAG, flag)) throw Error();
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(flag);
                System.Runtime.InteropServices.Marshal.FreeHGlobal(info);
                System.Runtime.InteropServices.Marshal.FreeHGlobal(command);
                System.Runtime.InteropServices.Marshal.FreeHGlobal(reboot);
                System.Runtime.InteropServices.Marshal.FreeHGlobal(actions);
                CloseServiceHandle(svc);
            }
        }
        finally { CloseServiceHandle(scm); }
    }

    /// <summary>QueryServiceConfig2 with a sized buffer; null when Windows won't say.</summary>
    private static T? QueryConfig2<T>(IntPtr svc, uint level, Func<IntPtr, T> read) where T : struct
    {
        QueryServiceConfig2W(svc, level, IntPtr.Zero, 0, out var needed);
        if (needed == 0 || needed > 64 * 1024) return null;
        var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal((int)needed);
        try
        {
            return QueryServiceConfig2W(svc, level, buffer, needed, out _) ? read(buffer) : null;
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer); }
    }

    private static string? Text(IntPtr p)
    {
        if (p == IntPtr.Zero) return null;
        var s = System.Runtime.InteropServices.Marshal.PtrToStringUni(p);
        return string.IsNullOrEmpty(s) ? null : s;
    }

    private static Exception Error()
    {
        var w = new Win32Exception();
        return w.NativeErrorCode == ERROR_ACCESS_DENIED ? new UnauthorizedAccessException(w.Message, w) : w;
    }
}

/// <summary>
/// ServiceSetStartType. Records the old Start, the DelayedAutostart flag and the recovery (failure actions) settings,
/// and puts all of them back on undo.
/// Research rule: services are set to Manual (or back to automatic), never Disabled, except by the Services section
/// for the catalog entries whose verdict allows it (the SafetyGuard checks the catalog; this handler is a second wall
/// that still refuses drivers and core/security/update/anti-cheat services).
/// </summary>
public sealed class ServiceStartTypeHandler : IActionHandler
{
    internal const string PriorStartData = "priorStart";
    internal const string PriorDelayedData = "priorDelayed";
    internal const string PriorRecoveryData = "priorRecovery";

    /// <summary>
    /// Optional plan-item arg: the start type the scan saw ("Manual", "Disabled"… a <see cref="ServiceStart"/> name).
    /// Simulation on a practice PC reports it instead of asking the real Service Control Manager.
    /// </summary>
    public const string CurrentStartArg = "currentStart";

    /// <summary>The Services section: the only feature that may set Disabled (catalog-allowed targets, checked by the SafetyGuard).</summary>
    public const string ServicesFeature = "services";

    private static readonly Regex ValidName = new(@"^[A-Za-z0-9_.$\-]{1,256}$", RegexOptions.CultureInvariant);

    /// <summary>The bad-tweak finder: allowed to switch a protected service back on (same rule as the SafetyGuard).</summary>
    public const string RepairFeature = "tweaks.badtweaks";

    /// <summary>The Services section's "bad services" repair: the same exception, the same exact-default rule.</summary>
    public const string ServicesRepairFeature = "services.repair";

    private static readonly string[] RepairFeatures = { RepairFeature, ServicesRepairFeature };

    private readonly IServiceControl _control;

    public ServiceStartTypeHandler() : this(new WindowsServiceControl()) { }
    public ServiceStartTypeHandler(IServiceControl control) => _control = control;

    public bool CanHandle(PlanItem item) => item.Kind == ActionKind.ServiceSetStartType;

    /// <summary>
    /// Protected services: the one shared list (<see cref="ProtectedServices"/>: the SafetyGuard's never-touch list plus
    /// every catalog-Protected service). They are only ever switched back ON to their Windows default by the bad-tweak
    /// finder or the Services repair (see <see cref="IsRepair"/>), and undo never weakens them.
    /// </summary>
    internal static bool IsProtected(string name) => ProtectedServices.IsProtected(name);

    /// <summary>True if <paramref name="start"/> is a Windows default for protected service <paramref name="name"/>.</summary>
    internal static bool IsDefaultFor(string name, ServiceStart start) => ProtectedServices.IsDefaultFor(name, start);

    /// <summary>
    /// The one exception for protected services: the bad-tweak finder or the Services repair putting one back to its
    /// Windows default after an old "optimizer" switched it off (admin + system change, like the SafetyGuard requires).
    /// </summary>
    internal static bool IsRepair(PlanItem item)
        => RepairFeatures.Any(f => string.Equals(item.FeatureId, f, StringComparison.OrdinalIgnoreCase)) && item.NeedsAdmin && item.IsSystemChange
           && item.ServiceStartType is { } t && IsDefaultFor(item.ServiceName!, t);

    /// <summary>
    /// Disabled is only ever set by the Services section, as an admin, reversible system change (restore point first).
    /// Which services may be disabled is the catalog's call, enforced by the SafetyGuard before the step reaches here.
    /// </summary>
    internal static bool MayDisable(PlanItem item)
        => string.Equals(item.FeatureId, ServicesFeature, StringComparison.OrdinalIgnoreCase) && item.NeedsAdmin && item.IsSystemChange && item.Reversible;

    /// <summary>Checks the item itself (name, target, protected services). Null = fine.</summary>
    internal static string? Validate(PlanItem item)
    {
        if (string.IsNullOrWhiteSpace(item.ServiceName) || !ValidName.IsMatch(item.ServiceName)) return "The service name is not valid.";
        if (item.ServiceStartType is not { } target) return "This step doesn't say which start type to use.";
        if (target is ServiceStart.Boot or ServiceStart.System) return "PC Tidy never sets driver start types.";
        if (target == ServiceStart.Disabled && !MayDisable(item)) return "PC Tidy sets services to Manual instead of Disabled, so Windows can still start them when needed.";
        if (IsProtected(item.ServiceName) && !IsRepair(item)) return "This Windows service is essential or protects your PC, so PC Tidy never changes it.";
        return null;
    }

    public Task<StepRecord> ExecuteAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        if (ctx.Mode == ExecutionMode.Simulate) return SimulateAsync(step, ctx, ct);
        if (Validate(step.Item) is { } problem) return Task.FromResult(step.Skipped(problem));
        var name = step.Item.ServiceName!;
        var target = step.Item.ServiceStartType!.Value;
        if (ctx.Locations.IsSandbox && _control.TouchesRealSystem)
            return Task.FromResult(step.SandboxSimulated($"Would set the '{name}' service to {ServiceStartMapping.Friendly(target)}"));
        if (_control.TouchesRealSystem && !ctx.IsElevated())
            return Task.FromResult(step.Skipped("Changing a service needs admin rights. PC Tidy runs it in its admin helper."));

        try
        {
            var before = _control.Query(name);
            if (before is null) return Task.FromResult(step.Skipped($"The '{name}' service isn't on this PC."));
            if (before.IsDriver) return Task.FromResult(step.Skipped("This is a driver. PC Tidy never changes drivers here."));
            var prior = ServiceStartMapping.FromRaw(before.Start, before.DelayedAutostart);
            if (prior == target) return Task.FromResult(step.Skipped($"Already {ServiceStartMapping.Friendly(target)}. Nothing to change."));
            // Saved before anything changes, so undo can put back exactly what was there. Unreadable recovery settings
            // don't block the change (a start-type change never touches them); undo then restores the start type only.
            var recoveryBefore = TryQueryRecovery(name);

            var (start, delayed) = ServiceStartMapping.ToRaw(target);
            _control.SetStart(name, start, delayed);

            var after = _control.Query(name);
            if (after is null || ServiceStartMapping.FromRaw(after.Start, after.DelayedAutostart) != target)
            {
                _control.SetStart(name, before.Start, before.DelayedAutostart);
                return Task.FromResult(step.Failed("Windows didn't keep this change. It was put back as it was."));
            }

            var undo = step.UndoData is null ? new Dictionary<string, string>() : new Dictionary<string, string>(step.UndoData);
            undo[PriorStartData] = before.Start.ToString(CultureInfo.InvariantCulture);
            undo[PriorDelayedData] = before.DelayedAutostart ? "1" : "0";
            if (recoveryBefore is not null) undo[PriorRecoveryData] = recoveryBefore.Serialize();
            return Task.FromResult(step.Done($"Set the '{name}' service to {ServiceStartMapping.Friendly(target)} (was {ServiceStartMapping.Friendly(prior)}).") with { UndoData = undo });
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(step.Skipped("Windows didn't allow changing this service (access denied). Nothing was changed."));
        }
        catch (Win32Exception ex)
        {
            return Task.FromResult(step.Failed("The service couldn't be changed: " + ex.Message));
        }
    }

    public Task<StepRecord> SimulateAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        if (Validate(step.Item) is { } problem) return Task.FromResult(step.Skipped(problem));
        var name = step.Item.ServiceName!;
        var target = step.Item.ServiceStartType!.Value;
        // On a practice PC the real Service Control Manager is never asked: it describes this PC, not the practice one,
        // so preview and simulation could disagree. The start type comes from the plan item (what the scan saw).
        // Anywhere else (a fake control in tests, the real PC outside practice mode) the control is asked first.
        ServiceStart? now = null;
        if (!(ctx.Locations.IsSandbox && _control.TouchesRealSystem))
        {
            try
            {
                if (_control.Query(name) is { } c) now = ServiceStartMapping.FromRaw(c.Start, c.DelayedAutostart);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or Win32Exception or InvalidOperationException or IOException or System.Security.SecurityException)
            {
                // Only for the description: fall back to what the scan saw.
            }
        }
        now ??= RecordedStart(step.Item);
        if (now == target) return Task.FromResult(step.Skipped($"Already {ServiceStartMapping.Friendly(target)}. Nothing to change."));
        var was = now is { } n ? $" (now {ServiceStartMapping.Friendly(n)})" : "";
        return Task.FromResult(step.Simulated($"Would set the '{name}' service to {ServiceStartMapping.Friendly(target)}{was}."));
    }

    /// <summary>The start type the scan recorded on the plan item (<see cref="CurrentStartArg"/>), or null.</summary>
    internal static ServiceStart? RecordedStart(PlanItem item)
        => item.Args is { } a && a.TryGetValue(CurrentStartArg, out var v) && Enum.TryParse<ServiceStart>(v, ignoreCase: true, out var s) && Enum.IsDefined(s)
            ? s : null;

    public Task<StepRecord> UndoAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        if (step.Status != StepStatus.Done) return Task.FromResult(step.Skipped("Nothing to undo."));
        if (!TryReadPrior(step, out var start, out var delayed))
            return Task.FromResult(step.UndoFailed("The saved original start type is missing, so PC Tidy can't undo this."));
        var name = step.Item.ServiceName ?? "";
        if (!ValidName.IsMatch(name)) return Task.FromResult(step.UndoFailed("The service name is not valid."));
        // Undo never weakens protection, whatever the saved data says (ELV-1, UNDO-1): the journal is a file the user's
        // programs can change, and "put back what was there" must not mean PC Tidy switching Defender or Windows Update off.
        if (start is 0 or 1) return Task.FromResult(step.UndoFailed("The saved start type is one only drivers use. PC Tidy never sets it, so the service was left as it is."));
        var prior = ServiceStartMapping.FromRaw(start, delayed);
        if (IsProtected(name) && ProtectedServices.WeakerThanDefault(name, prior))
            return Task.FromResult(step.UndoFailed(
                $"PC Tidy didn't switch '{name}' off again. It protects your PC or keeps Windows working, so it stays as it is now instead of going back to {ServiceStartMapping.Friendly(prior)}."));
        if (ctx.Locations.IsSandbox && _control.TouchesRealSystem)
            return Task.FromResult(step.SandboxSimulated($"Would set the '{name}' service back"));
        if (_control.TouchesRealSystem && !ctx.IsElevated())
            return Task.FromResult(step.UndoFailed("Undoing this needs admin rights. PC Tidy runs it in its admin helper."));

        try
        {
            if (_control.Query(name) is null) return Task.FromResult(step.UndoFailed($"The '{name}' service isn't on this PC any more."));
            _control.SetStart(name, start, delayed);
            var after = _control.Query(name);
            var ok = after is not null && after.Start == start && (start != 2 || after.DelayedAutostart == delayed);
            var friendly = ServiceStartMapping.Friendly(ServiceStartMapping.FromRaw(start, delayed));
            if (!ok) return Task.FromResult(step.UndoFailed("Windows didn't keep the original start type."));
            if (RestoreRecovery(step, name, out var note) is { } problem)
                return Task.FromResult(step.UndoFailed($"Set the '{name}' service back to {friendly}, but {problem}"));
            return Task.FromResult(step.Undone($"Set the '{name}' service back to {friendly}.{(note is null ? "" : " " + note)}"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or Win32Exception)
        {
            return Task.FromResult(step.UndoFailed("The service couldn't be set back: " + ex.Message));
        }
    }

    private ServiceRecoveryConfig? TryQueryRecovery(string name)
    {
        try { return _control.QueryRecovery(name); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Puts the saved recovery settings back if they differ from what's there now (normally they don't: a start-type
    /// change never touches them, but something else may have since). Null = fine (or nothing was saved).
    /// Only "take no action", "restart the service" and "restart the computer" are ever put back: saved settings that run
    /// a command are skipped whatever the journal says (ELV-1: a changed journal must never make Windows run a program
    /// as the service's account), and <paramref name="note"/> says so.
    /// </summary>
    private string? RestoreRecovery(StepRecord step, string name, out string? note)
    {
        note = null;
        if (step.UndoData is not { } u || !u.TryGetValue(PriorRecoveryData, out var json)) return null;
        if (ServiceRecoveryConfig.TryParse(json) is not { } saved) return "its saved recovery settings were damaged, so they weren't touched.";
        if (saved.RunsCommand)
        {
            note = "Its saved recovery settings included running a program, which PC Tidy never sets, so its current recovery settings were left as they are.";
            return null;
        }
        try
        {
            if (saved.SameAs(TryQueryRecovery(name))) return null;
            _control.SetRecovery(name, saved);
            return saved.SameAs(TryQueryRecovery(name)) ? null : "Windows didn't keep its original recovery settings.";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or Win32Exception or NotSupportedException)
        {
            return "its recovery settings couldn't be put back: " + ex.Message;
        }
    }

    public Task<bool> WasCompletedAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        try
        {
            if (step.Item.ServiceName is not { } name || step.Item.ServiceStartType is not { } target || !ValidName.IsMatch(name)) return Task.FromResult(false);
            var c = _control.Query(name);
            return Task.FromResult(c is not null && ServiceStartMapping.FromRaw(c.Start, c.DelayedAutostart) == target);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    internal static bool TryReadPrior(StepRecord step, out int start, out bool delayed)
    {
        start = 0;
        delayed = false;
        if (step.UndoData is not { } u) return false;
        if (!u.TryGetValue(PriorStartData, out var s) || !int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out start) || start is < 0 or > 4)
            return false;
        delayed = u.TryGetValue(PriorDelayedData, out var d) && d == "1";
        return true;
    }
}
