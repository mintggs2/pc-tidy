namespace PcTidy.Features.Boost.State;

/// <summary>
/// What Game boost changed and hasn't put back yet. Saved before anything is changed and after every run, so if
/// PC Tidy (or the PC) stops mid-game the next start restores it. PC Tidy's own derived data only.
/// </summary>
public sealed class BoostState
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    /// <summary>A boost is in effect (a game started and its restore hasn't run yet).</summary>
    public bool Active { get; set; }
    public string? GameKey { get; set; }
    public string? GameName { get; set; }
    public DateTime? StartedUtc { get; set; }
    /// <summary>OneDrive was (or was about to be) shut down by the boost: start it again afterwards.</summary>
    public bool OneDrivePaused { get; set; }
    /// <summary>Apps the boost closed (or was about to close): reopen them afterwards.</summary>
    public List<ClosedApp> ClosedApps { get; set; } = new();
    public string? BoostRunId { get; set; }
    /// <summary>Apps closed by the last boost that weren't reopened automatically: the page offers a "Reopen" button.</summary>
    public List<ClosedApp> ReopenOffers { get; set; } = new();

    /// <summary>Something to put back: OneDrive or closed apps.</summary>
    public bool HasChanges => OneDrivePaused || ClosedApps.Count > 0;
}

/// <summary>A closed app, recorded read-only before closing it, so it can be started again the same way.</summary>
public sealed class ClosedApp
{
    public string ProcessName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    /// <summary>Full exe path (QueryFullProcessImageName).</summary>
    public string ExePath { get; set; } = "";
    /// <summary>Arguments from its command line (without the exe), or null.</summary>
    public string? Arguments { get; set; }
}
