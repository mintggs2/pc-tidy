using PcTidy.App.Composition;

namespace PcTidy.App;

/// <summary>Parsed command line.</summary>
public sealed record AppArgs
{
    /// <summary>--demo: in-memory fake services and sample data. Never touches the PC.</summary>
    public bool Demo { get; init; }
    /// <summary>--screenshots &lt;dir&gt;: render every page to PNG and exit (implies --demo unless --real-screenshots).</summary>
    public string? ScreenshotDir { get; init; }
    /// <summary>--elevated: run as the admin helper (no window).</summary>
    public bool Elevated { get; init; }
    /// <summary>--scheduled-scan: report-only scan from Task Scheduler (no window).</summary>
    public bool ScheduledScan { get; init; }
    /// <summary>--light / --dark: force a theme (screenshots, testing).</summary>
    public string? ForcedTheme { get; init; }
    /// <summary>--release 1|2|3: which release's sections are shown (default from AppServices).</summary>
    public int? Release { get; init; }
    /// <summary>
    /// --sandbox &lt;folder&gt;: practice mode. The full app, but every path points into that folder (e.g. a generated testbed);
    /// Windows tools only simulate. With --screenshots it captures this practice PC instead of demo data (scans and
    /// simulations only: screenshot mode never confirms a run).
    /// </summary>
    public string? SandboxRoot { get; init; }
    /// <summary>--window 1024x700: window size in screenshot mode (default 1280x800).</summary>
    public (int Width, int Height)? WindowSize { get; init; }
    /// <summary>--whatis &lt;path&gt;: Explorer's "What is this?" entry. Opens the small read-only explanation window.</summary>
    public string? WhatIs { get; init; }
    /// <summary>--tidy &lt;folder&gt;: Explorer's "Tidy this folder" entry. Opens Organize for that folder (preview only).</summary>
    public string? TidyFolder { get; init; }
    /// <summary>--tray: started with Windows; stays in the tray (no window) until opened.</summary>
    public bool Tray { get; init; }
    /// <summary>--unregister-toasts: removes PC Tidy's Windows notification registration and exits (uninstall).</summary>
    public bool UnregisterToasts { get; init; }
    public IReadOnlyList<string> Raw { get; init; } = Array.Empty<string>();

    public static AppArgs Parse(IReadOnlyList<string> args)
    {
        string? shots = null, theme = null, sandbox = null, whatIs = null, tidy = null;
        bool demo = false, elevated = false, scheduled = false, tray = false, unregister = false;
        int? release = null;
        (int, int)? size = null;
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--demo": demo = true; break;
                case "--screenshots" when i + 1 < args.Count: shots = args[++i]; break;
                case "--sandbox" when i + 1 < args.Count: sandbox = args[++i]; break;
                case "--elevated": elevated = true; break;
                case "--scheduled-scan": scheduled = true; break;
                case "--light": theme = "Light"; break;
                case "--dark": theme = "Dark"; break;
                case "--release" when i + 1 < args.Count && int.TryParse(args[i + 1], out var r): release = r; i++; break;
                case "--window" when i + 1 < args.Count && ParseSize(args[i + 1]) is { } s: size = s; i++; break;
                case "--whatis" when i + 1 < args.Count: whatIs = ExplorerPath(args[++i]); break;
                case "--tidy" when i + 1 < args.Count: tidy = ExplorerPath(args[++i]); break;
                case "--tray": tray = true; break;
                case "--unregister-toasts": unregister = true; break;
            }
        }
        return new AppArgs
        {
            // Screenshots use demo data unless they were pointed at a practice PC.
            Demo = demo || (shots != null && sandbox == null),
            ScreenshotDir = shots, SandboxRoot = sandbox, Elevated = elevated, ScheduledScan = scheduled,
            ForcedTheme = theme, Release = release, WindowSize = size, Raw = args.ToList(),
            WhatIs = whatIs, TidyFolder = tidy, Tray = tray, UnregisterToasts = unregister,
        };
    }

    /// <summary>
    /// A path as Explorer passes it. Explorer quotes "%V", so a drive root arrives as <c>C:"</c> (the backslash
    /// escaped the quote): put the backslash back. Anything that isn't a full local or network path is dropped.
    /// </summary>
    public static string? ExplorerPath(string raw)
    {
        var p = raw.Trim().Trim('"').Trim();
        if (p.Length == 2 && p[1] == ':') p += "\\";
        if (p.Length == 0 || p.Length > 32_000 || p.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;
        try { return Path.IsPathFullyQualified(p) ? Path.GetFullPath(p) : null; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static (int, int)? ParseSize(string s)
    {
        var parts = s.Split('x', 'X');
        return parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h) && w >= 640 && h >= 480 ? (w, h) : null;
    }
}

/// <summary>
/// Entry points for the window-less modes. The lead fills these in (PcTidy.Platform / PcTidy.Features.Schedule):
///   AppEntry.ElevatedHandler      = (args, services, ct) => ElevatedHelper.RunAsync(...);
///   AppEntry.ScheduledScanHandler = (args, services, ct) => ScheduledScan.RunAsync(...);
/// Each returns the process exit code. While null, those modes exit with code 2 and change nothing.
/// </summary>
public static class AppEntry
{
    public const int ExitNotWired = 2;

    public static Func<AppArgs, AppServices, CancellationToken, Task<int>>? ElevatedHandler { get; set; }
    public static Func<AppArgs, AppServices, CancellationToken, Task<int>>? ScheduledScanHandler { get; set; }
}
