using System.Runtime.ExceptionServices;
using System.Windows;
using Wpf.Ui.Markup;

namespace PcTidy.Setup.Tests;

/// <summary>
/// Builds the Setup and uninstall windows (XAML, resources, icons) without ever showing them, so a broken page is
/// caught here. The uninstall window gets the test environment; nothing is clicked, nothing is installed.
/// </summary>
[Collection("setup")]
public class WindowSmokeTests
{
    private static void OnSta(Action action)
    {
        ExceptionDispatchInfo? error = null;
        var t = new Thread(() =>
        {
            try
            {
                if (Application.Current is null)
                {
                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    app.Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Light });
                    app.Resources.MergedDictionaries.Add(new ControlsDictionary());
                }
                action();
            }
            catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        error?.Throw();
    }

    [Fact]
    public void Setup_and_uninstall_windows_load()
    {
        using var bed = new SetupBed("windows");
        var (_, payload, _) = bed.MakeSetupExe("1.2.3");
        OnSta(() =>
        {
            var install = SetupWindow.ForInstall(payload, "1.2.3");
            Assert.Equal("PC Tidy 1.2.3 Setup", install.Bar.Title);
            Assert.Equal(Visibility.Visible, install.WelcomePanel.Visibility);
            Assert.True(install.StartMenuBox.IsChecked);
            Assert.False(install.DesktopBox.IsChecked);
            Assert.True(install.LaunchBox.IsChecked);
            install.Close();

            var uninstall = SetupWindow.ForUninstall(bed.Env);
            Assert.Equal(Visibility.Visible, uninstall.UninstallPanel.Visibility);
            Assert.False(uninstall.RemoveDataBox.IsChecked); // keeping the data is the default
            Assert.Contains(bed.Env.DataRoot, uninstall.DataText.Text);
            uninstall.Close();
        });
    }
}
