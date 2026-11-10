using System.Windows;

namespace PcTidy.Features.Duplicates.UI;

/// <summary>Side-by-side compare of two copies (see <see cref="CompareViewModel"/>). Changes nothing on disk.</summary>
public partial class CompareWindow : PcTidy.UI.Controls.TidyWindow
{
    public CompareWindow(CompareViewModel model)
    {
        InitializeComponent();
        Model = model;
        DataContext = model;
    }

    public CompareViewModel Model { get; }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
