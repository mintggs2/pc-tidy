using PcTidy.Core.Model;

namespace PcTidy.Catalog.Tweaks;

/// <summary>
/// Every tweak from the research (Tweaks.md verdict table + Community tweaks §1 fixes and §2 additions).
/// Proven/Situational rows can be applied; Placebo rows are shown greyed with the reason; Harmful rows are
/// data only (the bad-tweak finder uses them to explain what it finds) and are never shown as tweaks.
/// Exact values were cross-checked against the appendix tags; values marked "(unverified)" in the research
/// are only used where a wrong guess is harmless (the value is read back after writing and undo restores it).
/// </summary>
public static partial class TweakCatalog
{
    // Abbreviations from Tweaks.md.
    internal const string Adv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    internal const string Cdm = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    internal const string Gcs = @"System\GameConfigStore";
    internal const string Mm = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
    internal const string Tcpip = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters";

    private static readonly Lazy<IReadOnlyList<TweakDefinition>> _all = new(() =>
        Gaming().Concat(Privacy()).Concat(Debloat()).Concat(UiExplorer()).Concat(Network()).Concat(Power()).Concat(Shelf())
            .Select(WithMeasured).ToList());

    /// <summary>All tweaks, in page order (group, then table order).</summary>
    public static IReadOnlyList<TweakDefinition> All => _all.Value;

    public static TweakDefinition? Find(string id) => All.FirstOrDefault(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Tweaks shown on the page (everything except harmful).</summary>
    public static IEnumerable<TweakDefinition> Shown => All.Where(t => t.Verdict != TweakVerdict.Harmful);

    // ------------------------------------------------------------------ helpers

    internal static RegistryValueChange Cu(string key, string name, int data, int? windowsDefault)
        => new(RegistryHive.CurrentUser, key, name, RegistryKind.DWord, Dec(data), windowsDefault is { } d ? Dec(d) : null);

    internal static RegistryValueChange Lm(string key, string name, int data, int? windowsDefault)
        => new(RegistryHive.LocalMachine, key, name, RegistryKind.DWord, Dec(data), windowsDefault is { } d ? Dec(d) : null);

    internal static RegistryValueChange CuText(string key, string name, string data, string? windowsDefault)
        => new(RegistryHive.CurrentUser, key, name, RegistryKind.String, data, windowsDefault);

    private static string Dec(int v) => unchecked((uint)v).ToString(System.Globalization.CultureInfo.InvariantCulture);

    internal static IReadOnlyList<string> Src(params string[] urls) => urls;
    internal static IReadOnlyList<TweakChange> Do(params TweakChange[] changes) => changes;
}
