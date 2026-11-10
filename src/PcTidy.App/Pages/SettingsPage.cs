using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using PcTidy.App.Composition;
using PcTidy.App.Shell;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.UI.Controls;
using PcTidy.UI.Flow;
using PcTidy.UI.Theming;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace PcTidy.App.Pages;

/// <summary>
/// Settings: every AppSettings field, plus Help/FAQ and About (version, privacy, licenses, changelog).
/// Changes save straight away through ISettingsStore (the shell's only settings write).
/// </summary>
public sealed partial class SettingsPage : UserControl
{
    private readonly AppServices _services;
    private readonly MainWindow _window;
    private readonly StackPanel _root = new();
    // UserControl pages get no outer ScrollViewer from WPF-UI's NavigationView, so the page scrolls itself.
    private readonly ScrollViewer _scroller = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly Dictionary<string, FrameworkElement> _sections = new();

    /// <summary>The groups in page order, for the row of section links at the top (label, heading).</summary>
    private readonly List<(string Label, FrameworkElement Heading)> _groups = new();
    private readonly StackPanel _header = new();

    public SettingsPage(AppServices services, MainWindow window)
    {
        _services = services;
        _window = window;
        AutomationProperties.SetName(this, "Settings");
        // Title, subtitle and the section links stay put; everything under them scrolls.
        _header.SetResourceReference(MarginProperty, "TidyPageMargin");
        _root.Margin = new Thickness(32, 0, 32, 20);
        _scroller.Content = _root;
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(_header);
        Grid.SetRow(_scroller, 1);
        grid.Children.Add(_scroller);
        Content = grid;
        Build();
        BuildSectionLinks();
    }

    /// <summary>
    /// One row of links (Appearance · Scanning · Safety …) under the title: the page is several screens long, and
    /// Help and About are at the very bottom.
    /// </summary>
    private void BuildSectionLinks()
    {
        var links = new WrapPanel { Margin = new Thickness(-8, 8, 0, 0) };
        AutomationProperties.SetName(links, "Jump to a part of Settings");
        foreach (var (label, heading) in _groups)
        {
            var b = new Button { Content = label, Appearance = ControlAppearance.Transparent, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 2, 2) };
            AutomationProperties.SetName(b, "Go to " + label);
            b.Click += (_, _) => ScrollTo(heading);
            links.Children.Add(b);
        }
        _header.Children.Add(links);
    }

    private void ScrollTo(FrameworkElement el)
    {
        if (!el.IsLoaded) return;
        var y = el.TransformToAncestor(_scroller).Transform(new Point(0, 0)).Y;
        _scroller.ScrollToVerticalOffset(_scroller.VerticalOffset + y - 8);
    }

    private AppSettings S => _services.Get<AppSettings>();
    private FlowHost Host => _window.FlowHost;

    private void Save()
    {
        _window.RefreshSummaryContext();
        // AppSettings refuses to overwrite a file it couldn't read until the user confirms; the banner at the top
        // of this page (and on Home) explains and has the OK button. Changes still apply for this session.
        if (S.LoadProblem is not null && !S.LoadProblemAcknowledged) return;
        try { _services.Get<ISettingsStore>().Save(S); }
        catch (Exception ex) { Host.ShowMessage("Settings not saved", "PC Tidy couldn't save your settings: " + ex.Message, isError: true); }
    }

    /// <summary>Scrolls to "help" or "about".</summary>
    public void ShowSection(string key)
    {
        if (!_sections.TryGetValue(key, out var el)) return;
        void Scroll() => Dispatcher.BeginInvoke(() => ScrollTo(el), System.Windows.Threading.DispatcherPriority.ContextIdle);
        if (IsLoaded) Scroll();
        else
        {
            RoutedEventHandler? once = null;
            once = (_, _) => { Loaded -= once; Scroll(); };
            Loaded += once;
        }
    }

    private void Build()
    {
        _header.Children.Add(Styled(new TextBlock { Text = "Settings" }, "PageTitle"));
        _header.Children.Add(Styled(new TextBlock
        {
            Text = _services.IsDemo ? "Demo mode: changes here only last until you close PC Tidy." : "Changes are saved as soon as you make them.",
        }, "PageSubtitle"));
        AddSettingsProblemBanner();

        // ---------------- Appearance ----------------
        Group("Appearance");
        var theme = new ComboBox { MinWidth = 160, ItemsSource = new[] { "Follow Windows", "Dark", "Light" } };
        theme.SelectedIndex = TidyTheme.Parse(S.Theme) switch { ThemePreference.Dark => 1, ThemePreference.Light => 2, _ => 0 };
        theme.SelectionChanged += (_, _) =>
        {
            S.Theme = theme.SelectedIndex switch { 1 => "Dark", 2 => "Light", _ => "System" };
            _window.ApplyTheme(TidyTheme.Parse(S.Theme));
            Save();
        };
        Card(SymbolRegular.DarkTheme24, "Theme", "Follows Windows unless you pick one. PC Tidy keeps its green accent in both.", theme);
        var prefs = ShellPrefs.Get(S);
        Card(SymbolRegular.HeartPulse24, "Show the health score on Home", "A simple score with every reason behind it, only after you press Check now. Turn it off if you'd rather not see it.",
            Toggle(prefs.ShowHealthScore, on => { ShellPrefs.Set(S, ShellPrefs.Get(S) with { ShowHealthScore = on }); Save(); }));

        // ---------------- Scanning ----------------
        Group("Scanning");
        var depth = new Segmented { Items = "Quick|Deep", Label = "Default scan depth", SelectedIndex = S.DefaultDepth == ScanDepth.Deep ? 1 : 0 };
        depth.SelectionChanged += (_, _) => { S.DefaultDepth = depth.SelectedIndex == 1 ? ScanDepth.Deep : ScanDepth.Quick; Save(); };
        Card(SymbolRegular.ScanDash24, "Default scan depth", "Quick looks in the usual places in seconds. Deep looks everywhere and takes longer.", depth);
        Card(SymbolRegular.Flash24, "Faster scans with admin", "Lets PC Tidy read the drive's file table directly (asks for admin once). Off by default.",
            Toggle(S.AllowAdminFastScan, on => { S.AllowAdminFastScan = on; Save(); }));
        FolderCard(SymbolRegular.FolderProhibited24, "Folders to skip when scanning", "PC Tidy won't even look inside these.", S.ScanExclusions);

        // ---------------- Safety ----------------
        Group("Safety");
        var days = new NumberBox { Minimum = 1, Maximum = 90, Value = S.QuarantineDays, MinWidth = 140, SmallChange = 1, LargeChange = 7, ClearButtonEnabled = false };
        AutomationProperties.SetName(days, "Days to keep quarantined items");
        days.ValueChanged += (_, _) => { if (days.Value is { } v) { S.QuarantineDays = (int)Math.Clamp(v, 1, 90); Save(); } };
        Card(SymbolRegular.ArchiveArrowBack24, "Keep quarantined items for (days)", "Until then, anything PC Tidy removed can be put back with one click.", days);
        Card(SymbolRegular.ChatHelp24, "Ask before emptying old quarantine", "Recommended. When off, items older than the limit above are emptied without asking.",
            Toggle(S.AskBeforePurge, on => { S.AskBeforePurge = on; Save(); }));
        var cap = new NumberBox { Minimum = 0, Maximum = 2000, Value = prefs.QuarantineCapGb, MinWidth = 140, SmallChange = 5, ClearButtonEnabled = false };
        AutomationProperties.SetName(cap, "Quarantine size limit in gigabytes");
        cap.ValueChanged += (_, _) => { if (cap.Value is { } v) { ShellPrefs.Set(S, ShellPrefs.Get(S) with { QuarantineCapGb = (int)Math.Clamp(v, 0, 2000) }); Save(); } };
        Card(SymbolRegular.Storage24, "Quarantine size limit (GB)", "When quarantine grows past this, PC Tidy offers to delete the oldest runs. It always asks. 0 = no limit.", cap);
        Card(SymbolRegular.Beaker24, "Suggest Simulation mode", "Offers \"Simulate first\" in the confirm step until your first real run.",
            Toggle(S.SuggestSimulationOnFirstUse, on => { S.SuggestSimulationOnFirstUse = on; Save(); }));
        FolderCard(SymbolRegular.LockClosed24, "Extra protected folders", "Never touched, by anything. Windows, apps, games and PC Tidy's own data are always protected.", S.ExtraProtectedFolders);

        var clouds = (_services.GetService(typeof(IKnownLocations)) as IKnownLocations)?.CloudRoots ?? Array.Empty<string>();
        if (clouds.Count > 0)
            FolderCard(SymbolRegular.Cloud24, "OneDrive folders PC Tidy may organize", "OneDrive is hands-off unless you add a folder here. Online-only files are never downloaded.", S.AllowedAskFirstFolders, clouds[0]);
        else
            Card(SymbolRegular.Cloud24, "OneDrive folders", "OneDrive isn't set up on this PC, so there's nothing to choose here.", null);

        Card(SymbolRegular.Warning24, "Expert mode", "Unlocks extra tweaks and services that only help in some setups. Each shows its warning and can be undone. Never part of one-click runs. Off by default.",
            Toggle(S.ExpertMode, on => SetExpertModeAsync(on).Forget(Host, "Expert mode not changed")), out var expertToggle);
        _expertToggle = expertToggle;

        // ---------------- Your data ----------------
        Group("Your data");
        var dataText = Styled(new TextBlock { Text = S.DataRoot, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 320, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) }, "Secondary");
        var change = new Button { Content = "Change…" };
        AutomationProperties.SetName(change, "Change the data folder");
        change.Click += (_, _) => ChangeDataFolder(dataText);
        var open = new Button { Content = "Open", Margin = new Thickness(6, 0, 0, 0) };
        AutomationProperties.SetName(open, "Open the data folder");
        open.Click += (_, _) => Host.OpenInExplorer(S.DataRoot);
        Card(SymbolRegular.Folder24, "Data folder", "Settings, run history, quarantine and logs live here, on this PC only.", Row(dataText, change, open));
        Card(SymbolRegular.Lightbulb24, "Learn from my choices", "Remembers things you always skip, on this PC only. You can see and delete everything it learned.",
            Toggle(S.LearningEnabled, on => { S.LearningEnabled = on; Save(); }));
        var obsidianText = Styled(new TextBlock { Text = S.ObsidianExportFolder ?? "Not set", TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 300, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) }, "Secondary");
        var pick = new Button { Content = "Choose…" };
        AutomationProperties.SetName(pick, "Choose a notes folder for reports");
        pick.Click += (_, _) => { if (PickFolder("Pick a folder for run reports") is { } f) { S.ObsidianExportFolder = f; obsidianText.Text = f; Save(); } };
        var clear = new Button { Content = "Clear", Margin = new Thickness(6, 0, 0, 0), Appearance = ControlAppearance.Transparent };
        AutomationProperties.SetName(clear, "Stop copying reports to a notes folder");
        clear.Click += (_, _) => { S.ObsidianExportFolder = null; obsidianText.Text = "Not set"; Save(); };
        Card(SymbolRegular.Notebook24, "Also copy run reports to a notes folder", "Optional, for Obsidian or similar. Off unless you pick a folder.", Row(obsidianText, pick, clear));
        Card(SymbolRegular.ArrowUndo24, "PC Tidy Rescue", "A small separate tool that can undo recent runs even if PC Tidy itself won't start.", RescueButton());

        BuildAssistant();
        BuildMoveToNewPc();

        // ---------------- Background, right-click menu, updates, uninstall (SettingsPage.Desktop.cs) ----------------
        BuildBackground();
        BuildRightClickMenu();
        BuildUpdates();
        BuildUninstall();

        // ---------------- Help ----------------
        var help = Group("Help & FAQ", "Help");
        _sections["help"] = help;
        foreach (var (q, a) in HelpContent.Faq)
        {
            var answer = string.IsNullOrEmpty(a) ? HelpContent.WhatWeDontClean : a;
            var exp = new CardExpander { Header = Styled(new TextBlock { Text = q }, "CardTitle"), Margin = new Thickness(0, 0, 0, 4), Icon = new SymbolIcon(SymbolRegular.QuestionCircle24) };
            var body = Styled(new TextBlock { Text = answer, TextWrapping = TextWrapping.Wrap }, "Body");
            if (q == HelpContent.SomethingWentWrong)
            {
                var rescue = RescueButton();
                rescue.Margin = new Thickness(0, 10, 0, 0);
                rescue.HorizontalAlignment = HorizontalAlignment.Left;
                exp.Content = new StackPanel { Children = { body, rescue } };
            }
            else exp.Content = body;
            NameExpander(exp, q);
            _root.Children.Add(exp);
        }

        // ---------------- About ----------------
        var about = Group("About");
        _sections["about"] = about;
        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
                      ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0";
        var card = new Border { Padding = new Thickness(20, 18, 20, 18) };
        card.SetResourceReference(StyleProperty, "TidyCard");
        var dock = new DockPanel();
        var logo = new System.Windows.Controls.Image { Width = 56, Height = 56, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Top };
        logo.SetResourceReference(System.Windows.Controls.Image.SourceProperty, "TidyLogo");
        dock.Children.Add(logo);
        var welcome = new Button { Content = "Show the welcome screens", VerticalAlignment = VerticalAlignment.Top };
        AutomationProperties.SetName(welcome, "Show the welcome screens again");
        welcome.Click += (_, _) => _window.ShowOnboarding(true);
        DockPanel.SetDock(welcome, Dock.Right);
        dock.Children.Add(welcome);
        var info = new StackPanel();
        info.Children.Add(Styled(new TextBlock { Text = "PC Tidy", FontSize = 20 }, "SectionTitle"));
        info.Children.Add(Styled(new TextBlock { Text = $"Version {version} · clean up, speed up and organize Windows, safely" }, "Secondary"));
        var privacy = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var lockIcon = new SymbolIcon { Symbol = SymbolRegular.ShieldCheckmark24, FontSize = 16, Margin = new Thickness(0, 0, 8, 0) };
        lockIcon.SetResourceReference(ForegroundProperty, "TidyAccentBrush");
        privacy.Children.Add(lockIcon);
        privacy.Children.Add(Styled(new TextBlock { Text = HelpContent.Privacy }, "BodyStrong"));
        info.Children.Add(privacy);
        info.Children.Add(Styled(new TextBlock
        {
            Text = "No telemetry: PC Tidy doesn't collect usage data, crash reports or file lists. Crash logs stay in your data folder. It works fully offline.",
            Margin = new Thickness(24, 2, 0, 0),
        }, "Secondary"));
        if (_services.IsDemo) info.Children.Add(Styled(new TextBlock { Text = _services.DemoReason ?? "", Margin = new Thickness(0, 8, 0, 0) }, "Caption"));
        dock.Children.Add(info);
        card.Child = dock;
        _root.Children.Add(card);
        Expander(SymbolRegular.Certificate24, "Licenses", HelpContent.Licences);
        Expander(SymbolRegular.DocumentBulletList24, "What's new", HelpContent.Changelog);
    }

    // ---------------- actions ----------------

    /// <summary>The settings file couldn't be read: say so here too, where the user checks them, with the same OK.</summary>
    private void AddSettingsProblemBanner()
    {
        if (S.LoadProblem is not { } why || S.LoadProblemAcknowledged) return;
        var bar = new InfoBar
        {
            Title = "Your settings file couldn't be read.",
            Message = ShellPrefs.FriendlyLoadProblem(why) + " Check the settings below. Changes apply now, and are saved once you press OK.",
            IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Margin = new Thickness(0, 14, 0, 0),
        };
        var ok = new Button { Content = "OK, save these settings", Appearance = ControlAppearance.Primary };
        AutomationProperties.SetName(ok, "OK, save these settings from now on");
        UIElement? banner = null;
        ok.Click += (_, _) =>
        {
            S.LoadProblemAcknowledged = true;
            Save();
            if (banner != null) _root.Children.Remove(banner);
        };
        banner = InfoBars.WithActions(bar, ok);
        _root.Children.Add(banner);
    }

    private Button RescueButton()
    {
        var b = new Button { Content = "Open PC Tidy Rescue", Icon = new SymbolIcon(SymbolRegular.Open24) };
        AutomationProperties.SetName(b, "Open PC Tidy Rescue");
        b.Click += (_, _) => Host.OpenRescueTool();
        return b;
    }

    private ToggleSwitch? _expertToggle;

    private async Task SetExpertModeAsync(bool on)
    {
        if (on)
        {
            var dialog = new ContentDialog(_window.DialogHost)
            {
                Title = "Turn on Expert mode?",
                Content = FlowHost.Body("Expert mode shows tweaks that can cause problems on some PCs (for example turning off search indexing). " +
                                        "Each one still shows its warning, makes a backup first and can be undone. Harmful changes stay unavailable, even here."),
                PrimaryButtonText = "Turn on",
                CloseButtonText = "Keep it off",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await Host.ShowDialogAsync(dialog) != ContentDialogResult.Primary)
            {
                if (_expertToggle != null) _expertToggle.IsChecked = false;
                return;
            }
        }
        S.ExpertMode = on;
        Save();
        _window.BuildRail();
    }

    private void ChangeDataFolder(TextBlock display)
    {
        if (PickFolder("Pick PC Tidy's data folder") is not { } folder) return;
        try
        {
            _services.Get<ISettingsStore>().SetDataFolder(S, folder);
            display.Text = S.DataRoot;
            var own = DataFolderChoice.Same(S.DataRoot, folder) ? ""
                : $" That folder already had other things in it, so PC Tidy made its own folder inside it: {S.DataRoot}. That way uninstalling never touches your files.";
            Host.ShowMessage("Data folder changed", "PC Tidy uses the new folder from its next start. Your existing history and quarantine stay in the old folder until then, so nothing is lost." + own);
        }
        catch (Exception ex)
        {
            Host.ShowMessage("Couldn't use that folder", ex.Message, isError: true);
        }
    }

    private static string? PickFolder(string title)
    {
        var dlg = new OpenFolderDialog { Title = title, Multiselect = false };
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }

    // ---------------- building blocks ----------------

    /// <param name="link">Short name in the section links at the top ("Background"); defaults to the title.</param>
    private FrameworkElement Group(string title, string? link = null)
    {
        var t = Styled(new TextBlock { Text = title }, "SettingsGroupTitle");
        _root.Children.Add(t);
        _groups.Add((link ?? title, t));
        return t;
    }

    private void Card(SymbolRegular icon, string title, string description, FrameworkElement? control) => Card(icon, title, description, control, out _);

    private void Card<T>(SymbolRegular icon, string title, string description, T? control, out T? created) where T : FrameworkElement
    {
        var header = new StackPanel();
        header.Children.Add(Styled(new TextBlock { Text = title }, "CardTitle"));
        header.Children.Add(Styled(new TextBlock { Text = description, Margin = new Thickness(0, 2, 16, 0) }, "Secondary"));
        if (control != null && AutomationProperties.GetName(control) is "" or null) AutomationProperties.SetName(control, title);
        // WPF-UI's CardControl is a button: as a Tab stop it would be an unnamed "button" between the real controls.
        // The row only hosts its switch or buttons, so it isn't a stop; for screen readers it carries its title.
        var card = new CardControl { Icon = new SymbolIcon(icon), Header = header, Content = control, Margin = new Thickness(0, 0, 0, 4), Focusable = false, IsTabStop = false };
        AutomationProperties.SetName(card, title);
        AutomationProperties.SetHelpText(card, description);
        _root.Children.Add(card);
        created = control;
    }

    private void FolderCard(SymbolRegular icon, string title, string description, List<string> folders, string? startIn = null)
    {
        var panel = new StackPanel { MinWidth = 280, HorizontalAlignment = HorizontalAlignment.Right };
        void Refresh()
        {
            panel.Children.Clear();
            foreach (var f in folders.ToList())
            {
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
                var remove = new Button { Content = "Remove", Appearance = ControlAppearance.Transparent, Padding = new Thickness(8, 2, 8, 2) };
                AutomationProperties.SetName(remove, "Remove " + f);
                remove.Click += (_, _) => { folders.Remove(f); Save(); Refresh(); };
                DockPanel.SetDock(remove, Dock.Right);
                row.Children.Add(remove);
                row.Children.Add(Styled(new TextBlock { Text = f, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 300, VerticalAlignment = VerticalAlignment.Center }, "Secondary"));
                panel.Children.Add(row);
            }
            if (folders.Count == 0) panel.Children.Add(Styled(new TextBlock { Text = "None yet", Margin = new Thickness(0, 0, 0, 6), HorizontalAlignment = HorizontalAlignment.Right }, "Caption"));
            var add = new Button { Content = "Add folder…", Icon = new SymbolIcon(SymbolRegular.Add24), HorizontalAlignment = HorizontalAlignment.Right };
            AutomationProperties.SetName(add, "Add a folder to " + title);
            add.Click += (_, _) =>
            {
                var dlg = new OpenFolderDialog { Title = title, InitialDirectory = startIn ?? "" };
                if (dlg.ShowDialog() == true && !folders.Contains(dlg.FolderName, StringComparer.OrdinalIgnoreCase))
                {
                    folders.Add(dlg.FolderName);
                    Save();
                    Refresh();
                }
            };
            panel.Children.Add(add);
        }
        Refresh();
        Card(icon, title, description, panel);
    }

    private void Expander(SymbolRegular icon, string title, string text)
    {
        var exp = new CardExpander { Header = Styled(new TextBlock { Text = title }, "CardTitle"), Icon = new SymbolIcon(icon), Margin = new Thickness(0, 4, 0, 0) };
        exp.Content = Styled(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, "Body");
        NameExpander(exp, title);
        _root.Children.Add(exp);
    }

    /// <summary>
    /// GOTCHA (WPF-UI 4.3): CardExpander's header is a ToggleButton inside its template with no name, so screen
    /// readers just say "button". Name it once the template exists ("Licenses, show or hide").
    /// </summary>
    private static void NameExpander(CardExpander exp, string title)
    {
        AutomationProperties.SetName(exp, title);
        exp.Loaded += (_, _) =>
        {
            foreach (var toggle in Descendants<System.Windows.Controls.Primitives.ToggleButton>(exp))
            {
                AutomationProperties.SetName(toggle, $"{title}, show or hide");
                AutomationProperties.SetHelpText(toggle, "Press Space or Enter to open or close.");
            }
        };
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }

    private static ToggleSwitch Toggle(bool value, Action<bool> changed)
    {
        var t = new ToggleSwitch { IsChecked = value };
        t.Checked += (_, _) => changed(true);
        t.Unchecked += (_, _) => changed(false);
        return t;
    }

    private static StackPanel Row(params UIElement[] children)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var c in children) sp.Children.Add(c);
        return sp;
    }

    private static T Styled<T>(T el, string style) where T : FrameworkElement
    {
        el.SetResourceReference(StyleProperty, style);
        if (el is TextBlock tb && tb.TextTrimming == TextTrimming.None) tb.TextWrapping = TextWrapping.Wrap;
        return el;
    }
}
