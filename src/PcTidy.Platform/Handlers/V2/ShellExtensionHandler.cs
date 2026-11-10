using PcTidy.Core.Model;
using RegistryHive = PcTidy.Core.Model.RegistryHive;

namespace PcTidy.Platform;

/// <summary>
/// ShellExtensionSetEnabled: hides (Enable = false) or shows again (Enable = true) a right-click menu entry without
/// deleting anything, the way Windows itself supports it:
/// <list type="bullet">
/// <item>Args "clsid": a shell extension, listed by CLSID in <c>HKCU\...\Shell Extensions\Blocked</c> (value name = CLSID).</item>
/// <item>Args "verbKey" (+ "hive" HKCU|HKLM): a static verb under <c>Software\Classes\...\shell\&lt;verb&gt;</c>,
/// hidden with an empty <c>LegacyDisable</c> value. HKLM needs the admin helper.</item>
/// </list>
/// The exact prior value is recorded (<see cref="StepRecord.RegistryPrior"/>) and undo puts it back exactly.
/// </summary>
public sealed class ShellExtensionHandler : SystemActionHandler
{
    public const string BlockedKey = @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";
    internal const string LegacyDisable = "LegacyDisable";

    private readonly string _blockedKey;
    private readonly string _classesPrefix;

    public ShellExtensionHandler() : this(BlockedKey, @"Software\Classes") { }

    /// <param name="blockedKey">HKCU key used as the Blocked list (tests: under Software\PCTidyTest).</param>
    /// <param name="classesPrefix">Required start of every verb key (tests: a key under Software\PCTidyTest).</param>
    public ShellExtensionHandler(string blockedKey, string classesPrefix)
    {
        _blockedKey = blockedKey.Trim('\\');
        _classesPrefix = classesPrefix.Trim('\\');
    }

    protected override ActionKind Kind => ActionKind.ShellExtensionSetEnabled;
    protected override bool TouchesRealSystem
        => !(SandboxPolicy.IsTestRegistryKey(RegistryHive.CurrentUser, _blockedKey) && SandboxPolicy.IsTestRegistryKey(RegistryHive.CurrentUser, _classesPrefix));
    protected override bool NeedsElevation(PlanItem item) => Hive(item) == RegistryHive.LocalMachine;
    protected override bool PerUserOnly(PlanItem item) => Hive(item) == RegistryHive.CurrentUser;

    private static bool IsClsid(PlanItem item) => item.Arg("clsid").Trim().Length > 0;

    private static RegistryHive Hive(PlanItem item)
        => !IsClsid(item) && item.Arg("hive").Trim().Equals("HKLM", StringComparison.OrdinalIgnoreCase) ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;

    /// <summary>The key and value this item changes.</summary>
    internal (RegistryHive Hive, string Key, string Value) Target(PlanItem item)
        => IsClsid(item)
            ? (RegistryHive.CurrentUser, _blockedKey, Guid.Parse(item.Arg("clsid").Trim()).ToString("B").ToUpperInvariant())
            : (Hive(item), item.Arg("verbKey").Trim().Trim('\\'), LegacyDisable);

    protected override string? Validate(PlanItem item)
    {
        if (item.Enable is null) return "This step doesn't say whether to show or hide the menu entry.";
        if (IsClsid(item))
            return Guid.TryParse(item.Arg("clsid").Trim(), out var g) && g != Guid.Empty ? null : "The shell extension's id is not valid.";
        var hive = item.Arg("hive").Trim();
        if (hive.Length > 0 && !hive.Equals("HKCU", StringComparison.OrdinalIgnoreCase) && !hive.Equals("HKLM", StringComparison.OrdinalIgnoreCase))
            return "The menu entry's registry area is not valid.";
        return VerbKeyProblem(item.Arg("verbKey").Trim().Trim('\\'));
    }

    /// <summary>A verb key must be Software\Classes\...\shell\&lt;verb&gt; with ordinary segments.</summary>
    private string? VerbKeyProblem(string key)
    {
        const string bad = "PC Tidy only hides right-click entries under Software\\Classes\\...\\shell.";
        if (key.Length == 0 || key.Length > 512 || key.Any(char.IsControl) || key.Contains('/')) return bad;
        if (!key.StartsWith(_classesPrefix + "\\", StringComparison.OrdinalIgnoreCase)) return bad;
        var parts = key.Split('\\');
        if (parts.Any(p => p.Trim().Length == 0 || p is "." or "..")) return bad;
        if (parts.Length < 4 || !parts[^2].Equals("shell", StringComparison.OrdinalIgnoreCase)) return bad;
        return null;
    }

    protected override string Describe(PlanItem item)
    {
        var (hive, key, value) = Target(item);
        var where = $@"{RegistryAccess.HiveShort(hive)}\{key}";
        return IsClsid(item)
            ? item.Enable == true ? $"Would remove {value} from {where}" : $"Would add {value} to {where}"
            : item.Enable == true ? $"Would remove {LegacyDisable} from {where}" : $"Would set an empty {LegacyDisable} on {where}";
    }

    protected override bool CanUndo(StepRecord step) => step.RegistryPrior is not null;
    protected override string DescribeUndo(StepRecord step) => "Would put the menu entry back as it was";

    protected override Task<StepRecord> RunAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var (hive, key, value) = Target(step.Item);
        if (ctx.Locations.IsSandbox && !SandboxPolicy.IsTestRegistryKey(hive, key)) return Task.FromResult(step.SandboxSimulated(Describe(step.Item)));
        if (!IsClsid(step.Item) && !RegistryAccess.KeyExists(hive, true, key)) return Task.FromResult(step.Skipped("This menu entry isn't on this PC."));

        var prior = RegistryAccess.Capture(hive, true, key, value);
        var hide = step.Item.Enable == false;
        if (hide == prior.ValueExisted) return Task.FromResult(step.Skipped(hide ? "Already hidden. Nothing to change." : "Already shown. Nothing to change."));

        var createdKey = hide && !prior.KeyExisted ? RegistryAccess.FirstMissingAncestor(hive, true, key) : null;
        var data = IsClsid(step.Item) ? Clean(step.Item.Arg("name")) : "";
        if (hide) RegistryAccess.SetValue(hive, true, key, value, RegistryKind.String, data);
        else RegistryAccess.DeleteValue(hive, true, key, value);

        var now = RegistryAccess.Capture(hive, true, key, value);
        if (now.ValueExisted != hide)
        {
            RegistryAccess.Restore(hive, true, key, value, prior, createdKey);
            return Task.FromResult(step.Failed("Windows didn't keep this change. It was put back as it was."));
        }
        var done = step.Done(hide ? "Hid the right-click entry (not deleted). Explorer picks this up the next time it opens a menu." : "Showed the right-click entry again.")
            with { RegistryPrior = prior };
        return Task.FromResult(createdKey is null ? done : done.WithUndo(("createdKey", createdKey)));
    }

    protected override Task<StepRecord> RevertAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        if (Validate(step.Item) is { } problem) return Task.FromResult(step.UndoFailed(problem));
        var (hive, key, value) = Target(step.Item);
        if (ctx.Locations.IsSandbox && !SandboxPolicy.IsTestRegistryKey(hive, key)) return Task.FromResult(step.SandboxSimulated(DescribeUndo(step)));
        var created = step.Undo("createdKey");
        RegistryAccess.Restore(hive, true, key, value, step.RegistryPrior!, created is not null && (key.Equals(created, StringComparison.OrdinalIgnoreCase) || key.StartsWith(created + "\\", StringComparison.OrdinalIgnoreCase)) ? created : null);
        return Task.FromResult(step.Undone("Put the right-click entry back as it was."));
    }

    protected override Task<bool> CheckCompletedAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var (hive, key, value) = Target(step.Item);
        return Task.FromResult(RegistryAccess.Capture(hive, true, key, value).ValueExisted == (step.Item.Enable == false));
    }

    private static string Clean(string name) => new string(name.Where(c => !char.IsControl(c)).Take(200).ToArray());
}
