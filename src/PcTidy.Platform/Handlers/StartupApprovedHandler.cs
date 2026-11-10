using PcTidy.Core.Model;

namespace PcTidy.Platform;

/// <summary>The 12-byte StartupApproved value Task Manager and Settings use to switch a startup item on or off.</summary>
public static class StartupApprovedValue
{
    public const int Length = 12;

    /// <summary>02 00 00 00 + 8 zero bytes.</summary>
    public static byte[] Enabled() => new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

    /// <summary>03 00 00 00 + the FILETIME it was turned off.</summary>
    public static byte[] Disabled(DateTime whenUtc)
    {
        var bytes = new byte[Length];
        bytes[0] = 0x03;
        BitConverter.GetBytes(whenUtc.ToFileTimeUtc()).CopyTo(bytes, 4);
        return bytes;
    }

    /// <summary>
    /// Windows treats an even first byte (02, 06) as on and an odd one (01, 03, 07) as off.
    /// No value at all means on: the item has never been switched.
    /// </summary>
    public static bool IsEnabled(byte[]? value) => value is not { Length: > 0 } || (value[0] & 1) == 0;
}

/// <summary>
/// StartupItemSetEnabled: switches a Run / Run32 / Startup-folder entry on or off exactly like Task Manager,
/// via <c>...\Explorer\StartupApproved\&lt;location&gt;</c> in HKCU or HKLM. The entry itself is never deleted.
/// Args: "location" = Run | Run32 | StartupFolder, "name" = the entry name, optional "hive" = HKCU (default) | HKLM.
/// The exact prior value (or its absence) is recorded and put back on undo.
/// </summary>
public sealed class StartupApprovedHandler : IActionHandler
{
    public const string DefaultBaseKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
    private const string CreatedKeyData = "createdKey";
    private static readonly string[] Locations = { "Run", "Run32", "StartupFolder" };

    private readonly string _baseKeyPath;

    public StartupApprovedHandler() : this(DefaultBaseKeyPath) { }

    /// <summary>Tests pass a key under HKCU\Software\PCTidyTest.</summary>
    public StartupApprovedHandler(string baseKeyPath) => _baseKeyPath = RegistryAccess.CleanKeyPath(baseKeyPath);

    public bool CanHandle(PlanItem item) => item.Kind == ActionKind.StartupItemSetEnabled;

    internal sealed record Target(RegistryHive Hive, string KeyPath, string Name, bool Enable)
    {
        public string Label => $"'{Name}'";
    }

    internal bool TryGetTarget(PlanItem item, out Target target, out string problem)
    {
        target = null!;
        problem = "";
        var location = Locations.FirstOrDefault(l => l.Equals(item.Arg("location"), StringComparison.OrdinalIgnoreCase));
        if (location is null) { problem = "Unknown startup location."; return false; }
        var name = item.Arg("name");
        if (name.Length == 0 || name.Length > 260 || name.Any(char.IsControl)) { problem = "The startup item's name is not valid."; return false; }
        var hiveArg = item.Arg("hive");
        RegistryHive hive;
        if (hiveArg.Length == 0 || hiveArg.Equals("HKCU", StringComparison.OrdinalIgnoreCase)) hive = RegistryHive.CurrentUser;
        else if (hiveArg.Equals("HKLM", StringComparison.OrdinalIgnoreCase)) hive = RegistryHive.LocalMachine;
        else { problem = "Unknown startup hive."; return false; }
        if (item.Enable is not { } enable) { problem = "This step doesn't say whether to turn the item on or off."; return false; }
        target = new Target(hive, $@"{_baseKeyPath}\{location}", name, enable);
        return true;
    }

    public Task<StepRecord> ExecuteAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        if (ctx.Mode == ExecutionMode.Simulate) return SimulateAsync(step, ctx, ct);
        if (!TryGetTarget(step.Item, out var t, out var problem)) return Task.FromResult(step.Failed(problem));
        if (ctx.Locations.IsSandbox && !SandboxPolicy.IsTestRegistryKey(t.Hive, t.KeyPath))
            return Task.FromResult(step.SandboxSimulated(Describe(t)));
        if (t.Hive == RegistryHive.LocalMachine && !ctx.IsElevated())
            return Task.FromResult(step.Skipped("This startup item is for all users, so it needs admin rights. PC Tidy runs it in its admin helper."));

        try
        {
            return Task.FromResult(Apply(step, t));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return Task.FromResult(step.Skipped("Windows didn't allow this change (access denied). Nothing was changed."));
        }
        catch (Exception ex) when (ex is IOException or UnsupportedRegistryValueException)
        {
            return Task.FromResult(step.Failed("The startup setting couldn't be changed: " + ex.Message));
        }
    }

    private static StepRecord Apply(StepRecord step, Target t)
    {
        var prior = RegistryAccess.Capture(t.Hive, true, t.KeyPath, t.Name);
        if (prior.ValueExisted && prior.Kind != RegistryKind.Binary)
            return step.Skipped("This startup setting is in an unexpected format, so PC Tidy leaves it alone.");
        if (CurrentlyEnabled(prior) == t.Enable)
            return step.Skipped(t.Enable ? "Already on. Nothing to change." : "Already off. Nothing to change.");

        var bytes = t.Enable ? StartupApprovedValue.Enabled() : StartupApprovedValue.Disabled(DateTime.UtcNow);
        var data = Convert.ToHexString(bytes);
        var createdKey = prior.KeyExisted ? null : RegistryAccess.FirstMissingAncestor(t.Hive, true, t.KeyPath);
        RegistryAccess.SetValue(t.Hive, true, t.KeyPath, t.Name, RegistryKind.Binary, data);

        if (!RegistryAccess.Matches(RegistryAccess.Capture(t.Hive, true, t.KeyPath, t.Name), RegistryKind.Binary, data))
        {
            RegistryAccess.Restore(t.Hive, true, t.KeyPath, t.Name, prior, createdKey);
            return step.Failed("Windows didn't keep this change. It was put back as it was.") with { RegistryPrior = prior };
        }

        var undo = step.UndoData is null ? new Dictionary<string, string>() : new Dictionary<string, string>(step.UndoData);
        if (createdKey is not null) undo[CreatedKeyData] = createdKey;
        return step.Done(t.Enable ? $"Turned {t.Label} back on at startup." : $"Turned {t.Label} off at startup. It stays installed.") with
        {
            RegistryPrior = prior,
            UndoData = undo.Count == 0 ? null : undo,
        };
    }

    public Task<StepRecord> SimulateAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        if (!TryGetTarget(step.Item, out var t, out var problem)) return Task.FromResult(step.Failed(problem));
        return Task.FromResult(step.Simulated(Describe(t) + "."));
    }

    public Task<StepRecord> UndoAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        if (step.Status != StepStatus.Done) return Task.FromResult(step.Skipped("Nothing to undo."));
        if (!TryGetTarget(step.Item, out var t, out var problem)) return Task.FromResult(step.UndoFailed(problem));
        if (step.RegistryPrior is not { } prior) return Task.FromResult(step.UndoFailed("The saved original value is missing, so PC Tidy can't undo this."));
        if (ctx.Locations.IsSandbox && !SandboxPolicy.IsTestRegistryKey(t.Hive, t.KeyPath))
            return Task.FromResult(step.SandboxSimulated("Would put the startup setting back"));
        if (t.Hive == RegistryHive.LocalMachine && !ctx.IsElevated())
            return Task.FromResult(step.UndoFailed("Undoing this needs admin rights. PC Tidy runs it in its admin helper."));

        try
        {
            var createdKey = step.UndoData is { } u && u.TryGetValue(CreatedKeyData, out var c) ? c : null;
            RegistryAccess.Restore(t.Hive, true, t.KeyPath, t.Name, prior, createdKey);
            var now = RegistryAccess.Capture(t.Hive, true, t.KeyPath, t.Name);
            var ok = prior.ValueExisted ? RegistryAccess.Matches(now, RegistryKind.Binary, prior.Data) : !now.ValueExisted;
            return Task.FromResult(ok ? step.Undone("Put the startup setting back as it was.") : step.UndoFailed("Windows didn't keep the original startup setting."));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or FormatException or UnsupportedRegistryValueException)
        {
            return Task.FromResult(step.UndoFailed("The startup setting couldn't be put back: " + ex.Message));
        }
    }

    public Task<bool> WasCompletedAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        if (!TryGetTarget(step.Item, out var t, out _)) return Task.FromResult(false);
        try
        {
            return Task.FromResult(CurrentlyEnabled(RegistryAccess.Capture(t.Hive, true, t.KeyPath, t.Name)) == t.Enable);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    private static bool CurrentlyEnabled(RegistryPriorState state)
        => !state.ValueExisted || StartupApprovedValue.IsEnabled(Convert.FromHexString(state.Data ?? ""));

    private static string Describe(Target t) => t.Enable ? $"Would turn {t.Label} on at startup" : $"Would turn {t.Label} off at startup";
}
