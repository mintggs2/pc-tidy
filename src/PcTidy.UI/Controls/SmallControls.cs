using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using PcTidy.Core;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace PcTidy.UI.Controls;

/// <summary>A byte count rendered as friendly text ("6.2 GB"). Bind <see cref="Bytes"/>.</summary>
public class SizeText : TextBlock
{
    public static readonly DependencyProperty BytesProperty = DependencyProperty.Register(
        nameof(Bytes), typeof(long), typeof(SizeText), new PropertyMetadata(0L, (d, e) => ((SizeText)d).Update()));
    public static readonly DependencyProperty PrefixProperty = DependencyProperty.Register(
        nameof(Prefix), typeof(string), typeof(SizeText), new PropertyMetadata("", (d, e) => ((SizeText)d).Update()));
    public static readonly DependencyProperty ZeroTextProperty = DependencyProperty.Register(
        nameof(ZeroText), typeof(string), typeof(SizeText), new PropertyMetadata(null, (d, e) => ((SizeText)d).Update()));

    public long Bytes { get => (long)GetValue(BytesProperty); set => SetValue(BytesProperty, value); }
    public string Prefix { get => (string)GetValue(PrefixProperty); set => SetValue(PrefixProperty, value); }
    /// <summary>Shown instead of "0 B" (e.g. "—" for information-only rows).</summary>
    public string? ZeroText { get => (string?)GetValue(ZeroTextProperty); set => SetValue(ZeroTextProperty, value); }

    public SizeText()
    {
        SetResourceReference(StyleProperty, "SizeTextStyle");
        Update();
    }

    private void Update() => Text = Bytes == 0 && ZeroText != null ? ZeroText : Prefix + Sizes.Format(Bytes);
}

/// <summary>
/// The tick at the left of a sticky bottom bar: a filled accent tick when something is selected, an empty grey box
/// when nothing is (so it never looks ticked next to "Nothing selected"). Bind or set <see cref="HasSelection"/>.
/// </summary>
public class SelectionGlyph : ContentControl
{
    public static readonly DependencyProperty HasSelectionProperty = DependencyProperty.Register(
        nameof(HasSelection), typeof(bool), typeof(SelectionGlyph), new PropertyMetadata(false, (d, _) => ((SelectionGlyph)d).Update()));

    private readonly SymbolIcon _icon = new() { FontSize = 22 };

    public bool HasSelection { get => (bool)GetValue(HasSelectionProperty); set => SetValue(HasSelectionProperty, value); }

    public SelectionGlyph()
    {
        Focusable = false;
        IsTabStop = false;
        VerticalAlignment = VerticalAlignment.Center;
        Content = _icon;
        Update();
    }

    // Decorative: the text beside it already says what is selected, so screen readers skip it.
    protected override System.Windows.Automation.Peers.AutomationPeer? OnCreateAutomationPeer() => null;

    private void Update()
    {
        _icon.Symbol = HasSelection ? SymbolRegular.CheckboxChecked24 : SymbolRegular.CheckboxUnchecked24;
        _icon.Filled = HasSelection;
        _icon.SetResourceReference(ForegroundProperty, HasSelection ? "TidyAccentBrush" : "TextFillColorTertiaryBrush");
    }
}

/// <summary>
/// One drive as a ring: free space in the middle, label and "used of total" underneath.
/// Turns red under 10% free.
/// </summary>
public class DriveRing : StackPanel
{
    private readonly RingGauge _ring = new();
    private readonly TextBlock _free = new() { HorizontalAlignment = HorizontalAlignment.Center, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _freeCaption = new() { HorizontalAlignment = HorizontalAlignment.Center, Text = "free" };
    private readonly TextBlock _label = new() { Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _usage = new();
    private readonly Grid _ringHost = new() { HorizontalAlignment = HorizontalAlignment.Left };
    private readonly StackPanel _text = new() { VerticalAlignment = VerticalAlignment.Center };

    public static readonly DependencyProperty RootProperty = Reg(nameof(Root), typeof(string), "");
    public static readonly DependencyProperty DriveLabelProperty = Reg(nameof(DriveLabel), typeof(string), "");
    public static readonly DependencyProperty TotalBytesProperty = Reg(nameof(TotalBytes), typeof(long), 0L);
    public static readonly DependencyProperty FreeBytesProperty = Reg(nameof(FreeBytes), typeof(long), 0L);
    public static readonly DependencyProperty RingSizeProperty = Reg(nameof(RingSize), typeof(double), 96.0);
    public static readonly DependencyProperty IsSecondaryProperty = Reg(nameof(IsSecondary), typeof(bool), false);

    private static DependencyProperty Reg(string name, Type t, object def) =>
        DependencyProperty.Register(name, t, typeof(DriveRing), new PropertyMetadata(def, (d, _) => ((DriveRing)d).Update()));

    public string Root { get => (string)GetValue(RootProperty); set => SetValue(RootProperty, value); }
    public string DriveLabel { get => (string)GetValue(DriveLabelProperty); set => SetValue(DriveLabelProperty, value); }
    public long TotalBytes { get => (long)GetValue(TotalBytesProperty); set => SetValue(TotalBytesProperty, value); }
    public long FreeBytes { get => (long)GetValue(FreeBytesProperty); set => SetValue(FreeBytesProperty, value); }
    public double RingSize { get => (double)GetValue(RingSizeProperty); set => SetValue(RingSizeProperty, value); }
    /// <summary>Second and later drives use a blue ring so two rings side by side are easy to tell apart.</summary>
    public bool IsSecondary { get => (bool)GetValue(IsSecondaryProperty); set => SetValue(IsSecondaryProperty, value); }

    public DriveRing()
    {
        var centre = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _free.SetResourceReference(TextBlock.FontFamilyProperty, "TidyDisplay");
        _free.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        _freeCaption.SetResourceReference(StyleProperty, "Caption");
        centre.Children.Add(_free);
        centre.Children.Add(_freeCaption);
        _ringHost.Children.Add(_ring);
        _ringHost.Children.Add(centre);
        _label.SetResourceReference(StyleProperty, "CardTitle");
        _usage.SetResourceReference(StyleProperty, "Caption");
        _text.Children.Add(_label);
        _text.Children.Add(_usage);
        Children.Add(_ringHost);
        Children.Add(_text);
        Update();
    }

    private void Update()
    {
        double size = RingSize;
        bool side = Orientation == Orientation.Horizontal;
        _text.Margin = side ? new Thickness(12, 0, 0, 0) : new Thickness(0);
        _label.Margin = side ? new Thickness(0) : new Thickness(0, 10, 0, 0);
        _ringHost.Width = _ringHost.Height = size;
        _ring.Thickness = Math.Max(5, size / 10.5);
        _free.FontSize = Math.Max(12, size / 5);
        _freeCaption.Visibility = size >= 72 ? Visibility.Visible : Visibility.Collapsed;
        long used = Math.Max(0, TotalBytes - FreeBytes);
        double usedFraction = TotalBytes > 0 ? (double)used / TotalBytes : 0;
        _ring.Value = usedFraction;
        bool low = TotalBytes > 0 && FreeBytes < TotalBytes / 10;
        _ring.SetResourceReference(RingGauge.RingBrushProperty, low ? "TidyDriveLowBrush" : IsSecondary ? "TidySecondDriveBrush" : "TidyAccentBrush");
        _free.Text = Sizes.Format(FreeBytes);
        var root = (Root ?? "").TrimEnd('\\');
        _label.Text = string.IsNullOrWhiteSpace(DriveLabel) ? root : $"{root}  {DriveLabel}";
        _usage.Text = TotalBytes > 0 ? $"{Sizes.Format(used)} used of {Sizes.Format(TotalBytes)}" : "Size unknown";
        AutomationProperties.SetName(this, $"Drive {root}: {Sizes.Format(FreeBytes)} free of {Sizes.Format(TotalBytes)}{(low ? ", almost full" : "")}");
    }
}

/// <summary>Friendly empty / not-available state: icon, title, message, optional action button.</summary>
public class EmptyState : ContentControl
{
    private readonly SymbolIcon _icon = new() { FontSize = 28, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _title = new() { HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 14, 0, 0) };
    private readonly TextBlock _message = new() { HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, MaxWidth = 460, Margin = new Thickness(0, 4, 0, 0) };
    private readonly Wpf.Ui.Controls.Button _action = new() { Appearance = ControlAppearance.Primary, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 0), MinWidth = 140, Height = 38 };

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(SymbolRegular), typeof(EmptyState),
        new PropertyMetadata(SymbolRegular.Info24, (d, _) => ((EmptyState)d).Update()));
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(EmptyState),
        new PropertyMetadata("", (d, _) => ((EmptyState)d).Update()));
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(nameof(Message), typeof(string), typeof(EmptyState),
        new PropertyMetadata("", (d, _) => ((EmptyState)d).Update()));
    public static readonly DependencyProperty ActionTextProperty = DependencyProperty.Register(nameof(ActionText), typeof(string), typeof(EmptyState),
        new PropertyMetadata(null, (d, _) => ((EmptyState)d).Update()));
    public static readonly DependencyProperty ActionIconProperty = DependencyProperty.Register(nameof(ActionIcon), typeof(SymbolRegular), typeof(EmptyState),
        new PropertyMetadata(SymbolRegular.Empty, (d, _) => ((EmptyState)d).Update()));

    public SymbolRegular Icon { get => (SymbolRegular)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Message { get => (string)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    public string? ActionText { get => (string?)GetValue(ActionTextProperty); set => SetValue(ActionTextProperty, value); }
    public SymbolRegular ActionIcon { get => (SymbolRegular)GetValue(ActionIconProperty); set => SetValue(ActionIconProperty, value); }

    public event RoutedEventHandler? ActionClick;

    public EmptyState()
    {
        Focusable = false;
        var tile = new Border { Width = 64, Height = 64, CornerRadius = new CornerRadius(16), HorizontalAlignment = HorizontalAlignment.Center, Child = _icon };
        tile.SetResourceReference(Border.BackgroundProperty, "TidyAccentSoftBrush");
        _icon.SetResourceReference(ForegroundProperty, "TidyAccentBrush");
        _title.SetResourceReference(StyleProperty, "SectionTitle");
        _message.SetResourceReference(StyleProperty, "Secondary");
        _message.TextAlignment = TextAlignment.Center;
        _action.Click += (s, e) => ActionClick?.Invoke(this, e);
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 32, 24, 32) };
        stack.Children.Add(tile);
        stack.Children.Add(_title);
        stack.Children.Add(_message);
        stack.Children.Add(_action);
        Content = stack;
        Update();
    }

    private void Update()
    {
        _icon.Symbol = Icon;
        _title.Text = Title;
        _message.Text = Message;
        _message.Visibility = string.IsNullOrEmpty(Message) ? Visibility.Collapsed : Visibility.Visible;
        _action.Content = ActionText;
        _action.Icon = ActionIcon == SymbolRegular.Empty ? null : new SymbolIcon(ActionIcon);
        _action.Visibility = string.IsNullOrEmpty(ActionText) ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(_action, ActionText ?? "");
        AutomationProperties.SetName(this, Title);
    }
}

/// <summary>
/// Segmented picker (Quick | Deep, Safe | Recommended | Gamer | Privacy). Built from RadioButtons so it is
/// keyboard reachable (Tab, arrows) and screen-reader friendly.
/// </summary>
public class Segmented : Border
{
    private static int _groupCounter;
    private readonly StackPanel _panel = new() { Orientation = Orientation.Horizontal };
    private readonly string _group = "seg" + Interlocked.Increment(ref _groupCounter);
    private bool _building;

    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items), typeof(string), typeof(Segmented), new PropertyMetadata("", (d, _) => ((Segmented)d).Build()));
    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(
        nameof(SelectedIndex), typeof(int), typeof(Segmented),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((Segmented)d).Sync()));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(Segmented), new PropertyMetadata("", (d, _) => AutomationProperties.SetName(d, (string)_.NewValue)));

    /// <summary>"A|B|C" labels.</summary>
    public string Items { get => (string)GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public int SelectedIndex { get => (int)GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, value); }
    /// <summary>Screen-reader name of the whole picker ("Scan depth").</summary>
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    public string SelectedText => (Items ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(SelectedIndex) ?? "";

    public event EventHandler? SelectionChanged;

    public Segmented()
    {
        CornerRadius = new CornerRadius(6);
        BorderThickness = new Thickness(1);
        Padding = new Thickness(2);
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;
        SetResourceReference(BackgroundProperty, "ControlFillColorSecondaryBrush");
        SetResourceReference(BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        KeyboardNavigation.SetDirectionalNavigation(_panel, KeyboardNavigationMode.Cycle);
        Child = _panel;
        Build();
    }

    private void Build()
    {
        _building = true;
        _panel.Children.Clear();
        var parts = (Items ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            int idx = i;
            var rb = new RadioButton { Content = parts[i], GroupName = _group, IsChecked = i == SelectedIndex };
            rb.SetResourceReference(StyleProperty, "TidySegmentButton");
            AutomationProperties.SetName(rb, parts[i]);
            rb.Checked += (_, _) =>
            {
                if (_building) return;
                SelectedIndex = idx;
            };
            _panel.Children.Add(rb);
        }
        _building = false;
    }

    private void Sync()
    {
        _building = true;
        for (int i = 0; i < _panel.Children.Count; i++)
            if (_panel.Children[i] is ToggleButton tb) tb.IsChecked = i == SelectedIndex;
        _building = false;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }
}
