using System.Runtime.InteropServices;

namespace PcTidy.Platform;

/// <summary>The visual-effect switches Windows' Performance Options dialog flips, read and written with SystemParametersInfo.</summary>
public interface IVisualEffectsApi
{
    bool TouchesRealSystem { get; }
    IReadOnlyDictionary<string, bool> Read();
    /// <summary>Applies these states with SPIF_UPDATEINIFILE | SPIF_SENDCHANGE (Windows also updates UserPreferencesMask).</summary>
    void Apply(IReadOnlyDictionary<string, bool> states);
}

/// <summary>
/// Writing <c>HKCU\...\Explorer\VisualEffects\VisualFXSetting</c> alone changes nothing until the next sign-in, and
/// UserPreferencesMask would stay stale. So when the registry handler changes that value it also applies the matching
/// effect switches through SystemParametersInfo (which keeps UserPreferencesMask in step), and undo puts back the exact
/// switch states read before.
/// </summary>
public static class VisualEffects
{
    public const string Key = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects";
    public const string Value = "VisualFXSetting";

    /// <summary>Effect name → (get, set, set-uses-uiParam). Min/max animation uses ANIMATIONINFO and is handled separately.</summary>
    internal static readonly IReadOnlyDictionary<string, (uint Get, uint Set, bool UiParam)> Switches = new Dictionary<string, (uint, uint, bool)>
    {
        ["ClientAreaAnimation"] = (0x1042, 0x1043, false),
        ["DropShadow"] = (0x1024, 0x1025, false),
        ["MenuAnimation"] = (0x1002, 0x1003, false),
        ["MenuFade"] = (0x1012, 0x1013, false),
        ["ComboBoxAnimation"] = (0x1004, 0x1005, false),
        ["ListBoxSmoothScrolling"] = (0x1006, 0x1007, false),
        ["TooltipAnimation"] = (0x1016, 0x1017, false),
        ["SelectionFade"] = (0x1014, 0x1015, false),
        ["CursorShadow"] = (0x101A, 0x101B, false),
        ["DragFullWindows"] = (0x0026, 0x0025, true),
    };

    public const string MinMaxAnimation = "MinMaxAnimation";
    /// <summary>Master switch: only ever turned ON here (turning it off would override every other choice).</summary>
    public const string UiEffects = "UIEffects";

    /// <summary>
    /// The switch states for a VisualFXSetting value: 2 = "best performance" (effects off), 0 / 1 = "let Windows choose" /
    /// "best appearance" (effects on). 3 = custom, or anything else: null (nothing is applied).
    /// </summary>
    public static IReadOnlyDictionary<string, bool>? TargetFor(string? data)
    {
        var on = data?.Trim() switch { "2" => false, "0" or "1" => true, _ => (bool?)null };
        if (on is null) return null;
        var target = Switches.Keys.ToDictionary(k => k, _ => on.Value);
        target[MinMaxAnimation] = on.Value;
        if (on.Value) target[UiEffects] = true;
        return target;
    }

    public static bool IsVisualFxValue(Core.Model.RegistryValueSpec spec, string key)
        => spec.Hive == Core.Model.RegistryHive.CurrentUser && string.Equals(spec.KeyPath.Trim('\\'), key, StringComparison.OrdinalIgnoreCase)
           && string.Equals(spec.ValueName, Value, StringComparison.OrdinalIgnoreCase);
}

public sealed class WindowsVisualEffectsApi : IVisualEffectsApi
{
    private const uint SpiGetAnimation = 0x0048;
    private const uint SpiSetAnimation = 0x0049;
    private const uint SpiGetUiEffects = 0x103E;
    private const uint SpiSetUiEffects = 0x103F;
    private const uint UpdateAndBroadcast = 0x1 | 0x2; // SPIF_UPDATEINIFILE | SPIF_SENDCHANGE

    public bool TouchesRealSystem => true;

    public IReadOnlyDictionary<string, bool> Read()
    {
        var states = new Dictionary<string, bool>();
        foreach (var (name, (get, _, _)) in VisualEffects.Switches)
            if (SystemParametersInfoW(get, 0, out int v, 0)) states[name] = v != 0;
        var anim = new ANIMATIONINFO { cbSize = (uint)Marshal.SizeOf<ANIMATIONINFO>() };
        if (SystemParametersInfoW(SpiGetAnimation, anim.cbSize, ref anim, 0)) states[VisualEffects.MinMaxAnimation] = anim.iMinAnimate != 0;
        if (SystemParametersInfoW(SpiGetUiEffects, 0, out int ui, 0)) states[VisualEffects.UiEffects] = ui != 0;
        return states;
    }

    public void Apply(IReadOnlyDictionary<string, bool> states)
    {
        foreach (var (name, on) in states)
        {
            if (VisualEffects.Switches.TryGetValue(name, out var s))
            {
                if (s.UiParam) SystemParametersInfoW(s.Set, on ? 1u : 0u, IntPtr.Zero, UpdateAndBroadcast);
                else SystemParametersInfoW(s.Set, 0, new IntPtr(on ? 1 : 0), UpdateAndBroadcast);
            }
            else if (name == VisualEffects.MinMaxAnimation)
            {
                var anim = new ANIMATIONINFO { cbSize = (uint)Marshal.SizeOf<ANIMATIONINFO>(), iMinAnimate = on ? 1 : 0 };
                SystemParametersInfoW(SpiSetAnimation, anim.cbSize, ref anim, UpdateAndBroadcast);
            }
            else if (name == VisualEffects.UiEffects)
            {
                SystemParametersInfoW(SpiSetUiEffects, 0, new IntPtr(on ? 1 : 0), UpdateAndBroadcast);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ANIMATIONINFO
    {
        public uint cbSize;
        public int iMinAnimate;
    }

    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint action, uint uiParam, out int pvParam, uint winIni);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint action, uint uiParam, IntPtr pvParam, uint winIni);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint action, uint uiParam, ref ANIMATIONINFO pvParam, uint winIni);
}
