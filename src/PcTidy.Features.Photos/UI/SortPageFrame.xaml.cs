using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace PcTidy.Features.Photos.UI;

/// <summary>
/// The shared page frame of the Photos and Desktop tidy pages (DataContext: a <see cref="SortPageViewModel"/>).
/// Pages put their own options (folders, mode…) into <see cref="Options"/>.
/// </summary>
public partial class SortPageFrame : UserControl
{
    public static readonly DependencyProperty OptionsProperty = DependencyProperty.Register(
        nameof(Options), typeof(object), typeof(SortPageFrame), new PropertyMetadata(null));
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(SymbolRegular), typeof(SortPageFrame), new PropertyMetadata(SymbolRegular.Folder24));
    public static readonly DependencyProperty DeepHintProperty = DependencyProperty.Register(
        nameof(DeepHint), typeof(string), typeof(SortPageFrame), new PropertyMetadata(""));

    public SortPageFrame()
    {
        InitializeComponent();
    }

    /// <summary>The page's own options row, shown under the step bar.</summary>
    public object? Options { get => GetValue(OptionsProperty); set => SetValue(OptionsProperty, value); }
    /// <summary>Icon of the "scan to start" card.</summary>
    public SymbolRegular Icon { get => (SymbolRegular)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    /// <summary>What Deep adds on this page (tooltip of the Quick | Deep switch).</summary>
    public string DeepHint { get => (string)GetValue(DeepHintProperty); set => SetValue(DeepHintProperty, value); }

    /// <summary>WPF-UI's InfoBar close button has no name of its own: give it one for screen readers.</summary>
    private void OnNoteLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not DependencyObject bar) return;
        var stack = new Stack<DependencyObject>();
        stack.Push(bar);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (cur is System.Windows.Controls.Primitives.ButtonBase b && string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(b)))
                System.Windows.Automation.AutomationProperties.SetName(b, "Close this note");
            for (int i = System.Windows.Media.VisualTreeHelper.GetChildrenCount(cur) - 1; i >= 0; i--) stack.Push(System.Windows.Media.VisualTreeHelper.GetChild(cur, i));
        }
    }

    private void OnScanNow(object sender, RoutedEventArgs e)
    {
        if (DataContext is SortPageViewModel vm && vm.ScanCommand.CanExecute(null)) vm.ScanCommand.Execute(null);
    }
}
