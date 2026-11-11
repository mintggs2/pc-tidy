using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using PcTidy.App.Composition;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.UI.Flow;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace PcTidy.App.Shell;

/// <summary>
/// The shell's flow host: confirm dialog → IExecutor (with progress) → Done card; simulation; undo; approved runs;
/// and the ONLY place in PC Tidy that starts Explorer (to reveal an item, never to change anything).
/// </summary>
public sealed class FlowHost : IShellFlowHost
{
    private readonly MainWindow _window;
    private readonly AppServices _services;
    private readonly SemaphoreSlim _dialogGate = new(1, 1);

    public FlowHost(MainWindow window, AppServices services)
    {
        _window = window;
        _services = services;
    }

    private AppSettings Settings => _services.Get<AppSettings>();

    // ---------------- runs in progress (the window asks before closing mid-run) ----------------

    private int _activeRuns;
    private TaskCompletionSource _idle = CompletedIdle();
    private CancellationTokenSource? _stopCurrent;

    private static TaskCompletionSource CompletedIdle()
    {
        var t = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        t.SetResult();
        return t;
    }

    /// <summary>True while PC Tidy is changing something (a run, an undo, a resume or a roll back).</summary>
    public bool IsWorking => Volatile.Read(ref _activeRuns) > 0 || EngineReportsRunning();

    /// <summary>
    /// The engine's executor exposes <c>bool IsRunning</c> (not part of Core's IExecutor contract). Read it by name so the
    /// shell needs no engine reference; an executor without it simply counts as idle (FlowHost tracks its own runs anyway).
    /// </summary>
    private bool EngineReportsRunning()
    {
        if (_services.GetService(typeof(IExecutor)) is not { } executor) return false;
        try { return executor.GetType().GetProperty("IsRunning", typeof(bool))?.GetValue(executor) is true; }
        catch (Exception) { return false; }
    }

    /// <summary>Completes when no run, undo, resume or roll back is in progress.</summary>
    public Task WhenIdleAsync() => _idle.Task;

    /// <summary>Asks the run in progress to stop after its current step (what already ran stays journaled and undoable).</summary>
    public void StopAfterCurrentStep() => _stopCurrent?.Cancel();

    /// <summary>
    /// Marks work in progress for <see cref="IsWorking"/>; dispose to end it. UI thread only. It also holds the
    /// one-long-task-at-a-time gate (<see cref="RunGate"/>): Run, Preview and Simulate elsewhere wait and say why.
    /// </summary>
    /// <param name="task">Short name for the busy reason ("Clean", "the undo"): "Available when Clean finishes".</param>
    internal IDisposable Working(string task = "the current task")
    {
        if (_activeRuns++ == 0) _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return new WorkScope(this, Gate.Begin(task));
    }

    /// <summary>The gate pages read (the app-wide one unless the services bring their own, as tests do).</summary>
    private RunGate Gate => _services.GetService(typeof(RunGate)) as RunGate ?? RunGate.Shared;

    /// <summary>Title of the message shown when a second task is started while one is going.</summary>
    public const string OneTaskTitle = "One task at a time";

    /// <summary>
    /// One long task at a time, enforced here as well as on the buttons: a run, simulation, undo, resume or roll back
    /// started while another is going (from History, a Done card, a page without the gate on its buttons) is refused
    /// with a message that says what to wait for. Nothing is started, nothing is lost.
    /// </summary>
    private bool RefuseWhileBusy()
    {
        if (!Gate.IsBusy) return false;
        ShowMessage(OneTaskTitle, BusyMessage(Gate.Reason));
        return true;
    }

    /// <summary>"Available when Clean finishes. PC Tidy does one change at a time…"</summary>
    public static string BusyMessage(string? reason)
        => $"{reason ?? RunGate.BusyReason}. PC Tidy does one change at a time, so nothing new was started. Try again when it's done.";

    /// <summary>Waits until no long task is going (runs the user already approved, such as watch-folder rules, queue up).</summary>
    private async Task WhenGateFreeAsync(CancellationToken ct)
    {
        while (Gate.IsBusy)
        {
            var free = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnChanged(object? s, EventArgs e) { if (!Gate.IsBusy) free.TrySetResult(); }
            Gate.Changed += OnChanged;
            try
            {
                if (!Gate.IsBusy) return;
                await free.Task.WaitAsync(ct);
            }
            finally { Gate.Changed -= OnChanged; }
        }
    }

    /// <summary>Raised on the UI thread after a run, an undo, a resume or a roll back finished (the rail's counters refresh).</summary>
    public event Action? RunsChanged;

    private sealed class WorkScope(FlowHost host, IDisposable gate) : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            gate.Dispose();
            if (--host._activeRuns == 0)
            {
                host._idle.TrySetResult();
                try { host.RunsChanged?.Invoke(); }
                catch (Exception ex) { CrashHandler.Write(ex, "Updating after a run", terminating: false); }
            }
        }
    }

    // Every public method is safe to call from any thread: UI work is marshalled to the window's dispatcher.
    private bool OnUiThread => _window.Dispatcher.CheckAccess();
    private Task<T> OnUi<T>(Func<Task<T>> work) => _window.Dispatcher.InvokeAsync(work).Task.Unwrap();
    public int QuarantineDays => Settings.QuarantineDays;

    // ---------------- IFlowHost (single plan, used by custom feature pages) ----------------

    public async Task<RunRecord?> ReviewAndRunAsync(ActionPlan plan, CancellationToken ct)
    {
        if (!OnUiThread) return await OnUi(() => ReviewAndRunAsync(plan, ct));
        var outcome = await ReviewAndRunAsync(new[] { plan }, new FlowOptions { Title = $"Ready? {plan.Title}", ShowResult = true }, ct);
        return outcome.Kind == FlowOutcomeKind.Ran ? outcome.Records.FirstOrDefault() : null;
    }

    public async Task<RunRecord?> SimulateAsync(ActionPlan plan, CancellationToken ct)
        => !OnUiThread ? await OnUi(() => SimulateAsync(plan, ct)) : (await SimulateAsync(new[] { plan }, new FlowOptions { Title = plan.Title, ShowResult = true }, ct)).FirstOrDefault();

    /// <summary>Safe from any thread (watch-folder rules call it from a background thread).</summary>
    /// <remarks>An approved run waits for the task in progress to finish (it is queued, not refused).</remarks>
    public async Task<RunRecord?> RunApprovedAsync(ActionPlan plan, CancellationToken ct)
    {
        if (!OnUiThread) return await OnUi(() => RunApprovedAsync(plan, ct));
        await WhenGateFreeAsync(ct);
        return (await ExecuteAsync(new[] { plan }, ExecutionMode.Real, plan.Title, plan.Title, showProgress: false, ct)).FirstOrDefault();
    }

    // ---------------- IShellFlowHost ----------------

    public async Task<FlowOutcome> ReviewAndRunAsync(IReadOnlyList<ActionPlan> plans, FlowOptions options, CancellationToken ct)
    {
        if (!OnUiThread) return await OnUi(() => ReviewAndRunAsync(plans, options, ct));
        plans = plans.Where(p => p.Items.Count > 0).ToList();
        if (plans.Count == 0)
        {
            ShowMessage("Nothing selected", "Tick at least one item first.");
            return FlowOutcome.Cancelled;
        }
        if (RefuseWhileBusy()) return FlowOutcome.Cancelled;
        bool suggest = Settings.SuggestSimulationOnFirstUse;
        ConfirmResult confirm;
        await _dialogGate.WaitAsync(ct);
        try { confirm = await ConfirmDialog.ShowAsync(_window.DialogHost, SafeOrder.Sort(plans), options, QuarantineDays, suggest, ct); }
        finally { _dialogGate.Release(); }

        switch (confirm.Choice)
        {
            case ConfirmChoice.Cancel:
                return FlowOutcome.Cancelled;
            case ConfirmChoice.Simulate:
                var sim = await SimulateAsync(plans, options, ct);
                return new FlowOutcome(FlowOutcomeKind.Simulated, sim);
        }
        // Something else may have started while the dialog was open (a watch-folder rule, another page's button).
        if (RefuseWhileBusy()) return FlowOutcome.Cancelled;

        var runTitle = confirm.Plans.Count == 1 ? $"{confirm.Plans[0].Title}: working on it…" : "Working through every section…";
        var gateName = confirm.Plans.Count == 1 ? confirm.Plans[0].Title : "the current run";
        // PC Tidy's own switches (right-click menu, start with Windows) change nothing worth measuring.
        bool measure = confirm.Plans.Any(p => p.FeatureId != WhatIs.ShellVerbs.FeatureId);
        var records = await ExecuteAsync(confirm.Plans, ExecutionMode.Real, runTitle, gateName, showProgress: true, ct, measure, options.RestartItemIds);
        if (records.Count == 0) return FlowOutcome.Cancelled;
        if (suggest)
        {
            Settings.SuggestSimulationOnFirstUse = false;
            TrySaveSettings();
        }
        Learning?.RecordDecisions(options.OfferedGroups, records);
        var beforeAfter = measure ? await AfterRunAsync(records) : Array.Empty<string>();
        if (options.ShowResult) await ShowDoneAsync(records, beforeAfter);
        return new FlowOutcome(FlowOutcomeKind.Ran, records) { BeforeAfter = beforeAfter };
    }

    private IFlowLearning? Learning => _services.GetService(typeof(IFlowLearning)) as IFlowLearning;

    private RunProofBridge? _proof;
    private RunProofBridge Proof => _proof ??= new RunProofBridge(_services);

    /// <summary>
    /// After a confirmed run: before/after lines for the Done card, the run report, and the report copy the user
    /// switched on in Schedule &amp; reports (a journaled, undoable WriteTextFile run without another confirm).
    /// </summary>
    private async Task<IReadOnlyList<string>> AfterRunAsync(IReadOnlyList<RunRecord> records)
    {
        if (!Proof.IsAvailable) return Array.Empty<string>();
        var (lines, copy) = await Proof.AfterAsync(records);
        if (copy is not null)
        {
            try { await RunApprovedAsync(copy, CancellationToken.None); }
            catch (Exception ex) { CrashHandler.Write(ex, "Copying the run report", terminating: false); }
        }
        return lines;
    }

    public async Task<IReadOnlyList<RunRecord>> SimulateAsync(IReadOnlyList<ActionPlan> plans, FlowOptions options, CancellationToken ct)
    {
        if (!OnUiThread) return await OnUi(() => SimulateAsync(plans, options, ct));
        if (RefuseWhileBusy()) return Array.Empty<RunRecord>();
        var records = await ExecuteAsync(SafeOrder.Sort(plans), ExecutionMode.Simulate, "Simulating, nothing changes…", "the simulation", showProgress: true, ct);
        if (options.ShowResult && records.Count > 0)
        {
            var view = new SimulationReportView { Height = 480, ShowActions = false };
            view.Show(records);
            await ShowDialogAsync(new ContentDialog(_window.DialogHost) { Title = "What would happen", Content = view, CloseButtonText = "Close", DialogMaxWidth = 900 });
        }
        return records;
    }

    public async Task<bool> UndoRunsAsync(IReadOnlyList<RunRecord> runs, CancellationToken ct)
    {
        if (!OnUiThread) return await OnUi(() => UndoRunsAsync(runs, ct));
        var undoable = runs.Where(r => r.Mode == ExecutionMode.Real && r.Status is not (RunStatus.Undone or RunStatus.RolledBack)).ToList();
        if (undoable.Count == 0) return false;
        if (RefuseWhileBusy()) return false;
        int steps = undoable.Sum(r => r.Steps.Count(s => s.Status == StepStatus.Done && s.Item.Reversible));
        var ask = new ContentDialog(_window.DialogHost)
        {
            Title = undoable.Count > 1 ? "Undo this whole run?" : "Undo this run?",
            Content = Body($"PC Tidy puts back {steps:N0} item{PlanFacts.S(steps)} exactly where they were. Nothing is overwritten: if something is in the way, the restored copy gets \"(restored)\" in its name."),
            PrimaryButtonText = "Undo",
            PrimaryButtonIcon = new SymbolIcon(SymbolRegular.ArrowUndo24),
            CloseButtonText = "Keep as is",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await ShowDialogAsync(ask) != ContentDialogResult.Primary) return false;
        if (RefuseWhileBusy()) return false; // something started while the question was open

        var undo = _services.Get<IUndoEngine>();
        using var working = Working("the undo");
        _window.ShowProgress("Putting things back…", canStop: false);
        var reports = new List<UndoReport>();
        try
        {
            foreach (var r in undoable.OrderByDescending(r => r.CreatedUtc))
            {
                var progress = new Progress<RunProgress>(_window.ReportProgress);
                reports.Add(await Task.Run(() => undo.UndoRunAsync(r.RunId, progress, CancellationToken.None), CancellationToken.None));
            }
        }
        catch (Exception ex)
        {
            _window.HideProgress();
            ShowMessage("Undo didn't finish", ex.Message + " Whatever was already put back stays put back. You can try again from History.", isError: true);
            return false;
        }
        _window.HideProgress();

        int restored = reports.Sum(x => x.Restored), skipped = reports.Sum(x => x.Skipped), failed = reports.Sum(x => x.Failed);
        if (restored > 0) Learning?.RecordUndo(undoable);
        var details = reports.SelectMany(x => x.Messages).Distinct().Take(4).ToList();
        ShowMessage(failed + skipped == 0 ? "Everything is back" : "Mostly put back",
            $"{restored:N0} item{PlanFacts.S(restored)} put back." + (skipped + failed > 0 ? $" {skipped + failed} couldn't be: " + string.Join(" ", details) : ""),
            isError: failed > 0);
        return failed + skipped == 0;
    }

    public async Task<bool> ResumeAsync(RunRecord run)
    {
        if (!OnUiThread) return await OnUi(() => ResumeAsync(run));
        if (RefuseWhileBusy()) return false;
        using var working = Working("the resumed run");
        _window.ShowProgress("Finishing the run…", canStop: false);
        try
        {
            var progress = new Progress<RunProgress>(_window.ReportProgress);
            await Task.Run(() => _services.Get<IUndoEngine>().ResumeAsync(run.RunId, progress, CancellationToken.None));
            return true;
        }
        catch (Exception ex) { ShowMessage("Couldn't resume", ex.Message, isError: true); return false; }
        finally { _window.HideProgress(); }
    }

    public async Task<bool> RollbackAsync(RunRecord run)
    {
        if (!OnUiThread) return await OnUi(() => RollbackAsync(run));
        if (RefuseWhileBusy()) return false;
        using var working = Working("the roll back");
        _window.ShowProgress("Rolling back…", canStop: false);
        try
        {
            var progress = new Progress<RunProgress>(_window.ReportProgress);
            await Task.Run(() => _services.Get<IUndoEngine>().RollbackAsync(run.RunId, progress, CancellationToken.None));
            return true;
        }
        catch (Exception ex) { ShowMessage("Couldn't roll back", ex.Message, isError: true); return false; }
        finally { _window.HideProgress(); }
    }

    /// <summary>Opens a page and hands it a parameter (History selects the run of a <see cref="HistoryOpenRequest"/>).</summary>
    public void NavigateTo(string pageKey, object? parameter)
    {
        if (!OnUiThread) { _window.Dispatcher.BeginInvoke(() => NavigateTo(pageKey, parameter)); return; }
        if (parameter is null) { NavigateTo(pageKey); return; }
        _window.NavigateWithParameter(pageKey, parameter);
    }

    public void NavigateTo(string pageKey)
    {
        if (!OnUiThread) { _window.Dispatcher.BeginInvoke(() => NavigateTo(pageKey)); return; }
        // "cleanup#cleanup.browser-privacy" opens Clean and brings that group into view (scanning first if needed).
        var (module, group) = ShellNavigation.SplitKey(pageKey);
        if (group is not null) _window.Navigation.Open(module, null, group);
        else _window.NavigateTo(pageKey);
    }

    // ---------------- Explorer & messages ----------------

    /// <summary>
    /// Starts PC Tidy Rescue (next to PcTidy.exe, else the spare in the data folder). Process starts in the app and UI
    /// projects: this, <see cref="OpenInExplorer"/>, and PC Tidy's own verified installer / uninstaller after a final
    /// confirm (<see cref="Updates.OwnProcesses"/>), Windows' own System Restore window (rstrui.exe, History › Restore points) and the
    /// read-only nvidia-smi temperature query of Home's Now card (<see cref="Pages.Now.NvidiaTemperature"/>). Not in demo or practice
    /// mode: Rescue works on the real data folder, which those modes must never touch.
    /// </summary>
    public void OpenRescueTool()
    {
        if (!OnUiThread) { _window.Dispatcher.BeginInvoke(OpenRescueTool); return; }
        if (_services.IsDemo || _services.Args.SandboxRoot != null)
        {
            ShowMessage("Not in this mode", "PC Tidy Rescue works on your real History and quarantine, so it isn't started from demo or practice mode.");
            return;
        }
        if (RescueTool.Find(Settings.DataRoot) is not { } exe)
        {
            ShowMessage("Rescue tool not found", $"\"{RescueTool.ExeName}\" isn't next to PC Tidy or in the Rescue folder of your data folder. Reinstalling PC Tidy puts it back.");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! })?.Dispose();
        }
        catch (Exception ex)
        {
            ShowMessage("Couldn't start PC Tidy Rescue", ex.Message, isError: true);
        }
    }

    /// <summary>
    /// Reveals a file or folder in Explorer (explorer.exe /select,"path"). Harmless: opens a window, changes nothing.
    /// See <see cref="OpenRescueTool"/> for the complete list of process starts.
    /// </summary>
    public void OpenInExplorer(string path)
    {
        if (!OnUiThread) { _window.Dispatcher.BeginInvoke(() => OpenInExplorer(path)); return; }
        try
        {
            if (string.IsNullOrWhiteSpace(path) || path.Contains('"') || !Path.IsPathFullyQualified(path))
            {
                ShowMessage("Can't show that", "That item doesn't have a folder on this PC.");
                return;
            }
            var full = Path.GetFullPath(path);
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            string args;
            if (File.Exists(full) || Directory.Exists(full)) args = $"/select,\"{full}\"";
            else if (Path.GetDirectoryName(full) is { } parent && Directory.Exists(parent)) args = $"\"{parent}\"";
            else
            {
                ShowMessage("It isn't there any more", "That item has moved or no longer exists" + (_services.IsDemo ? " (this is sample data)." : "."));
                return;
            }
            Process.Start(new ProcessStartInfo(explorer, args) { UseShellExecute = false })?.Dispose();
            (Learning as LearningBridge)?.RecordFolderVisit(Directory.Exists(full) ? full : Path.GetDirectoryName(full)!);
        }
        catch (Exception ex)
        {
            ShowMessage("Couldn't open Explorer", ex.Message, isError: true);
        }
    }

    public void ShowMessage(string title, string message, bool isError = false)
    {
        if (!OnUiThread) { _window.Dispatcher.BeginInvoke(() => ShowMessage(title, message, isError)); return; }
        var content = new StackPanel();
        if (isError)
        {
            var bar = new InfoBar { Message = message, IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning };
            content.Children.Add(bar);
        }
        else content.Children.Add(Body(message));
        ShowAndForget(new ContentDialog(_window.DialogHost) { Title = title, Content = content, CloseButtonText = "OK", DialogMaxWidth = 560 });
    }

    /// <summary>A message nobody waits for: a dialog failure is logged, never thrown into the void.</summary>
    private async void ShowAndForget(ContentDialog dialog)
    {
        try { await ShowDialogAsync(dialog); }
        catch (Exception ex) { CrashHandler.Write(ex, "Showing a message", terminating: false); }
    }

    /// <summary>One dialog at a time (the dialog host can show only one).</summary>
    public async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        await _dialogGate.WaitAsync();
        try { return await dialog.ShowAsync(); }
        finally { _dialogGate.Release(); }
    }

    internal static TextBlock Body(string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 };
        t.SetResourceReference(FrameworkElement.StyleProperty, "Body");
        return t;
    }

    // ---------------- execution ----------------

    /// <summary>Runs plans one after another in the given (safe) order, with the progress overlay.</summary>
    /// <param name="measure">Confirmed real runs: take the "before" sample for the before/after proof first.</param>
    /// <param name="restartIds">Plan item ids the preview marked "needs a restart" (the overlay says so per section).</param>
    /// <param name="gateName">Short name for the busy reason elsewhere ("Clean", "the simulation").</param>
    private async Task<IReadOnlyList<RunRecord>> ExecuteAsync(IReadOnlyList<ActionPlan> plans, ExecutionMode mode, string title, string gateName, bool showProgress, CancellationToken ct, bool measure = false,
        IReadOnlySet<string>? restartIds = null)
    {
        var executor = _services.Get<IExecutor>();
        var records = new List<RunRecord>();
        // Real runs hold the one-long-task gate through Working(); a simulation holds it too (it uses the same overlay).
        using var working = mode == ExecutionMode.Real ? Working(gateName) : null;
        using var simulating = mode == ExecutionMode.Simulate ? Gate.Begin(gateName) : null;
        var stop = showProgress ? _window.ShowProgress(title, canStop: plans.Sum(p => p.Items.Count) > 1) : CancellationToken.None;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stop);
        var previousStop = _stopCurrent;
        _stopCurrent = linked;
        // Layout spec §4.3: per-section states (Waiting · Running · Done · Skipped · Needs restart) when there are several
        // sections, and "Everything done so far is already saved and can be undone" during a real run.
        var sections = new ProgressSections(plans.Select(p => p.Title));
        if (showProgress) _window.ShowProgressSections(plans.Count > 1 ? sections : null, savedLine: mode == ExecutionMode.Real);
        int current = -1;
        try
        {
            if (mode == ExecutionMode.Real && plans.Any(p => p.RequiresRestorePoint) && !await EnsureRestorePointAsync(showProgress))
                return records;
            Dictionary<string, double>? before = null;
            if (measure && mode == ExecutionMode.Real && Proof.IsAvailable)
            {
                if (showProgress) _window.SetProgressTitle("Measuring first, for the before/after…");
                before = await Proof.BeforeAsync(plans[0].ParentRunId ?? plans[0].RunId);
                if (showProgress) _window.SetProgressTitle(title);
            }
            var options = new ExecuteOptions { Mode = mode, ContinueWithoutRestorePoint = AskContinueWithoutRestorePointAsync, MetricsBefore = before };
            for (int i = 0; i < plans.Count; i++)
            {
                var plan = plans[i];
                if (linked.IsCancellationRequested) break;
                if (showProgress && plans.Count > 1) _window.SetProgressTitle($"{plan.Title}: working on it…");
                current = i;
                sections.Start(i);
                var progress = new Progress<RunProgress>(p => { if (showProgress) _window.ReportProgress(p); });
                var record = await Task.Run(() => executor.ExecuteAsync(plan, options, progress, linked.Token), CancellationToken.None);
                records.Add(record);
                sections.Finish(i, record, ProgressSections.NeedsRestart(record, restartIds));
                current = -1;
            }
        }
        catch (OperationCanceledException) { /* stopped: what ran is journaled and undoable */ }
        catch (Exception ex)
        {
            ShowMessage("That didn't finish", ex.Message + " Everything that already ran is in History, where you can undo it.", isError: true);
        }
        finally
        {
            if (current >= 0) sections.Finish(current, null, needsRestart: false);
            sections.StopRemaining();
            _stopCurrent = previousStop;
            if (showProgress) _window.HideProgress();
            // Feature pages store their options in AppSettings without saving; a run is a natural save point.
            if (mode == ExecutionMode.Real) TrySaveSettings();
        }
        return records;
    }

    /// <summary>Restore point first (Plan §5). The engine also checks; doing it here puts it before every section.</summary>
    private async Task<bool> EnsureRestorePointAsync(bool showProgress)
    {
        if (_services.GetService(typeof(IRestorePointService)) is not IRestorePointService rps) return true;
        if (showProgress) _window.SetProgressTitle("Making a restore point first…");
        RestorePointInfo info;
        try { info = await Task.Run(() => rps.EnsureAsync("PC Tidy", CancellationToken.None)); }
        catch (Exception ex) { info = new RestorePointInfo(false, false, null, null, ex.Message); }
        if (info.Created || info.Reused) return true;
        return await AskContinueWithoutRestorePointAsync(info.Problem ?? "Windows didn't make one.");
    }

    private async Task<bool> AskContinueWithoutRestorePointAsync(string problem)
    {
        return await _window.Dispatcher.InvokeAsync(async () =>
        {
            var d = new ContentDialog(_window.DialogHost)
            {
                Title = "No restore point",
                Content = Body($"Windows couldn't make a restore point: {problem}\n\nEverything PC Tidy does can still be undone from History. Continue without a restore point?"),
                PrimaryButtonText = "Continue",
                CloseButtonText = "Stop here",
                DefaultButton = ContentDialogButton.Close,
            };
            return await d.ShowAsync() == ContentDialogResult.Primary;
        }).Task.Unwrap();
    }

    private async Task ShowDoneAsync(IReadOnlyList<RunRecord> records, IReadOnlyList<string> beforeAfter)
    {
        var card = new DoneCard(QuarantineDays) { Runs = records, Width = 600 };
        if (beforeAfter.Count > 0) card.BeforeAfter = beforeAfter;
        var dialog = new ContentDialog(_window.DialogHost) { Title = "Done", Content = card, CloseButtonText = "Close", DialogMaxWidth = 700 };
        card.UndoRequested += runs =>
        {
            dialog.Hide();
            UndoAfterYieldAsync(runs).Forget(this, "Undo didn't finish");
        };
        card.HistoryRequested += () => { dialog.Hide(); NavigateTo("history"); };
        card.RunCheckLauncher = _services.GetService(typeof(IRunCheckLauncher)) as IRunCheckLauncher;
        card.CheckRequested += _ => dialog.Hide(); // Measure opens behind it
        await ShowDialogAsync(dialog);
    }

    private async Task UndoAfterYieldAsync(IReadOnlyList<RunRecord> runs)
    {
        await Task.Yield(); // let the Done dialog close first (one dialog at a time)
        await UndoRunsAsync(runs, CancellationToken.None);
    }

    /// <summary>
    /// Saves after a run. While the settings file couldn't be read and the user hasn't confirmed on Home, saving is
    /// refused on purpose (it would overwrite their file with defaults): Home already explains, so stay quiet here.
    /// </summary>
    private void TrySaveSettings()
    {
        if (Settings.LoadProblem is not null && !Settings.LoadProblemAcknowledged) return;
        try { _services.Get<ISettingsStore>().Save(Settings); }
        catch (Exception ex) { ShowMessage("Settings not saved", ex.Message, isError: true); }
    }
}
