using System.Text.Json;
using System.Text.RegularExpressions;
using PcTidy.Core.Model;
using RegistryHive = PcTidy.Core.Model.RegistryHive;

namespace PcTidy.Platform;

/// <summary>
/// ShellVerbSetInstalled: adds (Enable = true) or removes (Enable = false) one of PC Tidy's OWN right-click entries,
/// per user, under <c>HKCU\Software\Classes\&lt;on&gt;\shell\PCTidy.&lt;verb&gt;</c>. Nothing else is ever touched.
/// Args: "verb" ([A-Za-z0-9_-]{1,64}), "command" (a quoted absolute .exe path plus arguments; no shells or script hosts),
/// optional "label" (menu text) and "on" (Directory [default], Directory\Background, Drive, *, AllFilesystemObjects, or .ext).
/// The exact prior values are recorded, so undo puts back what was there (which may be nothing).
/// </summary>
public sealed partial class ShellVerbHandler : SystemActionHandler
{
    internal const string PriorData = "priorVerb";
    public const string VerbPrefix = "PCTidy.";

    private static readonly string[] ForbiddenHosts = { "cmd", "powershell", "pwsh", "wscript", "cscript", "mshta", "rundll32", "regsvr32", "bash", "wsl" };

    [GeneratedRegex(@"^[A-Za-z0-9_\-]{1,64}$", RegexOptions.CultureInvariant)] private static partial Regex VerbPattern();
    [GeneratedRegex("^\"(?<exe>[^\"]+)\"(?<rest> [^\\r\\n]*)?$", RegexOptions.CultureInvariant)] private static partial Regex CommandPattern();
    [GeneratedRegex(@"^(\*|Directory|Directory\\Background|Drive|AllFilesystemObjects|\.[A-Za-z0-9]{1,16})$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex OnPattern();

    private readonly string _classesKey;

    public ShellVerbHandler() : this(@"Software\Classes") { }

    /// <param name="classesKey">HKCU key that plays the role of Software\Classes (tests: a key under Software\PCTidyTest).</param>
    public ShellVerbHandler(string classesKey) => _classesKey = classesKey.Trim('\\');

    protected override ActionKind Kind => ActionKind.ShellVerbSetInstalled;
    protected override bool TouchesRealSystem => !SandboxPolicy.IsTestRegistryKey(RegistryHive.CurrentUser, _classesKey);
    protected override bool PerUserOnly(PlanItem item) => true;

    /// <summary>The verb's key path (under HKCU).</summary>
    internal string VerbKey(PlanItem item) => $@"{_classesKey}\{On(item)}\shell\{VerbPrefix}{item.Arg("verb").Trim()}";

    private static string On(PlanItem item) => item.Arg("on").Trim() is { Length: > 0 } on ? on : "Directory";

    protected override string? Validate(PlanItem item)
    {
        if (item.Enable is null) return "This step doesn't say whether to add or remove the menu entry.";
        if (!VerbPattern().IsMatch(item.Arg("verb").Trim())) return "The menu entry's name is not valid.";
        if (!OnPattern().IsMatch(On(item))) return "PC Tidy only adds menu entries for folders, drives, all files or one file type.";
        if (item.Arg("label").Any(char.IsControl) || item.Arg("label").Length > 100) return "The menu text is not valid.";
        if (item.Enable == true && CommandProblem(item.Arg("command").Trim()) is { } p) return p;
        return null;
    }

    /// <summary>Null when the command is a quoted absolute program path (not a shell or script host) plus arguments.</summary>
    internal static string? CommandProblem(string command)
    {
        var m = CommandPattern().Match(command);
        if (!m.Success || command.Length > 2048) return "The menu entry's command must start with the full path of a program in quotes.";
        var exe = m.Groups["exe"].Value;
        if (!PathArgs.IsPlainLocal(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return "The menu entry's program path is not valid.";
        if (ForbiddenHosts.Contains(Path.GetFileNameWithoutExtension(exe), StringComparer.OrdinalIgnoreCase)) return "PC Tidy never adds menu entries that run a command shell or script.";
        return null;
    }

    protected override string Describe(PlanItem item) => item.Enable == true
        ? $@"Would add the right-click entry HKCU\{VerbKey(item)} running {item.Arg("command").Trim()}"
        : $@"Would remove the right-click entry HKCU\{VerbKey(item)}";

    protected override bool CanUndo(StepRecord step) => step.Undo(PriorData) is not null;
    protected override string DescribeUndo(StepRecord step) => $@"Would put HKCU\{VerbKey(step.Item)} back as it was";

    /// <summary>The verb key as it was: did it exist, its menu text, its command.</summary>
    internal sealed record VerbState(bool Existed, RegistryPriorState Label, RegistryPriorState Command);

    private static VerbState Capture(string key) => new(
        RegistryAccess.KeyExists(RegistryHive.CurrentUser, true, key),
        RegistryAccess.Capture(RegistryHive.CurrentUser, true, key, "MUIVerb"),
        RegistryAccess.Capture(RegistryHive.CurrentUser, true, key + @"\command", ""));

    protected override Task<StepRecord> RunAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var item = step.Item;
        var key = VerbKey(item);
        var command = item.Arg("command").Trim();
        if (item.Enable == true && !File.Exists(CommandPattern().Match(command).Groups["exe"].Value))
            return Task.FromResult(step.Skipped("The program for this menu entry isn't on this PC."));

        var before = Capture(key);
        var label = item.Arg("label").Trim() is { Length: > 0 } l ? l : item.Arg("verb").Trim();
        if (item.Enable == true && RegistryAccess.Matches(before.Command, RegistryKind.String, command) && RegistryAccess.Matches(before.Label, RegistryKind.String, label))
            return Task.FromResult(step.Skipped("This menu entry is already there. Nothing to change."));
        if (item.Enable == false && !before.Existed) return Task.FromResult(step.Skipped("This menu entry isn't there. Nothing to change."));

        var prior = JsonSerializer.Serialize(before, ElevationProtocol.Json);
        if (item.Enable == true)
        {
            RegistryAccess.SetValue(RegistryHive.CurrentUser, true, key, "MUIVerb", RegistryKind.String, label);
            RegistryAccess.SetValue(RegistryHive.CurrentUser, true, key + @"\command", "", RegistryKind.String, command);
            if (!RegistryAccess.Matches(RegistryAccess.Capture(RegistryHive.CurrentUser, true, key + @"\command", ""), RegistryKind.String, command))
            {
                Restore(key, before);
                return Task.FromResult(step.Failed("Windows didn't keep the menu entry. Nothing was changed."));
            }
            return Task.FromResult(step.Done($"Added the right-click entry '{label}'.").WithUndo((PriorData, prior)));
        }

        DeleteVerbKey(key);
        return Task.FromResult(step.Done("Removed PC Tidy's right-click entry.").WithUndo((PriorData, prior)));
    }

    protected override Task<StepRecord> RevertAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        VerbState? before;
        try { before = JsonSerializer.Deserialize<VerbState>(step.Undo(PriorData)!, ElevationProtocol.Json); }
        catch (JsonException) { before = null; }
        if (before is null) return Task.FromResult(step.UndoFailed("The saved menu entry is missing, so PC Tidy can't undo this."));
        Restore(VerbKey(step.Item), before);
        return Task.FromResult(step.Undone(before.Existed ? "Put the right-click entry back as it was." : "Removed the right-click entry again."));
    }

    protected override Task<bool> CheckCompletedAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var key = VerbKey(step.Item);
        return Task.FromResult(step.Item.Enable == true
            ? RegistryAccess.Matches(RegistryAccess.Capture(RegistryHive.CurrentUser, true, key + @"\command", ""), RegistryKind.String, step.Item.Arg("command").Trim())
            : !RegistryAccess.KeyExists(RegistryHive.CurrentUser, true, key));
    }

    private static void Restore(string key, VerbState before)
    {
        if (!before.Existed) { DeleteVerbKey(key); return; }
        RegistryAccess.Restore(RegistryHive.CurrentUser, true, key, "MUIVerb", before.Label, null);
        RegistryAccess.Restore(RegistryHive.CurrentUser, true, key + @"\command", "", before.Command, null);
    }

    /// <summary>Deletes only a PCTidy.* verb key (the name is checked again right here).</summary>
    private static void DeleteVerbKey(string key)
    {
        var leaf = key[(key.LastIndexOf('\\') + 1)..];
        if (!leaf.StartsWith(VerbPrefix, StringComparison.Ordinal)) throw new InvalidOperationException("Only PC Tidy's own menu entries are removed.");
        using var root = RegistryAccess.OpenBase(RegistryHive.CurrentUser, true);
        root.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
    }
}
