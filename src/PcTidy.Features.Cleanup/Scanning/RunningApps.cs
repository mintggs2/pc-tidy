namespace PcTidy.Features.Cleanup.Scanning;

/// <summary>
/// Which of the apps whose caches we clean are open right now. PC Tidy never closes them:
/// it lists them, says so, and the engine skips their items while they're running.
/// </summary>
internal sealed class RunningApps
{
    public const string Chrome = "chrome";
    public const string Edge = "msedge";
    public const string Brave = "brave";
    public const string Firefox = "firefox";
    public const string Discord = "discord";
    public const string DiscordPtb = "discordptb";
    public const string DiscordCanary = "discordcanary";
    public const string Steam = "steam";
    public const string Telegram = "telegram";
    public const string Spotify = "spotify";

    private static readonly Dictionary<string, string> DisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        [Chrome] = "Chrome", [Edge] = "Edge", [Brave] = "Brave", [Firefox] = "Firefox",
        [Discord] = "Discord", [DiscordPtb] = "Discord PTB", [DiscordCanary] = "Discord Canary",
        [Steam] = "Steam", [Telegram] = "Telegram", [Spotify] = "Spotify",
    };

    private readonly HashSet<string> _running;

    public RunningApps(IEnumerable<string> runningProcesses)
    {
        // Normalise defensively: callers should pass lower-case names without ".exe", but don't rely on it.
        _running = new HashSet<string>(
            runningProcesses.Select(p => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p[..^4] : p),
            StringComparer.OrdinalIgnoreCase);
    }

    public static string DisplayName(string process) => DisplayNames.TryGetValue(process, out var n) ? n : process;

    /// <summary>Display names of the given processes that are running.</summary>
    public IReadOnlyList<string> OpenAmong(IEnumerable<string>? processes)
        => processes is null
            ? Array.Empty<string>()
            : processes.Where(_running.Contains).Select(DisplayName).Distinct().ToList();

    public bool IsRunning(string process) => _running.Contains(process);
}
