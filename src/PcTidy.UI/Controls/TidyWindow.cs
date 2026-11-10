using System.Windows;
using PcTidy.UI.Theming;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace PcTidy.UI.Controls;

/// <summary>
/// Base for PC Tidy's pop-up windows (Sort one by one, Rule presets, Compare copies...): a WPF-UI
/// <see cref="FluentWindow"/> like the main window and "What is this?", so they share its look: Mica behind the
/// content (a solid theme background in screenshot mode and Windows high contrast), content drawn under a
/// <see cref="TitleBar"/> that the window puts in its first row, rounded corners, the TidyText font, and a frame
/// that follows the app's dark/light theme while it's open.
/// </summary>
/// <remarks>
/// The title bar's icon should use <c>{DynamicResource TidyLogo}</c> (not StaticResource), so the window still
/// builds where the app's resources aren't loaded (UI tests).
/// </remarks>
public class TidyWindow : FluentWindow
{
    public TidyWindow()
    {
        ExtendsContentIntoTitleBar = true;
        WindowCornerPreference = WindowCornerPreference.Round;
        WindowBackdropType = Solid ? WindowBackdropType.None : WindowBackdropType.Mica;
        // Mica replaces this background once the window has a handle; with no backdrop it is the window's colour.
        SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
        SetResourceReference(FontFamilyProperty, "TidyText");
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Closed += (_, _) => ApplicationThemeManager.Changed -= OnThemeChanged;
    }

    /// <summary>Mica can't be captured in screenshots and isn't used in high contrast.</summary>
    private static bool Solid => TidyTheme.SolidBackdrop || SystemParameters.HighContrast;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Only the main window is updated by the theme manager: give this one the current theme, and keep it in step.
        WindowBackgroundManager.UpdateBackground(this, ApplicationThemeManager.GetAppTheme(), WindowBackdropType);
        ApplicationThemeManager.Changed += OnThemeChanged;
    }

    private void OnThemeChanged(ApplicationTheme theme, System.Windows.Media.Color accent)
        => Dispatcher.BeginInvoke(() => WindowBackgroundManager.UpdateBackground(this, theme, WindowBackdropType));
}
