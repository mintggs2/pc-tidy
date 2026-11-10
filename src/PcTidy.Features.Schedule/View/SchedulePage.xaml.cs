using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PcTidy.Features.Schedule.DiskHealth;
using PcTidy.Features.Schedule.Drift;
using PcTidy.Features.Schedule.Metrics;
using PcTidy.Features.Schedule.Reports;
using PcTidy.Features.Schedule.Runner;
using PcTidy.Features.Schedule.Schedules;
using PcTidy.Features.Schedule.Storage;
using PcTidy.UI.Flow;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace PcTidy.Features.Schedule.View;

/// <summary>
/// Schedule &amp; reports page: the two report-only schedules (applied through the standard confirm), the last scan and
/// the tweak drift check, the before/after history graph, disk health history and the saved reports.
/// Every read happens off the UI thread; every change (task create/delete, report copies) is a plan run by IFlowHost.
/// </summary>
public partial class SchedulePage : UserControl
{
    private readonly IServiceProvider _services;
    private readonly ScheduleEnvironment? _env;
    private readonly IFlowHost? _flow;
    private ScheduleSettings _saved;
    private IReadOnlyList<ScheduledTaskState> _tasks = Array.Empty<ScheduledTaskState>();
    private IReadOnlyList<MetricsHistoryEntry> _metrics = Array.Empty<MetricsHistoryEntry>();
    private IReadOnlyList<ReportInfo> _reports = Array.Empty<ReportInfo>();
    private CancellationTokenSource? _scanCts;
    private bool _populating;

    public SchedulePage(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;
        _env = ScheduleEnvironment.From(services);
        _flow = services.GetService(typeof(IFlowHost)) as IFlowHost;
        _saved = _env is null ? new ScheduleSettings() : ScheduleSettings.Load(_env.Settings);
        FillChoices();
        ShowSettings(_saved);
        Loaded += async (_, _) => await RefreshAsync(sampleIfDue: true);
        Unloaded += (_, _) => _scanCts?.Cancel();
    }

    // ------------------------------------------------------------------ loading

    private async Task RefreshAsync(bool sampleIfDue = false)
    {
        if (_env is not { } env)
        {
            ShowNotice("Schedules and reports aren't available in this build.", InfoBarSeverity.Warning);
            IsEnabled = false;
            return;
        }
        if (env.IsPractice) ShowNotice("Practice PC: schedules are planned and simulated here, but no real Windows task is read or made.", InfoBarSeverity.Informational);

        var data = await Task.Run(() =>
        {
            var tasks = SafeRead(() => env.Tasks.ReadOwnTasks(CancellationToken.None), Array.Empty<ScheduledTaskState>());
            return (Tasks: tasks, Metrics: env.Store.LoadMetrics(), Disks: env.Store.LoadDiskHealth(),
                Reports: env.Store.ListReports(10), Scans: env.Store.LoadScanSummaries());
        });
        _tasks = data.Tasks;
        _metrics = data.Metrics;
        _reports = data.Reports;
        ShowTaskStates();
        ShowLastScan(data.Scans);
        ShowMetrics();
        ShowDisks(data.Disks);
        ShowReports();

        if (sampleIfDue && _saved.DiskHealthHistory && env.Store.DiskSampleDueToday(DateTime.UtcNow)) await ReadDisksAsync();
    }

    private static T SafeRead<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or InvalidOperationException) { return fallback; }
    }

    // ------------------------------------------------------------------ schedules

    private void FillChoices()
    {
        _populating = true;
        foreach (var day in Enum.GetValues<DayOfWeek>())
            WeeklyDay.Items.Add(new ComboBoxItem { Content = SchedulePlanner.LocalDayName(day.ToString()), Tag = day.ToString() });
        for (int d = 1; d <= SchedulePlanner.MaxMonthDay; d++)
            MonthlyDay.Items.Add(new ComboBoxItem { Content = $"Day {d}", Tag = d.ToString(CultureInfo.InvariantCulture) });
        foreach (var box in new[] { WeeklyTime, MonthlyTime })
            for (int m = 0; m < 24 * 60; m += 30)
            {
                var hhmm = $"{m / 60:00}:{m % 60:00}";
                box.Items.Add(new ComboBoxItem { Content = SchedulePlanner.LocalTime(hhmm), Tag = hhmm });
            }
        _populating = false;
    }

    private void ShowSettings(ScheduleSettings s)
    {
        _populating = true;
        WeeklyToggle.IsChecked = s.Weekly.Enabled;
        MonthlyToggle.IsChecked = s.Monthly.Enabled;
        Select(WeeklyDay, SchedulePlanner.NormalizeDay(ScheduleKind.WeeklyScan, s.Weekly.Day) ?? "Sunday");
        Select(MonthlyDay, SchedulePlanner.NormalizeDay(ScheduleKind.MonthlyCheckup, s.Monthly.Day) ?? "1");
        Select(WeeklyTime, SchedulePlanner.NormalizeTime(s.Weekly.Time) ?? "19:00");
        Select(MonthlyTime, SchedulePlanner.NormalizeTime(s.Monthly.Time) ?? "19:00");
        HtmlToggle.IsChecked = s.SaveHtmlReports;
        CopyToggle.IsChecked = s.CopyReportsToFolder;
        _populating = false;
        UpdateCopyControls();
        UpdateApplyButton();
    }

    private static void Select(ComboBox box, string tag)
    {
        var item = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => string.Equals((string)i.Tag, tag, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            // A time saved outside the half-hour list (edited by hand) still shows.
            item = new ComboBoxItem { Content = tag.Contains(':') ? SchedulePlanner.LocalTime(tag) : tag, Tag = tag };
            box.Items.Add(item);
        }
        box.SelectedItem = item;
    }

    private static string? SelectedTag(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string;

    /// <summary>The schedule as currently shown on the page.</summary>
    private ScheduleSettings Edited()
    {
        var s = ScheduleSettings.Load(_env!.Settings);
        s.Weekly = new ScheduleChoice { Enabled = WeeklyToggle.IsChecked == true, Day = SelectedTag(WeeklyDay) ?? "Sunday", Time = SelectedTag(WeeklyTime) ?? "19:00" };
        s.Monthly = new ScheduleChoice { Enabled = MonthlyToggle.IsChecked == true, Day = SelectedTag(MonthlyDay) ?? "1", Time = SelectedTag(MonthlyTime) ?? "19:00" };
        return s;
    }

    /// <summary>What Windows has: the real tasks, or (practice PC, or Task Scheduler unreadable) what was last saved.</summary>
    private IReadOnlyList<ScheduledTaskState> Current()
    {
        if (_env is { Tasks.IsAvailable: true }) return _tasks;
        var list = new List<ScheduledTaskState>();
        foreach (var kind in new[] { ScheduleKind.WeeklyScan, ScheduleKind.MonthlyCheckup })
        {
            var c = _saved.For(kind);
            if (c.Enabled)
                list.Add(new ScheduledTaskState(kind, ScheduleIds.TaskPath(kind), true, SchedulePlanner.NormalizeDay(kind, c.Day),
                    SchedulePlanner.NormalizeTime(c.Time), ScheduleIds.Arguments(kind), null, null, null));
        }
        return list;
    }

    private void OnScheduleEdited(object sender, RoutedEventArgs e) => UpdateApplyButton();
    private void OnScheduleSelection(object sender, SelectionChangedEventArgs e) => UpdateApplyButton();

    private void UpdateApplyButton()
    {
        if (_populating || _env is null) return;
        WeeklyDay.IsEnabled = WeeklyTime.IsEnabled = WeeklyToggle.IsChecked == true;
        MonthlyDay.IsEnabled = MonthlyTime.IsEnabled = MonthlyToggle.IsChecked == true;
        var items = SchedulePlanner.Reconcile(Edited(), Current());
        ApplyScheduleButton.IsEnabled = items.Count > 0 && _flow is not null;
        bool anyOn = WeeklyToggle.IsChecked == true || MonthlyToggle.IsChecked == true;
        ScheduleHint.Text = items.Count > 0 ? "You'll see exactly what changes before anything is saved."
            : anyOn ? "No changes to save." : "Nothing is scheduled. Turn on a schedule to save it.";
    }

    private void ShowTaskStates()
    {
        WeeklyState.Text = StateText(ScheduleKind.WeeklyScan);
        MonthlyState.Text = StateText(ScheduleKind.MonthlyCheckup);
        UpdateApplyButton();
    }

    private string StateText(ScheduleKind kind)
    {
        if (_env is { Tasks.IsAvailable: false })
            return _saved.For(kind).Enabled ? "On (practice PC: no real task)" : "Off";
        var task = _tasks.FirstOrDefault(t => t.Kind == kind);
        if (task is null) return "Off: not in Windows' Task Scheduler.";
        if (!task.Enabled) return "Turned off in Task Scheduler.";
        var next = task.NextRunUtc is { } n ? "Next: " + n.ToLocalTime().ToString("ddd d MMM, t", CultureInfo.CurrentCulture) : "Next run not known";
        var last = task.LastRunUtc is { } l ? $" · last ran {RunSummary.WhenInSentence(l)}" + (task.LastResult is { } r and not 0 ? $" (code {r})" : "") : "";
        return next + last;
    }

    private async void OnApplySchedule(object sender, RoutedEventArgs e)
    {
        if (_env is null || _flow is null) return;
        var wanted = Edited();
        var items = SchedulePlanner.Reconcile(wanted, Current());
        if (items.Count == 0) return;
        ApplyScheduleButton.IsEnabled = false;
        try
        {
            var run = await _flow.ReviewAndRunAsync(SchedulePlanner.Plan(items), CancellationToken.None);
            // Remember the choice only when Windows really took it (every step done; simulated on a practice PC).
            if (run is { Steps.Count: > 0 } && run.Steps.All(s => s.Status is StepStatus.Done or StepStatus.Simulated))
            {
                wanted.Store(_env.Settings); // the shell saves settings after a run
                _saved = wanted;
            }
        }
        finally { await RefreshAsync(); }
    }

    // ------------------------------------------------------------------ scan now / checkup now

    private void OnScanNow(object sender, RoutedEventArgs e) => _ = ScanAsync(checkup: false);
    private void OnCheckupNow(object sender, RoutedEventArgs e) => _ = ScanAsync(checkup: true);
    private void OnStopScan(object sender, RoutedEventArgs e) => _scanCts?.Cancel();

    private async Task ScanAsync(bool checkup)
    {
        if (_env is null || _scanCts is not null) return;
        _scanCts = new CancellationTokenSource();
        ScanningTitle.Text = checkup ? "Running the checkup…" : "Scanning…";
        ScanningCard.Visibility = Visibility.Visible;
        ScanNowButton.IsEnabled = CheckupNowButton.IsEnabled = false;
        try
        {
            // The same report-only runner the schedule uses: no plan can run from here.
            var result = await Task.Run(() => ScheduledScan.RunAsync(_services, new ScheduledScanRequest(checkup, Manual: true), _scanCts.Token,
                new ScheduledScanOptions { Environment = _env }));
            if (result.ExitCode == ScheduledScanExitCodes.Done && result.Findings is { } f)
            {
                var review = f.CleanBytes > 0 && _flow is IShellFlowHost shell
                    ? MakeButton("Review in Optimize", "Sparkle24", () => shell.NavigateTo("optimize"))
                    : null;
                ShowNotice(result.Summary, InfoBarSeverity.Success, review);
            }
            else ShowNotice(result.Summary, result.ExitCode == ScheduledScanExitCodes.Cancelled ? InfoBarSeverity.Informational : InfoBarSeverity.Warning);
        }
        finally
        {
            _scanCts.Dispose();
            _scanCts = null;
            ScanningCard.Visibility = Visibility.Collapsed;
            ScanNowButton.IsEnabled = CheckupNowButton.IsEnabled = true;
            await RefreshAsync();
        }
    }

    // ------------------------------------------------------------------ last scan + drift

    private void ShowLastScan(IReadOnlyList<ScanSummary> scans)
    {
        var last = scans.OrderByDescending(s => s.TimeUtc).FirstOrDefault();
        LastScanText.Text = last is null
            ? "No scheduled scan has run yet. Use Scan now, or turn on a schedule."
            : $"{(last.Checkup ? "Checkup" : "Scan")} {RunSummary.WhenInSentence(last.TimeUtc)}: "
              + (last.CleanBytes > 0 ? $"{Sizes.Format(last.CleanBytes)} you can clean." : "nothing needed cleaning.");
        var lastCheckup = scans.Where(s => s.Checkup).OrderByDescending(s => s.TimeUtc).FirstOrDefault();
        if (DriftList.Children.Count == 0)
        {
            DriftText.Text = lastCheckup is null
                ? "The checkup also looks at whether Windows changed back any tweaks you applied."
                : lastCheckup.DriftCount > 0
                    ? $"At the last checkup, {lastCheckup.DriftCount} of your tweaks had been changed back. Check again to see which."
                    : "At the last checkup, your tweaks were all still in place.";
            OpenTweaksButton.Visibility = lastCheckup is { DriftCount: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        }
        CheckDriftButton.IsEnabled = _services.GetService(typeof(IRunJournal)) is IRunJournal;
    }

    private async void OnCheckDrift(object sender, RoutedEventArgs e)
    {
        if (_env is null || _services.GetService(typeof(IRunJournal)) is not IRunJournal journal) return;
        CheckDriftButton.IsEnabled = false;
        try
        {
            var report = await Task.Run(() => new TweakDriftChecker(journal, _env.Registry).CheckAsync(CancellationToken.None));
            ShowDrift(report);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            DriftText.Text = "The tweak check couldn't read the run history this time.";
        }
        finally { CheckDriftButton.IsEnabled = true; }
    }

    private void ShowDrift(DriftReport report)
    {
        DriftList.Children.Clear();
        DriftText.Text = report.Headline is { } h ? h + " " + report.Advice
            : report.Checked > 0 ? $"All {report.Checked} tweaks you applied are still in place."
            : "You haven't applied any tweaks with PC Tidy, so there's nothing to check.";
        foreach (var d in report.Drifted.Take(20))
        {
            var row = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            row.Children.Add(new TextBlock { Text = d.Description, TextWrapping = TextWrapping.Wrap, Style = (Style)FindResource("BodyStrong") });
            row.Children.Add(new TextBlock
            {
                Text = $"PC Tidy set {d.AppliedText}, now {d.CurrentText} · {d.Path}",
                TextWrapping = TextWrapping.Wrap, Style = (Style)FindResource("Caption"),
            });
            DriftList.Children.Add(row);
        }
        if (report.Drifted.Count > 20) DriftList.Children.Add(new TextBlock { Text = $"…and {report.Drifted.Count - 20} more.", Style = (Style)FindResource("Caption") });
        OpenTweaksButton.Visibility = report.Drifted.Count > 0 && _flow is IShellFlowHost ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Re-apply goes through Tweaks' own preview and confirm: nothing is re-applied from here.</summary>
    private void OnOpenTweaks(object sender, RoutedEventArgs e) => (_flow as IShellFlowHost)?.NavigateTo("tweaks");

    // ------------------------------------------------------------------ metrics graph

    private void ShowMetrics()
    {
        var keys = MetricKeys.Ordered(_metrics.SelectMany(m => m.Values.Keys)).Where(k => MetricKeys.Describe(k) is not null).ToList();
        var previous = SelectedTag(MetricPicker);
        _populating = true;
        MetricPicker.Items.Clear();
        foreach (var k in keys) MetricPicker.Items.Add(new ComboBoxItem { Content = MetricKeys.Describe(k)!.Label, Tag = k });
        var systemKey = MetricsSampler.DriveLetter(_env?.Locations.SystemDrive ?? "") is { } letter ? MetricKeys.FreeKey(letter) : null;
        var pick = keys.FirstOrDefault(k => k == previous) ?? keys.FirstOrDefault(k => k == systemKey) ?? keys.FirstOrDefault();
        if (pick is not null) Select(MetricPicker, pick);
        // No samples yet: no empty picker either.
        MetricPicker.Visibility = keys.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _populating = false;
        UpdateGraph();

        var lastAfter = _metrics.Where(m => m.Kind == SampleKind.After && m.RunId is not null).OrderByDescending(m => m.TimeUtc).FirstOrDefault();
        var lastBefore = lastAfter is null ? null : _metrics.LastOrDefault(m => m.Kind == SampleKind.Before && m.RunId == lastAfter.RunId);
        if (lastAfter is not null && lastBefore is not null)
        {
            var changed = BeforeAfter.Compare(lastBefore.Values, lastAfter.Values).Where(c => !c.Unchanged).Take(3).Select(c => c.Text).ToList();
            LastChangeText.Text = changed.Count > 0 ? "Last run: " + string.Join(" · ", changed) : "Last run: no measurable change.";
        }
        else LastChangeText.Text = "Numbers are measured before and after each run, and once a day when a schedule runs.";
    }

    private void OnMetricChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_populating) UpdateGraph();
    }

    private void UpdateGraph()
    {
        var key = SelectedTag(MetricPicker);
        Graph.Metric = key is null ? null : MetricKeys.Describe(key);
        Graph.Points = key is null
            ? Array.Empty<GraphPoint>()
            : _metrics.Where(m => m.Values.ContainsKey(key)).Select(m => new GraphPoint(m.TimeUtc, m.Values[key], m.Kind)).TakeLast(200).ToList();
        // A chart of one dot looks broken: with fewer than two readings, say what there is instead.
        var points = Graph.Points;
        bool chart = points.Count >= 2;
        Graph.Visibility = chart ? Visibility.Visible : Visibility.Collapsed;
        GraphLegend.Visibility = Graph.Visibility;
        GraphNote.Visibility = chart ? Visibility.Collapsed : Visibility.Visible;
        GraphNote.Text = points.Count == 1 && Graph.Metric is { } info
            ? $"One reading so far: {info.Label} was {info.Format(points[0].Value)} on {points[0].TimeUtc.ToLocalTime().ToString("d MMM", CultureInfo.CurrentCulture)}. The chart appears after the next run."
            : "No readings yet. PC Tidy measures before and after each run, and once a day when a schedule runs.";
    }

    // ------------------------------------------------------------------ disk health

    private void ShowDisks(IReadOnlyList<DiskHealthSample> history)
    {
        DiskWarnings.Children.Clear();
        DiskList.Children.Clear();
        foreach (var w in DiskHealthAdvice.Warnings(history))
            DiskWarnings.Children.Add(new InfoBar { Message = w, IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Margin = new Thickness(0, 0, 0, 6) });
        var latest = history.OrderByDescending(s => s.TimeUtc).FirstOrDefault();
        if (latest is null || latest.Disks.Count == 0)
        {
            DiskList.Children.Add(new TextBlock
            {
                Text = _saved.DiskHealthHistory ? "No reading yet. Windows' disk status isn't available on every PC." : "Disk health history is off.",
                Style = (Style)FindResource("Secondary"), TextWrapping = TextWrapping.Wrap,
            });
            return;
        }
        foreach (var disk in latest.Disks)
        {
            var temps = history.Select(s => s.Disks.FirstOrDefault(d => d.Id == disk.Id)?.TemperatureC).OfType<double>().ToList();
            var detail = new List<string> { DiskHealthAdvice.StatusText(disk.Health) };
            if (disk.TemperatureC is { } t) detail.Add($"{t:0} °C" + (temps.Count > 1 ? $" (range {temps.Min():0}–{temps.Max():0} °C)" : ""));
            if (disk.WearPercent is { } w) detail.Add($"{w:0} % worn");
            if (disk.PowerOnHours is { } hours) detail.Add($"{hours:N0} hours on");
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var icon = new SymbolIcon
            {
                Symbol = disk.Health switch { DiskHealthStatus.Healthy => SymbolRegular.CheckmarkCircle24, DiskHealthStatus.Unknown => SymbolRegular.QuestionCircle24, _ => SymbolRegular.Warning24 },
                FontSize = 20, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top,
            };
            icon.SetResourceReference(ForegroundProperty, disk.Health switch
            {
                DiskHealthStatus.Healthy => "SystemFillColorSuccessBrush",
                DiskHealthStatus.Warning => "SystemFillColorCautionBrush",
                DiskHealthStatus.Unhealthy => "SystemFillColorCriticalBrush",
                _ => "TextFillColorSecondaryBrush",
            });
            row.Children.Add(icon);
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = disk.Name + (disk.Media is DriveMedia.Ssd ? " (SSD)" : disk.Media is DriveMedia.Hdd ? " (HDD)" : ""), Style = (Style)FindResource("BodyStrong"), TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = string.Join(" · ", detail), Style = (Style)FindResource("Caption"), TextWrapping = TextWrapping.Wrap });
            row.Children.Add(text);
            DiskList.Children.Add(row);
        }
        DiskNote.Text = $"{history.Count} daily reading{(history.Count == 1 ? "" : "s")} since {history.Min(s => s.TimeUtc).ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture)}. "
                        + "Temperature and wear show only where Windows can read them.";
    }

    private async void OnReadDisks(object sender, RoutedEventArgs e) => await ReadDisksAsync();

    private async Task ReadDisksAsync()
    {
        if (_env is not { } env) return;
        ReadDisksButton.IsEnabled = false;
        try
        {
            var history = await Task.Run(() =>
            {
                var disks = env.Disks.Read(CancellationToken.None);
                if (disks.Count > 0 && env.Store.DiskSampleDueToday(DateTime.UtcNow))
                    env.Store.AppendDiskHealth(new DiskHealthSample(DateTime.UtcNow, disks));
                var saved = env.Store.LoadDiskHealth().ToList();
                // Show a fresh reading even when today's sample was already kept.
                if (disks.Count > 0 && !saved.Any(s => s.TimeUtc > DateTime.UtcNow.AddMinutes(-1))) saved.Add(new DiskHealthSample(DateTime.UtcNow, disks));
                return (IReadOnlyList<DiskHealthSample>)saved;
            });
            ShowDisks(history);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { /* card keeps the last readings */ }
        finally { ReadDisksButton.IsEnabled = true; }
    }

    // ------------------------------------------------------------------ reports

    private void ShowReports()
    {
        ReportList.Children.Clear();
        NoReportsText.Visibility = _reports.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var r in _reports)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var open = MakeButton("Show", "FolderOpen24", () => _flow?.OpenInExplorer(r.MarkdownPath));
            System.Windows.Automation.AutomationProperties.SetName(open, "Show the report " + r.Title + " in Explorer");
            DockPanel.SetDock(open, Dock.Right);
            row.Children.Add(open);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = r.Title, Style = (Style)FindResource("BodyStrong"), TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock
            {
                Text = RunSummary.When(r.TimeUtc) + " · " + (r.Kind switch { ReportKind.Checkup => "Checkup", ReportKind.Scan => "Scan", _ => "Run" }) + (r.HtmlPath is not null ? " · also as HTML" : ""),
                Style = (Style)FindResource("Caption"),
            });
            row.Children.Add(text);
            ReportList.Children.Add(row);
        }
        CopyLatestButton.IsEnabled = CanCopy() && _reports.Count > 0;
    }

    private void OnOpenReportsFolder(object sender, RoutedEventArgs e)
    {
        if (_env is null || _flow is null) return;
        var target = _reports.FirstOrDefault()?.MarkdownPath ?? _env.Store.ReportsFolder;
        _flow.OpenInExplorer(target);
    }

    private void OnReportOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_populating || _env is null) return;
        var s = ScheduleSettings.Load(_env.Settings);
        s.SaveHtmlReports = HtmlToggle.IsChecked == true;
        s.CopyReportsToFolder = CopyToggle.IsChecked == true;
        s.Store(_env.Settings);
        _saved = s;
        UpdateCopyControls();
    }

    private void OnPickFolder(object sender, RoutedEventArgs e)
    {
        if (_env is null) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Pick a folder for report copies" };
        if (_saved.ReportCopyFolder is { Length: > 0 } current && Directory.Exists(current)) dialog.InitialDirectory = current;
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var s = ScheduleSettings.Load(_env.Settings);
        s.ReportCopyFolder = dialog.FolderName;
        s.Store(_env.Settings);
        _saved = s;
        UpdateCopyControls();
    }

    private bool CanCopy() => _saved.ReportCopyFolder is { Length: > 0 } f && Directory.Exists(f) && _flow is not null;

    private void UpdateCopyControls()
    {
        PickFolderButton.IsEnabled = CopyToggle.IsChecked == true;
        CopyLatestButton.IsEnabled = CopyToggle.IsChecked == true && CanCopy() && _reports.Count > 0;
        CopyFolderText.Text = _saved.ReportCopyFolder is { Length: > 0 } f
            ? Directory.Exists(f) ? f : f + " (not found)"
            : "No folder picked yet.";
    }

    private async void OnCopyLatest(object sender, RoutedEventArgs e)
    {
        if (_flow is null || _reports.FirstOrDefault() is not { } latest || _saved.ReportCopyFolder is not { Length: > 0 } folder) return;
        var item = await Task.Run(() => ReportCopyPlanner.PlanSaved(latest.MarkdownPath, folder));
        if (item is null)
        {
            ShowNotice("That report is already in the folder.", InfoBarSeverity.Informational);
            return;
        }
        await _flow.ReviewAndRunAsync(new ActionPlan { FeatureId = ScheduleIds.FeatureId, Title = "Copy report", Items = new[] { item } }, CancellationToken.None);
    }

    // ------------------------------------------------------------------ helpers

    private void ShowNotice(string message, InfoBarSeverity severity, Button? action = null)
    {
        Notice.Message = message;
        Notice.Severity = severity;
        Notice.IsOpen = true;
        NoticeActions.Children.Clear();
        if (action is not null) NoticeActions.Children.Add(action);
        NoticeActions.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
        // The notice's ✕ is built when it opens: give it a screen-reader name then.
        Dispatcher.BeginInvoke(() => PcTidy.UI.Controls.AccessibleNames.NameUnnamedButtons(Notice, PcTidy.UI.Controls.AccessibleNames.CloseMessage),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static Button MakeButton(string text, string icon, Action onClick)
    {
        var b = new Button
        {
            Content = text,
            Appearance = ControlAppearance.Secondary,
            Icon = new SymbolIcon { Symbol = Enum.TryParse<SymbolRegular>(icon, out var s) ? s : SymbolRegular.Open24 },
            Margin = new Thickness(8, 0, 0, 0),
        };
        System.Windows.Automation.AutomationProperties.SetName(b, text);
        b.Click += (_, _) => onClick();
        return b;
    }
}
