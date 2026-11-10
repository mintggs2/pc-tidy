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
        var content = new ConfirmDialogContent(model, suggestSimulation);
        // The button names the action ("Clean 6.3 GB"), worked out from exactly what will run.
        var primaryText = options.PrimaryVerb ?? model.PrimaryText(false);
        // The three buttons share the width equally: widen the dialog so a long name ("Open Steam to uninstall (1)") fits.
        double dialogWidth = DialogWidthFor(primaryText);
        content.Width = dialogWidth - 80;
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
        content.AcknowledgedChanged += ack =>
        {
            dialog.IsPrimaryButtonEnabled = model.CanRun(ack);
            if (options.PrimaryVerb is null && model.CanRun(ack)) dialog.PrimaryButtonText = model.PrimaryText(ack);
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

    public ConfirmDialogContent(ConfirmModel model, bool suggestSimulation)
    {
        Width = 560;
        SetResourceReference(FontFamilyProperty, "TidyText");
        var stack = new StackPanel();

        if (suggestSimulation)
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
            head.Children.Add(Styled($"Permanent: {n} item{PlanFacts.S(n)} can't go to quarantine", "BodyStrong"));
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

        Content = stack;
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
