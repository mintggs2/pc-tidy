using PcTidy.Core.Model;

namespace PcTidy.Platform;

/// <summary>
/// RegistrySetValue / RegistryDeleteValue / RegistryDeleteKey.
/// Before any change it records the exact prior state (Tweaks rule 1: back up this PC's real value, not a default);
/// key deletes are exported to a .reg file first. After a write it reads the value back and fails (and puts the old
/// value back) if Windows didn't keep it. HKLM needs the elevated helper.
/// In the sandbox only HKCU\Software\PCTidyTest is really written; everything else is simulated.
/// </summary>
public sealed class RegistryHandler : IActionHandler
{
    private const string CreatedKeyData = "createdKey";

    /// <summary>Whole-key deletes are refused at or under these (shared/system trees). The SafetyGuard decides the rest.</summary>
    private static readonly (RegistryHive Hive, string Prefix)[] NeverDeleteKeysUnder =
    {
        (RegistryHive.LocalMachine, "SYSTEM"),
        (RegistryHive.LocalMachine, @"SOFTWARE\Classes"),
        (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer"),
        (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs"),
        (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion"),
        (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Classes"),
        (RegistryHive.CurrentUser, @"Software\Classes"),
        (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Installer"),
    };

    internal const string PriorVisualEffectsData = "priorVisualEffects";

    private readonly IVisualEffectsApi _visualEffects;
    private readonly string _visualFxKey;

    public RegistryHandler() : this(new WindowsVisualEffectsApi(), VisualEffects.Key) { }

    /// <param name="visualEffects">SystemParametersInfo (tests: a fake).</param>
    /// <param name="visualFxKey">The HKCU key holding VisualFXSetting (tests: a key under Software\PCTidyTest).</param>
    internal RegistryHandler(IVisualEffectsApi visualEffects, string visualFxKey)
    {
        _visualEffects = visualEffects;
        _visualFxKey = visualFxKey;
    }

    public bool CanHandle(PlanItem item)
        => item.Kind is ActionKind.RegistrySetValue or ActionKind.RegistryDeleteValue or ActionKind.RegistryDeleteKey;

    public async Task<StepRecord> ExecuteAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        if (ctx.Mode == ExecutionMode.Simulate) return await SimulateAsync(step, ctx, ct).ConfigureAwait(false);
        if (!TryGetSpec(step.Item, out var spec, out var problem)) return step.Failed(problem);
        if (ctx.Locations.IsSandbox && !SandboxPolicy.IsTestRegistryKey(spec.Hive, spec.KeyPath))
            return step.SandboxSimulated(Describe(step.Item.Kind, spec));
        if (spec.Hive == RegistryHive.LocalMachine && !ctx.IsElevated())
            return step.Skipped("This setting needs admin rights. PC Tidy runs it in its admin helper.");

        try
        {
            var result = step.Item.Kind switch
            {
                ActionKind.RegistrySetValue => SetValue(step, spec),
                ActionKind.RegistryDeleteValue => DeleteValue(step, spec),
                _ => await DeleteKeyAsync(step, spec, ctx, ct).ConfigureAwait(false),
            };
            return ApplyVisualEffects(result, spec, ctx);
        }
        catch (UnsupportedRegistryValueException ex) { return step.Skipped(ex.Message); }
        catch (FormatException ex) { return step.Failed(ex.Message); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return step.Skipped("Windows didn't allow this change (access denied). Nothing was changed.");
        }
        catch (IOException ex) { return step.Failed("The registry couldn't be changed: " + ex.Message); }
    }

    public Task<StepRecord> SimulateAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        if (!TryGetSpec(step.Item, out var spec, out var problem)) return Task.FromResult(step.Failed(problem));
        var current = "";
        try
        {
            if (step.Item.Kind != ActionKind.RegistryDeleteKey && spec.ValueName is { } name)
                current = $" Now: {RegistryAccess.Describe(RegistryAccess.Capture(spec.Hive, spec.View64, spec.KeyPath, name))}.";
        }
        catch
        {
            // Reading is only for the description.
        }
        return Task.FromResult(step.Simulated(Describe(step.Item.Kind, spec) + "." + current));
    }

    public async Task<StepRecord> UndoAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        if (step.Status != StepStatus.Done) return step.Skipped("Nothing to undo.");
        if (!TryGetSpec(step.Item, out var spec, out var problem)) return step.UndoFailed(problem);
        if (step.RegistryPrior is not { } prior) return step.UndoFailed("The saved original value is missing, so PC Tidy can't undo this.");
        // Undo never switches protection off again, whatever the saved data says (ELV-1, UNDO-1): a Defender policy or a
        // Windows Update pause/block policy that a repair removed is not put back.
        if ((step.Item.Kind == ActionKind.RegistryDeleteKey || prior.ValueExisted)
            && PcTidy.Catalog.Tweaks.ProtectionPolicyValues.WhyNeverRestore(spec.Hive, spec.KeyPath, spec.ValueName) is { } never)
            return step.UndoFailed(never + " Set it yourself if you really need it.");
        if (ctx.Locations.IsSandbox && !SandboxPolicy.IsTestRegistryKey(spec.Hive, spec.KeyPath))
            return step.SandboxSimulated("Would put the original value back");
        if (spec.Hive == RegistryHive.LocalMachine && !ctx.IsElevated())
            return step.UndoFailed("Undoing this needs admin rights. PC Tidy runs it in its admin helper.");

        try
        {
            if (step.Item.Kind == ActionKind.RegistryDeleteKey)
                return await RestoreKeyAsync(step, spec, prior, ctx, ct).ConfigureAwait(false);

            var name = spec.ValueName!;
            var createdKey = step.UndoData is { } u && u.TryGetValue(CreatedKeyData, out var c) ? c : null;
            RegistryAccess.Restore(spec.Hive, spec.View64, spec.KeyPath, name, prior, createdKey);
            var now = RegistryAccess.Capture(spec.Hive, spec.View64, spec.KeyPath, name);
            var restored = prior.ValueExisted ? RegistryAccess.Matches(now, prior.Kind!.Value, prior.Data) : !now.ValueExisted;
            if (restored) RestoreVisualEffects(step, ctx);
            return restored
                ? step.Undone($"Put back the original value ({RegistryAccess.Describe(prior)}).")
                : step.UndoFailed("Windows didn't keep the original value.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or FormatException or UnsupportedRegistryValueException)
        {
            return step.UndoFailed("The original value couldn't be put back: " + ex.Message);
        }
    }

    public Task<bool> WasCompletedAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        if (!TryGetSpec(step.Item, out var spec, out _)) return Task.FromResult(false);
        try
        {
            var done = step.Item.Kind switch
            {
                ActionKind.RegistrySetValue => RegistryAccess.Matches(RegistryAccess.Capture(spec.Hive, spec.View64, spec.KeyPath, spec.ValueName!), spec.Kind, spec.Data),
                ActionKind.RegistryDeleteValue => !RegistryAccess.Capture(spec.Hive, spec.View64, spec.KeyPath, spec.ValueName!).ValueExisted,
                _ => !RegistryAccess.KeyExists(spec.Hive, spec.View64, spec.KeyPath),
            };
            return Task.FromResult(done);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    // ------------------------------------------------------------------ visual effects

    /// <summary>
    /// After VisualFXSetting changed: record the current effect switches, then apply the ones that value stands for, so
    /// the change shows now (not at the next sign-in) and UserPreferencesMask stays in step.
    /// </summary>
    private StepRecord ApplyVisualEffects(StepRecord result, RegistryValueSpec spec, HandlerContext ctx)
    {
        if (result.Status != StepStatus.Done || result.Item.Kind != ActionKind.RegistrySetValue || !VisualEffects.IsVisualFxValue(spec, _visualFxKey)) return result;
        if (ctx.Locations.IsSandbox && _visualEffects.TouchesRealSystem) return result;
        if (VisualEffects.TargetFor(spec.Data) is not { } target) return result;
        var before = _visualEffects.Read();
        _visualEffects.Apply(target);
        return result.WithUndo((PriorVisualEffectsData, System.Text.Json.JsonSerializer.Serialize(before)));
    }

    private void RestoreVisualEffects(StepRecord step, HandlerContext ctx)
    {
        if (step.Undo(PriorVisualEffectsData) is not { } json || (ctx.Locations.IsSandbox && _visualEffects.TouchesRealSystem)) return;
        try
        {
            if (System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, bool>>(json) is { } prior) _visualEffects.Apply(prior);
        }
        catch (System.Text.Json.JsonException) { /* the registry value is back; the switches follow at the next sign-in */ }
    }

    // ------------------------------------------------------------------ operations

    private static StepRecord SetValue(StepRecord step, RegistryValueSpec spec)
    {
        var name = spec.ValueName!;
        RegistryValueCodec.Parse(spec.Kind, spec.Data); // validate before touching anything
        var prior = RegistryAccess.Capture(spec.Hive, spec.View64, spec.KeyPath, name);
        if (RegistryAccess.Matches(prior, spec.Kind, spec.Data))
            return step.Skipped("Already set this way. Nothing to change.");

        var createdKey = prior.KeyExisted ? null : RegistryAccess.FirstMissingAncestor(spec.Hive, spec.View64, spec.KeyPath);
        RegistryAccess.SetValue(spec.Hive, spec.View64, spec.KeyPath, name, spec.Kind, spec.Data);

        var after = RegistryAccess.Capture(spec.Hive, spec.View64, spec.KeyPath, name);
        if (!RegistryAccess.Matches(after, spec.Kind, spec.Data))
        {
            // Some values are protected (e.g. by Windows' user-choice protection): put things back as they were.
            RegistryAccess.Restore(spec.Hive, spec.View64, spec.KeyPath, name, prior, createdKey);
            return step.Failed("Windows didn't keep this change (the setting may be protected). It was put back as it was.") with { RegistryPrior = prior };
        }

        return step.Done($"Set {ValueLabel(spec)} to {RegistryAccess.Short(RegistryValueCodec.Normalize(spec.Kind, spec.Data))} (was {RegistryAccess.Describe(prior)}).") with
        {
            RegistryPrior = prior,
            UndoData = createdKey is null ? step.UndoData : Merge(step.UndoData, CreatedKeyData, createdKey),
        };
    }

    private static StepRecord DeleteValue(StepRecord step, RegistryValueSpec spec)
    {
        var name = spec.ValueName!;
        var prior = RegistryAccess.Capture(spec.Hive, spec.View64, spec.KeyPath, name);
        if (!prior.ValueExisted) return step.Skipped("Already gone. Nothing to change.");

        RegistryAccess.DeleteValue(spec.Hive, spec.View64, spec.KeyPath, name);
        if (RegistryAccess.Capture(spec.Hive, spec.View64, spec.KeyPath, name).ValueExisted)
            return step.Failed("Windows didn't remove this value.") with { RegistryPrior = prior };
        return step.Done($"Removed {ValueLabel(spec)} (was {RegistryAccess.Describe(prior)}).") with { RegistryPrior = prior };
    }

    private static async Task<StepRecord> DeleteKeyAsync(StepRecord step, RegistryValueSpec spec, HandlerContext ctx, CancellationToken ct)
    {
        if (IsNeverDeleted(spec.Hive, spec.KeyPath, out var why)) return step.Skipped(why);
        if (!RegistryAccess.KeyExists(spec.Hive, spec.View64, spec.KeyPath)) return step.Skipped("Already gone. Nothing to change.");

        var backup = RegFileBackup.PathFor(ctx.Locations.DataRoot, ctx.RunId, step.Index);
        if (await RegFileBackup.ExportAsync(spec.Hive, spec.View64, spec.KeyPath, backup, ct).ConfigureAwait(false) is { } exportProblem)
            return step.Failed(exportProblem);

        using (var root = RegistryAccess.OpenBase(spec.Hive, spec.View64))
            root.DeleteSubKeyTree(spec.KeyPath, throwOnMissingSubKey: false);

        var prior = new RegistryPriorState { KeyExisted = true, RegExportPath = backup };
        if (RegistryAccess.KeyExists(spec.Hive, spec.View64, spec.KeyPath))
            return step.Failed("Windows didn't remove the whole key. A backup was saved.") with { RegistryPrior = prior };
        return step.Done($"Removed {KeyLabel(spec)}. A backup was saved first.") with { RegistryPrior = prior };
    }

    private static async Task<StepRecord> RestoreKeyAsync(StepRecord step, RegistryValueSpec spec, RegistryPriorState prior, HandlerContext ctx, CancellationToken ct)
    {
        var file = prior.RegExportPath;
        if (string.IsNullOrEmpty(file) || !PathUtil.IsUnder(file, RegFileBackup.BackupFolder(ctx.Locations.DataRoot)))
            return step.UndoFailed("The registry backup isn't in PC Tidy's backup folder, so it wasn't used.");
        var privateFolder = Path.Combine(RegFileBackup.BackupFolder(ctx.Locations.DataRoot), "import");
        if (await RegFileBackup.ImportVerifiedCopyAsync(file, privateFolder, spec.Hive, spec.KeyPath, spec.View64, ct).ConfigureAwait(false) is { } importProblem)
            return step.UndoFailed(importProblem);
        return RegistryAccess.KeyExists(spec.Hive, spec.View64, spec.KeyPath)
            ? step.Undone($"Put back {KeyLabel(spec)} from its backup.")
            : step.UndoFailed("Windows didn't put the registry key back.");
    }

    // ------------------------------------------------------------------ validation & text

    internal static bool TryGetSpec(PlanItem item, out RegistryValueSpec spec, out string problem)
    {
        spec = null!;
        problem = "";
        if (item.Registry is not { } r) { problem = "This step is missing its registry details."; return false; }
        var path = RegistryAccess.CleanKeyPath(r.KeyPath ?? "");
        if (path.Length == 0 || path.StartsWith("HKEY_", StringComparison.OrdinalIgnoreCase) || path.StartsWith("HK", StringComparison.OrdinalIgnoreCase) && path.IndexOf('\\') is 4)
        {
            problem = "The registry key path is not valid.";
            return false;
        }
        if (item.Kind != ActionKind.RegistryDeleteKey && r.ValueName is null) { problem = "This step is missing the value name."; return false; }
        spec = r with { KeyPath = path };
        return true;
    }

    internal static bool IsNeverDeleted(RegistryHive hive, string keyPath, out string why)
    {
        why = "";
        if (keyPath.Split('\\', StringSplitOptions.RemoveEmptyEntries).Length < 3)
        {
            why = "PC Tidy never deletes top-level registry keys.";
            return true;
        }
        foreach (var (h, prefix) in NeverDeleteKeysUnder)
        {
            if (h == hive && (keyPath.Equals(prefix, StringComparison.OrdinalIgnoreCase) || keyPath.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase)))
            {
                why = "This registry area is shared by Windows and other apps, so PC Tidy never deletes keys there.";
                return true;
            }
        }
        return false;
    }

    private static string Describe(ActionKind kind, RegistryValueSpec spec) => kind switch
    {
        ActionKind.RegistrySetValue => $"Would set {ValueLabel(spec)} to {RegistryAccess.Short(spec.Data)}",
        ActionKind.RegistryDeleteValue => $"Would remove {ValueLabel(spec)}",
        _ => $"Would back up and remove {KeyLabel(spec)}",
    };

    private static string KeyLabel(RegistryValueSpec spec) => $@"{RegistryAccess.HiveShort(spec.Hive)}\{spec.KeyPath}";

    private static string ValueLabel(RegistryValueSpec spec)
        => $"{KeyLabel(spec)} → {(string.IsNullOrEmpty(spec.ValueName) ? "(default)" : spec.ValueName)}";

    private static Dictionary<string, string> Merge(Dictionary<string, string>? existing, string key, string value)
    {
        var d = existing is null ? new Dictionary<string, string>() : new Dictionary<string, string>(existing);
        d[key] = value;
        return d;
    }
}
