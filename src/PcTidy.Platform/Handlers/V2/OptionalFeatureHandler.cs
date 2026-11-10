using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using PcTidy.Core.Model;

namespace PcTidy.Platform;

/// <summary>Whether an optional feature / capability is present, as DISM reports it (language-neutral numbers).</summary>
public enum OptionalFeatureState { Missing, Enabled, Disabled }

/// <summary>Optional features and capabilities. Real: the documented DISM API (dismapi.dll). Tests: a fake.</summary>
public interface IOptionalFeatureControl
{
    bool TouchesRealSystem { get; }
    OptionalFeatureState Query(string name, bool capability);
    /// <summary>DismDisableFeature (keeps the payload) or DismRemoveCapability. Returns the HRESULT (3010 = restart needed).</summary>
    int Remove(string name, bool capability, string? logPath);
    /// <summary>DismEnableFeature, from the files Windows kept (features only).</summary>
    int EnableFeature(string name, string? logPath);
}

public sealed class DismOptionalFeatureControl : IOptionalFeatureControl
{
    private const string OnlineImage = "DISM_{53BFAE52-B167-4E2F-A258-0A37B57FF845}";
    private const int DismLogErrorsWarnings = 1;
    private const int PackageNone = 0;
    private static readonly object Gate = new();

    public bool TouchesRealSystem => true;

    public OptionalFeatureState Query(string name, bool capability) => WithSession(null, session =>
    {
        IntPtr info;
        var hr = capability ? DismGetCapabilityInfo(session, name, out info) : DismGetFeatureInfo(session, name, null, PackageNone, out info);
        if (hr < 0 || info == IntPtr.Zero) return OptionalFeatureState.Missing;
        try
        {
            // Both structs start with a name pointer followed by DismPackageFeatureState (DISM packs its structs).
            var state = Marshal.ReadInt32(info, IntPtr.Size);
            return state is 4 or 5 or 7 ? OptionalFeatureState.Enabled : OptionalFeatureState.Disabled; // Installed, InstallPending, PartiallyInstalled
        }
        finally { DismDelete(info); }
    });

    public int Remove(string name, bool capability, string? logPath) => WithSession(logPath, session => capability
        ? DismRemoveCapability(session, name, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero)
        : DismDisableFeature(session, name, null, false, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));

    public int EnableFeature(string name, string? logPath) => WithSession(logPath, session =>
        DismEnableFeature(session, name, null, PackageNone, true, IntPtr.Zero, 0, false, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));

    private static T WithSession<T>(string? logPath, Func<uint, T> action)
    {
        lock (Gate)
        {
            if (logPath is not null) Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            var hr = DismInitialize(DismLogErrorsWarnings, logPath, null);
            if (hr < 0) throw new COMException("DISM couldn't start.", hr);
            try
            {
                hr = DismOpenSession(OnlineImage, null, null, out var session);
                if (hr < 0) throw new COMException("DISM couldn't open Windows.", hr);
                try { return action(session); }
                finally { DismCloseSession(session); }
            }
            finally { DismShutdown(); }
        }
    }

    private const string Dll = "dismapi.dll";
    [DllImport(Dll, CharSet = CharSet.Unicode), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DismInitialize(int logLevel, string? logFilePath, string? scratchDirectory);
    [DllImport(Dll), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int DismShutdown();
    [DllImport(Dll, CharSet = CharSet.Unicode), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DismOpenSession(string imagePath, string? windowsDirectory, string? systemDrive, out uint session);
    [DllImport(Dll), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int DismCloseSession(uint session);
    [DllImport(Dll), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int DismDelete(IntPtr structure);
    [DllImport(Dll, CharSet = CharSet.Unicode), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DismGetFeatureInfo(uint session, string featureName, string? identifier, int packageIdentifier, out IntPtr featureInfo);
    [DllImport(Dll, CharSet = CharSet.Unicode), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DismGetCapabilityInfo(uint session, string name, out IntPtr info);
    [DllImport(Dll, CharSet = CharSet.Unicode), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DismDisableFeature(uint session, string featureName, string? packageName, [MarshalAs(UnmanagedType.Bool)] bool removePayload,
        IntPtr cancelEvent, IntPtr progress, IntPtr userData);
    [DllImport(Dll, CharSet = CharSet.Unicode), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DismEnableFeature(uint session, string featureName, string? identifier, int packageIdentifier, [MarshalAs(UnmanagedType.Bool)] bool limitAccess,
        IntPtr sourcePaths, uint sourcePathCount, [MarshalAs(UnmanagedType.Bool)] bool enableAll, IntPtr cancelEvent, IntPtr progress, IntPtr userData);
    [DllImport(Dll, CharSet = CharSet.Unicode), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DismRemoveCapability(uint session, string name, IntPtr cancelEvent, IntPtr progress, IntPtr userData);
}

/// <summary>
/// OptionalFeatureRemove. Args: "name", "kind" = feature | capability. Uses the DISM API (never DISM's text output).
/// A feature is turned off with its files kept (DismDisableFeature, no payload removal), so undo turns it back on
/// (DismEnableFeature, no download). A capability is removed (DismRemoveCapability); bringing it back needs a download
/// from Windows Update, so PC Tidy records how to re-add it and says so honestly instead of claiming an undo.
/// Features Windows security or common apps depend on are never removed.
/// </summary>
public sealed partial class OptionalFeatureHandler(IOptionalFeatureControl features) : SystemActionHandler
{
    internal const string ReAddData = "reAdd";
    internal const int RestartRequired = 3010;

    /// <summary>Same list as the SafetyGuard (a trailing * matches a prefix).</summary>
    private static readonly string[] NeverRemove = { "Windows-Defender*", "NetFx4*", "Microsoft-Windows-Client-Content*", "Windows-Identity-Foundation" };

    [GeneratedRegex(@"^[A-Za-z0-9_.~\-+]{1,256}$", RegexOptions.CultureInvariant)] private static partial Regex NamePattern();

    public OptionalFeatureHandler() : this(new DismOptionalFeatureControl()) { }

    protected override ActionKind Kind => ActionKind.OptionalFeatureRemove;
    protected override bool TouchesRealSystem => features.TouchesRealSystem;
    protected override bool NeedsElevation(PlanItem item) => true;

    private static string Name(PlanItem item) => item.Arg("name").Trim();
    private static bool IsCapability(PlanItem item) => item.Arg("kind").Trim() == "capability";

    protected override string? Validate(PlanItem item)
    {
        var name = Name(item);
        if (!NamePattern().IsMatch(name) || item.Arg("kind").Trim() is not ("feature" or "capability")) return "This step doesn't name a valid Windows feature.";
        if (NeverRemove.Any(p => p.EndsWith('*') ? name.StartsWith(p[..^1], StringComparison.OrdinalIgnoreCase) : name.Equals(p, StringComparison.OrdinalIgnoreCase)))
            return "Windows security or common apps need this feature, so PC Tidy never removes it.";
        return null;
    }

    protected override string Describe(PlanItem item) => IsCapability(item)
        ? $"Would remove the Windows capability {Name(item)} (DismRemoveCapability)"
        : $"Would turn off the Windows feature {Name(item)}, keeping its files (DismDisableFeature)";

    protected override bool CanUndo(StepRecord step) => !IsCapability(step.Item);
    protected override string DescribeUndo(StepRecord step) => $"Would turn the Windows feature {Name(step.Item)} back on (DismEnableFeature)";

    private static string Log(HandlerContext ctx, StepRecord step) => Path.Combine(DataPaths.Logs(ctx.Locations.DataRoot), "tools", $"{SafeRunId(ctx)}-{step.Index}-dism-api.log");

    protected override Task<StepRecord> RunAsync(StepRecord step, HandlerContext ctx, CancellationToken ct) => Task.Run(() =>
    {
        var name = Name(step.Item);
        var capability = IsCapability(step.Item);
        var before = features.Query(name, capability);
        if (before == OptionalFeatureState.Missing) return step.Skipped("This Windows feature isn't on this PC.");
        if (before == OptionalFeatureState.Disabled) return step.Skipped("This Windows feature is already off. Nothing to change.");

        var hr = features.Remove(name, capability, Log(ctx, step));
        if (hr < 0) return step.Failed($"Windows couldn't remove this feature (code 0x{hr:X8}). Details are in the log.");
        if (hr != RestartRequired && features.Query(name, capability) == OptionalFeatureState.Enabled) return step.Failed("Windows didn't turn this feature off.");
        var restart = hr == RestartRequired ? " Restart your PC to finish." : "";
        var reAdd = capability ? $"capability:{name}" : $"feature:{name}";
        return (capability
            ? step.Done($"Removed {name}.{restart} To add it back, use Settings > Apps > Optional features (Windows downloads it again).")
            : step.Done($"Turned off {name}.{restart} Its files are kept, so undo can turn it back on."))
            .WithUndo((ReAddData, reAdd));
    }, ct);

    protected override Task<StepRecord> RevertAsync(StepRecord step, HandlerContext ctx, CancellationToken ct) => Task.Run(() =>
    {
        var name = Name(step.Item);
        if (IsCapability(step.Item))
            return step.Skipped($"PC Tidy can't add {name} back itself. Use Settings > Apps > Optional features; Windows downloads it again.");
        if (features.Query(name, false) == OptionalFeatureState.Enabled) return step.Skipped("This Windows feature is on again already.");
        var hr = features.EnableFeature(name, Log(ctx, step));
        if (hr < 0) return step.UndoFailed($"Windows couldn't turn this feature back on (code 0x{hr:X8}).");
        return step.Undone(hr == RestartRequired ? $"Turned {name} back on. Restart your PC to finish." : $"Turned {name} back on.");
    }, ct);

    protected override Task<bool> CheckCompletedAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
        => Task.FromResult(features.Query(Name(step.Item), IsCapability(step.Item)) == OptionalFeatureState.Disabled);
}
