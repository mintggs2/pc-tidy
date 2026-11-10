using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PcTidy.Core;
using PcTidy.Features.Search.Backends;
using PcTidy.Features.Search.Favorites;
using PcTidy.Features.Search.Index;
using PcTidy.Features.Search.Results;
using PcTidy.UI.Controls;
using PcTidy.UI.Flow;

namespace PcTidy.Features.Search.UI;

/// <summary>
/// The Search page: a big search box (results while you type, 150 ms after the last key), a virtualized results
/// list, PC Tidy's own Favorites, and the "Suggest a folder structure" tab. Searching only reads; the only changes
/// it can start (pin to Quick access, structure moves) go through the shell's confirm → run → undo flow.
/// Works at 1000 px wide: wide pages show Favorites on the right; narrow ones give the results the whole width (all four
/// columns sized from the list's width) and open Favorites under them from a button. The results list is the only thing that scrolls.
/// </summary>
public partial class SearchPage : UserControl, IFixedHeightPage
{
    private const int MaxResults = 2_000_000;

    private readonly IServiceProvider _services;
    private readonly SearchRuntime _runtime;
    private readonly FavoritesList _favorites;
    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource? _searchCts;
    private SearchResults? _results;
    private SearchSort _sort = SearchSort.Name;
    private bool _descending;
    private long _searchedVersion = -1;
    private StructureTabView? _structure;
    /// <summary>Below this page width Favorites leaves the side, so the results keep room for all four columns.</summary>
    private const double SideFavoritesMinWidth = 860;
    private bool _narrow;

    public SearchPage(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;
        _runtime = SearchRuntime.For(services);
        _favorites = new FavoritesList(services.Get<AppSettings>());
        ShowRiskPill(searchTab: true);
        ShowSortArrows();
        TipsList.ItemsSource = SearchTips.All;
        _debounce = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = TimeSpan.FromMilliseconds(150) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); SearchNow(); };
        Tabs.SelectionChanged += (_, _) => ShowTab(Tabs.SelectedIndex);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        RefreshFavorites();
        UpdateStatus();
    }

    private IFlowHost? Host => _services.GetService(typeof(IFlowHost)) as IFlowHost;

    // ------------------------------------------------------------------ lifecycle

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _runtime.Index.Progress += OnIndexProgress;
        _runtime.Index.SnapshotChanged += OnSnapshotChanged;
        _runtime.EnsureStartedAsync().Forget(Host, "The search index didn't load");
        QueryBox.Focus();
        UpdateStatus();
        NameClearButton();
    }

    /// <summary>The search box's built-in clear (X) button has no name of its own; screen readers need one.</summary>
    private void NameClearButton()
    {
        QueryBox.ApplyTemplate();
        foreach (var button in Descendants(QueryBox).OfType<System.Windows.Controls.Primitives.ButtonBase>())
            if (string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(button)))
            {
                System.Windows.Automation.AutomationProperties.SetName(button, "Clear the search");
                button.ToolTip ??= "Clear the search";
            }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    /// <summary>The header pill says what the open tab can do: searching only looks; the structure tab moves what you pick.</summary>
    private void ShowRiskPill(bool searchTab)
    {
        RiskPill.Text = searchTab ? "Only looks" : "You pick each move";
        RiskPill.Tone = searchTab ? PillTone.Success : PillTone.Caution;
    }

    // ------------------------------------------------------------------ layout

    /// <summary>Wide: Favorites beside the results. Narrow: results get the full width; Favorites opens under them.</summary>
    private void OnSearchTabSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool narrow = e.NewSize.Width < SideFavoritesMinWidth;
        if (narrow == _narrow && e.PreviousSize.Width > 0) return;
        _narrow = narrow;
        FavoritesColumn.Width = new GridLength(narrow ? 0 : 260);
        FavoritesGap.Width = new GridLength(narrow ? 0 : 16);
        Grid.SetColumn(FavoritesCard, narrow ? 0 : 2);
        Grid.SetColumnSpan(FavoritesCard, narrow ? 3 : 1);
        Grid.SetRow(FavoritesCard, narrow ? 1 : 0);
        FavoritesCard.Margin = new Thickness(0, narrow ? 12 : 0, 0, 0);
        FavoritesCard.MaxHeight = narrow ? 230 : double.PositiveInfinity;
        FavoritesToggle.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
        UpdateFavoritesVisibility();
    }

    private void OnFavoritesToggled(object sender, RoutedEventArgs e) => UpdateFavoritesVisibility();

    private void UpdateFavoritesVisibility()
        => FavoritesCard.Visibility = !_narrow || FavoritesToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Column widths follow the list (Name 35 %, Folder 40 %, Size and Modified the rest), so all four always show.</summary>
    private void OnResultsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double width = Math.Max(0, e.NewSize.Width - 32); // row margins and the vertical scroll bar
        if (width <= 0) return;
        double size = Math.Max(76, width * 0.10), modified = Math.Max(152, width * 0.15);
        double rest = Math.Max(160, width - size - modified);
        NameColumn.Width = rest * 0.47;
        FolderColumn.Width = rest * 0.53;
        SizeColumn.Width = size;
        ModifiedColumn.Width = modified;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _runtime.Index.Progress -= OnIndexProgress;
        _runtime.Index.SnapshotChanged -= OnSnapshotChanged;
        _searchCts?.Cancel();
        _debounce.Stop();
    }

    private void OnIndexProgress(IndexProgress p) => Dispatcher.BeginInvoke(() =>
    {
        BuildBar.Visibility = Visibility.Visible;
        if (_results is null) StatusText.Text = "Building the index… you can search already. " + p.Message + ".";
    });

    /// <summary>A refreshed index re-runs the current search (only when PC Tidy's own index is answering).</summary>
    private void OnSnapshotChanged(IndexSnapshot s) => Dispatcher.BeginInvoke(() =>
    {
        UpdateStatus();
        if (QueryBox.Text.Length > 0 && _runtime.Router.Active is OwnIndexBackend && s.Version != _searchedVersion) SearchNow();
    }, DispatcherPriority.Background);

    private void ShowTab(int index)
    {
        bool search = index == 0;
        ShowRiskPill(search);
        SearchTab.Visibility = search ? Visibility.Visible : Visibility.Collapsed;
        StructureHost.Visibility = search ? Visibility.Collapsed : Visibility.Visible;
        if (!search && _structure is null) StructureHost.Content = _structure = new StructureTabView(_services);
    }

    // ------------------------------------------------------------------ search

    private void OnQueryChanged(object sender, TextChangedEventArgs e)
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _debounce.Stop(); SearchNow(); e.Handled = true; }
        else if (e.Key == Key.Down && ResultsList.Items.Count > 0)
        {
            ResultsList.SelectedIndex = Math.Max(0, ResultsList.SelectedIndex);
            ResultsList.Focus();
            e.Handled = true;
        }
    }

    private void OnTips(object sender, RoutedEventArgs e)
        => TipsPanel.Visibility = TipsPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private void SearchNow() => SearchAsync().Forget(Host, "Search didn't finish");

    private async Task SearchAsync()
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        var text = QueryBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            Show(null);
            return;
        }
        try
        {
            _searchedVersion = _runtime.Index.Current.Version;
            var results = await _runtime.Router.SearchAsync(text, MaxResults, cts.Token);
            if (_sort != SearchSort.Name || _descending)
            {
                var (sort, desc) = (_sort, _descending);
                results = await Task.Run(() => results.SortedBy(sort, desc), cts.Token);
            }
            if (!cts.IsCancellationRequested) Show(results);
        }
        catch (OperationCanceledException) { /* a newer search replaced it */ }
    }

    private void Show(SearchResults? results)
    {
        _results = results;
        ResultsList.ItemsSource = results is null ? null : new VirtualHitList(results);
        bool empty = results is null || results.Count == 0;
        ResultsEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (results is null)
        {
            ResultsEmpty.Title = "Type to search";
            ResultsEmpty.Message = "Words match file and folder names. Add filters like ext:pdf or size:>1gb (Tips shows them all).";
        }
        else if (results.Count == 0)
        {
            ResultsEmpty.Title = "Nothing found";
            ResultsEmpty.Message = "Try fewer words, or check the filters (Tips shows them all).";
        }
        NotesText.Text = results is null ? "" : string.Join(" ", results.Notes);
        NotesText.Visibility = NotesText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateStatus();
        UpdateResultButtons();
    }

    private void UpdateStatus()
    {
        var status = _runtime.Index.Status;
        BuildBar.Visibility = status.Building ? Visibility.Visible : Visibility.Collapsed;
        string index = status.Entries == 0
            ? (status.Building ? "Building the index… you can search already" : "PC Tidy's index starts building when the page opens")
            : status.Building
                ? $"Building the index… you can search already · {status.Entries:N0} files and folders so far"
                : $"Searching PC Tidy's own index · {status.Entries:N0} files and folders{Freshness(status.OldestBuildUtc)}";
        // Memory use is a detail for the curious, not the status line.
        StatusText.ToolTip = status.Entries == 0 ? null : $"{status.Entries:N0} files and folders, {Sizes.Format(status.MemoryBytes)} in memory";
        // Who answered these results (or, before a search, the last known answer: Active never blocks).
        bool everything = _results is not null ? _results.Backend == EverythingBackend.BackendName : _runtime.Router.Active is EverythingBackend;
        string backend = everything ? "Searching with Everything" : index;
        StatusText.Text = _results is null
            ? backend + (status.Message is { } m ? ". " + m : "")
            : $"{_results.Count:N0}{(_results.Truncated ? "+" : "")} result{(_results.Count == 1 ? "" : "s")} in {_results.Elapsed.TotalMilliseconds:N0} ms · {backend}";
    }

    /// <summary>" · updated just now" / " · updated 5 min ago" from when the oldest part of the index was built.</summary>
    public static string Freshness(DateTime? builtUtc, DateTime? nowUtc = null)
    {
        if (builtUtc is not { } built) return "";
        var age = (nowUtc ?? DateTime.UtcNow) - built;
        if (age < TimeSpan.FromMinutes(2)) return " · updated just now";
        if (age < TimeSpan.FromHours(1)) return $" · updated {(int)age.TotalMinutes} min ago";
        if (age < TimeSpan.FromHours(24)) return $" · updated {(int)age.TotalHours} h ago";
        return $" · updated {built.ToLocalTime():d MMM}";
    }

    private void OnSortHeader(object sender, RoutedEventArgs e)
    {
        if (sender is not GridViewColumnHeader { Tag: string tag } || !Enum.TryParse<SearchSort>(tag, out var sort)) return;
        _descending = sort == _sort && !_descending;
        _sort = sort;
        ShowSortArrows();
        if (_results is { } current) ResortAsync(current, sort, _descending).Forget(Host, "Sorting didn't finish");
    }

    /// <summary>An arrow on the sorted column's header (▲ A to Z / oldest first, ▼ the other way).</summary>
    private void ShowSortArrows()
    {
        foreach (var (header, name, sort) in new[] { (NameHeader, "Name", SearchSort.Name), (FolderHeader, "Folder", SearchSort.Folder), (SizeHeader, "Size", SearchSort.Size), (ModifiedHeader, "Modified", SearchSort.Modified) })
        {
            header.Content = sort == _sort ? name + (_descending ? " ▼" : " ▲") : name;
            System.Windows.Automation.AutomationProperties.SetName(header, sort == _sort ? $"{name}, sorted {(_descending ? "descending" : "ascending")}" : $"Sort by {name}");
        }
    }

    private async Task ResortAsync(SearchResults current, SearchSort sort, bool descending)
    {
        var sorted = await Task.Run(() => current.SortedBy(sort, descending));
        if (ReferenceEquals(_results, current)) Show(sorted);
    }

    private SearchHit? SelectedHit => (ResultsList.SelectedItem as HitRow)?.Hit;

    private void OnResultSelected(object sender, SelectionChangedEventArgs e) => UpdateResultButtons();

    private void UpdateResultButtons() => ShowInFolderButton.IsEnabled = AddFavoriteFromResultButton.IsEnabled = SelectedHit is not null;

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e) => ShowSelectedInFolder();

    private void OnResultsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ShowSelectedInFolder();
        e.Handled = true;
    }

    private void OnShowInFolder(object sender, RoutedEventArgs e) => ShowSelectedInFolder();

    /// <summary>Opens Explorer with the item selected (harmless: nothing changes).</summary>
    private void ShowSelectedInFolder()
    {
        if (SelectedHit is { } hit) Host?.OpenInExplorer(hit.FullPath);
    }

    private void OnCopyPath(object sender, RoutedEventArgs e)
    {
        if (SelectedHit is not { } hit) return;
        try { Clipboard.SetText(hit.FullPath); }
        catch (System.Runtime.InteropServices.COMException) { /* clipboard busy: the user can try again */ }
    }

    private void OnAddResultToFavorites(object sender, RoutedEventArgs e)
    {
        if (SelectedHit is not { } hit) return;
        var folder = hit.IsFolder ? hit.FullPath : hit.Folder;
        if (!_favorites.Add(folder)) Host?.ShowMessage("Already a favorite", $"'{FavoritesList.DefaultLabel(folder)}' is already in your Favorites (or the list is full).");
        RefreshFavorites(_favorites.Items.Count - 1);
    }

    // ------------------------------------------------------------------ favorites

    private FavoriteFolder? SelectedFavorite => FavoritesListBox.SelectedItem as FavoriteFolder;

    private void RefreshFavorites(int? select = null)
    {
        FavoritesListBox.ItemsSource = _favorites.Items.ToList();
        FavoritesEmpty.Visibility = _favorites.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (select is { } i && i >= 0 && i < _favorites.Items.Count) FavoritesListBox.SelectedIndex = i;
        UpdateFavoriteButtons();
    }

    private void OnFavoriteSelected(object sender, SelectionChangedEventArgs e) => UpdateFavoriteButtons();

    private void UpdateFavoriteButtons()
    {
        var f = SelectedFavorite;
        int i = FavoritesListBox.SelectedIndex;
        FavOpen.IsEnabled = FavRemove.IsEnabled = FavPin.IsEnabled = f is not null;
        FavUp.IsEnabled = f is not null && i > 0;
        FavDown.IsEnabled = f is not null && i < _favorites.Items.Count - 1;
    }

    private void OnAddFavorite(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder for your Favorites", Multiselect = false };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true || string.IsNullOrWhiteSpace(dialog.FolderName)) return;
        _favorites.Add(dialog.FolderName);
        RefreshFavorites(_favorites.Items.Count - 1);
    }

    private void OnOpenFavorite(object sender, RoutedEventArgs e)
    {
        if (SelectedFavorite is not { } f) return;
        if (!Directory.Exists(f.Path))
        {
            Host?.ShowMessage("Folder not found", $"'{f.Label}' isn't there any more ({f.Path}). You can remove it from Favorites.");
            return;
        }
        Host?.OpenInExplorer(f.Path);
    }

    private void OnFavoriteUp(object sender, RoutedEventArgs e) => MoveFavorite(-1);
    private void OnFavoriteDown(object sender, RoutedEventArgs e) => MoveFavorite(+1);

    private void MoveFavorite(int delta)
    {
        int i = FavoritesListBox.SelectedIndex;
        if (i < 0 || !_favorites.Move(i, i + delta)) return;
        RefreshFavorites(i + delta);
    }

    private void OnRemoveFavorite(object sender, RoutedEventArgs e)
    {
        if (SelectedFavorite is not { } f) return;
        int i = FavoritesListBox.SelectedIndex;
        _favorites.Remove(f.Path);
        RefreshFavorites(Math.Min(i, _favorites.Items.Count - 1));
    }

    /// <summary>Only when clicked: one confirmed, journaled, undoable change through the shell (undo unpins).</summary>
    private void OnPinFavorite(object sender, RoutedEventArgs e)
    {
        if (SelectedFavorite is not { } f || Host is not { } host) return;
        host.ReviewAndRunAsync(FavoritesList.PinPlan(f.Path), CancellationToken.None).Forget(host, "Pinning didn't work");
    }
}
