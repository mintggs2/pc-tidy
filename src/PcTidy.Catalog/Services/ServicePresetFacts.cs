namespace PcTidy.Catalog.Services;

/// <summary>One reason on the "Why there are no service presets" card, with where it comes from.</summary>
public sealed record ServicePresetFact(string Topic, string Text, string Source);

/// <summary>
/// The Services page's "Why there are no service presets" card (v3.1), shown in the "Doesn't help" style. Written in
/// PC Tidy's own words from the research (the services notes and the tuning-tool comparison page). Other tools are never named on screen
/// except the two public lists people search for (Black Viper, WinUtil); a tuning tool's own preset names are not copied.
/// </summary>
public static class ServicePresetFacts
{
    public const string Title = "Why there are no service presets";

    public const string Summary = "Lists that switch off dozens of services at once haven't been shown to make Windows or games faster, and some break things you use. "
                                  + "PC Tidy only offers the few service changes with a proven benefit, one by one, with undo.";

    public static readonly IReadOnlyList<ServicePresetFact> Facts = new ServicePresetFact[]
    {
        new("Black Viper lists and WinUtil-style \"set services to Manual\"",
            "No boot-time, memory or FPS numbers were found for them: the guides describe the change and promise a gain without measuring it.",
            "PC Tidy research, November 2026 (Black Viper's guide and WinUtil's tweak text)"),
        new("Xbox services",
            "Set to Manual, they use nothing until a game or the Xbox app asks for them. Turned off, Xbox and Game Pass sign-in and cloud saves stop working.",
            "Microsoft's Windows services guidance (Manual services only start when needed); PC Tidy research, November 2026"),
        new("The strongest presets in other tuning tools",
            "Their strongest levels can switch off dozens of Windows features at once, such as Hyper-V, clipboard sync, Phone Link and notifications, "
            + "and getting them back depends on what that tool saved.",
            "Other tuning tools' own confirm screens, seen in published videos (PC Tidy research, November 2026)"),
    };
}
