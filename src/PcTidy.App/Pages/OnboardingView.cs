using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;
using PcTidy.App.Composition;
using PcTidy.App.Shell;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.UI.Controls;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace PcTidy.App.Pages;

/// <summary>
/// First-run welcome, 3 screens (Plan §3): what PC Tidy does · the safety promise (+ privacy) ·
/// two quick choices (data folder, scan depth) with Simulation mode suggested.
/// </summary>
public sealed class OnboardingView : UserControl
{
    private readonly AppServices _services;
    private readonly ContentControl _screen = new() { Focusable = false };
    private readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _next = new() { MinWidth = 140, Height = 38, Appearance = ControlAppearance.Primary, Icon = new SymbolIcon(SymbolRegular.ArrowRight24) };
    private readonly Button _back = new() { Content = "Back", Appearance = ControlAppearance.Transparent, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _skip = new() { Content = "Skip", Appearance = ControlAppearance.Transparent, Margin = new Thickness(0, 0, 8, 0) };
    private TextBlock? _folderText;
    private Segmented? _depth;
    private ToggleSwitch? _simulate;

    public OnboardingView(AppServices services)
    {
        _services = services;
        SetResourceReference(FontFamilyProperty, "TidyText");
        AutomationProperties.SetName(this, "Welcome to PC Tidy");
        _next.Click += (_, _) => { if (Index < 2) Show(Index + 1); else Finish(); };
        _back.Click += (_, _) => Show(Math.Max(0, Index - 1));
        _skip.Click += (_, _) => Finish();
        AutomationProperties.SetName(_back, "Back");
        AutomationProperties.SetName(_skip, "Skip the welcome");

        var footer = new DockPanel { Margin = new Thickness(0, 28, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(_skip);
        buttons.Children.Add(_back);
        buttons.Children.Add(_next);
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        footer.Children.Add(_dots);

        // Same frame on every screen: one column of fixed width (centred when the window is wider), content from the
        // same top-left point (logo never moves), footer pinned to the bottom (Next never moves). Only the middle scrolls.
        var frame = new Grid { MaxWidth = 840, Margin = new Thickness(40, 36, 40, 28), HorizontalAlignment = HorizontalAlignment.Stretch };
        frame.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        frame.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var scroller = new ScrollViewer { Content = _screen, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        frame.Children.Add(scroller);
        footer.Margin = new Thickness(0, 20, 0, 0);
        Grid.SetRow(footer, 1);
        frame.Children.Add(footer);
        Content = frame;
        Show(0);
    }

    public event Action? Finished;
    public int Index { get; private set; }
    private AppSettings S => _services.Get<AppSettings>();

    public void FocusFirst() => Dispatcher.BeginInvoke(() => _next.Focus(), System.Windows.Threading.DispatcherPriority.Input);

    public void Show(int index)
    {
        Index = index;
        _screen.Content = index switch { 0 => WhatItDoes(), 1 => SafetyPromise(), _ => QuickChoices() };
        _next.Content = index < 2 ? "Next" : "Start using PC Tidy";
        AutomationProperties.SetName(_next, (string)_next.Content);
        _back.Visibility = index == 0 ? Visibility.Collapsed : Visibility.Visible;
        _skip.Visibility = index == 2 ? Visibility.Collapsed : Visibility.Visible;
        _dots.Children.Clear();
        for (int i = 0; i < 3; i++)
        {
            var dot = new Border { Width = i == index ? 22 : 6, Height = 6, Margin = new Thickness(0, 0, 6, 0), CornerRadius = new CornerRadius(3) };
            dot.SetResourceReference(Border.BackgroundProperty, i == index ? "AccentFillColorDefaultBrush" : "ControlStrongFillColorDefaultBrush");
            _dots.Children.Add(dot);
        }
        AutomationProperties.SetName(_dots, $"Screen {index + 1} of 3");
    }

    private StackPanel Header(int n, string headline, string body)
    {
        var sp = new StackPanel();
        var logo = new System.Windows.Controls.Image { Width = 72, Height = 72, HorizontalAlignment = HorizontalAlignment.Left };
        logo.SetResourceReference(System.Windows.Controls.Image.SourceProperty, "TidyLogo");
        sp.Children.Add(logo);
        var cap = T($"WELCOME TO PC TIDY · {n} OF 3", "CaptionStrong");
        cap.Margin = new Thickness(0, 20, 0, 0);
        cap.SetResourceReference(TextBlock.ForegroundProperty, "TidyAccentTextBrush");
        sp.Children.Add(cap);
        var h = new TextBlock { Text = headline, FontSize = 34, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        h.SetResourceReference(TextBlock.FontFamilyProperty, "TidyDisplay");
        h.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        sp.Children.Add(h);
        var b = T(body, "Secondary");
        b.FontSize = 15;
        b.Margin = new Thickness(0, 12, 0, 0);
        sp.Children.Add(b);
        return sp;
    }

    private static UniformGrid Cards(params (SymbolRegular Icon, string Title, string Text)[] cards)
    {
        var grid = new UniformGrid { Columns = cards.Length, Margin = new Thickness(0, 20, 0, 0) };
        for (int i = 0; i < cards.Length; i++)
        {
            var b = new Border { Padding = new Thickness(16, 14, 16, 14), Margin = new Thickness(i == 0 ? 0 : 4, 0, i == cards.Length - 1 ? 0 : 4, 0) };
            b.SetResourceReference(StyleProperty, "TidyCard");
            var sp = new StackPanel();
            var icon = new SymbolIcon { Symbol = cards[i].Icon, FontSize = 22, HorizontalAlignment = HorizontalAlignment.Left };
            icon.SetResourceReference(ForegroundProperty, "TidyAccentBrush");
            sp.Children.Add(icon);
            var title = T(cards[i].Title, "CardTitle");
            title.Margin = new Thickness(0, 8, 0, 2);
            sp.Children.Add(title);
            sp.Children.Add(T(cards[i].Text, "Secondary"));
            b.Child = sp;
            AutomationProperties.SetName(b, cards[i].Title + ". " + cards[i].Text);
            grid.Children.Add(b);
        }
        return grid;
    }

    private UIElement WhatItDoes()
    {
        var sp = Header(1, "A tidier, faster PC, without the worry.",
            "PC Tidy looks for space you can free, slow startup apps and messy folders, and explains everything in plain words. You decide what happens.");
        sp.Children.Add(Cards(
            (SymbolRegular.Broom24, "Clean up", "Temp files, caches and leftovers. Each one with its size and why it's safe."),
            (SymbolRegular.Rocket24, "Speed up", "Find apps that slow down startup, and settings that are proven to help."),
            (SymbolRegular.FolderArrowRight24, "Organize", "Sort Downloads and Desktop into tidy folders. Undo any time.")));
        return sp;
    }

    private UIElement SafetyPromise()
    {
        int days = S.QuarantineDays;
        var sp = Header(2, "PC Tidy never deletes anything without showing you first.",
            "Cleaning, speeding up and organizing all work the same way: PC Tidy looks, shows you a list, and only acts when you say so. And everything can be undone.");
        var steps = new StepBar { Margin = new Thickness(0, 22, 0, 0) };
        steps.Set(1, "Looks, changes nothing", "You tick what goes", "One last check", $"Undo for {days} days");
        sp.Children.Add(steps);
        sp.Children.Add(Cards(
            (SymbolRegular.Eye24, "See everything first", "Every item shows its real size and why it's safe."),
            (SymbolRegular.ArrowUndo24, $"Undo for {days} days", "Removed files wait in quarantine. One click puts them back."),
            (SymbolRegular.LockClosed24, "Hands off the important stuff", "Windows, apps, games and your documents are protected.")));
        var privacy = new Border { Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(16, 12, 16, 12) };
        privacy.SetResourceReference(StyleProperty, "TidyCard");
        privacy.SetResourceReference(Border.BackgroundProperty, "TidyAccentSoftBrush");
        var row = new DockPanel();
        var icon = new SymbolIcon { Symbol = SymbolRegular.ShieldCheckmark24, FontSize = 20, Margin = new Thickness(0, 0, 12, 0) };
        icon.SetResourceReference(ForegroundProperty, "TidyAccentBrush");
        row.Children.Add(icon);
        var txt = new StackPanel();
        txt.Children.Add(T("Your privacy", "CardTitle"));
        txt.Children.Add(T(HelpContent.Privacy, "Secondary"));
        row.Children.Add(txt);
        privacy.Child = row;
        sp.Children.Add(privacy);
        return sp;
    }

    private UIElement QuickChoices()
    {
        var sp = Header(3, "Three quick choices.", "You can change all three later in Settings.");
        var card = new Border { Margin = new Thickness(0, 20, 0, 0) };
        card.SetResourceReference(StyleProperty, "TidyCard");
        var inner = new StackPanel();

        inner.Children.Add(T("Where PC Tidy keeps its data", "CardTitle"));
        inner.Children.Add(T("Settings, history and quarantine. It stays on this PC.", "Secondary"));
        var folderRow = new DockPanel { Margin = new Thickness(0, 8, 0, 18) };
        var change = new Button { Content = "Change…" };
        AutomationProperties.SetName(change, "Change the data folder");
        change.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog { Title = "Pick PC Tidy's data folder" };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    _services.Get<ISettingsStore>().SetDataFolder(S, dlg.FolderName);
                    if (_folderText != null) _folderText.Text = S.DataRoot;
                }
                catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "PC Tidy"); }
            }
        };
        DockPanel.SetDock(change, Dock.Right);
        folderRow.Children.Add(change);
        _folderText = T(S.DataRoot, "Body");
        _folderText.VerticalAlignment = VerticalAlignment.Center;
        _folderText.TextTrimming = TextTrimming.CharacterEllipsis;
        _folderText.TextWrapping = TextWrapping.NoWrap;
        _folderText.Margin = new Thickness(0, 0, 16, 0);
        folderRow.Children.Add(_folderText);
        inner.Children.Add(folderRow);

        inner.Children.Add(T("How thorough scans should be", "CardTitle"));
        inner.Children.Add(T("Quick takes seconds and finds most things. Deep looks everywhere and takes longer.", "Secondary"));
        _depth = new Segmented { Items = "Quick (recommended)|Deep", Label = "Scan depth", SelectedIndex = S.DefaultDepth == ScanDepth.Deep ? 1 : 0, Margin = new Thickness(0, 8, 0, 18) };
        inner.Children.Add(_depth);

        var simRow = new DockPanel();
        // On by default: the welcome is the first-time path, and simulating first is the recommended way in.
        _simulate = new ToggleSwitch { IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        AutomationProperties.SetName(_simulate, "Try Simulation mode first (recommended)");
        DockPanel.SetDock(_simulate, Dock.Right);
        simRow.Children.Add(_simulate);
        var simText = new StackPanel();
        simText.Children.Add(T("Try Simulation mode first (recommended)", "CardTitle"));
        simText.Children.Add(T("Your first run offers \"Simulate first\": you see exactly what would happen, and nothing changes.", "Secondary"));
        simRow.Children.Add(simText);
        inner.Children.Add(simRow);

        card.Child = inner;
        sp.Children.Add(card);
        return sp;
    }

    private void Finish()
    {
        if (_depth != null) S.DefaultDepth = _depth.SelectedIndex == 1 ? ScanDepth.Deep : ScanDepth.Quick;
        if (_simulate != null) S.SuggestSimulationOnFirstUse = _simulate.IsChecked == true;
        S.OnboardingDone = true;
        // The welcome card already said the daily update check is on, so Home doesn't repeat it (review L8).
        ShellState.Update(S, s => s with { UpdateCheckNoticeSeen = true });
        try { _services.Get<ISettingsStore>().Save(S); }
        catch { /* not fatal: the welcome simply shows again next time */ }
        Finished?.Invoke();
    }

    private static TextBlock T(string text, string style)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(StyleProperty, style);
        return t;
    }
}
