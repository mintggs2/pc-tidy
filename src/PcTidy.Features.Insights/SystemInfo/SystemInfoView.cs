using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using PcTidy.Core;
using PcTidy.Features.Insights.Source;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using ListBox = System.Windows.Controls.ListBox;
using ListBoxItem = System.Windows.Controls.ListBoxItem;
using TextBlock = System.Windows.Controls.TextBlock;

using PcTidy.Features.Insights.SystemInfo;

namespace PcTidy.Features.Insights;

/// <summary>
/// PC care › System info (layout spec §4.5): a read-only list of categories on the left (CPU, Mainboard, Memory, Graphics,
/// Storage, Network) and label/value rows on the right, with links to the Insights cards that explain them. Nothing here
/// changes the PC. Serial numbers, MAC addresses, UUIDs, product keys and user names are never shown
/// (<see cref="SystemInfoPrivacy"/>). Usually shown through <see cref="SystemInfoWindow.Show"/>.
/// </summary>
public sealed class SystemInfoView : UserControl
{
    private readonly ListBox _categories;
    private readonly StackPanel _detail;
    private readonly TextBlock _windows;
    private readonly TextBlock _status;
    private SystemInfoReport? _report;

    /// <summary>Raised when the user picks a card link (the card id, e.g. "insights.ram"); the window closes and opens Insights.</summary>
    public event EventHandler<string>? OpenCardRequested;

    /// <summary>Reads this PC (or the practice PC in a sandbox) in the background when shown.</summary>
    /// <param name="services">Needs IKnownLocations; uses ISystemInfo for graphics names when WMI has none.</param>
    public SystemInfoView(IServiceProvider services) : this()
    {
        Loaded += async (_, _) =>
        {
            if (_report is not null) return;
            try
            {
                var report = await Task.Run(() => Read(services)).ConfigureAwait(true);
                ShowReport(report);
            }
            catch (Exception ex)
            {
                // An event handler must never take the app down: say so and show nothing.
                System.Diagnostics.Debug.WriteLine($"System info read failed: {ex}");
                _windows.Text = "PC Tidy couldn't read this PC's details. Nothing was changed.";
            }
        };
    }

    /// <summary>A ready report (tests, screenshots).</summary>
    internal SystemInfoView(SystemInfoReport report) : this() => ShowReport(report);

    private SystemInfoView()
    {
        SetResourceReference(FontFamilyProperty, "TidyText");
        AutomationProperties.SetName(this, "System info");

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Header: title, read-only pill and the Windows line.
        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(Styled(new TextBlock { Text = "System info" }, "SectionTitle"));
        titleRow.Children.Add(new PcTidy.UI.Controls.BadgePill { Text = "Read-only", Tone = PcTidy.UI.Controls.PillTone.Success, Margin = new Thickness(12, 2, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(titleRow);
        _windows = Styled(new TextBlock { Text = "Reading this PC…", Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap }, "Secondary");
        head.Children.Add(_windows);
        root.Children.Add(head);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        _categories = new ListBox { Margin = new Thickness(0, 0, 16, 0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Top };
        AutomationProperties.SetName(_categories, "System info categories");
        _categories.SelectionChanged += (_, _) => ShowCategory((_categories.SelectedItem as ListBoxItem)?.Tag as SystemInfoCategory);
        body.Children.Add(_categories);

        _detail = new StackPanel();
        _status = Styled(new TextBlock { TextWrapping = TextWrapping.Wrap }, "Secondary");
        _detail.Children.Add(_status);
        var scroll = new ScrollViewer { Content = _detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        AutomationProperties.SetName(scroll, "Details");
        Grid.SetColumn(scroll, 1);
        body.Children.Add(scroll);

        Content = root;
    }

    /// <summary>The categories in order (CPU first). Empty until the read finishes.</summary>
    internal IReadOnlyList<SystemInfoCategory> Categories => _report?.Categories ?? Array.Empty<SystemInfoCategory>();

    internal static SystemInfoReport Read(IServiceProvider services)
    {
        var locations = services.GetService(typeof(IKnownLocations)) as IKnownLocations
                        ?? throw new InvalidOperationException("System info needs IKnownLocations.");
        var system = services.GetService(typeof(ISystemInfo)) as ISystemInfo;
        var source = InsightsScanner.DefaultSource(locations);
        // The user and computer names are never shown; on a practice PC there's nothing real to hide.
        var privateWords = locations.IsSandbox ? Array.Empty<string>() : new[] { Environment.UserName, Environment.MachineName };
        return SystemInfoBuilder.Build(source.ReadInventory(), source.ReadSecurity(), source.ReadDisplays(), source.ReadWindowsIdentity(),
            source.ReadCpuName(), system?.GpuNames ?? Array.Empty<string>(), privateWords);
    }

    internal void ShowReport(SystemInfoReport report)
    {
        _report = report;
        _windows.Text = report.Windows + ". PC Tidy only reads these details.";
        _categories.Items.Clear();
        foreach (var c in report.Categories)
        {
            var item = new ListBoxItem { Tag = c, Padding = new Thickness(10, 8, 10, 8) };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new SymbolIcon { Symbol = Enum.TryParse<SymbolRegular>(c.Icon, out var s) ? s : SymbolRegular.Info24, FontSize = 16, Margin = new Thickness(0, 0, 10, 0) });
            row.Children.Add(Styled(new TextBlock { Text = c.Title, VerticalAlignment = VerticalAlignment.Center }, "Body"));
            item.Content = row;
            AutomationProperties.SetName(item, c.Title);
            _categories.Items.Add(item);
        }
        if (_categories.Items.Count > 0) _categories.SelectedIndex = 0;
    }

    /// <summary>Shows one category (by id, e.g. "memory"); false when it isn't there.</summary>
    internal bool Select(string categoryId)
    {
        for (int i = 0; i < _categories.Items.Count; i++)
            if (_categories.Items[i] is ListBoxItem { Tag: SystemInfoCategory c } && c.Id == categoryId)
            {
                _categories.SelectedIndex = i;
                return true;
            }
        return false;
    }

    private void ShowCategory(SystemInfoCategory? category)
    {
        _detail.Children.Clear();
        if (category is null) return;
        _detail.Children.Add(Styled(new TextBlock { Text = category.Title, Margin = new Thickness(0, 0, 0, 10) }, "CardTitle"));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 240 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        for (int i = 0; i < category.Rows.Count; i++)
        {
            var r = category.Rows[i];
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = Styled(new TextBlock { Text = r.Label, Margin = new Thickness(0, 0, 16, 8), TextWrapping = TextWrapping.Wrap }, "CaptionStrong");
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush"); // 4.5:1 in light too (UI review H1)
            var value = Styled(new TextBlock { Text = r.Value, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap }, "Body");
            AutomationProperties.SetName(value, $"{r.Label}: {r.Value}");
            Grid.SetRow(label, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            grid.Children.Add(label);
            grid.Children.Add(value);
        }
        _detail.Children.Add(grid);

        if (category.Links.Count > 0)
        {
            _detail.Children.Add(Styled(new TextBlock { Text = "Explained in Insights", Margin = new Thickness(0, 12, 0, 6) }, "CaptionStrong"));
            var links = new WrapPanel();
            foreach (var link in category.Links)
            {
                var b = new Button
                {
                    Content = new TextBlock { Text = link.Label, TextWrapping = TextWrapping.Wrap },
                    Appearance = ControlAppearance.Secondary, Margin = new Thickness(0, 0, 8, 6),
                    Icon = new SymbolIcon { Symbol = SymbolRegular.ArrowRight24 }, HorizontalContentAlignment = HorizontalAlignment.Left,
                };
                AutomationProperties.SetName(b, $"Open the {link.Label} card in Insights");
                var id = link.CardId;
                b.Click += (_, _) => OpenCardRequested?.Invoke(this, id);
                links.Children.Add(b);
            }
            _detail.Children.Add(links);
        }
    }

    private static T Styled<T>(T element, string style) where T : FrameworkElement
    {
        element.SetResourceReference(StyleProperty, style);
        return element;
    }
}
