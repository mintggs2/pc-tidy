using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using PcTidy.Core;
using PcTidy.Core.Model;
using Wpf.Ui.Controls;
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace PcTidy.App.Shell;

/// <summary>One place in an inner list: a page key ("home", "optimize", a module id), or a small group header.</summary>
public sealed record WorkbenchEntry(string Key, string Title, SymbolRegular Icon, bool IsHeader = false);

/// <summary>One top tab and the inner list shown on its left.</summary>
public sealed record WorkbenchTab(string Id, string Title, IReadOnlyList<WorkbenchEntry> Entries)
{
    public IEnumerable<WorkbenchEntry> Pages => Entries.Where(e => !e.IsHeader);
}

/// <summary>
/// 3.2 "Workbench" layout (option C of the 3.2 design study): six tabs along the top, each with a short list on the left.
/// History, Quarantine and Settings sit at the top right. Every section keeps its own page; only where it is reached
/// from changed. Data-driven: a section not named here goes into the tab of its old rail group.
/// </summary>
public static class WorkbenchLayout
{
    public const string Overview = "overview", Clean = "clean", Optimize = "optimize-tab", Apps = "apps-tab", Organize = "organize-tab", Reports = "reports";

    /// <summary>Tab, header (or null) and module ids, in order.</summary>
    private static readonly (string Tab, string Title, (string? Header, string[] Ids)[] Parts)[] Plan =
    {
        (Clean, "Clean", new[] { ((string?)null, new[] { "cleanup", "spacemap", "duplicates", "developer", "drivemove" }), ("PRIVACY", new[] { "privacy", "notifications" }) }),
        (Optimize, "Optimize", new[] { ((string?)null, new[] { "speed", "tweaks", "services", "drivers", "pccare" }), ("GAMES", new[] { "games", "boost", "measure" }) }),
        (Apps, "Apps", new[] { ((string?)null, new[] { "apps", "aichats" }) }),
        (Organize, "Organize", new[] { ((string?)null, new[] { "organize", "photos", "desktoptidy", "search" }) }),
        (Reports, "Reports", new[] { ((string?)null, new[] { "insights", "schedule", "learned", "assistant" }) }),
    };

    private static string TabForGroup(NavGroup g) => g switch
    {
        NavGroup.CleanUp => Clean,
        NavGroup.SpeedUp => Optimize,
        NavGroup.Organize => Organize,
        _ => Reports,
    };

    /// <param name="modules">The visible modules (Expert mode decides which).</param>
    public static IReadOnlyList<WorkbenchTab> Build(IReadOnlyList<IFeatureModule> modules)
    {
        var byId = modules.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tabs = new List<WorkbenchTab>
        {
            new(Overview, "Overview", new[]
            {
                new WorkbenchEntry("home", "Overview", SymbolRegular.Home24),
                new WorkbenchEntry("optimize", "Scan everything", SymbolRegular.Sparkle24),
            }),
        };
        foreach (var (tab, title, parts) in Plan)
        {
            var entries = new List<WorkbenchEntry>();
            foreach (var (header, ids) in parts)
            {
                var found = ids.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
                if (found.Count == 0) continue;
                if (header is not null) entries.Add(new WorkbenchEntry("", header, SymbolRegular.Empty, IsHeader: true));
                foreach (var m in found)
                {
                    entries.Add(new WorkbenchEntry(m.Id, m.Title, MainWindow.IconOf(m)));
                    placed.Add(m.Id);
                }
            }
            tabs.Add(new WorkbenchTab(tab, title, entries));
        }
        // Sections this table doesn't know yet: the tab of their old rail group, at the end.
        foreach (var m in modules.Where(m => !placed.Contains(m.Id)))
        {
            var target = TabForGroup(m.Group);
            int i = tabs.FindIndex(t => t.Id == target);
            tabs[i] = tabs[i] with { Entries = tabs[i].Entries.Append(new WorkbenchEntry(m.Id, m.Title, MainWindow.IconOf(m))).ToList() };
        }
        return tabs.Where(t => t.Pages.Any()).ToList();
    }

    /// <summary>The tab a page key lives in ("history", "quarantine" and "settings" live in none: null).</summary>
    public static WorkbenchTab? TabOf(IReadOnlyList<WorkbenchTab> tabs, string? key)
        => key is null ? null : tabs.FirstOrDefault(t => t.Pages.Any(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase)));
}

/// <summary>
/// The tab bar and the inner list (plain WPF, built in code). It only navigates: the shell's NavigationView still hosts
/// every page (its own left pane is hidden), so pages, keys, scrolling fixes and parameters work exactly as before.
/// </summary>
public sealed class WorkbenchChrome
{
    private readonly Action<string> _navigate;
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _right = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 8, 6) };
    private readonly StackPanel _inner = new() { Margin = new Thickness(8, 12, 8, 12) };
    private readonly Dictionary<string, Button> _tabButtons = new();
    private readonly Dictionary<string, Button> _entryButtons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _lastInTab = new();
    private readonly Dictionary<string, (string Title, string? Badge, string? Spoken)> _badges = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<WorkbenchTab> _layout = Array.Empty<WorkbenchTab>();
    private string? _tab, _key;

    public WorkbenchChrome(Action<string> navigate)
    {
        _navigate = navigate;
        var bar = new DockPanel { LastChildFill = true, Margin = new Thickness(12, 0, 0, 0) };
        DockPanel.SetDock(_right, Dock.Right);
        bar.Children.Add(_right);
        bar.Children.Add(_tabs);
        TabBar = new Border { Child = bar, BorderThickness = new Thickness(0, 0, 0, 1) };
        TabBar.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        AutomationProperties.SetName(_tabs, "Sections");
        InnerList = new Border { Width = 212, BorderThickness = new Thickness(0, 0, 1, 0), Child = new ScrollViewer { Content = _inner, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false } };
        InnerList.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        AutomationProperties.SetName(_inner, "Pages in this tab");
    }

    /// <summary>The row of tabs, with History · Quarantine · Settings on the right.</summary>
    public Border TabBar { get; }

    /// <summary>The current tab's pages, on the left of the page.</summary>
    public Border InnerList { get; }

    /// <summary>The top-right buttons by key ("history", "quarantine", "settings"), for the tour.</summary>
    public IReadOnlyDictionary<string, Button> CornerButtons => _corner;
    private readonly Dictionary<string, Button> _corner = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The tab buttons, for the tour.</summary>
    public FrameworkElement TabsElement => _tabs;

    public void Build(IReadOnlyList<WorkbenchTab> layout)
    {
        _layout = layout;
        // Fill the inner list again (Expert mode can add or remove pages), keeping the tab it showed.
        var previous = _tab;
        _tab = null;
        _tabs.Children.Clear();
        _tabButtons.Clear();
        foreach (var t in layout)
        {
            var b = TabButton(t.Title);
            b.Click += (_, _) => _navigate(_lastInTab.TryGetValue(t.Id, out var last) ? last : t.Pages.First().Key);
            _tabButtons[t.Id] = b;
            _tabs.Children.Add(b);
        }
        _right.Children.Clear();
        _corner.Clear();
        foreach (var (key, title, icon) in new[] { ("history", "History", SymbolRegular.History24), ("quarantine", "Quarantine", SymbolRegular.ArchiveArrowBack24), ("settings", "Settings", SymbolRegular.Settings24) })
        {
            var b = CornerButton(title, icon);
            b.Click += (_, _) => _navigate(key);
            _corner[key] = b;
            _right.Children.Add(b);
        }
        if (WorkbenchLayout.TabOf(_layout, _key) is null && previous is not null && _layout.FirstOrDefault(t => t.Id == previous) is { } kept)
        {
            _tab = kept.Id; // on History, Quarantine or Settings: the last tab's list stays
            FillInner(kept);
        }
        Show(_key);
    }

    /// <summary>A section's small counter ("20 of 37 on"), shown in the inner list (History's on its corner button).</summary>
    public void SetBadge(string key, string title, string? badge, string? spoken)
    {
        _badges[key] = (title, badge, spoken);
        if (_entryButtons.TryGetValue(key, out var b)) b.Content = EntryContent(title, badge, IconFor(key));
        if (_corner.TryGetValue(key, out var c)) c.Content = CornerContent(title, IconForCorner(key), badge);
        var target = _entryButtons.TryGetValue(key, out var e) ? e : _corner.TryGetValue(key, out var cb) ? cb : null;
        if (target is not null) AutomationProperties.SetName(target, badge is null ? title : $"{title}, {spoken ?? badge}");
    }

    /// <summary>The page on screen changed: its tab is selected and its inner list shown, with the page marked.</summary>
    public void Show(string? key)
    {
        _key = key;
        var tab = WorkbenchLayout.TabOf(_layout, key);
        if (tab is not null)
        {
            _lastInTab[tab.Id] = key!;
            if (_tab != tab.Id) { _tab = tab.Id; FillInner(tab); }
        }
        foreach (var (id, b) in _tabButtons) Mark(b, id == _tab && tab is not null, underline: true);
        foreach (var (k, b) in _entryButtons) Mark(b, k.Equals(key, StringComparison.OrdinalIgnoreCase), underline: false);
        foreach (var (k, b) in _corner) Mark(b, k.Equals(key, StringComparison.OrdinalIgnoreCase), underline: false);
        // History, Quarantine and Settings have no inner list of their own: the last tab's stays, nothing in it marked.
        InnerList.Visibility = _tab is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private SymbolRegular IconFor(string key) => _layout.SelectMany(t => t.Pages).FirstOrDefault(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Icon ?? SymbolRegular.Box24;

    private static SymbolRegular IconForCorner(string key) => key switch
    {
        "history" => SymbolRegular.History24,
        "quarantine" => SymbolRegular.ArchiveArrowBack24,
        _ => SymbolRegular.Settings24,
    };

    private void FillInner(WorkbenchTab tab)
    {
        _inner.Children.Clear();
        _entryButtons.Clear();
        foreach (var e in tab.Entries)
        {
            if (e.IsHeader)
            {
                var h = new TextBlock { Text = e.Title, Margin = new Thickness(12, 14, 0, 4), FontSize = 11, FontWeight = FontWeights.SemiBold };
                h.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                AutomationProperties.SetName(h, "Group: " + System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(e.Title.ToLowerInvariant()));
                _inner.Children.Add(h);
                continue;
            }
            var (title, badge, spoken) = _badges.TryGetValue(e.Key, out var known) ? known : (e.Title, null, null);
            var b = new Button { Content = EntryContent(e.Title, badge, e.Icon), HorizontalContentAlignment = HorizontalAlignment.Stretch, Height = 34, Margin = new Thickness(0, 1, 0, 1), Padding = new Thickness(10, 0, 8, 0), Cursor = Cursors.Hand };
            b.SetResourceReference(FrameworkElement.StyleProperty, "TidyNavButton");
            AutomationProperties.SetName(b, badge is null ? e.Title : $"{e.Title}, {spoken ?? badge}");
            var key = e.Key;
            b.Click += (_, _) => _navigate(key);
            _entryButtons[e.Key] = b;
            _inner.Children.Add(b);
        }
    }

    private static object EntryContent(string title, string? badge, SymbolRegular icon)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(new SymbolIcon(icon) { FontSize = 16, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        var t = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, FontSize = 13.5 };
        Grid.SetColumn(t, 1);
        g.Children.Add(t);
        if (badge is not null)
        {
            var pill = new PcTidy.UI.Controls.BadgePill { Text = badge, Tone = PcTidy.UI.Controls.PillTone.Neutral, ShowDot = false, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(6, 0, 6, 1), HorizontalAlignment = HorizontalAlignment.Right, ToolTip = badge };
            Grid.SetColumn(pill, 2);
            g.Children.Add(pill);
        }
        return g;
    }

    private static object CornerContent(string title, SymbolRegular icon, string? badge)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new SymbolIcon(icon) { FontSize = 16, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 });
        if (badge is not null) sp.Children.Add(new PcTidy.UI.Controls.BadgePill { Text = badge, Tone = PcTidy.UI.Controls.PillTone.Neutral, ShowDot = false, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(6, 0, 6, 1) });
        return sp;
    }

    private static Button TabButton(string title)
    {
        var b = new Button { Content = title, Height = 42, Padding = new Thickness(16, 0, 16, 0), Margin = new Thickness(0, 0, 2, 0), Cursor = Cursors.Hand };
        b.SetResourceReference(FrameworkElement.StyleProperty, "TidyTabButton");
        AutomationProperties.SetName(b, title + " tab");
        return b;
    }

    private static Button CornerButton(string title, SymbolRegular icon)
    {
        var b = new Button { Content = CornerContent(title, icon, null), Height = 32, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(2, 0, 0, 0), Cursor = Cursors.Hand };
        b.SetResourceReference(FrameworkElement.StyleProperty, "TidyNavButton");
        AutomationProperties.SetName(b, title);
        return b;
    }

    /// <summary>Selected look: a tab gets an accent underline and bold text; a list row or corner button a soft fill and an accent bar.</summary>
    private static void Mark(Button b, bool on, bool underline)
    {
        b.Tag = on ? "selected" : null;
        b.FontWeight = on && underline ? FontWeights.SemiBold : FontWeights.Normal;
        if (on) AutomationProperties.SetItemStatus(b, "Selected");
        else AutomationProperties.SetItemStatus(b, "");
    }
}
