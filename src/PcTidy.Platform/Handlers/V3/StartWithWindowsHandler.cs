using PcTidy.Core.Model;
using RegistryHive = PcTidy.Core.Model.RegistryHive;

namespace PcTidy.Platform;

/// <summary>
/// StartWithWindowsSet. Enable = true: HKCU Run value "PC Tidy" = <c>"&lt;PcTidy.exe&gt;" --tray</c>; Enable = false: that
/// value is removed. Only this one value is ever written. Task Manager's StartupApproved switch is respected, never
/// overwritten: if you turned PC Tidy off there, the step says so instead of switching it back on behind your back.
/// The exact prior value is recorded (<see cref="StepRecord.RegistryPrior"/>) and undo restores it. Never from the admin helper.
/// </summary>
public sealed class StartWithWindowsHandler : SystemActionHandler
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public const string ValueName = "PC Tidy";
    public const string TraySwitch = "--tray";

    private readonly string _runKey;
    private readonly string _approvedKey;
    private readonly string? _appPath;

    public StartWithWindowsHandler() : this(RunKey, ApprovedKey, Environment.ProcessPath) { }

    /// <param name="runKey">HKCU Run key (tests: under Software\PCTidyTest).</param>
    /// <param name="approvedKey">HKCU StartupApproved\Run key (tests: under Software\PCTidyTest).</param>
    /// <param name="appPath">PC Tidy's own program file.</param>
    public StartWithWindowsHandler(string runKey, string approvedKey, string? appPath)
    {
        _runKey = runKey.Trim('\\');
        _approvedKey = approvedKey.Trim('\\');
        _appPath = appPath;
    }

    protected override ActionKind Kind => ActionKind.StartWithWindowsSet;
    protected override bool TouchesRealSystem => !SandboxPolicy.IsTestRegistryKey(RegistryHive.CurrentUser, _runKey);
    protected override bool PerUserOnly(PlanItem item) => true;

    /// <summary>The exact command line written to the Run value.</summary>
    internal string Command => $"\"{_appPath}\" {TraySwitch}";

    protected override string? Validate(PlanItem item)
    {
        if (item.Enable is null) return "This step doesn't say whether PC Tidy should start with Windows.";
        if (item.Enable == true && (!PathArgs.IsPlainLocal(_appPath) || !_appPath!.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
            return "PC Tidy couldn't find its own program file.";
        return null;
    }

    protected override string Describe(PlanItem item) => item.Enable == true
        ? $@"Would set HKCU\{_runKey} → {ValueName} = {Command}"
        : $@"Would remove HKCU\{_runKey} → {ValueName}";

    protected override bool CanUndo(StepRecord step) => step.RegistryPrior is not null;
    protected override string DescribeUndo(StepRecord step) => $@"Would put HKCU\{_runKey} → {ValueName} back as it was";

    protected override Task<StepRecord> RunAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var prior = RegistryAccess.Capture(RegistryHive.CurrentUser, true, _runKey, ValueName);
        if (step.Item.Enable == true)
        {
            if (RegistryAccess.Matches(prior, RegistryKind.String, Command)) return Task.FromResult(step.Skipped("PC Tidy already starts with Windows." + ApprovedNote()));
            RegistryAccess.SetValue(RegistryHive.CurrentUser, true, _runKey, ValueName, RegistryKind.String, Command);
            if (!RegistryAccess.Matches(RegistryAccess.Capture(RegistryHive.CurrentUser, true, _runKey, ValueName), RegistryKind.String, Command))
            {
                RegistryAccess.Restore(RegistryHive.CurrentUser, true, _runKey, ValueName, prior, null);
                return Task.FromResult(step.Failed("Windows didn't keep this setting. It was put back as it was."));
            }
            return Task.FromResult(step.Done("PC Tidy now starts with Windows (in the tray)." + ApprovedNote()) with { RegistryPrior = prior });
        }

        if (!prior.ValueExisted) return Task.FromResult(step.Skipped("PC Tidy doesn't start with Windows. Nothing to change."));
        RegistryAccess.DeleteValue(RegistryHive.CurrentUser, true, _runKey, ValueName);
        return Task.FromResult(RegistryAccess.Capture(RegistryHive.CurrentUser, true, _runKey, ValueName).ValueExisted
            ? step.Failed("Windows didn't remove this setting.")
            : step.Done("PC Tidy no longer starts with Windows.") with { RegistryPrior = prior });
    }

    protected override Task<StepRecord> RevertAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        RegistryAccess.Restore(RegistryHive.CurrentUser, true, _runKey, ValueName, step.RegistryPrior!, null);
        return Task.FromResult(step.Undone(step.RegistryPrior!.ValueExisted ? "Put the start-with-Windows setting back as it was." : "Removed the start-with-Windows setting again."));
    }

    protected override Task<bool> CheckCompletedAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var now = RegistryAccess.Capture(RegistryHive.CurrentUser, true, _runKey, ValueName);
        return Task.FromResult(step.Item.Enable == true ? RegistryAccess.Matches(now, RegistryKind.String, Command) : !now.ValueExisted);
    }

    /// <summary>Task Manager's switch: 12 bytes, first byte odd (03) = turned off by the user.</summary>
    private string ApprovedNote()
    {
        try
        {
            var approved = RegistryAccess.Capture(RegistryHive.CurrentUser, true, _approvedKey, ValueName);
            return approved is { ValueExisted: true, Kind: RegistryKind.Binary, Data: { Length: >= 2 } hex } && hex.StartsWith("03", StringComparison.Ordinal)
                ? " Task Manager has PC Tidy turned off at startup; switch it on there (Startup apps) if you want it."
                : "";
        }
        catch (UnsupportedRegistryValueException) { return ""; }
    }
}
