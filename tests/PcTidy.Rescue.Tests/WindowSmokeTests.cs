using System.Runtime.ExceptionServices;
using System.Windows;

namespace PcTidy.Rescue.Tests;

/// <summary>Builds the Rescue window (XAML, bindings' targets, icon) without showing it.</summary>
[Collection("rescue")]
public class WindowSmokeTests
{
    [Fact]
    public void Rescue_window_loads()
    {
        var root = Path.Combine(RescueBed.TestbedRoot, "window-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(root);
        try
        {
            ExceptionDispatchInfo? error = null;
            var t = new Thread(() =>
            {
                try
                {
                    if (Application.Current is null)
                    {
                        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    }
                    var w = new MainWindow(RescueTarget.ForSandbox(root));
                    Assert.Equal("PC Tidy Rescue", w.Title);
                    Assert.NotNull(w.Icon);
                    w.Close();
                }
                catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            error?.Throw();
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
