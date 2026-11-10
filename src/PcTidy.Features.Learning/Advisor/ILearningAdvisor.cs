using PcTidy.Features.Learning.Learners;
using PcTidy.Features.Learning.Model;

namespace PcTidy.Features.Learning.Advisor;

/// <summary>
/// What PC Tidy learned, for other sections and the shell. Register one instance as a service
/// (<c>host.Add&lt;ILearningAdvisor&gt;(LearningAdvisor.Create(host))</c>).
/// Learning never acts on its own: answers only change what starts ticked, the order of things, or offer a suggestion
/// that still goes through the normal preview and confirm. All methods are safe to call from the UI thread (answers are
/// cached; recording is a few milliseconds) and never throw for a missing or damaged learning file.
/// </summary>
public interface ILearningAdvisor
{
    /// <summary>True while the user paused learning (or switched it off in Settings): nothing new is recorded.</summary>
    bool IsPaused { get; }

    /// <summary>Raised when anything learned changed (refresh defaults, suggestions, cards).</summary>
    event EventHandler? Changed;

    // ---- skip counter ----

    /// <summary>True when the user skipped this group the last 3 of 3 times: start it unticked (never hide it).</summary>
    bool IsUntickedByDefault(string featureId, string groupId);

    /// <summary>The plain-English why ("You skipped this 3 of the last 3 times…"), or null when nothing was learned.</summary>
    SkipVerdict? SkipAdvice(string featureId, string groupId);

    /// <summary>"Reset" next to an unticked-by-default group: forgets the choices for it.</summary>
    void ResetSkip(string featureId, string groupId);

    /// <summary>
    /// Call after a confirm, once per offered group: Accept when any of its items was ticked and run, Reject when it was
    /// left unticked. Pass the run id so a later undo can be counted.
    /// </summary>
    void RecordDecision(string featureId, string groupId, LearningDecision decision, string? runId = null, string? groupTitle = null, string? example = null);

    /// <summary>Call after the user undid a run: every group accepted in it gets an undo (counts as two skips).</summary>
    void RecordUndo(string runId);

    // ---- folders ----

    /// <summary>Call when the user opens a folder through PC Tidy (Open in Explorer, a search result, a favorite).</summary>
    void RecordFolderVisit(string folder);

    /// <summary>Folders the user uses most (fading 14-day score), best first. Only folders that still exist.</summary>
    IReadOnlyList<FrequentFolder> FrequentFolders(int max = 10);

    // ---- suggestions and facts ----

    /// <summary>Organize rules suggested from the user's own moves (≥5 moves, ≥80% consistent, ≥2 days).</summary>
    IReadOnlyList<SuggestedRule> SuggestedRules();

    /// <summary>Apps not opened for 30+ days while watched. Empty before 30 days of watching.</summary>
    IReadOnlyList<UnusedApp> UnusedApps();

    /// <summary>
    /// Lets the Apps section name installed apps to watch (so apps never launched at all can be noticed too).
    /// Launches of programs in an app's install folder count as using it.
    /// </summary>
    void ObserveInstalledApps(IEnumerable<WatchedApp> apps);

    /// <summary>Fill trend per drive; "full in about N days" only after 14+ daily readings with a real upward trend.</summary>
    IReadOnlyList<DriveForecast> DriveForecasts();

    /// <summary>Is now a good time for a long background job (no input, nothing full-screen, not on battery)?</summary>
    IdleState CheckIdle(TimeSpan? required = null);

    /// <summary>One idle check for the quiet-hours learner. Call from a background timer about every 30 minutes.</summary>
    void SampleIdle();

    /// <summary>Hours the PC is usually free (from earlier idle checks).</summary>
    QuietHours UsualQuietHours();

    /// <summary>
    /// The daily learning pass (call at start-up in the background, then about once a day): reads new finished runs from
    /// the journal, a free-space reading per drive, app launch counts, optionally Windows' Recent list, then folds
    /// events older than 90 days into totals. Read-only for everything except PC Tidy's own learning file.
    /// </summary>
    Task ObserveAsync(CancellationToken ct);
}
