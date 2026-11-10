using PcTidy.Core.Model;

namespace PcTidy.Platform;

/// <summary>Explorer's Quick access pins. Real: Shell.Application verbs "pintohome" / "unpinfromhome". Tests: a fake.</summary>
public interface IQuickAccessControl
{
    bool TouchesRealSystem { get; }
    /// <summary>True if the folder is pinned to Quick access (false if not, null if Windows doesn't say).</summary>
    bool? IsPinned(string folder);
    void Pin(string folder);
    void Unpin(string folder);
}

public sealed class ShellQuickAccessControl : IQuickAccessControl
{
    private const string QuickAccess = "shell:::{679f85cb-0220-4080-b29b-5540cc05aab6}";

    public bool TouchesRealSystem => true;

    public bool? IsPinned(string folder) => ShellInterop.OnSta<bool?>(() =>
    {
        dynamic shell = Shell();
        dynamic home = shell.Namespace(QuickAccess);
        if (home is null) return null;
        foreach (dynamic item in home.Items())
        {
            if (!string.Equals(((string)item.Path).TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
            // System.Home.IsPinned tells pinned folders from "frequent" ones (Windows 10 may not have it).
            var pinned = item.ExtendedProperty("System.Home.IsPinned");
            return pinned is bool b ? b : (bool?)null;
        }
        return false;
    });

    public void Pin(string folder) => ShellInterop.OnSta(() =>
    {
        dynamic shell = Shell();
        var parent = Path.GetDirectoryName(folder.TrimEnd('\\'));
        dynamic item = parent is null ? shell.Namespace(folder).Self : shell.Namespace(parent).ParseName(Path.GetFileName(folder.TrimEnd('\\')));
        if (item is null) throw new InvalidOperationException("Windows couldn't find this folder.");
        item.InvokeVerb("pintohome");
        return 0;
    });

    public void Unpin(string folder) => ShellInterop.OnSta(() =>
    {
        dynamic shell = Shell();
        dynamic home = shell.Namespace(QuickAccess);
        foreach (dynamic item in home.Items())
            if (string.Equals(((string)item.Path).TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) { item.InvokeVerb("unpinfromhome"); break; }
        return 0;
    });

    private static object Shell() => Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application") ?? throw new InvalidOperationException("Windows' shell isn't available."))!;
}

/// <summary>
/// QuickAccessPin. SourcePath = an existing local folder. Uses the shell's own "pintohome" verb; undo uses
/// "unpinfromhome" on that folder's Quick access entry, but only if PC Tidy pinned it. If Windows blocks it (policy),
/// the step is skipped with a friendly reason. Never from the admin helper.
/// </summary>
public sealed class QuickAccessPinHandler(IQuickAccessControl quickAccess) : SystemActionHandler
{
    internal const string PinnedData = "pinnedByPcTidy";

    public QuickAccessPinHandler() : this(new ShellQuickAccessControl()) { }

    protected override ActionKind Kind => ActionKind.QuickAccessPin;
    protected override bool TouchesRealSystem => quickAccess.TouchesRealSystem;
    protected override bool PerUserOnly(PlanItem item) => true;

    private static string Folder(PlanItem item) => item.SourcePath!.Trim().TrimEnd('\\');

    protected override string? Validate(PlanItem item)
        => PathArgs.IsPlainLocal(item.SourcePath?.Trim(), allowDriveRoot: true) ? null : "This step doesn't name a folder on this PC.";

    protected override string Describe(PlanItem item) => $"Would pin {Folder(item)} to Quick access (the shell's \"pintohome\" verb)";

    protected override bool CanUndo(StepRecord step) => step.Undo(PinnedData) == "1";
    protected override string DescribeUndo(StepRecord step) => $"Would unpin {Folder(step.Item)} from Quick access";

    protected override Task<StepRecord> RunAsync(StepRecord step, HandlerContext ctx, CancellationToken ct) => Task.Run(() =>
    {
        var folder = Folder(step.Item);
        if (!Directory.Exists(folder)) return step.Skipped("This folder isn't there any more.");
        if (quickAccess.IsPinned(folder) == true) return step.Skipped("This folder is already pinned to Quick access.");
        try { quickAccess.Pin(folder); }
        catch (Exception ex) when (IsWindowsError(ex) || ex is TimeoutException || ex is Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return step.Skipped("Windows didn't allow pinning this folder (your organization may have turned Quick access off).");
        }
        return quickAccess.IsPinned(folder) == false
            ? step.Skipped("Windows didn't pin this folder (your organization may have turned Quick access off).")
            : step.Done($"Pinned {Path.GetFileName(folder)} to Quick access.").WithUndo((PinnedData, "1"));
    }, ct);

    protected override Task<StepRecord> RevertAsync(StepRecord step, HandlerContext ctx, CancellationToken ct) => Task.Run(() =>
    {
        if (!CanUndo(step)) return step.Skipped("PC Tidy didn't pin this folder, so it leaves the pin alone.");
        var folder = Folder(step.Item);
        if (quickAccess.IsPinned(folder) == false) return step.Skipped("This folder isn't pinned any more.");
        quickAccess.Unpin(folder);
        return quickAccess.IsPinned(folder) == true ? step.UndoFailed("Windows didn't unpin this folder.") : step.Undone($"Unpinned {Path.GetFileName(folder)} from Quick access.");
    }, ct);

    protected override Task<bool> CheckCompletedAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
        => Task.FromResult(quickAccess.IsPinned(Folder(step.Item)) == true);
}
