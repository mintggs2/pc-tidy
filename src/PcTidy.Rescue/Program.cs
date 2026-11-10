using System.Runtime.InteropServices;
using System.Windows;
using PcTidy.Core;
using PcTidy.Hosting;

namespace PcTidy.Rescue;

/// <summary>
/// Entry point. Three modes: the elevated helper (same switch as the app, for undoing admin changes),
/// command line (<c>--list</c>, <c>--undo</c>...; see <see cref="RescueOptions"/>), or the window.
/// Everything is caught and logged: Rescue must never crash on the user.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            // Undoing an admin change relaunches this exe elevated with --elevated (see PcTidy.Platform HelperLauncher).
            if (PcTidyHost.TryRunElevatedHelper(args) is int helperExit) return helperExit;
        }
        catch (Exception ex)
        {
            RescueLog.Use(AppSettings.ResolveDataRoot());
            RescueLog.Error("Elevated helper", ex);
            return 1;
        }

        var options = RescueOptions.Parse(args);
        return options.IsCommandLine ? RunCommandLine(options) : RunWindow(options);
    }

    private static int RunCommandLine(RescueOptions options)
    {
        Native.AttachToParentConsole();
        try
        {
            Console.WriteLine();
            var cli = new RescueCli(Console.Out, AskYesNo);
            return cli.RunAsync(options).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            RescueLog.Error("Command line", ex);
            try { Console.WriteLine("Something went wrong: " + ex.Message); } catch { /* no console */ }
            return RescueCli.ExitCodes.Failed;
        }
    }

    /// <summary>
    /// A windowed exe can't reliably read the keyboard of the console it was started from (the shell reads it too),
    /// so typed answers come from redirected input (scripts: <c>echo y | "PC Tidy Rescue.exe" --undo-last</c>) and
    /// otherwise the question is asked in a small Yes/No box.
    /// </summary>
    private static bool AskYesNo(string question)
    {
        if (Console.IsInputRedirected)
        {
            var answer = Console.In.ReadLine()?.Trim();
            return answer is not null && (answer.Equals("y", StringComparison.OrdinalIgnoreCase) || answer.Equals("yes", StringComparison.OrdinalIgnoreCase));
        }
        return Native.AskYesNo("PC Tidy Rescue", question);
    }

    private static int RunWindow(RescueOptions options)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            RescueLog.Error("Unhandled", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            RescueLog.Error("Unobserved task", e.Exception);
            e.SetObserved();
        };

        RescueTarget target;
        try { target = RescueTarget.Resolve(options); }
        catch (Exception ex)
        {
            RescueLog.Error("Finding the data folder", ex);
            target = RescueTarget.ForDataFolder(AppSettings.DefaultDataRoot, "PC Tidy's usual data folder");
        }
        RescueLog.Use(target.DataRoot);

        try
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.DispatcherUnhandledException += (_, e) =>
            {
                RescueLog.Error("UI", e.Exception);
                e.Handled = true; // keep the window alive; the action reports its own failure
                MessageBox.Show("Something went wrong: " + e.Exception.Message + "\n\nDetails are in the log (Open log folder).",
                    "PC Tidy Rescue", MessageBoxButton.OK, MessageBoxImage.Warning);
            };
            return app.Run(new MainWindow(target));
        }
        catch (Exception ex)
        {
            RescueLog.Error("Starting the window", ex);
            Native.ShowError("PC Tidy Rescue couldn't open its window: " + ex.Message +
                             "\n\nYou can still use the command line, for example:\n\"PC Tidy Rescue.exe\" --list");
            return 1;
        }
    }

    private static class Native
    {
        private const int AttachParentProcess = -1;
        private const uint MbYesNo = 0x4, MbIconQuestion = 0x20, MbIconWarning = 0x30, MbDefButton2 = 0x100;
        private const int IdYes = 6;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

        public static void AttachToParentConsole()
        {
            try { AttachConsole(AttachParentProcess); } catch { /* started without a console: output is just lost */ }
        }

        public static bool AskYesNo(string caption, string text) => MessageBoxW(IntPtr.Zero, text, caption, MbYesNo | MbIconQuestion | MbDefButton2) == IdYes;

        public static void ShowError(string text)
        {
            try { MessageBoxW(IntPtr.Zero, text, "PC Tidy Rescue", MbIconWarning); } catch { /* nothing else we can do */ }
        }
    }
}
