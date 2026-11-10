using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace PcTidy.UI.Theming;

/// <summary>Which theme the user asked for in Settings.</summary>
public enum ThemePreference { System, Dark, Light }

/// <summary>
/// Theme + accent management for the whole app: WPF-UI theme, PC Tidy's green accent,
/// and PC Tidy's own theme-specific brushes (TidyDark.xaml / TidyLight.xaml, or a high-contrast set built
/// from Windows' SystemColors). Windows high contrast always wins over the Dark/Light choice.
/// </summary>
public static class TidyTheme
{
    private const string DarkUri = "pack://application:,,,/PcTidy.UI;component/Themes/TidyDark.xaml";
    private const string LightUri = "pack://application:,,,/PcTidy.UI;component/Themes/TidyLight.xaml";
    /// <summary>Key present in every PC Tidy brush dictionary, so the swap finds it however it was loaded.</summary>
    private const string MarkerKey = "TidyThemeDictionary";

    /// <summary>The single PC Tidy accent.</summary>
    public static readonly Color Accent = Color.FromRgb(0x2B, 0xB6, 0x73);

    private static Window? _watchedWindow;
    private static bool _applying;
    private static bool _hooked;

    /// <summary>When true (screenshots), Mica is replaced by a solid background because it can't be captured.</summary>
    public static bool SolidBackdrop { get; set; }

    public static ApplicationTheme Current { get; private set; } = ApplicationTheme.Dark;
    public static ThemePreference Preference { get; private set; } = ThemePreference.System;

    public static event Action? ThemeChanged;

    public static ThemePreference Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "dark" => ThemePreference.Dark,
        "light" => ThemePreference.Light,
        _ => ThemePreference.System,
    };

    /// <summary>
    /// Applies the user's preference. "System" follows Windows live (SystemThemeWatcher);
    /// Dark/Light stop following. Windows high contrast overrides both (accessibility first).
    /// </summary>
    public static void ApplyPreference(ThemePreference preference, Window? window)
    {
        Preference = preference;
        HookThemeChanges();
        if (window != null)
        {
            if (preference == ThemePreference.System)
            {
                if (_watchedWindow != window)
                {
                    SystemThemeWatcher.Watch(window, Backdrop, updateAccents: false);
                    _watchedWindow = window;
                }
            }
            else if (_watchedWindow != null)
            {
                SystemThemeWatcher.UnWatch(_watchedWindow);
                _watchedWindow = null;
            }
        }
        Apply(SystemParameters.HighContrast ? ApplicationTheme.HighContrast : preference switch
        {
            ThemePreference.Dark => ApplicationTheme.Dark,
            ThemePreference.Light => ApplicationTheme.Light,
            _ => SystemAppTheme(),
        });
    }

    /// <summary>Applies a concrete theme now (also used by screenshot mode).</summary>
    public static void Apply(ApplicationTheme theme)
    {
        if (Application.Current == null) return;
        _applying = true;
        try
        {
            Current = theme;
            // GOTCHA 1: updateAccent:true re-applies the Windows accent and wipes ours.
            // GOTCHA 2: some theme brushes resolve accent colours statically when the theme
            // dictionary loads, so the accent must be set BEFORE and AFTER the switch.
            // High contrast keeps Windows' own colours: no PC Tidy accent at all.
            if (theme != ApplicationTheme.HighContrast) ApplyAccent(theme);
            ApplicationThemeManager.Apply(theme, Backdrop, updateAccent: theme == ApplicationTheme.HighContrast);
            if (theme != ApplicationTheme.HighContrast) ApplyAccent(theme);
            SwapTidyBrushes(theme);
        }
        finally { _applying = false; }
        ThemeChanged?.Invoke();
    }

    private static WindowBackdropType Backdrop => SolidBackdrop || SystemParameters.HighContrast ? WindowBackdropType.None : WindowBackdropType.Mica;

    private static ApplicationTheme SystemAppTheme()
    {
        if (SystemParameters.HighContrast || ApplicationThemeManager.IsSystemHighContrast()) return ApplicationTheme.HighContrast;
        return ApplicationThemeManager.GetSystemTheme() switch
        {
            SystemTheme.Dark or SystemTheme.Glow or SystemTheme.CapturedMotion or SystemTheme.HCBlack or SystemTheme.HC1 or SystemTheme.HC2 => ApplicationTheme.Dark,
            _ => ApplicationTheme.Light,
        };
    }

    /// <summary>
    /// SystemThemeWatcher applies Windows' theme itself; re-apply ours on top so accent + brushes stay right.
    /// High contrast switched on or off in Windows re-applies the preference whatever it is.
    /// </summary>
    private static void HookThemeChanges()
    {
        if (_hooked) return;
        _hooked = true;
        ApplicationThemeManager.Changed += (theme, _) =>
        {
            if (_applying || Preference != ThemePreference.System) return;
            Application.Current?.Dispatcher.BeginInvoke(() => Apply(SystemParameters.HighContrast ? ApplicationTheme.HighContrast : theme));
        };
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(SystemParameters.HighContrast)) return;
            Application.Current?.Dispatcher.BeginInvoke(() => ApplyPreference(Preference, _watchedWindow));
        };
    }

    private static void SwapTidyBrushes(ApplicationTheme theme)
    {
        var merged = Application.Current.Resources.MergedDictionaries;
        var replacement = theme switch
        {
            ApplicationTheme.HighContrast => HighContrastBrushes(),
            ApplicationTheme.Light => Load(LightUri),
            _ => Load(DarkUri),
        };
        for (int i = 0; i < merged.Count; i++)
        {
            var src = merged[i].Source?.OriginalString ?? "";
            if (merged[i].Contains(MarkerKey)
                || src.EndsWith("TidyDark.xaml", StringComparison.OrdinalIgnoreCase) || src.EndsWith("TidyLight.xaml", StringComparison.OrdinalIgnoreCase))
            {
                merged[i] = replacement;
                return;
            }
        }
        merged.Add(replacement);
    }

    private static ResourceDictionary Load(string uri)
    {
        var d = new ResourceDictionary { Source = new Uri(uri) };
        d[MarkerKey] = uri;
        return d;
    }

    /// <summary>
    /// PC Tidy's brushes for Windows high contrast, built from the user's own SystemColors (Night sky, Desert,
    /// Aquatic, Dusk or a custom set): no tints, no transparency tricks, every colour one the user chose.
    /// </summary>
    public static ResourceDictionary HighContrastBrushes()
    {
        var d = new ResourceDictionary { [MarkerKey] = "high-contrast" };
        Color window = SystemColors.WindowColor, text = SystemColors.WindowTextColor, link = SystemColors.HotTrackColor,
              highlight = SystemColors.HighlightColor, gray = SystemColors.GrayTextColor;
        void Put(string key, Color c) => d[key] = Frozen(new SolidColorBrush(c));
        Put("TidyAccentBrush", link);
        Put("TidyAccentSoftBrush", window);
        Put("TidyAccentStrokeBrush", text);
        Put("TidyRingTrackBrush", gray);
        Put("TidySecondDriveBrush", highlight);
        Put("TidyDriveLowBrush", text);
        Put("TidyHeroBrush", window);
        Put("TidyStickyBarBrush", window);
        Put("TidyOverlayBrush", window);
        Put("TidyScrimBrush", Color.FromArgb(0xE6, window.R, window.G, window.B));
        Put("TidyDisabledRowBrush", window);
        Put("TidyItemRowBrush", window);
        Put("TidyRowSelectedBrush", Color.FromArgb(0x59, highlight.R, highlight.G, highlight.B));
        Put("TidyBreakdown1Brush", highlight);
        Put("TidyBreakdown2Brush", link);
        Put("TidyBreakdown3Brush", gray);
        Put("TidyBreakdown4Brush", text);
        Put("TidyProtectedFillBrush", gray);
        Put("TidyHatchLineBrush", text);
        Put("TidyTreemapTextBrush", text);
        Put("TidyTreemapGapBrush", window);
        return d;
    }

    private static Brush Frozen(Brush b)
    {
        b.Freeze();
        return b;
    }

    private static void ApplyAccent(ApplicationTheme theme)
    {
        if (theme == ApplicationTheme.Light)
        {
            // Light theme: darker variants (white text on accent buttons).
            ApplicationAccentColorManager.Apply(Accent,
                Color.FromRgb(0x1E, 0x9A, 0x5E), Color.FromRgb(0x17, 0x80, 0x4D), Color.FromRgb(0x10, 0x66, 0x3C));
        }
        else
        {
            // Dark theme: lighter variants (dark text on accent buttons).
            ApplicationAccentColorManager.Apply(Accent,
                Color.FromRgb(0x4F, 0xCB, 0x8E), Color.FromRgb(0x6F, 0xD9, 0xA4), Color.FromRgb(0x94, 0xE6, 0xBD));
        }
    }
}
