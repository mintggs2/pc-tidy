using System.Text.RegularExpressions;

namespace PcTidy.Catalog.Tweaks;

/// <summary>
/// Sorts a processor brand string (Win32_Processor.Name, or ProcessorNameString in HARDWARE\DESCRIPTION) into the CPU
/// families some advice depends on. The brand string is the maker's own text, the same in every Windows language.
/// Shared by Tweaks, Speed and Game boost so they all agree on what an X3D or hybrid CPU is.
/// </summary>
public static partial class CpuKinds
{
    /// <summary>Ryzen 9 "X3D" parts (7900X3D, 7950X3D, 9900X3D, 9950X3D) have two CCDs and only one carries the extra cache.</summary>
    public static CpuKind Classify(string? brandString)
    {
        if (string.IsNullOrWhiteSpace(brandString)) return CpuKind.None;
        if (DualCcdX3D().IsMatch(brandString)) return CpuKind.DualCcdX3D;
        if (IntelHybrid().IsMatch(brandString)) return CpuKind.Hybrid;
        return CpuKind.None;
    }

    /// <summary>
    /// Why a dual-CCD X3D CPU should stay on Balanced (shown by Speed, Game boost and the bad-tweak finder).
    /// AMD's driver parks the cores without the extra cache while a game runs; that needs Balanced, Game Mode,
    /// the Xbox Game Bar and AMD's chipset driver.
    /// </summary>
    public const string DualCcdX3DAdvice =
        "On this Ryzen X3D processor, keep the Balanced power plan, Game Mode, the Xbox Game Bar and AMD's chipset driver. "
        + "Together they park the cores without the extra cache while you play, so the game runs on the fast-cache cores. "
        + "\"Maximum performance\" plans keep every core awake and can put the game on the wrong half of the chip.";

    // Desktop 7900X3D/7950X3D/9900X3D/9950X3D and the laptop 7945HX3D/9955HX3D (also two CCDs, one with V-Cache).
    [GeneratedRegex(@"Ryzen\s+9\s+(PRO\s+)?\d{4}H?X3D", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex DualCcdX3D();
    // 12th-14th gen Core i5/i7/i9 (12400 has no E-cores, but treating it as hybrid only hides a placebo row) and Core Ultra
    // (its brand string reads "Intel(R) Core(TM) Ultra 7 155H", so the "(TM)" is optional).
    [GeneratedRegex(@"(Core\(TM\)\s+i[579]-1[2-4]\d{3}|Core(\(TM\))?\s+Ultra)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex IntelHybrid();
}
