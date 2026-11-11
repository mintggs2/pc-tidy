using System.Globalization;
using System.Windows;
using System.Windows.Data;
using PcTidy.Features.Measure.Import;
using PcTidy.Features.Measure.Storage;

namespace PcTidy.Features.Measure.UI;

/// <summary>
/// Measure page: 1 Record (help and links), 2 Import (drop or pick files), 3 Compare (verdict, Before / After columns,
/// conditions, "What this does not prove"). Reads only the files the user gives it; never runs a recorder and never undoes
/// anything (Keep / Undo point to History).
/// <para>Shell API: set <see cref="LinkedRunId"/> (or call <see cref="MeasureModule.LinkToRun"/> before navigating to
/// "measure") so the comparison is saved with that History RunId.</para>
/// </summary>
public partial class MeasurePage : System.Windows.Controls.UserControl, PcTidy.UI.Flow.INavigationTarget
{
    /// <summary>The shell's "Check if this helped" passes a <see cref="PcTidy.UI.Flow.RunCheckRequest"/>; the rail passes null.</summary>
    public void OnNavigatedTo(object? parameter)
    {
        if (parameter is PcTidy.UI.Flow.RunCheckRequest request) LinkedRunId = request.RunId;
    }

    public MeasurePage(IServiceProvider services, ComparisonStore store)
    {
        InitializeComponent();
        ViewModel = new MeasureViewModel(services, store);
        DataContext = ViewModel;
    }

    public MeasureViewModel ViewModel { get; }

    /// <summary>History run this comparison checks ("Check if this helped"). Null = not linked.</summary>
    public string? LinkedRunId
    {
        get => ViewModel.LinkedRunId;
        set
        {
            // A new run while a saved comparison for another run is open starts a new comparison (never overwrites that file).
            if (string.IsNullOrWhiteSpace(value)) ViewModel.LinkedRunId = null;
            else ViewModel.CheckRun(value);
        }
    }

    private void OnPickFiles(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Pick frame-time recordings (PC Tidy only reads them)",
            Filter = "Recordings (*.csv;*.json;*.txt)|*.csv;*.json;*.txt",
            CheckFileExists = true,
            Multiselect = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) _ = ViewModel.ImportFromPageAsync(dialog.FileNames); // observed: problems show in the import status
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files && files.Any(RecordingImporter.HasKnownExtension)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) _ = ViewModel.ImportFromPageAsync(files); // observed: problems show in the import status
    }
}

/// <summary>true → false (enables the Pick button while nothing is being read).</summary>
public sealed class InverseBool : IValueConverter
{
    public static readonly InverseBool Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
