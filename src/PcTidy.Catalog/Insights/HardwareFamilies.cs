using System.Globalization;
using System.Text.RegularExpressions;
using PcTidy.Catalog.Tweaks;

namespace PcTidy.Catalog.Insights;

/// <summary>Processor families the Insights cards adapt to.</summary>
public enum CpuFamily { Unknown, Intel, AmdZen4, AmdZen5, AmdOther, Other }

/// <summary>
/// Sorts processor and graphics names (the maker's own brand strings, the same in every Windows language) into the
/// families the measured cards talk about: AMD Zen 4/5 (Windows 11 24H2 gain), X3D (memory and core-parking cards),
/// dual-CCD X3D (C6) and Intel Arc discrete cards (Resizable BAR).
/// </summary>
public static partial class HardwareFamilies
{
    /// <summary>
    /// Ryzen 9 X3D parts have two core groups (CCDs) and only one carries the extra cache: 7900X3D, 7950X3D, 9900X3D,
    /// 9950X3D, and the laptop 7945HX3D and 9955HX3D. The desktop rule is shared with Tweaks (<see cref="CpuKinds"/>).
    /// </summary>
    public static bool IsDualCcdX3D(string? cpu)
        => !string.IsNullOrWhiteSpace(cpu) && (CpuKinds.Classify(cpu) == CpuKind.DualCcdX3D || LaptopDualCcdX3D().IsMatch(cpu));

    /// <summary>Any Ryzen with 3D V-Cache (5800X3D, 7800X3D, 9800X3D, the Ryzen 9 X3D parts...).</summary>
    public static bool IsX3D(string? cpu) => !string.IsNullOrWhiteSpace(cpu) && X3D().IsMatch(cpu);

    public static CpuFamily Classify(string? cpu)
    {
        if (string.IsNullOrWhiteSpace(cpu)) return CpuFamily.Unknown;
        if (cpu.Contains("Intel", StringComparison.OrdinalIgnoreCase) || cpu.Contains("Core(TM)", StringComparison.OrdinalIgnoreCase)) return CpuFamily.Intel;
        if (!cpu.Contains("Ryzen", StringComparison.OrdinalIgnoreCase))
            return cpu.Contains("AMD", StringComparison.OrdinalIgnoreCase) ? CpuFamily.AmdOther : CpuFamily.Other;
        // Ryzen AI 300 and later laptop chips are Zen 5.
        if (RyzenAi().IsMatch(cpu)) return CpuFamily.AmdZen5;
        var m = RyzenModel().Match(cpu);
        if (!m.Success) return CpuFamily.AmdOther;
        var digits = m.Groups["model"].Value;
        var suffix = m.Groups["suffix"].Value.ToUpperInvariant();
        int series = digits[0] - '0';
        bool laptop = suffix.StartsWith('U') || suffix.StartsWith('H');
        if (series == 9) return CpuFamily.AmdZen5;                    // 9000 desktop, 9955HX laptop, Threadripper 9000
        if (series is 7 or 8)
        {
            // Desktop 7000/8000 (7600X, 7800X3D, 8700G) and Threadripper 7000 are Zen 4. Laptop 7000/8000 parts carry the
            // design in the third digit: 7840HS, 7945HX, 8840U are Zen 4 ("4"); 7520U, 7735HS are older ("2", "3").
            if (!laptop) return CpuFamily.AmdZen4;
            return digits.Length >= 3 && digits[2] == '4' ? CpuFamily.AmdZen4 : CpuFamily.AmdOther;
        }
        return CpuFamily.AmdOther;
    }

    /// <summary>"AMD Zen 4", "Intel" (for the Windows build card's facts).</summary>
    public static string Describe(CpuFamily f) => f switch
    {
        CpuFamily.AmdZen4 => "AMD Zen 4",
        CpuFamily.AmdZen5 => "AMD Zen 5",
        CpuFamily.AmdOther => "AMD (older than Zen 4)",
        CpuFamily.Intel => "Intel",
        CpuFamily.Other => "Other",
        _ => "Unknown",
    };

    /// <summary>
    /// An Intel Arc graphics CARD (A310-A770, B570/B580, Pro A/B series): these lose about a quarter of their speed without
    /// Resizable BAR. Integrated "Intel Arc Graphics" / "Arc 140V" in Core Ultra laptops are not cards and don't count.
    /// </summary>
    public static bool IsDiscreteArc(string? gpu) => !string.IsNullOrWhiteSpace(gpu) && DiscreteArc().IsMatch(gpu);

    /// <summary>The Windows feature update a build number belongs to ("Windows 11 24H2").</summary>
    public static string WindowsVersion(int build) => build switch
    {
        <= 0 => "Windows (build unknown)",
        < 22000 => "Windows 10",
        < 22621 => "Windows 11 21H2",
        < 22631 => "Windows 11 22H2",
        < 26100 => "Windows 11 23H2",
        < 26200 => "Windows 11 24H2",
        26200 => "Windows 11 25H2",
        _ => "Windows 11 (newer than 25H2)",
    };

    /// <summary>First build of Windows 11 24H2, the update measured faster on Zen 4/5.</summary>
    public const int Build24H2 = 26100;

    public static string BuildText(int build, int ubr) => ubr > 0
        ? $"{WindowsVersion(build)} (build {build.ToString(CultureInfo.InvariantCulture)}.{ubr.ToString(CultureInfo.InvariantCulture)})"
        : $"{WindowsVersion(build)} (build {build.ToString(CultureInfo.InvariantCulture)})";

    [GeneratedRegex(@"\bX3D\b|\d{4}X3D|HX3D", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex X3D();
    [GeneratedRegex(@"Ryzen\s+9\s+(PRO\s+)?\d{4}HX3D", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex LaptopDualCcdX3D();
    [GeneratedRegex(@"Ryzen\s+AI\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex RyzenAi();
    [GeneratedRegex(@"Ryzen\s+(?:Threadripper\s+(?:PRO\s+)?|\d\s+(?:PRO\s+)?)(?<model>\d{4,5})(?<suffix>[A-Za-z][A-Za-z0-9]*)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex RyzenModel();
    [GeneratedRegex(@"\bArc(\(TM\)|™)?\s+(Pro\s+)?[AB]\d{2,3}[A-Z]?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex DiscreteArc();
}
