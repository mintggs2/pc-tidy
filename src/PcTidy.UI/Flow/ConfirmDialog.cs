using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using PcTidy.Core;
using PcTidy.Core.Model;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace PcTidy.UI.Flow;

public enum ConfirmChoice { Cancel, Run, Simulate }

public sealed record ConfirmResult(ConfirmChoice Choice, IReadOnlyList<ActionPlan> Plans);

/// <summary>
/// The standard confirm dialog: exactly what will happen, grouped (quarantine / moves / settings / admin /
/// restart / restore point), and a red "Permanent" box that needs its own "I understand" tick.
/// </summary>
public static class ConfirmDialog
{
    public static async Task<ConfirmResult> ShowAsync(ContentDialogHost host, IReadOnlyList<ActionPlan> plans, FlowOptions options,
        int quarantineDays, bool suggestSimulation, CancellationToken ct)
    {
        var model = new ConfirmModel(plans, options.RestartItemIds, quarantineDays);
        double hostHeight = host.ActualHeight > 0 ? host.ActualHeight : 700;
        // Short windows (1024×700): the "First time?" tip shrinks to one line, so the affected list gets the room.
        var content = new ConfirmDialogContent(model, suggestSimulation, compact: hostHeight < ConfirmDialogContent.CompactBelowHeight);
        // The button names the action ("Clean 6.3 GB"), worked out from exactly what will run.
        var primaryText = options.PrimaryVerb ?? model.PrimaryText(false);
        // The three buttons share the width equally: widen the dialog so a long name ("Open Steam to uninstall (1)") fits.
        double dialogWidth = DialogWidthFor(primaryText);
        content.Width = dialogWidth - 80;
        // Title, paddings and the footer take about 250 px: the body scrolls in the rest, so Back and Confirm never scroll away.
        content.BodyMaxHeight = Math.Max(220, hostHeight - 250);
        var dialog = new ContentDialog(host)
        {
            Title = options.Title,
            Content = content,
            PrimaryButtonText = primaryText,
            PrimaryButtonIcon = new SymbolIcon(PlanFacts.PrimaryIcon(model.Counts)),
            SecondaryButtonText = "Simulate first",
            SecondaryButtonIcon = new SymbolIcon(SymbolRegular.Beaker24),
            CloseButtonText = "Back",
            // Default (Enter) is the safe choice whenever anything can't be undone; on a first run it is "Simulate first".
            DefaultButton = model.HasPermanent ? ContentDialogButton.Close
                : suggestSimulation ? ContentDialogButton.Secondary : ContentDialogButton.Primary,
            PrimaryButtonAppearance = ControlAppearance.Primary,
            DialogMaxWidth = dialogWidth,
            IsPrimaryButtonEnabled = model.CanRun(false),
        };
        // Layout spec §4.6 item 5: Cancel first, Confirm second. WPF-UI's footer always puts its primary button first, so the
        // dialog draws its own footer ([Simulate first] … [Back] [Confirm]) and keeps the built-in one hidden. The built-in
        // button texts stay set, so Esc and the default button behave as before.
        dialog.IsFooterVisible = false;
        var footer = content.AddFooter(primaryText, PlanFacts.PrimaryIcon(model.Counts), model.CanRun(false), dialog.DefaultButton, r => dialog.Hide(r));
        content.AcknowledgedChanged += ack =>
        {
            dialog.IsPrimaryButtonEnabled = model.CanRun(ack);
            footer.Primary.IsEnabled = model.CanRun(ack);
            if (options.PrimaryVerb is null && model.CanRun(ack))
            {
                dialog.PrimaryButtonText = model.PrimaryText(ack);
                footer.SetPrimaryText(dialog.PrimaryButtonText);
            }
        };
        Current = dialog;
        try
        {
            var result = await dialog.ShowAsync(ct);
            return result switch
            {
                ContentDialogResult.Primary => new ConfirmResult(ConfirmChoice.Run, model.PlansToRun(content.Acknowledged)),
                ContentDialogResult.Secondary => new ConfirmResult(ConfirmChoice.Simulate, model.Plans),
                _ => new ConfirmResult(ConfirmChoice.Cancel, Array.Empty<ActionPlan>()),
            };
        }
        finally { Current = null; }
    }

    /// <summary>
    /// 640 px normally; wider (up to 820, which still fits the smallest window) when the main button's text needs it:
    /// about 7 px a character plus the icon and padding, times three equal buttons.
    /// </summary>
    public static double DialogWidthFor(string primaryText)
    {
        double button = primaryText.Length * 7.0 + 64;
        return Math.Clamp(3 * button + 64, 640, 820);
    }

    /// <summary>The dialog on screen right now (screenshot mode closes it programmatically).</summary>
    public static ContentDialog? Current { get; private set; }
}

/// <summary>Body of the confirm dialog.</summary>
public sealed class ConfirmDialogContent : UserControl
{
    private readonly System.Windows.Controls.CheckBox? _ack;

    /// <summary>Window heights below which the dialog is laid out for a short window (one-line tip, list keeps its rows).</summary>
    public const double CompactBelowHeight = 780;

    /// <summary>Rows of the affected list that always show (it never shrinks to one line on a short window).</summary>
    public const int ListMinRows = 4;

    /// <summary>Height of one affected-list row (12.5 px text on two 3 px margins), used for the list's minimum.</summary>
    internal const double ListRowHeight = 23;

    /// <param name="compact">Short window: the "First time?" tip is one line, not a banner.</param>
    public ConfirmDialogContent(ConfirmModel model, bool suggestSimulation, bool compact = false)
    {
        Width = 560;
        SetResourceReference(FontFamilyProperty, "TidyText");
        var stack = new StackPanel();

        if (suggestSimulation && compact)
        {
            var tip = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var info = new SymbolIcon { Symbol = SymbolRegular.Info24, FontSize = 14, Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Top };
            info.SetResourceReference(ForegroundProperty, "SystemFillColorAttentionBrush");
            tip.Children.Add(info);
            var tipText = Styled("First time? Try \"Simulate first\": the full list of what would happen, and nothing changes.", "Secondary");
            tip.Children.Add(tipText);
            AutomationProperties.SetName(tip, tipText.Text);
            FirstTimeTip = tip;
            stack.Children.Add(tip);
        }
        else if (suggestSimulation)
        {
            stack.Children.Add(new InfoBar
            {
                Title = "First time?",
                Message = "Try \"Simulate first\": you get the full list of what would happen, and nothing changes.",
                IsOpen = true,
                IsClosable = false,
                Severity = InfoBarSeverity.Informational,
                Margin = new Thickness(0, 0, 0, 12),
            });
        }

        foreach (var g in model.Groups)
            stack.Children.Add(GroupLine(g));

        if (model.HasPermanent)
        {
            var box = new Border { Padding = new Thickness(16, 12, 16, 12), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Margin = new Thickness(0, 2, 0, 0) };
            box.SetResourceReference(Border.BackgroundProperty, "SystemFillColorCriticalBackgroundBrush");
            box.SetResourceReference(Border.BorderBrushProperty, "SystemFillColorCriticalBrush");
            var inner = new StackPanel();
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            var warn = new SymbolIcon { Symbol = SymbolRegular.Warning24, FontSize = 16, Filled = true, Margin = new Thickness(0, 0, 8, 0) };
            warn.SetResourceReference(ForegroundProperty, "SystemFillColorCriticalBrush");
            head.Children.Add(warn);
            int n = model.Permanent.Count;
            var gone = model.PermanentBytes > 0 ? $" ({Sizes.Format(model.PermanentBytes)})" : "";
            head.Children.Add(Styled($"Permanent: {n} item{PlanFacts.S(n)}{gone} can't go to quarantine", "BodyStrong"));
            inner.Children.Add(head);

            foreach (var item in model.Permanent.Take(5))
            {
                var row = new Grid { Margin = new Thickness(24, 8, 0, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(Styled(item.Description, "Secondary"));
                if (item.ExpectedSize > 0)
                {
                    var size = Styled(Sizes.Format(item.ExpectedSize), "SizeTextStyle");
                    size.FontSize = 13;
                    Grid.SetColumn(size, 1);
                    row.Children.Add(size);
                }
                inner.Children.Add(row);
            }
            if (n > 5) inner.Children.Add(Styled($"and {n - 5} more", "Caption", new Thickness(24, 4, 0, 0)));

            _ack = new System.Windows.Controls.CheckBox { Content = "I understand these can't be undone", Margin = new Thickness(20, 10, 0, 0) };
            AutomationProperties.SetName(_ack, "I understand these can't be undone");
            _ack.Checked += (_, _) => AcknowledgedChanged?.Invoke(true);
            _ack.Unchecked += (_, _) => AcknowledgedChanged?.Invoke(false);
            inner.Children.Add(_ack);
            inner.Children.Add(Styled(model.ReversibleCount > 0
                ? "Leave it unticked to skip them. Everything else still runs."
                : "Nothing else is selected, so tick this to go ahead.", "Caption", new Thickness(24, 2, 0, 0)));
            box.Child = inner;
            stack.Children.Add(box);
        }

        // Every affected item, in a list that scrolls (layout spec §4.6 item 5): the groups above say what kind of change,
        // this says exactly which items. It sits under the scrolling body (not at its end), so on a short window it still
        // shows at least ListMinRows rows above the footer instead of a sliver.
        var lines = AffectedLines(model);
        var listPanel = new StackPanel();
        if (lines.Count > 0)
        {
            listPanel.Children.Add(Styled($"What will be affected ({lines.Count:N0})", "CaptionStrong", new Thickness(0, 10, 0, 4)));
            var list = new ListBox
            {
                ItemsSource = lines.Select(l => new AffectedRow(l)).ToList(),
                MaxHeight = ListMaxHeight,
                MinHeight = Math.Min(lines.Count, ListMinRows) * ListRowHeight + 12,
                Focusable = true,
                Padding = new Thickness(10, 6, 10, 6),
            };
            list.SetResourceReference(StyleProperty, "TidyFlatList");
            var frame = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = list };
            frame.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
            frame.SetResourceReference(Border.BackgroundProperty, "ControlFillColorSecondaryBrush");
            ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            VirtualizingPanel.SetIsVirtualizing(list, true);
            list.ItemTemplate = AffectedTemplate();
            AutomationProperties.SetName(list, $"What will be affected: {lines.Count:N0} item{PlanFacts.S(lines.Count)}");
            AffectedList = list;
            listPanel.Children.Add(frame);
            // Room the list section takes under the body: its header plus the list at its natural height (up to the max).
            _listSectionHeight = 30 + Math.Min(ListMaxHeight, lines.Count * ListRowHeight + 12);
        }

        // The body scrolls inside the dialog; the affected list and the footer (added by AddFooter) stay in view under it
        // on short windows.
        _body = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, Padding = new Thickness(0, 0, 4, 0) };
        _frame.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _frame.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _frame.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _frame.Children.Add(_body);
        Grid.SetRow(listPanel, 1);
        _frame.Children.Add(listPanel);
        Content = _frame;
    }

    /// <summary>Tallest the affected list gets before it scrolls (about seven rows).</summary>
    public const double ListMaxHeight = 7 * ListRowHeight + 12;

    /// <summary>Shortest the scrolling body gets when the window is short (the list and the footer get the rest).</summary>
    public const double MinBodyHeight = 120;

    private readonly ScrollViewer _body;
    private readonly Grid _frame = new();
    private readonly double _listSectionHeight;
    private double _bodyMaxHeight = double.PositiveInfinity;

    /// <summary>
    /// Tallest the body and the affected list may get together (the dialog sets it from the window height, so the footer
    /// always shows). The list keeps its own height; the groups above it scroll in what is left.
    /// </summary>
    public double BodyMaxHeight
    {
        get => _bodyMaxHeight;
        set
        {
            _bodyMaxHeight = value;
            _body.MaxHeight = Math.Max(MinBodyHeight, value - _listSectionHeight);
        }
    }

    /// <summary>Height the groups above the list may scroll in (what's left of <see cref="BodyMaxHeight"/>).</summary>
    public double GroupsMaxHeight => _body.MaxHeight;

    /// <summary>The one-line "First time?" tip of a short window (null when the full banner or no tip shows).</summary>
    public FrameworkElement? FirstTimeTip { get; }

    /// <summary>The scrollable list of affected items (null when the plans are empty).</summary>
    public ListBox? AffectedList { get; }

    /// <summary>One line per plan item, in run order: what it is, its size when it has one, and "can't be undone" when permanent.</summary>
    public static IReadOnlyList<string> AffectedLines(ConfirmModel model)
        => model.Plans.SelectMany(p => p.Items)
            .Select(i => i.Description
                         + (i.ExpectedSize > 0 ? " · " + Sizes.Format(i.ExpectedSize) : "")
                         + (i.Reversible ? "" : " · can't be undone"))
            .ToList();

    private static DataTemplate AffectedTemplate()
    {
        var f = new FrameworkElementFactory(typeof(TextBlock));
        f.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(AffectedRow.Text)));
        // Long paths wrap (readable with the keyboard too), and the full line is also the tooltip.
        f.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        f.SetBinding(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding(nameof(AffectedRow.Text)));
        f.SetValue(TextBlock.FontSizeProperty, 12.5);
        f.SetValue(TextBlock.MarginProperty, new Thickness(2, 3, 2, 3));
        f.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        return new DataTemplate { VisualTree = f };
    }

    /// <summary>The confirm dialog's own footer: [Simulate first] on the left, then [Back] [Confirm]: Cancel first, Confirm second.</summary>
    public sealed class Footer
    {
        internal Footer(Wpf.Ui.Controls.Button simulate, Wpf.Ui.Controls.Button back, Wpf.Ui.Controls.Button primary)
        {
            Simulate = simulate;
            Back = back;
            Primary = primary;
        }

        public Wpf.Ui.Controls.Button Simulate { get; }
        public Wpf.Ui.Controls.Button Back { get; }
        public Wpf.Ui.Controls.Button Primary { get; }

        /// <summary>The buttons from left to right, as a screen reader and Tab meet them.</summary>
        public IReadOnlyList<Wpf.Ui.Controls.Button> InOrder => new[] { Simulate, Back, Primary };

        public void SetPrimaryText(string text)
        {
            Primary.Content = text;
            AutomationProperties.SetName(Primary, text);
        }
    }

    /// <summary>Adds the footer under the content; each button closes the dialog with the matching result.</summary>
    public Footer AddFooter(string primaryText, SymbolRegular primaryIcon, bool canRun, ContentDialogButton defaultButton, Action<ContentDialogResult> close)
    {
        Wpf.Ui.Controls.Button Make(string text, SymbolRegular icon, ControlAppearance look, ContentDialogResult result)
        {
            var b = new Wpf.Ui.Controls.Button { Content = text, Icon = new SymbolIcon(icon), Appearance = look, MinWidth = 120, Height = 36, Margin = new Thickness(8, 0, 0, 0) };
            AutomationProperties.SetName(b, text);
            b.Click += (_, _) => close(result);
            return b;
        }
        var simulate = Make("Simulate first", SymbolRegular.Beaker24, ControlAppearance.Secondary, ContentDialogResult.Secondary);
        simulate.Margin = new Thickness(0);
        AutomationProperties.SetHelpText(simulate, "See exactly what would happen. Nothing changes.");
        var back = Make("Back", SymbolRegular.ArrowLeft24, ControlAppearance.Secondary, ContentDialogResult.None);
        AutomationProperties.SetHelpText(back, "Close without changing anything.");
        var primary = Make(primaryText, primaryIcon, ControlAppearance.Primary, ContentDialogResult.Primary);
        primary.IsEnabled = canRun;

        var row = new DockPanel { Margin = new Thickness(0, 20, 0, 0), LastChildFill = false };
        DockPanel.SetDock(simulate, Dock.Left);
        DockPanel.SetDock(primary, Dock.Right);
        DockPanel.SetDock(back, Dock.Right);
        row.Children.Add(simulate);
        row.Children.Add(primary); // docked right first, so it ends up rightmost
        row.Children.Add(back);
        // Tab order follows reading order: Simulate first, Back, then Confirm.
        System.Windows.Input.KeyboardNavigation.SetTabIndex(simulate, 0);
        System.Windows.Input.KeyboardNavigation.SetTabIndex(back, 1);
        System.Windows.Input.KeyboardNavigation.SetTabIndex(primary, 2);
        Grid.SetRow(row, 2);
        _frame.Children.Add(row);

        var first = defaultButton switch { ContentDialogButton.Primary => primary, ContentDialogButton.Secondary => simulate, _ => back };
        Loaded += (_, _) => Dispatcher.BeginInvoke(() => (first.IsEnabled ? first : back).Focus(), System.Windows.Threading.DispatcherPriority.Input);
        return new Footer(simulate, back, primary);
    }

    public bool Acknowledged => _ack?.IsChecked == true;
    public event Action<bool>? AcknowledgedChanged;

    private static UIElement GroupLine(ConfirmGroup g)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var tile = new Border();
        tile.SetResourceReference(StyleProperty, "IconTile");
        var icon = new SymbolIcon
        {
            FontSize = 18,
            Symbol = g.Kind switch
            {
                ConfirmModel.KindQuarantine => SymbolRegular.ArchiveArrowBack24,
                ConfirmModel.KindMoves => SymbolRegular.FolderArrowRight24,
                ConfirmModel.KindStartup => SymbolRegular.Rocket24,
                ConfirmModel.KindSettings => SymbolRegular.WrenchScrewdriver24,
                ConfirmModel.KindAdmin => SymbolRegular.Shield24,
                ConfirmModel.KindRestart => SymbolRegular.ArrowClockwise24,
                ConfirmModel.KindOpen => SymbolRegular.Open24,
                ConfirmModel.KindCookies => SymbolRegular.Cookies24,
                _ => SymbolRegular.ShieldCheckmark24,
            },
        };
        // Clearing cookies signs you out of websites: the same amber as the "Check" badge the rows carry.
        icon.SetResourceReference(ForegroundProperty, g.Kind == ConfirmModel.KindCookies ? "SystemFillColorCautionBrush" : "TidyAccentBrush");
        tile.Child = icon;
        grid.Children.Add(tile);

        var text = new StackPanel { Margin = new Thickness(14, 0, 0, 0) };
        text.Children.Add(Styled(g.Title, "BodyStrong"));
        text.Children.Add(Styled(g.Detail, "Secondary"));
        if ((g.Kind is ConfirmModel.KindAdmin or ConfirmModel.KindRestart or ConfirmModel.KindStartup or ConfirmModel.KindSettings or ConfirmModel.KindCookies && g.Examples.Count > 0)
            || (g.Kind == ConfirmModel.KindOpen && g.Examples.Count > 1))
            text.Children.Add(Styled(string.Join(" · ", g.Examples), "Caption", new Thickness(0, 2, 0, 0)));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    private static TextBlock Styled(string text, string style, Thickness? margin = null)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(StyleProperty, style);
        if (margin is { } m) t.Margin = m;
        return t;
    }
}

/// <summary>One line of the confirm dialog's "What will be affected" list (its text is also its screen-reader name).</summary>
public sealed record AffectedRow(string Text)
{
    public string AutomationName => Text;
    public string HelpText => "";
    public Thickness SelectionMargin => new(0);
    public CornerRadius SelectionRadius => new(4);
}
