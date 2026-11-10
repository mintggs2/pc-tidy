using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using PcTidy.Core;

namespace PcTidy.App.Shell;

/// <summary>
/// Global crash handler: UI thread, other threads and unobserved tasks. Writes
/// &lt;data folder&gt;\logs\crash-*.log (local only, never sent anywhere) and shows a friendly message.
/// </summary>
public static class CrashHandler
{
    private static int _dialogShown;

    /// <summary>Set as soon as the data folder is known; until then the default data folder is used.</summary>
    public static string DataRoot { get; set; } = AppSettings.ResolveDataRoot();

    public static void Install(Application app)
    {
        app.DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write(e.ExceptionObject as Exception, "AppDomain", e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write(e.Exception, "Task", terminating: false);
            e.SetObserved(); // a forgotten background task must not take the app down
        };
    }

    private static void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var log = Write(e.Exception, "UI", terminating: false);
        e.Handled = true; // keep running: every change is journaled, so the safest thing is to stay up
        if (Interlocked.Exchange(ref _dialogShown, 1) == 1) return;
        try
        {
            MessageBox.Show(
                "Something went wrong inside PC Tidy, sorry about that.\n\n" +
                "Nothing on your PC was changed by this error. Any run that was in progress is saved in History, " +
                "where you can resume it or undo it.\n\n" +
                (log != null ? $"Details were saved on this PC only:\n{log}" : "The details couldn't be saved."),
                "PC Tidy", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch { /* the UI itself may be broken */ }
        finally { Interlocked.Exchange(ref _dialogShown, 0); }
    }

    /// <summary>Writes one crash log. Returns its path, or null if even that failed.</summary>
    public static string? Write(Exception? ex, string source, bool terminating)
    {
        try
        {
            var dir = DataPaths.Logs(DataRoot);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
            var sb = new StringBuilder()
                .AppendLine($"PC Tidy {typeof(CrashHandler).Assembly.GetName().Version} crash log")
                .AppendLine($"Time: {DateTime.Now:O}")
                .AppendLine($"Source: {source}{(terminating ? " (terminating)" : "")}")
                .AppendLine($"OS: {Environment.OSVersion}  64-bit: {Environment.Is64BitOperatingSystem}  .NET: {Environment.Version}")
                .AppendLine()
                .AppendLine(ex?.ToString() ?? "(no exception object)");
            File.AppendAllText(path, sb.ToString());
            return path;
        }
        catch { return null; }
    }
}
