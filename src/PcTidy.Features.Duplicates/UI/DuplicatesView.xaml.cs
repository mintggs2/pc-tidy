using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using PcTidy.UI.Flow;

namespace PcTidy.Features.Duplicates.UI;

/// <summary>
/// The Duplicates page: Scan → groups (pick which copy to keep, tick what goes) → the shell's confirm dialog → Done
/// card with Undo. Only the lists scroll; the header and the sticky bar stay put.
/// </summary>
public partial class DuplicatesView : UserControl, IFixedHeightPage
{
    private readonly IServiceProvider _services;

    public DuplicatesView(IServiceProvider services, DuplicatesModule module)
    {
        InitializeComponent();
        _services = services;
        Model = new DuplicatesViewModel(services, module);
        Model.PropertyChanged += OnModelChanged;

        ExactList.ItemsSource = Model.Groups;
        FolderList.ItemsSource = Model.FolderGroups;
        SimilarList.ItemsSource = Model.Similar;
        ProbableList.ItemsSource = Model.Probable;
        ExtensionList.ItemsSource = Model.Extensions;
        Model.Probable.CollectionChanged += (_, _) => { ProbableTab.Header = Model.ProbableTabTitle; UpdateTabStrip(); };
        MasterList.ItemsSource = Model.MasterMatches;
        ReferenceList.ItemsSource = Model.ReferenceFolders;
        NotesList.ItemsSource = Model.Notes;
        JunkPicker.SelectionChanged += (_, _) => ShowJunkList();
        DepthPicker.SelectedIndex = Model.DepthIndex;
        DepthPicker.SelectionChanged += (_, _) => Model.DepthIndex = DepthPicker.SelectedIndex;
        Tabs.SelectionChanged += (_, e) => { if (ReferenceEquals(e.OriginalSource, Tabs)) { SyncTabStrip(); RenderSelection(); } };
        if (Tabs.SelectedIndex < 0) Tabs.SelectedIndex = 0;
        CapRowWidths(ExactList, FolderList, ProbableList, SimilarList, JunkList, ExtensionList, MasterList);
        ShowJunkList();
        UpdateCompareFolders();
        Render();
    }

    public DuplicatesViewModel Model { get; }

    private IFlowHost? Host => _services.GetService(typeof(IFlowHost)) as IFlowHost;

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => OnModelChanged(sender, e)); return; }
        switch (e.PropertyName)
        {
            case nameof(DuplicatesViewModel.State):
            case nameof(DuplicatesViewModel.Summary):
                Render();
                break;
            case nameof(DuplicatesViewModel.ProgressText):
            case nameof(DuplicatesViewModel.Progress):
                ScanMessage.Text = Model.ProgressText;
                ScanBar.IsIndeterminate = Model.Progress is null;
                ScanBar.Value = (Model.Progress ?? 0) * 100;
                break;
            case nameof(DuplicatesViewModel.SelectionText):
            case nameof(DuplicatesViewModel.SelectionCaption):
            case nameof(DuplicatesViewModel.HasSelection):
                RenderSelection();
                break;
            case nameof(DuplicatesViewModel.CompareStatus):
                CompareText.Text = Model.CompareStatus;
                break;
            case nameof(DuplicatesViewModel.MasterFolder):
            case nameof(DuplicatesViewModel.OtherFolder):
                UpdateCompareFolders();
                break;
        }
    }

    private void Render()
    {
        var state = Model.State;
        ReadyState.Visibility = Vis(state == PageState.Ready);
        ScanningState.Visibility = Vis(state == PageState.Scanning);
        ResultsState.Visibility = Vis(state == PageState.Results);
        ScanButton.IsEnabled = state != PageState.Scanning;
        DepthPicker.IsEnabled = ScanButton.IsEnabled;
        ScanButton.Content = state == PageState.Results ? "Scan again" : "Scan";
        AutomationProperties.SetName(ScanButton, (string)ScanButton.Content);

        ExactTab.Header = Model.ExactTabTitle;
        FolderTab.Header = Model.FolderTabTitle;
        SimilarTab.Header = Model.SimilarTabTitle;
        ProbableTab.Header = Model.ProbableTabTitle;
        ExtensionTab.Header = Model.ExtensionTabTitle;
        JunkTab.Header = Model.JunkTabTitle;
        UpdateTabStrip();
        SummaryBar.Message = Model.Summary;

        int found = Model.Groups.Count + Model.FolderGroups.Count + Model.Similar.Count + Model.EmptyFolders.Count + Model.ZeroByte.Count + Model.Shortcuts.Count
                    + Model.Probable.Count + Model.Extensions.Count;
        switch (state)
        {
            case PageState.Ready: Steps.Set(1, "Looks, changes nothing", "Pick what goes", "One last check", ""); break;
            case PageState.Scanning: Steps.Set(1, "Scanning…", "Pick what goes", "One last check", ""); break;
            default: Steps.Set(2, $"{found:N0} found", "Pick what goes", "One last check", ""); break;
        }
        RenderSelection();
    }

    private void RenderSelection()
    {
        SelectionIcon.HasSelection = Model.HasSelection;
        // The bar counts every tab; say so when nothing on the open tab is ticked, or it reads like this tab's total.
        SelectedText.Text = Model.HasSelection && !RowsOn(Tabs.SelectedItem as TabItem).Any(r => r.IsChecked && r.CanRemove)
            ? Model.SelectionText + " (on other tabs)"
            : Model.SelectionText;
        SelectedCaption.Text = Model.SelectionCaption;
        PrimaryButton.IsEnabled = Model.HasSelection;
        SimulateButton.IsEnabled = Model.HasSelection;
        var items = Model.SelectedItems();
        PrimaryButton.Content = DuplicatesViewModel.PrimaryVerb(items);
    }

    /// <summary>
    /// Rows are never wider than their list. GOTCHA: a virtualized list keeps rows at their widest after the window
    /// narrows, so they ran off the right edge at ~1000 px. Built in code so it can extend the shared row style.
    /// </summary>
    private void CapRowWidths(params ListBox[] lists)
    {
        var style = new Style(typeof(ListBoxItem), TryFindResource("TidyRowContainer") as Style);
        style.Setters.Add(new Setter(MaxWidthProperty, new Binding(nameof(ScrollViewer.ViewportWidth))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ScrollViewer), 1),
            Converter = new RowMaxWidthConverter(),
        }));
        foreach (var list in lists) list.ItemContainerStyle = style;
    }

    // ------------------------------------------------------------------ section strip (segmented tabs)

    private readonly List<RadioButton> _tabButtons = new();
    private bool _syncingTabs;

    /// <summary>One segmented button per section, labelled like its tab ("Duplicates (5)"); built once, relabelled after each scan.</summary>
    private void UpdateTabStrip()
    {
        var items = Tabs.Items.OfType<TabItem>().ToList();
        if (_tabButtons.Count != items.Count)
        {
            TabButtons.Children.Clear();
            _tabButtons.Clear();
            for (int i = 0; i < items.Count; i++)
            {
                int index = i;
                var rb = new RadioButton { GroupName = "dup-sections-" + GetHashCode() };
                rb.SetResourceReference(StyleProperty, "TidySegmentButton");
                rb.Checked += (_, _) => { if (!_syncingTabs) Tabs.SelectedIndex = index; };
                _tabButtons.Add(rb);
                TabButtons.Children.Add(rb);
            }
        }
        for (int i = 0; i < items.Count; i++)
        {
            var label = items[i].Header as string ?? AutomationProperties.GetName(items[i]);
            _tabButtons[i].Content = label;
            AutomationProperties.SetName(_tabButtons[i], label);
            AutomationProperties.SetHelpText(_tabButtons[i], AutomationProperties.GetName(items[i]));
        }
        SyncTabStrip();
    }

    private void SyncTabStrip()
    {
        _syncingTabs = true;
        for (int i = 0; i < _tabButtons.Count; i++) _tabButtons[i].IsChecked = i == Tabs.SelectedIndex;
        _syncingTabs = false;
    }

    /// <summary>The tickable rows a tab shows (Compare shows the master-folder matches).</summary>
    private IEnumerable<IRemovableRow> RowsOn(TabItem? tab)
    {
        if (tab == ExactTab) return Model.Groups.SelectMany(g => g.Copies);
        if (tab == FolderTab) return Model.FolderGroups.SelectMany(g => g.Copies);
        if (tab == ProbableTab) return Model.Probable.SelectMany(g => g.Copies);
        if (tab == SimilarTab) return Model.Similar.SelectMany(g => g.Images);
        if (tab == JunkTab) return Model.EmptyFolders.Concat(Model.ZeroByte).Concat(Model.Shortcuts);
        if (tab == ExtensionTab) return Model.Extensions;
        return Model.MasterMatches;
    }

    private static Visibility Vis(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    private void ShowJunkList()
    {
        JunkList.ItemsSource = JunkPicker.SelectedIndex switch
        {
            1 => Model.ZeroByte,
            2 => Model.Shortcuts,
            _ => Model.EmptyFolders,
        };
    }

    private void UpdateCompareFolders()
    {
        MasterText.Text = Model.MasterFolder ?? "Not chosen yet";
        OtherText.Text = Model.OtherFolder ?? "Not chosen yet";
    }

    // ------------------------------------------------------------------ events

    private void OnScan(object sender, RoutedEventArgs e) => _ = Model.ScanAsync();
    private void OnCancel(object sender, RoutedEventArgs e) => Model.CancelScan();
    private void OnRun(object sender, RoutedEventArgs e) => _ = Model.RunAsync(simulate: false);
    private void OnSimulate(object sender, RoutedEventArgs e) => _ = Model.RunAsync(simulate: true);
    private void OnClearCases(object sender, RoutedEventArgs e) => Model.SelectClearCasesOnly();
    private void OnCompare(object sender, RoutedEventArgs e) => _ = Model.CompareAsync();
    private void OnTickAllJunk(object sender, RoutedEventArgs e) => Model.SetAll((IEnumerable<SimpleRow>)JunkList.ItemsSource, true);
    private void OnTickAllMatches(object sender, RoutedEventArgs e) => Model.SetAll(Model.MasterMatches, true);

    private void OnOpenInExplorer(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string path) Host?.OpenInExplorer(path);
    }

    private void OnCheckFully(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is GroupRow row) _ = Model.CheckFullyAsync(row);
    }

    private void OnCompareGroup(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is GroupRow row) ShowCompare(CompareViewModel.For(row));
    }

    private void OnCompareSimilar(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SimilarGroupRow row) ShowCompare(CompareViewModel.For(row));
    }

    private void ShowCompare(CompareViewModel model)
        => new CompareWindow(model) { Owner = Window.GetWindow(this) }.ShowDialog();

    private void OnImageTileLoaded(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ImageRow row) _ = row.LoadThumbnailAsync();
    }

    private void OnPickMaster(object sender, RoutedEventArgs e)
    {
        if (PickFolder("Choose the master folder (it's never changed)") is { } f) Model.MasterFolder = f;
    }

    private void OnPickOther(object sender, RoutedEventArgs e)
    {
        if (PickFolder("Choose the folder to check against the master") is { } f) Model.OtherFolder = f;
    }

    private void OnAddReference(object sender, RoutedEventArgs e)
    {
        if (PickFolder("Choose a reference folder (copies here are never removed)") is { } f) Model.AddReferenceFolder(f);
    }

    private void OnRemoveReference(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ReferenceFolderRow row) Model.RemoveReferenceFolder(row);
    }

    private static string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}

/// <summary>Null, empty or false → Collapsed, anything else → Visible. ConverterParameter "invert" flips it.</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool visible = !(value is null || value is string { Length: 0 } || value is false);
        if (parameter is "invert") visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>The list's viewport width as a row's MaxWidth (0, before the first layout, means no limit).</summary>
public sealed class RowMaxWidthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double w && w > 0 ? w : double.PositiveInfinity;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
