using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using PcTidy.App.Composition;
using PcTidy.App.Shell;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.UI.Controls;
using PcTidy.UI.Flow;
using Wpf.Ui.Controls;

namespace PcTidy.App.Pages;

/// <summary>One quarantined run (header row). Opens to show its items, each with a tick for "Restore selected".</summary>
public sealed class QuarantineRunRow : ListRow
{
    private bool _isExpanded;

    public QuarantineRunRow(string runId, string title, DateTime whenUtc, IReadOnlyList<QuarantineEntry> entries, int keepDays)
    {
        RunId = runId;
        Title = title;
        WhenUtc = whenUtc;
        Entries = entries;
        Bytes = entries.Sum(e => e.SizeBytes);
        AgeDays = Math.Max(0, (int)(DateTime.UtcNow - whenUtc).TotalDays);
        IsOld = AgeDays >= keepDays;
        Items = entries.Select(e => new QuarantineItemRow(this, e)).ToList();
        SelectionMargin = new Thickness(0, 6, 0, 0);
        SelectionRadius = new CornerRadius(8);
    }

    public string RunId { get; }
    public string Title { get; }
    public DateTime WhenUtc { get; }
    public IReadOnlyList<QuarantineEntry> Entries { get; }
    public IReadOnlyList<QuarantineItemRow> Items { get; }
    public long Bytes { get; }
    public int AgeDays { get; }
    public bool IsOld { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetProperty(ref _isExpanded, value)) return;
            OnPropertyChanged(nameof(ChevronSymbol));
            OnPropertyChanged(nameof(ExpandName));
            OnPropertyChanged(nameof(AutomationName));
        }
    }

    public SymbolRegular ChevronSymbol => IsExpanded ? SymbolRegular.ChevronUp24 : SymbolRegular.ChevronDown24;
    public string ExpandName => (IsExpanded ? "Hide the items of " : "Show the items of ") + Title;
    public string Detail => $"{Entries.Count:N0} item{PlanFacts.S(Entries.Count)} · quarantined {RunSummary.WhenInSentence(WhenUtc)}";
    public string AgeText => AgeDays == 0 ? "Today" : AgeDays == 1 ? "1 day old" : $"{AgeDays} days old";
    /// <summary>Old runs get a calm caution tone, never red: nothing is wrong, it's just a reminder.</summary>
    public PillTone AgeTone => IsOld ? PillTone.Caution : PillTone.Neutral;
    public string RestoreName => $"Restore all of {Title}";
    public string DeleteName => $"Delete {Title} for good";
    public override string AutomationName => $"{Title}, {Detail}, {Sizes.Format(Bytes)}, {AgeText}" + (IsExpanded ? ", open" : ", closed");
    public override string HelpText => "Press Enter to show or hide its items.";
}

public sealed class QuarantineItemRow : ListRow
{
    private bool _isChecked;
    internal Action? Changed;

    public QuarantineItemRow(QuarantineRunRow run, QuarantineEntry entry)
    {
        Run = run;
        Entry = entry;
        Name = Path.GetFileName(entry.OriginalPath.TrimEnd('\\'));
        Folder = Path.GetDirectoryName(entry.OriginalPath) ?? "";
    }

    public QuarantineRunRow Run { get; }
    public QuarantineEntry Entry { get; }
    public string Name { get; }
    public string Folder { get; }
    public bool IsChecked { get => _isChecked; set { if (SetProperty(ref _isChecked, value)) { Changed?.Invoke(); OnPropertyChanged(nameof(AutomationName)); } } }
    public override string AutomationName => $"{Name} from {Folder}, {Sizes.Format(Entry.SizeBytes)}{(IsChecked ? ", ticked" : ", not ticked")}";
    public override string HelpText => "Press Space to tick or untick.";
}

/// <summary>
/// Quarantine: runs with age and size, restore all / selected, delete for good (typed confirmation),
/// and two purge helpers that only act after the user confirms: older than N days, and over the size cap.
/// Store calls run off the UI thread (they read and move files).
/// </summary>
public partial class QuarantinePage : UserControl, IFixedHeightPage
{
    private readonly AppServices _services;
    private List<QuarantineRunRow> _runs = new();
    private bool _refreshing;

    public QuarantinePage(AppServices services)
    {
        _services = services;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            AdminHost.Content ??= AdminQuarantineSection.TryCreate(_services, Host, () => Window, practice: _services.Args.SandboxRoot is not null);
            RefreshAsync().Forget(Host, "Couldn't read the quarantine");
        };
    }

    private FlowHost Host => (FlowHost)_services.Get<IShellFlowHost>();
    private MainWindow Window => (MainWindow)(System.Windows.Window.GetWindow(this) ?? Application.Current.MainWindow);
    private IQuarantineStore Store => _services.Get<IQuarantineStore>();
    private AppSettings Settings => _services.Get<AppSettings>();
    private int CapGb => ShellPrefs.Get(Settings).QuarantineCapGb;

    public async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try { await LoadAsync(); }
        finally { _refreshing = false; }
    }

    private async Task LoadAsync()
    {
        IReadOnlyList<QuarantineEntry> entries;
        IReadOnlyList<RunRecord> runs;
        try
        {
            var store = Store;
            var journal = _services.Get<IRunJournal>();
            (entries, runs) = await Task.Run(async () => (
                (IReadOnlyList<QuarantineEntry>)(await store.ListAsync(null, CancellationToken.None)).Where(e => !e.Restored && !e.Purged).ToList(),
                await journal.ListAsync(CancellationToken.None)));
        }
        catch (Exception ex)
        {
            Host.ShowMessage("Couldn't read the quarantine", ex.Message, isError: true);
            return;
        }
        // Sections of an Optimize-everything run say so, so two "Clean" runs on one day aren't confusing.
        var titles = runs.GroupBy(r => r.RunId).ToDictionary(g => g.Key, g =>
        {
            var r = g.First();
            return (r.ParentRunId != null ? $"Optimize everything: {r.Title}" : r.Title, r.CreatedUtc);
        });
        int keep = Settings.QuarantineDays;
        var open = _runs.Where(r => r.IsExpanded).Select(r => r.RunId).ToHashSet();
        _runs = entries.GroupBy(e => e.RunId)
            .Select(g =>
            {
                var t = titles.TryGetValue(g.Key, out var x) ? x : ("Earlier run", g.Min(e => e.QuarantinedUtc));
                return new QuarantineRunRow(g.Key, t.Item1, t.Item2, g.ToList(), keep) { IsExpanded = open.Contains(g.Key) };
            })
            .OrderByDescending(r => r.WhenUtc).ToList();
        foreach (var r in _runs) foreach (var i in r.Items) i.Changed = UpdateBar;

        long total = _runs.Sum(r => r.Bytes);
        Subtitle.Text = total > 0
            ? $"{Sizes.Format(total)} in {_runs.Count} run{PlanFacts.S(_runs.Count)}, all restorable. Kept {keep} days; " + (Settings.AskBeforePurge ? "PC Tidy asks before deleting anything." : "then emptied.")
            : "Nothing here right now.";
        EmptyCard.Visibility = _runs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Bar.Visibility = _runs.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        Rebuild();
        ShowReminders(total);
        UpdateBar();
    }

    private void Rebuild()
    {
        var rows = new List<ListRow>();
        foreach (var r in _runs)
        {
            rows.Add(r);
            if (r.IsExpanded) rows.AddRange(r.Items);
        }
        var selected = List.SelectedItem;
        List.ItemsSource = new ObservableCollection<ListRow>(rows);
        if (selected != null && rows.Contains(selected)) List.SelectedItem = selected;
    }

    private void ShowReminders(long total)
    {
        Reminders.Children.Clear();
        int keep = Settings.QuarantineDays;
        var old = _runs.Where(r => r.IsOld).ToList();
        PurgeOldButton.Content = $"Delete older than {keep} days…";
        PurgeOldButton.IsEnabled = old.Count > 0;
        AutomationProperties.SetName(PurgeOldButton, (string)PurgeOldButton.Content);
        TrimButton.Content = CapGb > 0 ? $"Keep under {CapGb} GB…" : "No size limit set";
        TrimButton.IsEnabled = CapGb > 0 && total > (long)CapGb << 30;
        AutomationProperties.SetName(TrimButton, (string)TrimButton.Content);

        if (old.Count > 0)
            Reminders.Children.Add(Reminder($"{old.Count} run{PlanFacts.S(old.Count)} older than {keep} days ({Sizes.Format(old.Sum(r => r.Bytes))}).",
                "Nothing is deleted on its own. Delete them for good now, or keep them a while longer.", "Review and delete…", OnPurgeOld));
        if (CapGb > 0 && total > (long)CapGb << 30)
            Reminders.Children.Add(Reminder($"Quarantine is over your {CapGb} GB limit.",
                "PC Tidy can delete the oldest runs until it's under the limit. It asks first.", "Review…", OnTrim));
    }

    /// <summary>A calm reminder with its action button underneath (InfoBar can't show buttons itself).</summary>
    private static UIElement Reminder(string title, string message, string action, RoutedEventHandler click)
    {
        var b = new Wpf.Ui.Controls.Button { Content = action };
        AutomationProperties.SetName(b, action);
        b.Click += click;
        var bar = new InfoBar { Title = title, Message = message, IsOpen = true, IsClosable = true, Severity = InfoBarSeverity.Informational, Margin = new Thickness(0, 0, 0, 10) };
        return InfoBars.WithActions(bar, b);
    }

    private void UpdateBar()
    {
        var picked = _runs.SelectMany(r => r.Items).Where(i => i.IsChecked).ToList();
        SelectedText.Text = picked.Count == 0
            ? "Open a run (arrow on the right) to tick single items, or use Restore all"
            : $"Selected {picked.Count:N0} item{PlanFacts.S(picked.Count)} · {Sizes.Format(picked.Sum(i => i.Entry.SizeBytes))}";
        RestoreSelected.IsEnabled = picked.Count > 0;
    }

    // ---------------- list interaction ----------------

    private void OnRunHeaderClick(object sender, MouseButtonEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d != null; d = d is Visual ? VisualTreeHelper.GetParent(d) : null)
            if (d is ButtonBase) return;
        if ((sender as FrameworkElement)?.DataContext is QuarantineRunRow run) Toggle(run);
    }

    private void OnToggleRun(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is QuarantineRunRow run) Toggle(run);
    }

    /// <summary>Opens the newest run (screenshots, "show me" links).</summary>
    public void OpenFirstRun()
    {
        if (_runs.FirstOrDefault() is { IsExpanded: false } run) Toggle(run);
    }

    private void Toggle(QuarantineRunRow run)
    {
        run.IsExpanded = !run.IsExpanded;
        Rebuild();
        List.SelectedItem = run;
    }

    private void OnListKey(object sender, KeyEventArgs e)
    {
        switch (List.SelectedItem)
        {
            case QuarantineRunRow run when e.Key is Key.Enter or Key.Space:
                Toggle(run); e.Handled = true; break;
            case QuarantineItemRow item when e.Key == Key.Space:
                item.IsChecked = !item.IsChecked; e.Handled = true; break;
        }
    }

    // ---------------- restore ----------------

    private void OnRestoreRun(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is QuarantineRunRow run) RestoreAsync(run.Entries).Forget(Host, "Restore didn't finish");
    }

    private void OnRestoreSelected(object sender, RoutedEventArgs e)
        => RestoreAsync(_runs.SelectMany(r => r.Items).Where(i => i.IsChecked).Select(i => i.Entry).ToList()).Forget(Host, "Restore didn't finish");

    private async Task RestoreAsync(IReadOnlyList<QuarantineEntry> entries)
    {
        if (entries.Count == 0) return;
        var renamed = new List<string>();
        var failed = new List<string>();
        var store = Store;
        using (Host.Working("the restore"))
        {
            Window.ShowProgress("Putting files back…", canStop: false);
            try
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    Window.ReportProgress(new RunProgress(i, entries.Count, entry.OriginalPath));
                    try
                    {
                        var restoredTo = await Task.Run(() => store.RestoreAsync(entry, CancellationToken.None));
                        if (!string.Equals(restoredTo, entry.OriginalPath, StringComparison.OrdinalIgnoreCase)) renamed.Add(Path.GetFileName(restoredTo));
                    }
                    catch (Exception ex) { failed.Add($"{Path.GetFileName(entry.OriginalPath)}: {ex.Message}"); }
                }
            }
            finally { Window.HideProgress(); }
        }
        int ok = entries.Count - failed.Count;
        var msg = $"{ok:N0} item{PlanFacts.S(ok)} put back where {(ok == 1 ? "it was" : "they were")}.";
        if (renamed.Count > 0) msg += $" {renamed.Count} came back with a new name because something was in the way (for example {renamed[0]}).";
        if (failed.Count > 0) msg += $" {failed.Count} couldn't be restored: {failed[0]}";
        Host.ShowMessage(failed.Count == 0 ? "Restored" : "Mostly restored", msg, isError: failed.Count > 0);
        await RefreshAsync();
    }

    // ---------------- delete for good (always typed confirmation) ----------------

    private void OnDeleteRun(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is QuarantineRunRow run)
            PurgeAsync(new[] { run }, $"Delete \"{run.Title}\" for good?", $"{run.Entries.Count:N0} items ({Sizes.Format(run.Bytes)}) will be deleted permanently. You won't be able to restore them.")
                .Forget(Host, "Delete didn't finish");
    }

    private void OnPurgeOld(object sender, RoutedEventArgs e)
    {
        var old = _runs.Where(r => r.IsOld).ToList();
        if (old.Count == 0) return;
        PurgeAsync(old, $"Delete runs older than {Settings.QuarantineDays} days?",
            $"{old.Count} run{PlanFacts.S(old.Count)} ({Sizes.Format(old.Sum(r => r.Bytes))}) will be deleted permanently. Newer runs stay restorable.")
            .Forget(Host, "Delete didn't finish");
    }

    private void OnTrim(object sender, RoutedEventArgs e)
    {
        long cap = (long)CapGb << 30, total = _runs.Sum(r => r.Bytes);
        var victims = new List<QuarantineRunRow>();
        foreach (var r in _runs.OrderBy(r => r.WhenUtc))
        {
            if (total <= cap) break;
            victims.Add(r);
            total -= r.Bytes;
        }
        if (victims.Count == 0) return;
        PurgeAsync(victims, $"Get quarantine under {CapGb} GB?",
            $"The {victims.Count} oldest run{PlanFacts.S(victims.Count)} ({Sizes.Format(victims.Sum(r => r.Bytes))}) will be deleted permanently.")
            .Forget(Host, "Delete didn't finish");
    }

    private async Task PurgeAsync(IReadOnlyList<QuarantineRunRow> runs, string title, string message)
    {
        if (!await Dialogs.ConfirmTypedAsync(Host, Window.DialogHost, title, message, "Delete for good")) return;
        var failures = new List<string>();
        var store = Store;
        using (Host.Working("the delete"))
        {
            Window.ShowProgress("Deleting for good…", canStop: false);
            try
            {
                for (int i = 0; i < runs.Count; i++)
                {
                    var r = runs[i];
                    Window.ReportProgress(new RunProgress(i, runs.Count, r.Title));
                    try { await Task.Run(() => store.PurgeRunAsync(r.RunId, CancellationToken.None)); }
                    catch (Exception ex) { failures.Add($"{r.Title}: {ex.Message}"); }
                }
            }
            finally { Window.HideProgress(); }
        }
        if (failures.Count > 0) Host.ShowMessage("Some runs weren't deleted", string.Join(" ", failures), isError: true);
        await RefreshAsync();
    }
}
