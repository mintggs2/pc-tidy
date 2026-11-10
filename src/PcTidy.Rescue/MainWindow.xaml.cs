using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;

namespace PcTidy.Rescue;

/// <summary>The Rescue window. Only wiring and dialogs here; behaviour is in <see cref="RescueViewModel"/>.</summary>
public partial class MainWindow : Window, IRescueDialogs
{
    private readonly RescueViewModel _vm;

    public MainWindow(RescueTarget target)
    {
        InitializeComponent();
        _vm = new RescueViewModel(target, this);
        DataContext = _vm;
        Loaded += async (_, _) => await _vm.LoadAsync();
        Closing += (_, e) =>
        {
            if (_vm.IsBusy && !Confirm("Still working", "Rescue is still putting things back. Close anyway? What's already done stays done, and you can run it again later."))
                e.Cancel = true;
        };
        Closed += (_, _) => _vm.Dispose();
    }

    public bool Confirm(string title, string message)
        => MessageBox.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    public string? PickFolder(string? startIn)
    {
        var dialog = new OpenFolderDialog { Title = "Pick the PC Tidy data folder (it has a \"runs\" folder inside)" };
        if (startIn is not null && Directory.Exists(startIn)) dialog.InitialDirectory = startIn;
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    public void Show(string title, string message, bool isError)
        => MessageBox.Show(this, message, title, MessageBoxButton.OK, isError ? MessageBoxImage.Warning : MessageBoxImage.Information);

    public void OpenFolder(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { RescueLog.Error("Opening " + path, ex); }
    }
}
