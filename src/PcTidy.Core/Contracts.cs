using PcTidy.Core.Model;

namespace PcTidy.Core;

// =====================================================================================
//  CONTRACTS. Owned by the lead. Builder agents implement these in their own projects.
//  Rule: only PcTidy.Engine and PcTidy.Platform may change files/registry/services.
//  Feature projects are READ-ONLY: they scan and produce PlanItems.
// =====================================================================================

/// <summary>Every path PC Tidy knows about. Real implementation: PcTidy.Safety. Tests: <see cref="SandboxKnownLocations"/>.</summary>
public interface IKnownLocations
{
    string UserProfile { get; }
    string LocalAppData { get; }
    string RoamingAppData { get; }
    string ProgramData { get; }
    string WindowsDir { get; }
    string SystemDrive { get; }          // "C:\"
    string ProgramFiles { get; }
    string ProgramFilesX86 { get; }
    string UserTemp { get; }
    string WindowsTemp { get; }
    string Desktop { get; }
    string Documents { get; }
    string Downloads { get; }
    string Pictures { get; }
    string Videos { get; }
    string Music { get; }
    /// <summary>OneDrive (and other cloud) sync roots.</summary>
    IReadOnlyList<string> CloudRoots { get; }
    /// <summary>Steam install folder (null if not installed).</summary>
    string? SteamRoot { get; }
    /// <summary>All Steam library folders (each contains steamapps).</summary>
    IReadOnlyList<string> SteamLibraries { get; }
    /// <summary>Epic manifests folder (…\Epic\EpicGamesLauncher\Data\Manifests).</summary>
    string? EpicManifestsDir { get; }
    /// <summary>Install folders of every detected game (Steam, Epic, GOG, EA, Ubisoft, Battle.net, Xbox).</summary>
    IReadOnlyList<string> GameInstallDirs { get; }
    /// <summary>InstallLocation of every installed app (from the Uninstall registry).</summary>
    IReadOnlyList<string> AppInstallDirs { get; }
    /// <summary>%USERPROFILE%\.claude and \.codex (may not exist).</summary>
    string ClaudeDir { get; }
    string CodexDir { get; }
    /// <summary>PC Tidy's data folder (settings, runs, quarantine...). Default %LOCALAPPDATA%\PC Tidy.</summary>
    string DataRoot { get; }
    /// <summary>Fixed local drives, e.g. "C:\", "D:\".</summary>
    IReadOnlyList<string> FixedDrives { get; }
    /// <summary>True when this is a sandbox (tests / simulation testbed): real-system tools must not run.</summary>
    bool IsSandbox { get; }
}

/// <summary>Decides whether a path / plan item may be touched. Real implementation: PcTidy.Safety.</summary>
public interface ISafetyGuard
{
    /// <summary>Protection level for a path (no carve-outs considered).</summary>
    SafetyVerdict ClassifyPath(string path);
    /// <summary>Full check of a plan item, including carve-outs, user permissions and action-kind rules.</summary>
    SafetyVerdict Check(PlanItem item);
    /// <summary>True if the path is inside a cloud sync root (OneDrive).</summary>
    bool IsInCloudRoot(string path);
    /// <summary>True if the folder (or a parent up to a drive root) looks like a code project / app / game folder.</summary>
    bool IsInsideProjectOrAppFolder(string path, out string? why);
}

/// <summary>Restart Manager + exclusive-open check. Real implementation: PcTidy.Platform.</summary>
public interface IInUseChecker
{
    Task<IReadOnlyList<InUseInfo>> CheckAsync(IReadOnlyList<string> paths, CancellationToken ct);
    /// <summary>Quick single-file test: can it be opened exclusively right now?</summary>
    bool IsLocked(string path);
}

/// <summary>Read-only fast file listing. Default: <see cref="ManagedFileWalker"/>; PcTidy.Platform may add an MFT walker.</summary>
public interface IFileWalker
{
    /// <summary>
    /// Walks <paramref name="root"/>. Never follows junctions/symlinks (reported as entries with IsReparsePoint).
    /// Never opens file contents. Skips inaccessible folders silently (counted in stats).
    /// Return false from <paramref name="onEntry"/> for a directory to skip descending into it.
    /// </summary>
    WalkStats Walk(string root, WalkOptions options, Func<FileEntry, bool> onEntry, CancellationToken ct);
}

public sealed record WalkOptions
{
    public bool Recurse { get; init; } = true;
    public bool IncludeHidden { get; init; } = true;
    public bool IncludeSystem { get; init; } = false;
    public int MaxDepth { get; init; } = int.MaxValue;
}

public sealed record WalkStats(long Files, long Directories, long Bytes, long Inaccessible, TimeSpan Elapsed);

public readonly record struct FileEntry(
    string FullPath,
    string Name,
    bool IsDirectory,
    long Size,
    FileAttributes Attributes,
    DateTime CreationUtc,
    DateTime LastWriteUtc,
    DateTime LastAccessUtc,
    int Depth)
{
    public bool IsReparsePoint => (Attributes & FileAttributes.ReparsePoint) != 0;
    /// <summary>OneDrive/cloud "online-only" placeholder: NEVER open its content (it would download).</summary>
    public bool IsCloudPlaceholder => CloudFile.IsPlaceholder(Attributes);
    public bool IsHidden => (Attributes & FileAttributes.Hidden) != 0;
    public bool IsSystem => (Attributes & FileAttributes.System) != 0;
    /// <summary>Newer of modified and created: "last touched" (last-access is unreliable on Windows).</summary>
    public DateTime LastTouchedUtc => LastWriteUtc > CreationUtc ? LastWriteUtc : CreationUtc;
}

/// <summary>Quarantine store. Real implementation: PcTidy.Engine.</summary>
public interface IQuarantineStore
{
    /// <summary>Moves a file/folder into quarantine (same drive: rename; other drive: copy → verify → remove original).</summary>
    Task<QuarantineEntry> QuarantineAsync(string path, string runId, CancellationToken ct);
    /// <summary>Puts it back. Never overwrites: if the original path is taken, restores as "name (restored).ext".</summary>
    Task<string> RestoreAsync(QuarantineEntry entry, CancellationToken ct);
    Task<IReadOnlyList<QuarantineEntry>> ListAsync(string? runId, CancellationToken ct);
    /// <summary>Permanently deletes a run's quarantined files. Only called after explicit user confirmation.</summary>
    Task PurgeRunAsync(string runId, CancellationToken ct);
    Task<long> TotalBytesAsync(CancellationToken ct);
}

/// <summary>Write-ahead run journal. Real implementation: PcTidy.Engine.</summary>
public interface IRunJournal
{
    Task<RunRecord> CreateAsync(ActionPlan plan, ExecutionMode mode, CancellationToken ct);
    Task SaveStepAsync(string runId, StepRecord step, CancellationToken ct);
    Task SaveRunAsync(RunRecord run, CancellationToken ct);
    Task<RunRecord?> LoadAsync(string runId, CancellationToken ct);
    Task<IReadOnlyList<RunRecord>> ListAsync(CancellationToken ct);
    /// <summary>Runs left in Running state by a crash/power cut.</summary>
    Task<IReadOnlyList<RunRecord>> FindUnfinishedAsync(CancellationToken ct);
}

public sealed record ExecuteOptions
{
    public ExecutionMode Mode { get; init; } = ExecutionMode.Real;
    /// <summary>Called when a restore point is needed but couldn't be made. Return true to continue anyway.</summary>
    public Func<string, Task<bool>>? ContinueWithoutRestorePoint { get; init; }
    /// <summary>Metrics captured before the run (optional, v2 before/after proof).</summary>
    public Dictionary<string, double>? MetricsBefore { get; init; }
}

/// <summary>Runs a confirmed plan. Real implementation: PcTidy.Engine.</summary>
public interface IExecutor
{
    Task<RunRecord> ExecuteAsync(ActionPlan plan, ExecuteOptions options, IProgress<RunProgress>? progress, CancellationToken ct);
}

/// <summary>
/// What a plan would remove or move, measured on disk now (folders are walked, links are never followed), against the
/// delete cap. Real implementation: PcTidy.Engine (the same measure the executor uses before a real run).
/// </summary>
/// <param name="CappedItems">Plan items that remove or move originals.</param>
/// <param name="Files">Files those items cover.</param>
/// <param name="Bytes">Their size.</param>
/// <param name="QuarantineOnly">True when every one of them goes to quarantine (restorable).</param>
public sealed record RemovalSize(int CappedItems, long Files, long Bytes, bool QuarantineOnly, int MaxFiles, long MaxBytes)
{
    /// <summary>More than the delete cap allows without the user confirming the size.</summary>
    public bool OverLimit => Files > MaxFiles || Bytes > MaxBytes;
}

/// <summary>
/// Measures a plan against the delete cap before it runs, so the app can show the real size and ask once
/// ("This moves 58,120 files (9.4 GB) to quarantine. Continue?"). Optional service: without it the executor's own
/// cap still stops an oversized plan before any change.
/// </summary>
public interface IRemovalMeasure
{
    /// <summary>Slow for big folders: call it off the UI thread.</summary>
    RemovalSize Measure(ActionPlan plan, CancellationToken ct);

    /// <summary>The same plan with the cap override on every item that removes or moves originals (after the user said yes).</summary>
    ActionPlan WithSizeConfirmed(ActionPlan plan);

    /// <summary>The same plan with any cap override removed (a "yes" is never carried over from an earlier run).</summary>
    ActionPlan WithoutSizeConfirmed(ActionPlan plan);
}

/// <summary>
/// The check around a run that moves files (3.3, owner: "make sure no files are deleted or missing"). The shell calls
/// <see cref="Before"/> right before a confirmed real run and <see cref="IMoveCheckSession.After"/> right after it,
/// while it still holds the one-task-at-a-time gate, for every way a run starts (a page's button, Optimize everything,
/// watch folders, one by one). When the check fails the shell rolls the run back and calls
/// <see cref="IMoveCheckSession.AfterPutBack"/> to say where every file is. Optional: without it nothing is checked.
/// </summary>
public interface IMoveCheck
{
    /// <summary>True when the plan moves files this check covers (cheap; no disk access).</summary>
    bool Covers(ActionPlan plan);

    /// <summary>Null when the plan moves nothing this check covers. Reads only; slow for big folders (off the UI thread).</summary>
    /// <param name="progress">"Noting files: 1,204 of 3,000" while it reads.</param>
    IMoveCheckSession? Before(ActionPlan plan, IProgress<string>? progress, CancellationToken ct);

    /// <summary>The latest result for a run (the Organize page shows it), or null.</summary>
    MoveCheckResult? ResultFor(string runId);
}

public interface IMoveCheckSession
{
    /// <summary>Stopping it early gives a result with <see cref="MoveCheckResult.Stopped"/> set: never a failure.</summary>
    MoveCheckResult After(RunRecord run, IProgress<string>? progress, CancellationToken ct);
    /// <summary>After a roll back: is every planned file back, or still where the run put it? Names the rest, with Undo's own reasons.</summary>
    MoveCheckResult AfterPutBack(RunRecord run, UndoReport report, CancellationToken ct);
}

/// <param name="Line">"26 files before · 26 after · 0 missing" (plus "· 1 changed" when there is one).</param>
/// <param name="Problems">What's wrong with files PC Tidy moved. Empty = passed.</param>
/// <param name="Notes">Facts that aren't failures ("notes.dat is gone, and PC Tidy's journal shows it didn't move it").</param>
public sealed record MoveCheckResult(string Line, bool Passed, IReadOnlyList<string> Problems, IReadOnlyList<string> Notes)
{
    /// <summary>Set after a put back: every planned file was found at its old or its new place.</summary>
    public bool AllAccountedFor { get; init; }
    /// <summary>Set after a put back.</summary>
    public bool PutBack { get; init; }
    /// <summary>The user stopped the check: what was checked had no problem; the rest wasn't checked.</summary>
    public bool Stopped { get; init; }
    /// <summary>Files PC Tidy moved that aren't at their new place.</summary>
    public int Missing { get; init; }
    /// <summary>Files at their new place with a different size or fingerprint but the same modified time (not an edit).</summary>
    public int Changed { get; init; }
    public int FilesAfter { get; init; }
}

/// <summary>The words for a failed check (3.3): only what was seen, never a claim nobody verified.</summary>
public static class MoveCheckText
{
    /// <summary>The heading and text after a failed check, given what the roll back did.</summary>
    /// <param name="back">The check after the roll back; null when the roll back didn't run or failed (see <paramref name="error"/>).</param>
    public static (string Heading, string Text) Failure(MoveCheckResult result, MoveCheckResult? back, string? error)
    {
        var text = new System.Text.StringBuilder();
        text.Append("The check after moving files found a problem: ").Append(result.Line).Append('.').Append('\n');
        List(text, result.Problems);
        text.Append('\n');
        if (back is null)
        {
            text.Append("PC Tidy couldn't put the run back")
                .Append(error is null ? "." : $": {error}")
                .Append(" Every file it moved is still at its new place, except the ones listed above. Open History to undo this run.");
            return ("A check after moving files failed, and the run couldn't be put back", text.ToString().TrimEnd());
        }
        if (back.Passed)
        {
            text.Append($"So PC Tidy put back everything this run moved ({back.Line}). Every file is at its old place again.");
            return ("A check after moving files failed, so the run was put back", text.ToString().TrimEnd());
        }
        text.Append($"PC Tidy put back what it could ({back.Line}):").Append('\n');
        List(text, back.Problems);
        text.Append(back.AllAccountedFor
            ? "Every file is at its old place or its new one."
            : "PC Tidy couldn't find the files listed above at their old place or their new one. This run is in History.");
        return ("A check after moving files failed, so the run was put back where it could be", text.ToString().TrimEnd());
    }

    private static void List(System.Text.StringBuilder sb, IReadOnlyList<string> lines)
    {
        foreach (var p in lines.Take(5)) sb.Append("• ").Append(p).Append('\n');
        if (lines.Count > 5) sb.Append($"• and {lines.Count - 5:N0} more").Append('\n');
    }
}

/// <summary>Runs a check after the run put back (rolled back, or partly) are not "done" anywhere in the app.</summary>
public static class PutBackRuns
{
    public static bool WasPutBack(this RunRecord run) => run.Status is RunStatus.RolledBack or RunStatus.Undone or RunStatus.PartiallyUndone;
}

/// <summary>Admin steps skipped because the Windows admin prompt was declined, timed out or the helper couldn't start.</summary>
public static class AdminSteps
{
    /// <summary>Part of every such step's message; a later run of the same steps may work, so the app offers Try again.</summary>
    public const string SkippedMarker = "the steps that need admin rights were skipped";

    /// <summary>Skipped only because the admin helper wasn't there: running the same steps again may work.</summary>
    public static bool CanTryAgain(StepRecord s)
        => s.Status == StepStatus.Skipped && s.Item.NeedsAdmin && s.Message?.Contains(SkippedMarker, StringComparison.Ordinal) == true;
}

/// <summary>Undo / resume / roll back. Real implementation: PcTidy.Engine.</summary>
public interface IUndoEngine
{
    Task<UndoReport> UndoRunAsync(string runId, IProgress<RunProgress>? progress, CancellationToken ct);
    Task<RunRecord> ResumeAsync(string runId, IProgress<RunProgress>? progress, CancellationToken ct);
    Task<UndoReport> RollbackAsync(string runId, IProgress<RunProgress>? progress, CancellationToken ct);
}

/// <summary>
/// Carries out one family of action kinds. Engine has the file handlers; Platform has the system handlers.
/// Handlers must be idempotent-aware: on resume they're asked whether a Started step already happened.
/// </summary>
public interface IActionHandler
{
    bool CanHandle(PlanItem item);
    /// <summary>Do it. Return the step to record (Done/Skipped/Failed + UndoData).</summary>
    Task<StepRecord> ExecuteAsync(StepRecord step, HandlerContext ctx, CancellationToken ct);
    /// <summary>Describe what would happen, change nothing.</summary>
    Task<StepRecord> SimulateAsync(StepRecord step, HandlerContext ctx, CancellationToken ct);
    /// <summary>Reverse a Done step.</summary>
    Task<StepRecord> UndoAsync(StepRecord step, HandlerContext ctx, CancellationToken ct);
    /// <summary>For resume: did a step that was Started (but not recorded Done) actually complete?</summary>
    Task<bool> WasCompletedAsync(StepRecord step, HandlerContext ctx, CancellationToken ct);
}

public sealed class HandlerContext
{
    public required string RunId { get; init; }
    public required IKnownLocations Locations { get; init; }
    public required ISafetyGuard Safety { get; init; }
    public required IInUseChecker InUse { get; init; }
    public required IQuarantineStore Quarantine { get; init; }
    public required AppSettings Settings { get; init; }
    public required ExecutionMode Mode { get; init; }
    /// <summary>True when this process is the elevated helper.</summary>
    public bool IsElevated { get; init; }
}

/// <summary>Restore points. Real implementation: PcTidy.Platform.</summary>
public interface IRestorePointService
{
    /// <summary>Creates a restore point, or reuses one made in the last 24 h. Verifies by listing afterwards.</summary>
    Task<RestorePointInfo> EnsureAsync(string description, CancellationToken ct);
}

/// <summary>Runs admin-only plan items in the elevated helper (one UAC prompt per run). Real implementation: PcTidy.Platform.</summary>
public interface IElevationService
{
    bool IsElevated { get; }
    /// <summary>Executes the given steps elevated and returns their recorded results (same order).</summary>
    Task<IReadOnlyList<StepRecord>> ExecuteElevatedAsync(string runId, IReadOnlyList<StepRecord> steps, ExecutionMode mode, bool undo, CancellationToken ct);
}

/// <summary>
/// Optional on an <see cref="IElevationService"/>: the helper's read-only "settle" request for admin steps the journal
/// shows as Started. The helper answers from its own ledger (finished → its real result and undo data; only started →
/// its handler's check; never seen → not done). Unreachable → the steps come back still Started. Never re-runs a step.
/// </summary>
public interface IElevationSettlement
{
    Task<IReadOnlyList<StepRecord>> SettleElevatedAsync(string runId, IReadOnlyList<StepRecord> steps, CancellationToken ct);
}

/// <summary>A feature's read-only scanner (one per section, or several).</summary>
public interface IFeatureScanner
{
    string FeatureId { get; }
    string Title { get; }
    Release Release { get; }
    /// <summary>Included in "Optimize &amp; Organize everything"?</summary>
    bool IncludedInOptimizeAll { get; }
    Task<ScanResult> ScanAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct);
}

/// <summary>A section in the left rail. UI-agnostic: pages are created as object (WPF UserControl/Page).</summary>
public interface IFeatureModule
{
    string Id { get; }
    string Title { get; }
    /// <summary>One-line description for Home / help.</summary>
    string Summary { get; }
    NavGroup Group { get; }
    /// <summary>WPF-UI SymbolRegular name, e.g. "Broom24".</summary>
    string Icon { get; }
    int Order { get; }
    Release Release { get; }
    /// <summary>Scanners used by the standard flow page and by Optimize-all.</summary>
    IReadOnlyList<IFeatureScanner> Scanners { get; }
    /// <summary>Custom page, or null to use the shell's standard Scan → Preview → Confirm → Done page.</summary>
    object? CreatePage(IServiceProvider services);
}

/// <summary>
/// Optional on an <see cref="IFeatureModule"/> (v3.1): a small, factual counter for its rail row, e.g. Tweaks "7 of 31 on",
/// Services "3 changed" (from Windows default). Read from the real system state (never from what PC Tidy remembers), read-only,
/// cheap (the shell calls it after start-up and after each run, off the UI thread). Null = show nothing. No "NEW" badges.
/// </summary>
public interface INavBadgeSource
{
    Task<string?> GetNavBadgeAsync(CancellationToken ct);
}

/// <summary>
/// Implemented by the App shell. Custom feature pages call this to show the standard
/// confirm dialog, run (or simulate) the plan, and show the Done card with Undo.
/// </summary>
public interface IFlowHost
{
    /// <summary>Confirm dialog → run → Done card with Undo. Returns null if the user cancelled.</summary>
    Task<RunRecord?> ReviewAndRunAsync(ActionPlan plan, CancellationToken ct);
    /// <summary>"What would happen" report, no changes.</summary>
    Task<RunRecord?> SimulateAsync(ActionPlan plan, CancellationToken ct);
    /// <summary>
    /// Runs a plan WITHOUT a confirm dialog. Only for rules the user explicitly switched to automatic
    /// (e.g. watch-folder rules). Still journaled, still undoable, still safety-checked.
    /// </summary>
    Task<RunRecord?> RunApprovedAsync(ActionPlan plan, CancellationToken ct);
    /// <summary>Opens Explorer with the item selected (harmless, no change).</summary>
    void OpenInExplorer(string path);
    void ShowMessage(string title, string message, bool isError = false);
}

/// <summary>
/// Services available to feature pages through IServiceProvider.GetService(typeof(T)):
/// IKnownLocations, ISafetyGuard, IInUseChecker, IFileWalker, AppSettings, IFlowHost,
/// IRunJournal, IQuarantineStore, IUndoEngine, IEnumerable&lt;IFeatureModule&gt;.
/// </summary>
public static class ServiceProviderExtensions
{
    public static T Get<T>(this IServiceProvider sp) where T : class
        => sp.GetService(typeof(T)) as T ?? throw new InvalidOperationException($"Service {typeof(T).Name} not registered");
}
