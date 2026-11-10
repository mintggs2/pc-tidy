using PcTidy.Core.Model;

namespace PcTidy.App.WhatIs;

/// <summary>
/// PC Tidy's own Explorer right-click entries, as plan items for the Platform's ShellVerbSetInstalled handler
/// (classic per-user verbs under <c>HKCU\Software\Classes\&lt;on&gt;\shell\PCTidy.&lt;verb&gt;</c>; no admin; under
/// "Show more options" on Windows 11). The uninstaller removes every <c>PCTidy.*</c> verb.
/// Args: "verb" is given WITHOUT the "PCTidy." prefix (the handler adds it; its name check allows no dot).
/// </summary>
public static class ShellVerbs
{
    public const string FeatureId = "shell";
    public const string WhatIsVerb = "WhatIsThis";
    public const string TidyVerb = "TidyFolder";
    public const string WhatIsLabel = "What is this? (PC Tidy)";
    public const string TidyLabel = "Tidy this folder (PC Tidy)";

    /// <summary>Where each entry appears: files, folders, and the background of an open folder.</summary>
    private static readonly (string On, string Placeholder, string What)[] WhatIsTargets =
    {
        ("*", "%1", "files"), ("Directory", "%1", "folders"), (@"Directory\Background", "%V", "the inside of a folder"),
    };

    private static readonly (string On, string Placeholder, string What)[] TidyTargets =
    {
        ("Directory", "%1", "folders"), (@"Directory\Background", "%V", "the inside of a folder"),
    };

    /// <summary>Adds (enable) or removes "What is this?" for files, folders and folder backgrounds.</summary>
    public static IReadOnlyList<PlanItem> WhatIs(string exePath, bool enable) => Items(exePath, enable, WhatIsVerb, WhatIsLabel, "--whatis", WhatIsTargets);

    /// <summary>Adds (enable) or removes "Tidy this folder" for folders and folder backgrounds.</summary>
    public static IReadOnlyList<PlanItem> TidyFolder(string exePath, bool enable) => Items(exePath, enable, TidyVerb, TidyLabel, "--tidy", TidyTargets);

    /// <summary>The exact command written for one entry: the quoted PcTidy.exe, the switch, the quoted placeholder.</summary>
    public static string Command(string exePath, string @switch, string placeholder) => $"\"{exePath}\" {@switch} \"{placeholder}\"";

    private static IReadOnlyList<PlanItem> Items(string exe, bool enable, string verb, string label, string @switch, (string On, string Placeholder, string What)[] targets)
        => targets.Select(t => new PlanItem
        {
            Kind = ActionKind.ShellVerbSetInstalled,
            FeatureId = FeatureId,
            Description = enable ? $"Add \"{label}\" to the right-click menu of {t.What}" : $"Remove \"{label}\" from the right-click menu of {t.What}",
            Enable = enable,
            Args = new Dictionary<string, string>
            {
                ["verb"] = verb,
                ["label"] = label,
                ["on"] = t.On,
                ["command"] = Command(exe, @switch, t.Placeholder),
            },
        }).ToList();

    /// <summary>Start with Windows (HKCU Run value "PC Tidy" = "&lt;PcTidy.exe&gt;" --tray), through the Platform handler.</summary>
    public static PlanItem StartWithWindows(bool enable) => new()
    {
        Kind = ActionKind.StartWithWindowsSet,
        FeatureId = FeatureId,
        Description = enable ? "Start PC Tidy with Windows, in the tray (no window)" : "Stop starting PC Tidy with Windows",
        Enable = enable,
    };

    /// <summary>
    /// The switch may show "on" only when every step happened, was simulated (practice PC), or was skipped because
    /// it was already that way (the Platform handlers say "Nothing to change." then). Anything else keeps it as it was.
    /// </summary>
    public static bool Succeeded(RunRecord? run)
        => run is not null && run.Steps.Count > 0 && run.Steps.All(s => s.Status is StepStatus.Done or StepStatus.Simulated
            || (s.Status == StepStatus.Skipped && s.Message is { } m
                && (m.Contains("Nothing to change", StringComparison.OrdinalIgnoreCase) || m.Contains("already", StringComparison.OrdinalIgnoreCase))));
}
