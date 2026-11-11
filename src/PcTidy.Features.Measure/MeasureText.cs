namespace PcTidy.Features.Measure;

/// <summary>Every fixed sentence on the Measure page, in one place (tests check the honesty rules against these).</summary>
public static class MeasureText
{
    public const string Subtitle =
        "Check a change on your own PC: record the same scene before and after with a free tool, import the files, and compare. PC Tidy only reads the files.";

    /// <summary>A free recording tool. PC Tidy doesn't bundle any of them; the links open the makers' own pages.</summary>
    public sealed record Tool(string Name, string What, string Url);

    public static readonly IReadOnlyList<Tool> Tools = new[]
    {
        new Tool("Intel PresentMon", "Free and open source (MIT). Records a CSV per run.", "https://game.intel.com/intel-presentmon/"),
        new Tool("CapFrameX", "Free and open source. Records runs and exports CSV or JSON.", "https://www.capframex.com/"),
        new Tool("NVIDIA FrameView", "Free. Writes a CSV log per run.", "https://www.nvidia.com/en-us/geforce/technologies/frameview/"),
        new Tool("OCAT", "Free, from AMD GPUOpen. Writes a CSV per capture.", "https://github.com/GPUOpen-Tools/OCAT"),
    };

    public static readonly IReadOnlyList<string> RecordSteps = new[]
    {
        "Pick one repeatable scene: a built-in benchmark, or the same save and the same walk each time.",
        "Play it once without recording (warm-up). The first run after a change or a driver update loads shaders and is slower.",
        "Record 3 runs of 60 to 120 seconds each, all the same length. These are your Before runs.",
        "Make the change (for example a tweak in PC Tidy). If it needs a restart, restart, wait a few minutes, and do one warm-up run.",
        "Record 3 more runs of the same scene. These are your After runs.",
        "Optional: undo the change and record 3 runs again (Before again). This shows whether the PC drifted while you tested.",
        "Keep everything else the same: power plan, plugged in, resolution, FPS cap, V-sync/VRR, driver, and close other apps.",
    };

    public const string ImportHelp =
        "Drop CSV or JSON files here, or pick them. PresentMon 1.x and 2.x, CapFrameX (CSV or JSON), OCAT and FrameView files work. " +
        "When a file has several processes, pick the game.";

    public const string Header_WhatThisDoesNotProve = "What this does not prove";

    public static readonly IReadOnlyList<string> DoesNotProve = new[]
    {
        "It applies only to this game, this scene and this PC. Another game or another PC can behave differently.",
        "Frame times don't measure input latency. Smoother frames don't prove the game reacts faster.",
        "\"No measurable change\" doesn't mean harmless. Power use, heat, stability and other apps aren't measured here.",
        "A small change can hide in the run-to-run noise. More and longer runs make it easier to see.",
        "This page never turns a result into a general claim for other people's PCs.",
    };

    public const string KeepUndoNote =
        "Measure never changes or undoes anything. Keep or undo the change in History.";

    public const string LicenceNote =
        "PC Tidy doesn't include any of these tools. Download them from their makers and check their licences.";
}
