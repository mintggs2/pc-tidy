namespace PcTidy.Catalog.Tweaks;

/// <summary>
/// The three presets from Tweaks.md "Recommended presets". A preset only ticks rows; the user still sees the
/// preview and confirms. Membership lives on each <see cref="TweakDefinition"/> (Preset / PresetDefault) so
/// there is one source of truth.
/// </summary>
public static class TweakPresets
{
    public sealed record PresetInfo(TweakPreset Preset, string Id, string Title, string Description, string Excluded);

    /// <summary>Group ids used by the scanner. They contain the names Optimize-all looks for ("safe-gaming" etc.).</summary>
    public static readonly IReadOnlyList<PresetInfo> All = new[]
    {
        new PresetInfo(TweakPreset.SafeGaming, "safe-gaming", "Safe gaming",
            "Game Mode on, background recording off, optimizations for windowed games. Pointer precision is there for you to pick.",
            "Not included: Memory integrity, boot (bcdedit) settings, MSI mode, core parking and services."),
        new PresetInfo(TweakPreset.PrivacyBasic, "privacy-basic", "Privacy basic",
            "Advertising ID, tailored experiences, tips and silent installs, lock-screen ads, optional diagnostic data, feedback prompts, typing personalization. Copilot removal and CEIP are there for you to pick.",
            "Not included: AllowTelemetry=0, the telemetry service, Edge and Office policies."),
        new PresetInfo(TweakPreset.CleanExplorer, "clean-explorer", "Clean Explorer",
            "File extensions, hidden files, open to This PC, End task in the taskbar, no web results in Start. Classic menu, taskbar left, Widgets, search icon and compact view are there for you to pick.",
            "Web results in Start uses a policy, so Windows will say \"Managed by your organization\"."),
    };

    public static PresetInfo Info(TweakPreset preset) => All.First(p => p.Preset == preset);

    public static IEnumerable<TweakDefinition> Members(TweakPreset preset) => TweakCatalog.Shown.Where(t => t.Preset == preset && t.IsOffered);

    /// <summary>Rows the preset ticks: proven, in the preset, not opt-in, not expert.</summary>
    public static IEnumerable<TweakDefinition> DefaultTicks(TweakPreset preset)
        => Members(preset).Where(t => t.PresetDefault && t.Verdict == TweakVerdict.Proven && !t.IsExpertOnly && t.HasActions);
}
