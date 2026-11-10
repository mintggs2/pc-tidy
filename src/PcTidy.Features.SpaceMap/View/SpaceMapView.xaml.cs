using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.SpaceMap.Actions;
using PcTidy.Features.SpaceMap.Index;
using PcTidy.Features.SpaceMap.Treemap;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;
using SymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace PcTidy.Features.SpaceMap.View;

/// <summary>
/// Space map page: pick a drive or folder → scan → squarified treemap linked to a sorted list, with
/// Folders · File types · Top files · Big and old views. Every change goes through IFlowHost.ReviewAndRunAsync;
/// the page itself never touches a file. A fixed-height page: the map fills the window and only the lists scroll.
/// </summary>
public partial class SpaceMapView : UserControl, PcTidy.UI.Flow.IFixedHeightPage
{
    private const int MaxListRows = 2000;
    private const int PivotFolders = 0, PivotTypes = 1, PivotTop = 2, PivotBigOld = 3;

    private sealed record DriveChoice(string Label, string? Path);

    private readonly IKnownLocations _locations;
    private readonly ISafetyGuard _safety;
    private readonly AppSettings _settings;
    private readonly IFlowHost _flow;
    private readonly SpaceIndexScanner _scanner;
    private readonly SpaceActions _actions;
    private readonly DriveSpaceProbe _drives;

    private SpaceIndex? _index;
    private int _viewRoot;
    private IReadOnlyList<int> _selection = Array.Empty<int>();
    private CancellationTokenSource? _scanCts;
    private int _listVersion;
    private DriveChoice? _lastDrive;
    private bool _mapIsStale;

    public SpaceMapView(IServiceProvider services)
    {
        InitializeComponent();
        _locations = services.Get<IKnownLocations>();
        _safety = services.Get<ISafetyGuard>();
        _settings = services.Get<AppSettings>();
        _flow = services.Get<IFlowHost>();
        _drives = new DriveSpaceProbe(services.GetService(typeof(ISystemInfo)) as ISystemInfo);
        _scanner = new SpaceIndexScanner(services.Get<IFileWalker>(), _safety, drives: _drives);
        _actions = new SpaceActions(_locations, _safety, _drives, _settings.QuarantineDays);

        Tip.Background = (TryFindResource("ApplicationBackgroundBrush") ?? TryFindResource("SolidBackgroundFillColorBaseBrush")) as Brush
            ?? new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B));
        FillDrivePicker();
        Unloaded += (_, _) => _scanCts?.Cancel();
    }

    // ================= drive picker + scan =================

    private void FillDrivePicker()
    {
        var items = new List<DriveChoice>();
        foreach (var d in _locations.FixedDrives)
        {
            var label = d.TrimEnd('\\');
            if (SamePath(d, _locations.SystemDrive)) label += "  Windows";
            if (_drives.ForFixedDrive(d) is { } s) label += $"  ·  {Sizes.Format(s.FreeBytes)} free of {Sizes.Format(s.TotalBytes)}";
            items.Add(new DriveChoice(label, d));
        }
        items.Add(new DriveChoice("Pick a folder…", null));
        DrivePicker.ItemsSource = items;
        DrivePicker.SelectedIndex = items.Count > 1 ? 0 : -1;
    }

    private void OnDrivePicked(object sender, SelectionChangedEventArgs e)
    {
        if (DrivePicker.SelectedItem is not DriveChoice choice) return;
        if (choice.Path is not null) { _lastDrive = choice; return; }

        var dialog = new OpenFolderDialog { Title = "Pick a folder to map" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            var items = ((List<DriveChoice>)DrivePicker.ItemsSource).ToList();
            var picked = new DriveChoice(dialog.FolderName, dialog.FolderName);
            items.Insert(items.Count - 1, picked);
            DrivePicker.ItemsSource = items;
            DrivePicker.SelectedItem = picked;
        }
        else DrivePicker.SelectedItem = _lastDrive;
    }

    private async void OnScan(object sender, RoutedEventArgs e)
    {
        if (DrivePicker.SelectedItem is not DriveChoice { Path: { } root }) return;
        if (!Directory.Exists(root))
        {
            ShowNotice("Can't open that place", "It isn't there any more, or this account can't read it.", InfoBarSeverity.Warning);
            return;
        }

        _scanCts?.Cancel();
        var cts = new CancellationTokenSource();
        _scanCts = cts;
        Notice.IsOpen = false;
        SetScanning(true, root);
        var started = DateTime.UtcNow;
        var progress = new Progress<SpaceScanProgress>(p =>
        {
            if (_scanCts != cts) return;
            var secs = Math.Max(0.1, (DateTime.UtcNow - started).TotalSeconds);
            ScanCounts.Text = $"{p.Files:N0} files · {p.Folders:N0} folders · {Sizes.Format(p.Bytes)} · {p.Files / secs:N0} files a second";
            if (p.CurrentFolder is not null) ScanFolder.Text = p.CurrentFolder;
        });
        var options = new SpaceScanOptions { Exclusions = _settings.ScanExclusions, GameRoots = _locations.GameInstallDirs };

        try
        {
            var index = await _scanner.ScanAsync(root, options, progress, cts.Token);
            if (_scanCts != cts) return;
            _index = index;
            _mapIsStale = false;
            _viewRoot = SpaceIndex.RootNode;
            SetScanning(false, null);
            ShowScanSummary(index);
            Navigate(SpaceIndex.RootNode);
        }
        catch (OperationCanceledException)
        {
            if (_scanCts != cts) return;
            SetScanning(false, null);
            ShowNotice("Scan stopped", "Nothing was changed. Press Scan to start again.", InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            SetScanning(false, null);
            ShowNotice("The scan couldn't finish", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void OnCancelScan(object sender, RoutedEventArgs e) => _scanCts?.Cancel();

    private void SetScanning(bool scanning, string? root)
    {
        ScanButton.Visibility = scanning ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
        DrivePicker.IsEnabled = !scanning;
        if (scanning)
        {
            ScanHeadline.Text = $"Measuring {root}";
            ScanCounts.Text = "Starting…";
            ScanFolder.Text = "";
        }
        ScanningState.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = !scanning && _index is null ? Visibility.Visible : Visibility.Collapsed;
        Pivot.IsEnabled = !scanning && _index is not null;
        Pivot.Visibility = _index is null ? Visibility.Collapsed : Visibility.Visible;
        ShowPivotContent(scanning ? -1 : Pivot.SelectedIndex);
    }

    private void ShowScanSummary(SpaceIndex index)
    {
        var s = index.Summary;
        var used = s.DriveUsedBytes is long u && s.DriveTotalBytes is long t
            ? $"{Sizes.Format(u)} used of {Sizes.Format(t)}"
            : $"{Sizes.Format(s.ScannedBytes)} in this folder";
        SubtitleText.Text = $"{s.RootPath} · {used} · {s.Files:N0} files measured in {s.Elapsed.TotalSeconds:0.#} s. PC Tidy never opens your files to measure them.";
        if (s.Inaccessible > 0)
            ShowNotice("Some folders couldn't be read",
                $"{s.Inaccessible:N0} folders need permissions this account doesn't have. Their space is counted in \"Unscanned / system\".",
                InfoBarSeverity.Informational);
    }

    // ================= navigation =================

    private void Navigate(int folder)
    {
        if (_index is null) return;
        _viewRoot = folder;
        Map.Show(_index, folder);
        Breadcrumb.ItemsSource = _index.PathFromRoot(folder)
            .Select((n, i) => new Crumb(n, i == 0 ? _index.RootPath.TrimEnd('\\') : _index.Name(n), i == 0)).ToList();
        UpButton.IsEnabled = folder != SpaceIndex.RootNode;
        SetSelection(folder == SpaceIndex.RootNode ? Array.Empty<int>() : new[] { folder });
        RefreshPivotList();
    }

    private bool CanZoomInto(int node)
        => _index is not null && _index.IsDirectory(node) && !_index.Has(node, SpaceNodeFlags.Link) && _index.ChildCount(node) > 0;

    private void ZoomInto(int node)
    {
        if (_index is null) return;
        // Zoom one level: the child of the current view that contains the node.
        int step = node;
        while (step != SpaceIndex.RootNode && _index.Parent(step) != _viewRoot) step = _index.Parent(step);
        if (_index.Parent(step) != _viewRoot && step != node) return;
        if (CanZoomInto(step)) Navigate(step);
    }

    private void ZoomOut()
    {
        if (_index is not null && _viewRoot != SpaceIndex.RootNode) Navigate(_index.Parent(_viewRoot));
    }

    private void OnZoomOut(object sender, RoutedEventArgs e) => ZoomOut();

    private void OnBreadcrumb(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: int node }) Navigate(node);
    }

    // ================= pivot + lists =================

    private void OnPivotChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded && _index is null) return;
        ShowPivotContent(Pivot.SelectedIndex);
        if (Pivot.SelectedIndex != PivotTypes) Map.Highlight = null;
        // The file lists have their own (multi-)selection; don't leave a map selection showing over them.
        if (Pivot.SelectedIndex is PivotTop or PivotBigOld) SetSelection(Array.Empty<int>());
        RefreshPivotList();
    }

    private void ShowPivotContent(int pivot)
    {
        bool ready = _index is not null && pivot >= 0;
        MapView.Visibility = ready && pivot is PivotFolders or PivotTypes ? Visibility.Visible : Visibility.Collapsed;
        FilesView.Visibility = ready && pivot is PivotTop or PivotBigOld ? Visibility.Visible : Visibility.Collapsed;
        FolderList.Visibility = pivot == PivotTypes ? Visibility.Collapsed : Visibility.Visible;
        TypeList.Visibility = pivot == PivotTypes ? Visibility.Visible : Visibility.Collapsed;
        ListTitle.Text = pivot == PivotTypes ? "File types in here" : "Biggest in here";
    }

    /// <summary>Recomputes the visible list off the UI thread (big folders can hold a million files).</summary>
    private async void RefreshPivotList()
    {
        if (_index is not { } ix) return;
        int version = ++_listVersion, root = _viewRoot, pivot = Pivot.SelectedIndex;
        var now = DateTime.UtcNow;
        object rows;
        try
        {
            rows = await Task.Run<object>(() => pivot switch
            {
                PivotTypes => BuildTypeRows(ix, root),
                PivotTop => SpaceQueries.TopFiles(ix, root).Select(n => ToFileRow(ix, n, now, withMove: false)).ToList(),
                PivotBigOld => SpaceQueries.BigAndOld(ix, root, now, _safety).Select(n => ToFileRow(ix, n, now, withMove: true)).ToList(),
                _ => BuildFolderRows(ix, root),
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowNotice("Couldn't build the list", ex.Message, InfoBarSeverity.Warning);
            return;
        }
        if (version != _listVersion || ix != _index) return;

        var place = root == SpaceIndex.RootNode ? ix.RootPath : ix.Name(root);
        switch (pivot)
        {
            case PivotTypes:
                TypeList.ItemsSource = (List<TypeRow>)rows;
                break;
            case PivotTop:
                FileList.ItemsSource = (List<FileRow>)rows;
                FilesIntro.Text = $"The {((List<FileRow>)rows).Count} biggest files in {place}. Select several to act on them together.";
                break;
            case PivotBigOld:
                var big = (List<FileRow>)rows;
                FileList.ItemsSource = big;
                FilesIntro.Text = big.Count == 0
                    ? $"Nothing in {place} is over 500 MB and untouched for 6 months. Nice."
                    : $"{big.Count} files in {place} are over 500 MB and haven't changed in 6 months or more. "
                      + (big.Any(r => r.MoveLabel is not null)
                          ? "Moving them to another drive is usually better than deleting."
                          : $"Quarantine keeps them restorable for {_settings.QuarantineDays} days.");
                break;
            default:
                FolderList.ItemsSource = (List<object>)rows;
                break;
        }
    }

    private static List<object> BuildFolderRows(SpaceIndex ix, int root)
    {
        var kids = ix.Children(root);
        long largest = kids.Length > 0 ? ix.Size(kids[0]) : 0;
        var rows = new List<object>(Math.Min(kids.Length, MaxListRows) + 1);
        for (int i = 0; i < kids.Length && i < MaxListRows; i++) rows.Add(FolderRow.From(ix, kids[i], largest));
        if (kids.Length > MaxListRows)
        {
            long rest = 0;
            for (int i = MaxListRows; i < kids.Length; i++) rest += ix.Size(kids[i]);
            rows.Add(new MoreRow($"…and {kids.Length - MaxListRows:N0} smaller items ({Sizes.Format(rest)})"));
        }
        return rows;
    }

    private static List<TypeRow> BuildTypeRows(SpaceIndex ix, int root)
    {
        var categories = SpaceQueries.ByCategory(ix, root);
        var extensions = SpaceQueries.ByExtension(ix, root);
        long top = Math.Max(1, categories.Count > 0 ? categories[0].Bytes : 1);
        var rows = categories.Select(c => new TypeRow(FileCategories.DisplayName(c.Category), Sizes.Format(c.Bytes), SpaceText.Files(c.Files),
            (double)c.Bytes / top, CategoryBrushes.Of(c.Category), c.Category)).ToList();
        long topExt = Math.Max(1, extensions.Count > 0 ? extensions[0].Bytes : 1);
        rows.AddRange(extensions.Take(300).Select(t => new TypeRow(t.Extension.Length == 0 ? "(no extension)" : "." + t.Extension,
            Sizes.Format(t.Bytes), $"{SpaceText.Files(t.Files)} · {FileCategories.DisplayName(t.Category)}",
            (double)t.Bytes / topExt, CategoryBrushes.Of(t.Category), t.Category)));
        return rows;
    }

    private FileRow ToFileRow(SpaceIndex ix, int node, DateTime now, bool withMove)
    {
        var path = ix.FullPath(node)!;
        bool locked = _actions.WhyLocked(ix, node) is not null;
        string? move = null;
        if (withMove && !locked && _actions.MoveTargets(path, ix.Size(node)).FirstOrDefault() is { } target)
            move = $"Move to {DriveName(target.DriveRoot)}";
        return new FileRow(node, ix.Name(node), Path.GetDirectoryName(path) ?? "", Sizes.Format(ix.Size(node)),
            "Changed " + SpaceText.Ago(ix.LastTouchedUtc(node), now),
            locked ? CategoryBrushes.Protected : CategoryBrushes.Of(ix.Category(node)), locked, move);
    }

    private void OnFolderListSelection(object sender, SelectionChangedEventArgs e)
    {
        if (FolderList.SelectedItem is FolderRow row) SetSelection(new[] { row.Node });
    }

    private void OnFolderListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FolderList.SelectedItem is FolderRow row && CanZoomInto(row.Node)) Navigate(row.Node);
    }

    private void OnFolderListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && FolderList.SelectedItem is FolderRow row && CanZoomInto(row.Node)) { Navigate(row.Node); e.Handled = true; }
        else if (e.Key == Key.Back) { ZoomOut(); e.Handled = true; }
    }

    private void OnTypeSelection(object sender, SelectionChangedEventArgs e)
        => Map.Highlight = (TypeList.SelectedItem as TypeRow)?.Category;

    private void OnFileListSelection(object sender, SelectionChangedEventArgs e)
        => SetSelection(FileList.SelectedItems.OfType<FileRow>().Select(r => r.Node).ToList());

    // ================= treemap =================

    private void OnMapHover(object? sender, TreemapHitEventArgs e)
    {
        if (_index is null || e.Hit is not { } hit) { Tip.Visibility = Visibility.Collapsed; return; }
        var t = hit.Info;
        if (t.Node >= 0)
        {
            TipTitle.Text = _index.Name(t.Node);
            var parts = new List<string> { Sizes.Format(_index.Size(t.Node)), $"{Sizes.Format(_index.SizeOnDisk(t.Node))} on disk" };
            if (_index.IsDirectory(t.Node)) parts.Add(SpaceText.Files(_index.FileCount(t.Node)));
            TipSize.Text = string.Join(" · ", parts);
            TipPath.Text = _actions.WhyLocked(_index, t.Node) is { } why && _index.Has(t.Node, SpaceNodeFlags.Protected | SpaceNodeFlags.Pseudo)
                ? why
                : _index.FullPath(t.Node) ?? "";
        }
        else
        {
            TipTitle.Text = $"{t.MergedCount:N0} small items";
            TipSize.Text = $"{Sizes.Format(t.Bytes)} together";
            TipPath.Text = $"In {_index.FullPath(t.Folder)}";
        }
        Tip.Visibility = Visibility.Visible;
        Tip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double x = hit.Position.X + 16, y = hit.Position.Y + 18;
        if (x + Tip.DesiredSize.Width > TipLayer.ActualWidth) x = Math.Max(0, hit.Position.X - Tip.DesiredSize.Width - 8);
        if (y + Tip.DesiredSize.Height > TipLayer.ActualHeight) y = Math.Max(0, hit.Position.Y - Tip.DesiredSize.Height - 8);
        Canvas.SetLeft(Tip, x);
        Canvas.SetTop(Tip, y);
    }

    private void OnMapClick(object? sender, TreemapHitEventArgs e)
    {
        if (_index is null || e.Hit is not { } hit) return;
        double scale = VisualTreeHelper.GetDpi(Map).DpiScaleX;
        bool onCardHeader = hit.Info.Kind == TileKind.Card && hit.Position.Y * scale < hit.Info.Rect.Y + 28 * scale;
        if (e.ClickCount >= 2 || onCardHeader) ZoomInto(hit.Node);
        else SetSelection(new[] { hit.Node });
    }

    private void OnMapContext(object? sender, TreemapHitEventArgs e)
    {
        if (e.Hit is not { } hit) return;
        SetSelection(new[] { hit.Node });
        var menu = new ContextMenu { PlacementTarget = Map };
        FillContextMenu(menu);
        menu.IsOpen = true;
    }

    private void OnMapKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _selection.Count == 1) { ZoomInto(_selection[0]); e.Handled = true; }
        else if (e.Key is Key.Back or Key.Escape) { ZoomOut(); e.Handled = true; }
    }

    private void OnListContextOpened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu) FillContextMenu(menu);
    }

    private void FillContextMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        if (_index is null || _selection.Count == 0) { menu.IsOpen = false; return; }
        // Harmless items first; Quarantine is never the first entry (easy to hit by accident).
        var state = ActionState();
        if (_selection.Count == 1)
        {
            int node = _selection[0];
            if (_index.Has(node, SpaceNodeFlags.Pseudo))
                menu.Items.Add(Item("Explain missing space", SymbolRegular.Info24, ExplainMissingSpace));
            else
                menu.Items.Add(Item("Show in Explorer", SymbolRegular.Open24, () => OpenInExplorer(node)));
            if (CanZoomInto(node) && node != _viewRoot)
                menu.Items.Add(Item("Zoom in", SymbolRegular.ZoomIn24, () => ZoomInto(node)));
        }
        if (_viewRoot != SpaceIndex.RootNode)
            menu.Items.Add(Item("Up one level", SymbolRegular.ArrowUp24, ZoomOut));
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = $"{_selection.Count} items selected", IsEnabled = false });
        menu.Items.Add(new Separator());
        if (state.CanMove) menu.Items.Add(Item("Move to…", SymbolRegular.Storage24, MoveSelection));
        menu.Items.Add(Item("Quarantine", SymbolRegular.BoxArrowLeft24, QuarantineSelection, state.CanQuarantine));
        if (state.LockReason is not null)
            menu.Items.Add(new MenuItem { Header = state.LockReason, IsEnabled = false });
    }

    private void OnExplainMissingSpace(object sender, RoutedEventArgs e) => ExplainMissingSpace();

    private void ExplainMissingSpace()
    {
        if (_index is null) return;
        _flow.ShowMessage("Where the rest of the space is", MissingSpace.Format(MissingSpace.Explain(_index)));
    }

    private static MenuItem Item(string header, SymbolRegular icon, Action onClick, bool enabled = true)
    {
        var item = new MenuItem { Header = header, Icon = new SymbolIcon { Symbol = icon }, IsEnabled = enabled };
        AutomationPropertiesHelper.SetName(item, header);
        item.Click += (_, _) => onClick();
        return item;
    }

    // ================= selection + actions =================

    private sealed record SelectionState(bool CanQuarantine, bool CanMove, string? LockReason, long ActionableBytes);

    private SelectionState ActionState()
    {
        if (_index is not { } ix || _selection.Count == 0) return new(false, false, null, 0);
        var actionable = _selection.Where(n => _actions.WhyLocked(ix, n) is null).ToList();
        long bytes = actionable.Sum(n => ix.Size(n));
        string? lockReason = actionable.Count == _selection.Count ? null
            : _selection.Count == 1 ? _actions.WhyLocked(ix, _selection[0])
            : $"{_selection.Count - actionable.Count} of {_selection.Count} can't be touched (protected or links) and will be left out.";
        bool canMove = actionable.Count > 0 && _actions.MoveTargets(ix.FullPath(actionable[0])!, bytes).Count > 0;
        return new(actionable.Count > 0, canMove, lockReason, bytes);
    }

    private void SetSelection(IReadOnlyList<int> nodes)
    {
        _selection = nodes;
        Map.SelectedNode = nodes.Count == 1 ? nodes[0] : -1;
        if (_index is not { } ix || nodes.Count == 0) { SelectionBar.Visibility = Visibility.Collapsed; return; }

        var state = ActionState();
        SelectionBar.Visibility = Visibility.Visible;
        if (nodes.Count == 1)
        {
            int n = nodes[0];
            bool locked = ix.Has(n, SpaceNodeFlags.Protected | SpaceNodeFlags.Pseudo);
            SelectionSwatch.Background = locked ? CategoryBrushes.Protected : CategoryBrushes.Of(ix.Category(n));
            SelectionTitle.Text = $"{RelativeName(ix, n)} · {Sizes.Format(ix.Size(n))}";
            SelectionCaption.Text = state.LockReason ?? Describe(ix, n);
            bool pseudo = ix.Has(n, SpaceNodeFlags.Pseudo);
            OpenButton.Visibility = pseudo ? Visibility.Collapsed : Visibility.Visible;
            OpenButton.IsEnabled = true;
            ExplainButton.Visibility = pseudo ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            SelectionSwatch.Background = CategoryBrushes.Merged;
            SelectionTitle.Text = $"{nodes.Count} items · {Sizes.Format(nodes.Sum(ix.Size))}";
            SelectionCaption.Text = state.LockReason ?? $"{Sizes.Format(nodes.Sum(ix.SizeOnDisk))} on disk";
            OpenButton.Visibility = Visibility.Visible;
            OpenButton.IsEnabled = false;
            ExplainButton.Visibility = Visibility.Collapsed;
        }
        // A lock only for protected areas; a folder that just stays put as a whole (Users, Downloads) gets an info mark.
        bool reallyProtected = nodes.Any(n => ix.Has(n, SpaceNodeFlags.Protected | SpaceNodeFlags.Pseudo));
        SelectionLock.Symbol = reallyProtected ? SymbolRegular.LockClosed24 : SymbolRegular.Info24;
        SelectionLock.Visibility = state.LockReason is not null ? Visibility.Visible : Visibility.Collapsed;
        QuarantineButton.IsEnabled = state.CanQuarantine;
        // One-drive PCs (or no room elsewhere): Move isn't offered at all.
        MoveButton.Visibility = state.CanMove ? Visibility.Visible : Visibility.Collapsed;
    }

    private const string LongPathNote = "path is over 260 characters, so some apps can't open it";

    private static string RelativeName(SpaceIndex ix, int node)
    {
        var path = ix.FullPath(node);
        if (path is null) return ix.Name(node);
        var rel = Path.GetRelativePath(ix.RootPath, path);
        return rel == "." ? path : rel;
    }

    private static string Describe(SpaceIndex ix, int n)
    {
        var parts = new List<string>();
        if (ix.IsDirectory(n)) parts.Add(SpaceText.Files(ix.FileCount(n)));
        parts.Add($"{Sizes.Format(ix.SizeOnDisk(n))} on disk");
        parts.Add("last changed " + SpaceText.Ago(ix.LastTouchedUtc(n), DateTime.UtcNow));
        if (ix.Has(n, SpaceNodeFlags.AskFirst)) parts.Add("PC Tidy asks before changing anything here");
        if (ix.Has(n, SpaceNodeFlags.LongPath)) parts.Add(LongPathNote);
        if (ix.IsDirectory(n) && ix.ChildCount(n) > 0 && ix.Size(ix.Children(n)[0]) > 0)
            parts.Add($"biggest: {ix.Name(ix.Children(n)[0])} ({Sizes.Format(ix.Size(ix.Children(n)[0]))})");
        return string.Join(" · ", parts);
    }

    private void OnOpenInExplorer(object sender, RoutedEventArgs e)
    {
        if (_selection.Count == 1) OpenInExplorer(_selection[0]);
    }

    private void OpenInExplorer(int node)
    {
        if (_index?.FullPath(node) is { } path) _flow.OpenInExplorer(path);
    }

    private void OnQuarantine(object sender, RoutedEventArgs e) => QuarantineSelection();
    private void OnMoveTo(object sender, RoutedEventArgs e) => MoveSelection();

    private void OnRowQuarantine(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FileRow row }) _ = RunAsync("Quarantine from Space map", new[] { row.Node }, n => _actions.Quarantine(_index!, n));
    }

    private void OnRowMove(object sender, RoutedEventArgs e)
    {
        if (_index is null || sender is not FrameworkElement { DataContext: FileRow row }) return;
        var target = _actions.MoveTargets(_index.FullPath(row.Node)!, _index.Size(row.Node)).FirstOrDefault();
        if (target is null) { _flow.ShowMessage("Nowhere to move it", "No other drive on this PC has room for it. Quarantine is still an option."); return; }
        _ = RunAsync($"Move to {target.DriveRoot}", new[] { row.Node }, n => _actions.MoveToDrive(_index!, n, target));
    }

    private void QuarantineSelection()
    {
        if (_index is null || _selection.Count == 0) return;
        _ = RunAsync("Quarantine from Space map", _selection, n => _actions.Quarantine(_index, n));
    }

    private void MoveSelection()
    {
        if (_index is not { } ix || _selection.Count == 0) return;
        var state = ActionState();
        var targets = _selection.Where(n => _actions.WhyLocked(ix, n) is null).Select(n => ix.FullPath(n)!)
            .Select(p => _actions.MoveTargets(p, state.ActionableBytes)).FirstOrDefault() ?? Array.Empty<MoveTarget>();
        if (targets.Count == 0)
        {
            _flow.ShowMessage("Nowhere to move it", "No other drive on this PC has room for it. Quarantine is still an option.");
            return;
        }
        var dialog = new OpenFolderDialog { Title = "Move to… (pick a folder on another drive)", InitialDirectory = targets[0].DriveRoot };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var folder = dialog.FolderName;
        _ = RunAsync($"Move to {folder}", _selection, n => _actions.MoveInto(ix, n, folder));
    }

    /// <summary>Builds the plan from the selection and hands it to the shell's confirm → run → undo flow.</summary>
    private async Task RunAsync(string title, IReadOnlyList<int> nodes, Func<int, ActionResult> build)
    {
        if (_index is null) return;
        var results = nodes.Select(build).ToList();
        var items = results.Where(r => r.Ok).Select(r => r.Item!).ToList();
        if (items.Count == 0)
        {
            _flow.ShowMessage("PC Tidy can't do that here", results.Select(r => r.WhyNot).FirstOrDefault(w => w is not null) ?? "Nothing to do.");
            return;
        }
        var plan = new ActionPlan { FeatureId = SpaceActions.FeatureId, Title = title, Items = items };
        try
        {
            var run = await _flow.ReviewAndRunAsync(plan, CancellationToken.None);
            if (run is { DoneCount: > 0 })
            {
                _mapIsStale = true;
                ShowNotice("Done", "The map still shows the old layout. Press Scan to refresh it.", InfoBarSeverity.Success);
            }
            if (results.Count(r => !r.Ok) is > 0 and var skipped)
                ShowNotice("Some items were left out", $"{skipped} of {results.Count} can't be changed: {results.First(r => !r.Ok).WhyNot}", InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            _flow.ShowMessage("That didn't work", ex.Message, isError: true);
        }
    }

    private void ShowNotice(string title, string message, InfoBarSeverity severity)
    {
        Notice.Title = title;
        Notice.Message = message;
        Notice.Severity = severity;
        Notice.IsOpen = true;
    }

    /// <summary>True after a run changed files that the current map still shows.</summary>
    public bool IsMapStale => _mapIsStale;

    /// <summary>"D:" for a real drive; the folder name for a sandbox drive (keeps buttons short).</summary>
    private static string DriveName(string driveRoot)
    {
        var trimmed = driveRoot.TrimEnd('\\');
        return trimmed.Length <= 3 ? trimmed : Path.GetFileName(trimmed);
    }

    private static bool SamePath(string a, string b)
        => a.TrimEnd('\\').Equals(b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}

/// <summary>Sets automation names on items created in code.</summary>
internal static class AutomationPropertiesHelper
{
    public static void SetName(DependencyObject d, string name) => System.Windows.Automation.AutomationProperties.SetName(d, name);
}
