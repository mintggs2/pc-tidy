using System.Text.RegularExpressions;
using PcTidy.Core.Model;

namespace PcTidy.Catalog.Games;

/// <summary>
/// Per-app GPU preference for hybrid laptops (comparison §8.1 B7): the value Windows' Settings → System → Display →
/// Graphics writes for one program. HKCU only, no admin. The value name is the program's full exe path and the data
/// is "GpuPreference=N;" (0 = let Windows decide, 1 = power saving, 2 = high performance).
/// VERIFY: key, value-name form and data format are from memory of the Settings → Graphics behaviour and Microsoft's
/// DirectX per-app settings; confirm on a clean hybrid-GPU VM before release.
/// Evidence: SITUATIONAL. No FPS data was found; it only matters when Windows picks the integrated GPU for a game.
/// AllowDelete stays on so undo can remove a value that wasn't there before.
/// </summary>
public sealed class GpuPreferenceAllowances : IRegistryAllowanceSource
{
    public const string Source = "games.gpu-preference";

    /// <summary>HKCU key (verify).</summary>
    public const string Key = @"Software\Microsoft\DirectX\UserGpuPreferences";

    /// <summary>An absolute exe path such as C:\Games\Shooter\Shooter.exe (no wildcards, quotes, pipes or line breaks).</summary>
    public const string ExePathPattern = @"^[A-Za-z]:\\[^<>""|?*\r\n]+\.exe$";

    /// <summary>The only data PC Tidy writes or restores: exactly one GpuPreference token (verify).</summary>
    public const string DataPattern = @"^GpuPreference=[0-2];$";

    /// <summary>High performance: the discrete GPU.</summary>
    public const string HighPerformance = "GpuPreference=2;";

    public IEnumerable<RegistryAllowance> Allowances() => new[]
    {
        new RegistryAllowance(Source, RegistryHive.CurrentUser, Key, null, RegistryKind.String, Array.Empty<string>(),
            AllowDelete: true, NeedsAdmin: false, DataPattern: DataPattern)
        {
            ValueNamePattern = ExePathPattern,
        },
    };

    /// <summary>"GpuPreference=2;" → 2. Null when the value is missing, has other settings in it, or isn't in this form.</summary>
    public static int? ParsePreference(string? data)
        => data is not null && Regex.Match(data, DataPattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) is { Success: true }
            ? data[14] - '0'
            : null;

    public static string Describe(int preference) => preference switch
    {
        1 => "Power saving",
        2 => "High performance",
        _ => "Let Windows decide",
    };
}

/// <summary>What kind of display adapter a GPU name describes.</summary>
public enum GpuKind { Discrete, Integrated, Virtual }

/// <summary>
/// Classifies display adapter names (from the PC's own adapter list) as integrated, discrete or virtual, to decide
/// whether a PC is a hybrid-GPU laptop. Name-based heuristic: unknown names count as discrete, virtual and remote
/// display adapters are ignored.
/// </summary>
public static class GpuKinds
{
    private static readonly string[] VirtualMarkers =
    {
        "Basic Display", "Basic Render", "Remote Display", "Virtual", "DisplayLink", "Parsec", "VMware", "VirtualBox",
        "Hyper-V", "Citrix", "spacedesk", "Indirect Display", "Mirror Driver",
    };

    private static readonly Regex IntelDiscrete = new(@"\bArc\b.*\b[AB]\d{3}\b|Iris.*Xe\s*MAX|\bDG[12]\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AmdIntegrated = new(
        @"^(AMD\s+)?Radeon(\(TM\)|™)?\s+(Graphics|Vega\s*\d*\s*Graphics|\d{3,4}[MS](\s+Graphics)?)\b|Radeon(\(TM\))?\s+Vega\s+\d+\s+Graphics|Radeon\s+R[2-7]\s+Graphics",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static GpuKind Classify(string name)
    {
        var n = name.Trim();
        if (n.Length == 0 || VirtualMarkers.Any(m => n.Contains(m, StringComparison.OrdinalIgnoreCase))) return GpuKind.Virtual;
        if (n.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) return GpuKind.Discrete;
        if (n.Contains("Intel", StringComparison.OrdinalIgnoreCase))
            return IntelDiscrete.IsMatch(n) ? GpuKind.Discrete : GpuKind.Integrated;
        if (n.Contains("Radeon", StringComparison.OrdinalIgnoreCase) || n.StartsWith("AMD", StringComparison.OrdinalIgnoreCase))
        {
            if (Regex.IsMatch(n, @"\bRX\b|\bPro\b|\bFirePro\b|\bInstinct\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return GpuKind.Discrete;
            var plain = n.Replace("AMD ", "", StringComparison.OrdinalIgnoreCase);
            return AmdIntegrated.IsMatch(n) || AmdIntegrated.IsMatch(plain) ? GpuKind.Integrated : GpuKind.Discrete;
        }
        return GpuKind.Discrete;
    }

    /// <summary>
    /// Two or more real GPUs, at least one integrated and at least one discrete: the only case where a per-app
    /// "High performance" choice changes which GPU a game uses.
    /// </summary>
    public static bool IsHybrid(IEnumerable<string>? gpuNames)
    {
        var real = (gpuNames ?? Enumerable.Empty<string>()).Select(Classify).Where(k => k != GpuKind.Virtual).ToList();
        return real.Count >= 2 && real.Contains(GpuKind.Integrated) && real.Contains(GpuKind.Discrete);
    }
}
