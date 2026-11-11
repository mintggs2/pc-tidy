using System.Runtime.InteropServices;
using PcTidy.Core.Model;

namespace PcTidy.Platform;

/// <summary>
/// One device as Configuration Manager reports it (never pnputil's localized text).
/// <paramref name="ProblemCode"/> 22 = CM_PROB_DISABLED (turned off by a user, a tool or an old optimizer).
/// </summary>
public sealed record DeviceFacts(bool Present, bool HasProblem, int ProblemCode, string? ClassName, Guid? ClassGuid)
{
    public const int ProblemDisabled = 22;
    public bool Disabled => HasProblem && ProblemCode == ProblemDisabled;
}

/// <summary>Devices for <see cref="DeviceSetEnabledHandler"/>. Real: cfgmgr32 + pnputil. Tests: a fake.</summary>
public interface IDeviceControl
{
    bool TouchesRealSystem { get; }
    /// <summary>Windows build number (pnputil /enable-device and /disable-device need 19041, Windows 10 version 2004).</summary>
    int WindowsBuild { get; }
    /// <summary>The device now, or null when Windows doesn't know it at all.</summary>
    DeviceFacts? Read(string instanceId);
    /// <summary>Runs %windir%\System32\pnputil.exe with these exact arguments.</summary>
    Task<ProcessResult> RunPnputilAsync(IReadOnlyList<string> arguments, string? logPath, CancellationToken ct);
}

public sealed class WindowsDeviceControl : IDeviceControl
{
    public bool TouchesRealSystem => true;
    public int WindowsBuild => Environment.OSVersion.Version.Build;

    public DeviceFacts? Read(string instanceId)
    {
        try
        {
            var cr = CM_Locate_DevNodeW(out var devInst, instanceId, CmLocateDevnodeNormal);
            if (cr == CrNoSuchDevnode)
            {
                // Known but not connected ("phantom"): it can't be turned on now.
                if (CM_Locate_DevNodeW(out var phantom, instanceId, CmLocateDevnodePhantom) != CrSuccess) return null;
                return new DeviceFacts(false, false, 0, RegistryString(phantom, CmDrpClass), ParseGuid(RegistryString(phantom, CmDrpClassGuid)));
            }
            if (cr != CrSuccess) return null;
            var hasProblem = false;
            var problem = 0;
            if (CM_Get_DevNode_Status(out var status, out var code, devInst, 0) == CrSuccess)
            {
                hasProblem = (status & DnHasProblem) != 0;
                problem = (int)code;
            }
            return new DeviceFacts(true, hasProblem, problem, RegistryString(devInst, CmDrpClass), ParseGuid(RegistryString(devInst, CmDrpClassGuid)));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    public Task<ProcessResult> RunPnputilAsync(IReadOnlyList<string> arguments, string? logPath, CancellationToken ct)
        => ProcessRunner.RunAsync(Path.Combine(Environment.SystemDirectory, "pnputil.exe"), arguments, TimeSpan.FromMinutes(2), logPath, ct);

    private static Guid? ParseGuid(string? text) => Guid.TryParse(text, out var g) ? g : null;

    private static string? RegistryString(uint devInst, uint property)
    {
        uint length = 0;
        CM_Get_DevNode_Registry_PropertyW(devInst, property, out _, null, ref length, 0);
        if (length is 0 or > 4096) return null;
        var buffer = new byte[length];
        if (CM_Get_DevNode_Registry_PropertyW(devInst, property, out _, buffer, ref length, 0) != CrSuccess) return null;
        return System.Text.Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(length, (uint)buffer.Length)).TrimEnd('\0');
    }

    private const uint CmLocateDevnodeNormal = 0x0, CmLocateDevnodePhantom = 0x1;
    private const int CrSuccess = 0x0, CrNoSuchDevnode = 0xD;
    private const uint DnHasProblem = 0x00000400;
    private const uint CmDrpClass = 0x00000008, CmDrpClassGuid = 0x00000009;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);
    [DllImport("cfgmgr32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CM_Get_DevNode_Status(out uint status, out uint problemNumber, uint devInst, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CM_Get_DevNode_Registry_PropertyW(uint devInst, uint property, out uint regDataType, byte[]? buffer, ref uint length, uint flags);
}

/// <summary>
/// DeviceSetEnabled. Args: "instanceId". Turns a device that is turned off (problem code 22) back on with
/// <c>pnputil /enable-device "&lt;id&gt;"</c> (Windows 10 version 2004 and later). The state before is recorded, and undo
/// turns the device off again with <c>pnputil /disable-device "&lt;id&gt;"</c> (never /force): that is the only way PC Tidy
/// ever turns a device off, and only for a step it ran itself (the admin helper undoes only steps in its own ledger).
/// A plan item with Enable = false is refused here and by the SafetyGuard. Devices whose class could affect starting
/// Windows (disks, volumes, storage controllers, firmware, CPU...) are never touched. Admin.
/// </summary>
public sealed class DeviceSetEnabledHandler(IDeviceControl devices) : SystemActionHandler, ISettledStepNote
{
    internal const string PriorStateData = "priorState";
    internal const string PriorProblemData = "priorProblem";
    internal const string DisabledState = "disabled";
    internal const int MinimumBuild = 19041;
    internal const int RestartNeeded = 3010; // verify: pnputil's "restart required" exit code (ERROR_SUCCESS_REBOOT_REQUIRED)

    /// <summary>
    /// Same classes as the SafetyGuard (names and the system-defined class GUIDs), and at least every class the bad-tweak
    /// finder never offers (tests check all three lists agree).
    /// </summary>
    public static readonly IReadOnlySet<string> ProtectedClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "DiskDrive", "Volume", "VolumeSnapshot", "SCSIAdapter", "HDC", "System", "Firmware", "Processor", "Computer", "SecurityDevices",
        "SecurityAccelerator",
    };

    /// <summary>System-defined device setup class GUIDs of <see cref="ProtectedClasses"/> (verify against Microsoft Learn's list).</summary>
    public static readonly IReadOnlySet<Guid> ProtectedClassGuids = new HashSet<Guid>
    {
        new("268c95a1-edfe-11d3-95c3-0010dc4050a5"), // SecurityAccelerator
        new("4d36e967-e325-11ce-bfc1-08002be10318"), // DiskDrive
        new("71a27cdd-812a-11d0-bec7-08002be2092f"), // Volume
        new("533c5b84-ec70-11d2-9505-00c04f79deaf"), // VolumeSnapshot
        new("4d36e97b-e325-11ce-bfc1-08002be10318"), // SCSIAdapter
        new("4d36e96a-e325-11ce-bfc1-08002be10318"), // HDC
        new("4d36e97d-e325-11ce-bfc1-08002be10318"), // System
        new("f2e7dd72-6468-4e36-b6f1-6488f42c1b52"), // Firmware
        new("50127dc3-0f36-415e-a6cc-4cb3be910b65"), // Processor
        new("4d36e966-e325-11ce-bfc1-08002be10318"), // Computer
        new("d94ee5d8-d189-4994-83d2-f68d7d41b0e6"), // SecurityDevices
    };

    public DeviceSetEnabledHandler() : this(new WindowsDeviceControl()) { }

    protected override ActionKind Kind => ActionKind.DeviceSetEnabled;
    protected override bool TouchesRealSystem => devices.TouchesRealSystem;
    protected override bool NeedsElevation(PlanItem item) => true;

    private static string Id(PlanItem item) => item.Arg("instanceId").Trim();

    /// <summary>"ENUMERATOR\DEVICE\INSTANCE": allowed characters only, 2+ non-empty parts, no "." or ".." part.</summary>
    public static bool IsWellFormedInstanceId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 400) return false;
        if (!id.All(c => char.IsAsciiLetterOrDigit(c) || c is '\\' or '&' or '_' or '.' or '-' or '{' or '}' or '#')) return false;
        var parts = id.Split('\\');
        return parts.Length >= 2 && parts.All(p => p.Length > 0 && p is not ("." or ".."));
    }

    internal static bool IsProtected(DeviceFacts facts)
        => (facts.ClassName is { } n && ProtectedClasses.Contains(n)) || (facts.ClassGuid is { } g && ProtectedClassGuids.Contains(g));

    protected override string? Validate(PlanItem item)
    {
        if (item.Enable != true) return "PC Tidy only turns devices back on. It turns one off again only when you undo its own change from History.";
        if (!IsWellFormedInstanceId(Id(item))) return "This step doesn't name a valid device.";
        return null;
    }

    protected override string Describe(PlanItem item) => $"Would turn the device back on (pnputil /enable-device \"{Id(item)}\")";

    protected override bool CanUndo(StepRecord step) => step.Undo(PriorStateData) == DisabledState;
    protected override string DescribeUndo(StepRecord step) => $"Would turn the device off again, as it was (pnputil /disable-device \"{Id(step.Item)}\")";

    private static string Log(HandlerContext ctx, StepRecord step, string what)
        => Path.Combine(DataPaths.Logs(ctx.Locations.DataRoot), "tools", $"{SafeRunId(ctx)}-{step.Index}-{what}.log");

    protected override async Task<StepRecord> RunAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        if (devices.WindowsBuild < MinimumBuild) return step.Skipped("Turning a device on needs Windows 10 version 2004 or later.");
        var id = Id(step.Item);
        var before = devices.Read(id);
        if (before is null) return step.Skipped("Windows doesn't know this device any more. Nothing to change.");
        if (IsProtected(before)) return step.Skipped("This kind of device can affect how Windows starts, so PC Tidy never changes it.");
        if (!before.Present) return step.Skipped("This device isn't connected right now. Connect it and scan again.");
        if (!before.Disabled)
            return step.Skipped(before.HasProblem
                ? "This device isn't turned off (it has a different problem), so PC Tidy left it alone."
                : "This device is already on. Nothing to change.");

        var result = await devices.RunPnputilAsync(new[] { "/enable-device", id }, Log(ctx, step, "enable-device"), ct).ConfigureAwait(false);
        if (result.TimedOut || result.ExitCode is not (0 or RestartNeeded))
            return step.Failed($"Windows couldn't turn the device on (code {result.ExitCode}). Nothing was changed.");
        var restart = result.ExitCode == RestartNeeded;
        if (!restart && devices.Read(id) is { Disabled: true }) return step.Failed("Windows didn't turn the device on.");
        return step.Done(restart ? "Turned the device back on. Restart your PC to finish." : "Turned the device back on. Undo turns it off again.")
            .WithUndo((PriorStateData, DisabledState), (PriorProblemData, DeviceFacts.ProblemDisabled.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// A step recorded as finished only after a crash (PC Tidy saw the device on, but stopped before it wrote down that the
    /// device had been off) has no before-state. PC Tidy never guesses one: it doesn't turn the device off, and says so.
    /// </summary>
    internal const string NoRecordedStateMessage =
        "PC Tidy stopped before it could record that this device was turned off, so it can't turn it off again automatically. " +
        "The device is still on. If you want it off, turn it off in Device Manager.";

    /// <summary>Added to the step's message when the admin helper settles such a step as finished.</summary>
    string? ISettledStepNote.NoteWhenSettledDone(StepRecord step)
        => CanUndo(step) ? null : "PC Tidy can't undo this one automatically: it stopped before it recorded that the device was off.";

    protected override async Task<StepRecord> RevertAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        if (!CanUndo(step)) return step.Skipped(NoRecordedStateMessage);
        if (devices.WindowsBuild < MinimumBuild) return step.UndoFailed("Turning a device off needs Windows 10 version 2004 or later.");
        var id = Id(step.Item);
        var now = devices.Read(id);
        if (now is null) return step.Skipped("Windows doesn't know this device any more, so there's nothing to put back.");
        if (IsProtected(now)) return step.UndoFailed("This kind of device can affect how Windows starts, so PC Tidy never changes it.");
        if (now.Disabled) return step.Skipped("The device is already turned off. Nothing to put back.");
        if (!now.Present) return step.UndoFailed("This device isn't connected right now. Connect it, then undo again.");

        // Never /force: Windows refuses to turn off a device it needs, and that answer is kept.
        var result = await devices.RunPnputilAsync(new[] { "/disable-device", id }, Log(ctx, step, "disable-device"), ct).ConfigureAwait(false);
        if (result.TimedOut || result.ExitCode is not (0 or RestartNeeded))
            return step.UndoFailed($"Windows couldn't turn the device off again (code {result.ExitCode}).");
        return step.Undone(result.ExitCode == RestartNeeded
            ? "Turned the device off again, as it was. Restart your PC to finish."
            : "Turned the device off again, as it was.");
    }

    protected override Task<bool> CheckCompletedAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
        => Task.FromResult(devices.Read(Id(step.Item)) is { Present: true, Disabled: false });
}
