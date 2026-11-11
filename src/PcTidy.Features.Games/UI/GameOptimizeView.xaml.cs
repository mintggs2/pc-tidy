using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Games.Optimize;
using PcTidy.UI.Controls;
using PcTidy.UI.Flow;
using Wpf.Ui.Controls;

namespace PcTidy.Features.Games.UI;

/// <summary>
/// Games → Optimize as master/detail (layout spec §4.4). Left: search, All · Installed · Not tuned, and each game with
/// its status chip (from the real config files). Right: the selected game's recipe rows with evidence tags and measured
/// numbers, the "Doesn't help (measured)" shelf (greyed, never applied), and Reset to my backup · Clear shader cache
/// (off by default) · Preview. Read-only until the user confirms through the shell's flow host; undo from History.
/// </summary>
public partial class GameOptimizeView : UserControl
{
    private readonly IServiceProvider _services;
    private readonly OptimizerScanner _scanner = new();
    private IReadOnlyList<GameEntry> _entries = Array.Empty<GameEntry>();
    private GameEntry? _selected;
    private BackupLookup? _backup;
    private List<RecipeRow> _rows = new();
    private CancellationTokenSource? _scanCts;
    private bool _scanned;

    public GameOptimizeView(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;
        FilterPicker.SelectionChanged += (_, _) => RefreshList();
        ShaderWarning.Text = GameCleanupTargetsText.Warning;
        ShowDetail(null);
    }

    private IFlowHost? Host => _services.GetService(typeof(IFlowHost)) as IFlowHost;
    private IShellFlowHost? Shell => _services.GetService(typeof(IShellFlowHost)) as IShellFlowHost ?? Host as IShellFlowHost;

    /// <summary>The entries of the last scan (tests and the detail pane).</summary>
    internal IReadOnlyList<GameEntry> Entries => _entries;

    /// <summary>Scans the first time the tab is shown (reads launchers and config files, changes nothing).</summary>
    public void EnsureScanned()
    {
        if (_scanned) return;
        if (IsLoaded) _ = ScanAsync();
        else
        {
            void OnLoaded(object s, RoutedEventArgs e) { Loaded -= OnLoaded; EnsureScanned(); }
            Loaded += OnLoaded;
        }
    }

    private void OnScanClick(object sender, RoutedEventArgs e) => _ = ScanAsync();

    public async Task ScanAsync()
    {
        if (_scanCts is not null) return;
        _scanned = true;
        _scanCts = new CancellationTokenSource();
        ScanButton.IsEnabled = false;
        ScanningState.Visibility = Visibility.Visible;
        MasterDetail.Visibility = Visibility.Collapsed;
        ScanMessage.Text = "Starting…";
        try
        {
            var ctx = ScanRunner.CreateContext(_services, ScanDepth.Quick);
            var progress = new Progress<ScanProgress>(p => Dispatcher.BeginInvoke(() => ScanMessage.Text = p.Message));
            var token = _scanCts.Token;
            var result = await Task.Run(() => _scanner.ScanAsync(ctx, progress, token), token);
            Load(_scanner.LastEntries, result.Notes);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Host?.ShowMessage("Scan didn't finish", "Something went wrong while looking: " + ex.Message + " Nothing was changed.", isError: true);
        }
        finally
        {
            _scanCts?.Dispose();
            _scanCts = null;
            ScanButton.IsEnabled = true;
            ScanButton.Content = "Scan again";
            ScanningState.Visibility = Visibility.Collapsed;
            MasterDetail.Visibility = Visibility.Visible;
        }
    }

    internal void Load(IReadOnlyList<GameEntry> entries, IReadOnlyList<string> notes)
    {
        var keep = _selected?.Key;
        _entries = entries;
        NotesPanel.Children.Clear();
        foreach (var note in notes)
        {
            var bar = new InfoBar { Message = note, IsOpen = true, IsClosable = true, Severity = InfoBarSeverity.Informational, Margin = new Thickness(0, 0, 0, 8) };
            bar.Loaded += (_, _) => GamesTabView.NameCloseButtons(bar);
            NotesPanel.Children.Add(bar);
        }
        RefreshList(keep);
    }

    // ---------------- list: search + filter ----------------

    private GameListFilter Filter => FilterPicker.SelectedIndex switch { 1 => GameListFilter.Installed, 2 => GameListFilter.NotTuned, _ => GameListFilter.All };

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => RefreshList();

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Down || GameListBox.Items.Count == 0) return;
        if (GameListBox.SelectedIndex < 0) GameListBox.SelectedIndex = 0;
        (GameListBox.ItemContainerGenerator.ContainerFromIndex(GameListBox.SelectedIndex) as ListBoxItem)?.Focus();
        e.Handled = true;
    }

    private void RefreshList(string? keepKey = null)
    {
        keepKey ??= _selected?.Key;
        var shown = GameList.Apply(_entries, SearchBox.Text, Filter).Select(e => new GameListRow(e)).ToList();
        GameListBox.ItemsSource = shown;
        NoMatch.Visibility = shown.Count == 0 && _entries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var again = shown.FirstOrDefault(r => r.Entry.Key == keepKey) ?? shown.FirstOrDefault();
        GameListBox.SelectedItem = again;
        if (again is null) ShowDetail(null);
    }

    private void OnGameSelected(object sender, SelectionChangedEventArgs e)
        => ShowDetail((GameListBox.SelectedItem as GameListRow)?.Entry);

    // ---------------- detail ----------------

    private void ShowDetail(GameEntry? entry)
    {
        _selected = entry;
        _backup = null;
        NoSelection.Visibility = entry is null ? Visibility.Visible : Visibility.Collapsed;
        DetailPane.Visibility = entry is null ? Visibility.Collapsed : Visibility.Visible;
        if (entry is null) { _rows = new(); UpdateButtons(); return; }

        DetailTitle.Text = entry.Name;
        DetailStatus.Text = entry.StatusLabel;
        DetailStatus.Tone = GameListRow.ToneFor(entry.Status);
        AutomationProperties.SetName(DetailStatus, "Status: " + entry.StatusLabel);
        DetailSummary.Text = entry.Summary;
        DetailWhy.Text = entry.StatusWhy;

        var settings = entry.Settings.Concat(entry.GpuPreference is { } gpu ? new[] { gpu } : Array.Empty<ScanItem>())
            .Select(i => new RecipeRow(i, greyed: false)).ToList();
        var placebo = entry.Placebo.Select(i => new RecipeRow(i, greyed: true)).ToList();
        foreach (var r in settings) r.PropertyChanged += (_, _) => UpdateButtons();
        _rows = settings;
        SettingsList.ItemsSource = settings;
        PlaceboList.ItemsSource = placebo;
        NoSettings.Visibility = settings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PlaceboSection.Visibility = placebo.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        DetailScroll.ScrollToTop();

        ShaderCacheCheck.IsChecked = false; // never on by default (card C9)
        var cache = entry.ShaderCache;
        ShaderCacheCheck.IsEnabled = cache is { DisabledReason: null, Actions.Count: > 0 };
        ShaderCacheCheck.Content = cache is { SizeBytes: > 0 } ? $"Clear shader cache ({Sizes.Format(cache.SizeBytes)})" : "Clear shader cache";
        ShaderWarning.Text = cache is null
            ? "PC Tidy knows no shader cache of its own for this game. Your GPU driver's caches are in Clean → GPU shader caches."
            : cache.DisabledReason ?? GameCleanupTargetsText.Warning;

        ResetNote.Text = entry.Installed ? "Looking for a backup…" : "Not installed: nothing to reset or apply.";
        UpdateButtons();
        if (entry.Installed) _ = LookUpBackupAsync(entry);
    }

    private async Task LookUpBackupAsync(GameEntry entry)
    {
        BackupLookup lookup;
        try
        {
            var journal = _services.GetService(typeof(IRunJournal)) as IRunJournal;
            var runs = journal is null ? Array.Empty<RunRecord>() : await journal.ListAsync(CancellationToken.None);
            lookup = BackupFinder.For(entry, runs);
        }
        catch (Exception ex)
        {
            lookup = new BackupLookup(Array.Empty<RunRecord>(), "PC Tidy couldn't read History: " + ex.Message);
        }
        if (!ReferenceEquals(_selected, entry)) return; // moved on
        _backup = lookup;
        ResetNote.Text = ResetText(entry, lookup);
        AutomationProperties.SetHelpText(ResetButton, ResetNote.Text);
        UpdateButtons();
    }

    /// <summary>What Reset will do, with every step it will undo listed before the user clicks.</summary>
    internal static string ResetText(GameEntry entry, BackupLookup lookup)
    {
        if (!lookup.CanReset) return lookup.Problem ?? "";
        var head = $"Reset puts back the backup PC Tidy made before changing {entry.Name}'s settings ({lookup.Runs.Count} change{(lookup.Runs.Count == 1 ? "" : "s")} in History). It undoes:";
        return head + string.Concat(lookup.Steps.Select(s => "\n• " + s));
    }

    private void OnSelectionChanged(object sender, RoutedEventArgs e) => UpdateButtons();

    private IReadOnlyList<ScanItem> Picked()
    {
        if (_selected is null) return Array.Empty<ScanItem>();
        var picked = _rows.Where(r => r.IsSelectable && r.IsChecked).Select(r => r.Item).ToList();
        if (ShaderCacheCheck.IsChecked == true && _selected.ShaderCache is { DisabledReason: null } cache && cache.Actions.Count > 0) picked.Add(cache);
        return picked;
    }

    private void UpdateButtons()
    {
        var picked = Picked();
        PreviewButton.IsEnabled = picked.Count > 0;
        PreviewButton.Content = picked.Count == 0 ? "Preview" : $"Preview {picked.Count} change{(picked.Count == 1 ? "" : "s")}";
        AutomationProperties.SetName(PreviewButton, picked.Count == 0 ? "Preview: tick a setting first" : $"Preview {picked.Count} ticked change{(picked.Count == 1 ? "" : "s")}");
        ResetButton.IsEnabled = _backup?.CanReset == true;
    }

    /// <summary>The selected game's ticked rows as one plan (one game per run, so "Reset to my backup" stays per game).</summary>
    internal ActionPlan? BuildPlan()
    {
        if (_selected is null) return null;
        var picked = Picked();
        if (picked.Count == 0) return null;
        var plan = PlanBuilder.FromSelection(Common.GamesContext.FeatureId, $"Game settings: {_selected.Name}", picked);
        return plan with { Items = EditMerger.Merge(plan.Items) };
    }

    private async void OnPreview(object sender, RoutedEventArgs e)
    {
        ActionPlan? plan;
        try { plan = BuildPlan(); }
        catch (EditMerger.FileChangedException ex) { Host?.ShowMessage("Scan again first", ex.Message); return; }
        if (plan is null || plan.Items.Count == 0) return;
        try
        {
            bool ran;
            if (Shell is { } shell)
                ran = (await shell.ReviewAndRunAsync(new[] { plan }, new FlowOptions { Title = plan.Title, ShowResult = true }, CancellationToken.None)).Kind == FlowOutcomeKind.Ran;
            else ran = Host is { } host && await host.ReviewAndRunAsync(plan, CancellationToken.None) is not null;
            if (ran) await ScanAsync();
        }
        catch (Exception ex)
        {
            Host?.ShowMessage("That didn't work", ex.Message + " Anything that already ran is in History, where you can undo it.", isError: true);
        }
    }

    private async void OnReset(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _backup is not { CanReset: true } backup) return;
        if (Shell is not { } shell)
        {
            Host?.ShowMessage("Undo it from History", "Open History and undo the change to put this game's settings back.");
            return;
        }
        try
        {
            // The steps are listed under the button (ResetText) before this click; only runs where every finished step
            // belongs to this game get here (BackupFinder). The normal undo path: the engine puts back its byte-exact
            // backup (or the exact registry value) for each step.
            await shell.UndoRunsAsync(backup.Runs, CancellationToken.None);
            await ScanAsync();
        }
        catch (Exception ex)
        {
            Host?.ShowMessage("That didn't work", ex.Message + " Nothing else was changed. You can also undo it from History.", isError: true);
        }
    }
}

/// <summary>The C9 warning shown next to "Clear shader cache".</summary>
internal static class GameCleanupTargetsText
{
    public static string Warning => Catalog.Games.GameCleanupTargets.ShaderRebuildWarning + " " + Catalog.Games.FpsMeasurements.ShaderRebuild.Line + ".";
}

/// <summary>One game in the left list.</summary>
public sealed class GameListRow
{
    public GameListRow(GameEntry entry) => Entry = entry;

    public GameEntry Entry { get; }
    public string Name => Entry.Name;
    public string Status => Entry.StatusLabel;
    public PillTone Tone => ToneFor(Entry.Status);
    public string AutomationName => $"{Entry.Name}, {Entry.StatusLabel}";

    public static PillTone ToneFor(GameTuneStatus s) => s switch
    {
        GameTuneStatus.NotTuned => PillTone.Caution,
        GameTuneStatus.Tuned => PillTone.Success,
        GameTuneStatus.InfoOnly => PillTone.Info,
        _ => PillTone.Neutral,
    };

    public override string ToString() => AutomationName;
}

/// <summary>One recipe row in the detail pane: a tick when it can be applied, never ticked by default.</summary>
public sealed partial class RecipeRow : ObservableObject
{
    public RecipeRow(ScanItem item, bool greyed)
    {
        Item = item;
        _shelf = greyed;
        IsGreyed = greyed || item.DisabledReason is not null;
        IsSelectable = !greyed && item.DisabledReason is null && item.Actions.Count > 0;
        _isChecked = false;
    }

    private readonly bool _shelf;

    public ScanItem Item { get; }
    public bool IsSelectable { get; }
    public bool IsGreyed { get; }

    [ObservableProperty] private bool _isChecked;

    public string Title => Item.Title;
    public string Reason => Item.Reason;
    public string? Measured => Item.Details?.GetValueOrDefault("Measured");
    public bool HasMeasured => !string.IsNullOrEmpty(Measured);

    /// <summary>
    /// Shelf rows: "never applied" (the number is on its own line). Other rows: the disabled reason, or the current state
    /// ("Already set", "Now: -high").
    /// </summary>
    public string? Note => _shelf
        ? (HasMeasured ? "PC Tidy never applies this." : "No test found that shows a gain. PC Tidy never applies this.")
        : Item.DisabledReason ?? (Item.CurrentState is { Length: > 0 } s && s != "Not installed" ? s : null);
    public bool HasNote => !string.IsNullOrEmpty(Note);

    public string SourceLine
    {
        get
        {
            var d = Item.Details;
            var parts = new List<string>();
            if (d?.GetValueOrDefault("Evidence") is { } ev) parts.Add("Evidence: " + ev);
            if (d?.GetValueOrDefault("Launch option") is { } lo) parts.Add("Launch option: " + lo);
            var src = d?.GetValueOrDefault("Measured source") ?? d?.GetValueOrDefault("Source");
            if (src is not null) parts.Add("Source: " + src);
            return string.Join(" · ", parts);
        }
    }

    public string Tag => TagFor(Item);
    public PillTone Tone => Item.Details?.GetValueOrDefault("Verdict") switch
    {
        { } v when v.StartsWith("Proven", StringComparison.Ordinal) => PillTone.Success,
        { } v when v.StartsWith("Situational", StringComparison.Ordinal) => PillTone.Info,
        { } v when v.StartsWith("Placebo", StringComparison.Ordinal) => PillTone.Neutral,
        { } v when v.StartsWith("Harmful", StringComparison.Ordinal) => PillTone.Critical,
        _ => PillTone.Neutral,
    };
    public string TagAutomationName => "Evidence: " + Tag;

    /// <summary>Ticking only includes the row in the preview; nothing changes until the user confirms (UI review M7).</summary>
    public string AutomationName => $"Include in preview: {Title}";

    /// <summary>"Proven", "Situational", "Doesn't help" or "Harmful" (the spec's pills, same as Tweaks), plus "· unverified" for community reports.</summary>
    internal static string TagFor(ScanItem item)
    {
        var verdict = item.Details?.GetValueOrDefault("Verdict") ?? "";
        var label = verdict.StartsWith("Proven", StringComparison.Ordinal) ? "Proven"
            : verdict.StartsWith("Situational", StringComparison.Ordinal) ? "Situational"
            : verdict.StartsWith("Placebo", StringComparison.Ordinal) ? "Doesn't help"
            : verdict.StartsWith("Harmful", StringComparison.Ordinal) ? "Harmful"
            : "Info";
        var unverified = item.Details?.GetValueOrDefault("Evidence")?.StartsWith("Community report", StringComparison.Ordinal) == true;
        return unverified ? label + " · unverified" : label;
    }
}
