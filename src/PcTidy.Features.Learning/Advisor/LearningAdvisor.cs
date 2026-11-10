using System.Globalization;
using Microsoft.Data.Sqlite;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Learning.Learners;
using PcTidy.Features.Learning.Model;
using PcTidy.Features.Learning.Sources;
using PcTidy.Features.Learning.Store;

namespace PcTidy.Features.Learning.Advisor;

/// <summary>The read-only sources learning uses. Any of them may be missing (that part then learns nothing).</summary>
public sealed class LearningSources
{
    public IAppUsageSource? UserAssist { get; init; }
    public IAppUsageSource? Steam { get; init; }
    public IDriveSpaceSource? Drives { get; init; }
    public IRecentFoldersSource? Recent { get; init; }
    public IIdleSignals? Idle { get; init; }
    /// <summary>Programs running now (sampled with the idle checks): an app seen running is an app that's used.</summary>
    public IRunningAppsSource? Running { get; init; }
    /// <summary>Programs that start with Windows: never listed as "apps you don't open".</summary>
    public IStartupAppsSource? Startup { get; init; }
    /// <summary>Download host of a moved file (default: its Zone.Identifier).</summary>
    public Func<string, string?>? HostOf { get; init; }
    public Func<string, bool> FolderExists { get; init; } = Directory.Exists;
}

/// <summary>
/// The learning service: records what the user does (through <see cref="LearningStore"/>, the only writer), runs the
/// pure learners over it, and answers other sections. Answers are cached and rebuilt after every change.
/// Nothing here ever changes the PC: it only adjusts defaults, order and suggestions.
/// </summary>
public sealed class LearningAdvisor : ILearningAdvisor
{
    private const int FolderCards = 5;

    private readonly LearningStore _store;
    private readonly AppSettings _settings;
    private readonly IRunJournal? _journal;
    private readonly LearningSources _sources;
    private readonly ILearningClock _clock;
    private readonly PrivacyFilter _privacy;
    private readonly JournalMiner _miner;
    private readonly Func<string, string?> _featureTitle;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _observeGate = new(1, 1);
    private Snapshot? _snapshot;
    private IReadOnlyList<WatchedApp> _watchedApps = Array.Empty<WatchedApp>();

    public LearningAdvisor(LearningStore store, AppSettings settings, IKnownLocations locations, IRunJournal? journal,
        LearningSources sources, ILearningClock? clock = null, Func<string, string?>? featureTitle = null)
    {
        _store = store;
        _settings = settings;
        _journal = journal;
        _sources = sources;
        _clock = clock ?? SystemLearningClock.Instance;
        _privacy = new PrivacyFilter(locations, settings);
        _miner = new JournalMiner(_privacy, sources.HostOf);
        _featureTitle = featureTitle ?? (_ => null);
        if (store.OpenProblem is { } problem) Problem = ProblemText(problem);
        _store.Changed += (_, _) => Invalidate();
    }

    /// <summary>
    /// The advisor for the app: the store in &lt;DataRoot&gt;\learning, the real read-only sources. In a sandbox the real
    /// registry and Windows' Recent list are never read (sandbox paths only).
    /// </summary>
    public static LearningAdvisor Create(IServiceProvider services)
    {
        var settings = services.Get<AppSettings>();
        var locations = services.Get<IKnownLocations>();
        var system = services.GetService(typeof(ISystemInfo)) as ISystemInfo;
        var modules = services.GetService(typeof(IEnumerable<IFeatureModule>)) as IEnumerable<IFeatureModule>;
        var titles = modules?.ToDictionary(m => m.Id, m => m.Title, StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, string>();
        var sources = new LearningSources
        {
            UserAssist = locations.IsSandbox ? null : new UserAssistSource(WindowsRegistryKeyReader.Instance, locations),
            Steam = new SteamPlayedSource(locations),
            Drives = system is null ? null : new SystemInfoDriveSpace(system),
            Recent = new WindowsRecentFolders(locations),
            Idle = locations.IsSandbox ? null : WindowsIdleSignals.Instance,
            Running = locations.IsSandbox ? null : WindowsRunningApps.Instance,
            Startup = locations.IsSandbox ? null : new WindowsStartupApps(WindowsRegistryKeyReader.Instance),
        };
        var dataRoot = string.IsNullOrWhiteSpace(settings.DataRoot) ? locations.DataRoot : settings.DataRoot;
        return new LearningAdvisor(LearningStore.Open(dataRoot), settings, locations, services.GetService(typeof(IRunJournal)) as IRunJournal,
            sources, featureTitle: id => titles.GetValueOrDefault(id));
    }

    public LearningStore Store => _store;

    /// <summary>Set when the learning file couldn't be read; "Forget everything" starts a fresh one.</summary>
    public string? Problem { get; private set; }

    public bool IsPaused => !_settings.LearningEnabled || Safe(() => _store.Paused, false);

    /// <summary>Learning is switched off in Settings (as opposed to paused on the learned page).</summary>
    public bool IsOffInSettings => !_settings.LearningEnabled;

    public event EventHandler? Changed;

    // ------------------------------------------------------------------ skip counter

    public bool IsUntickedByDefault(string featureId, string groupId)
        => SkipAdvice(featureId, groupId)?.UntickByDefault == true && !IsOff(SkipId(featureId, groupId));

    public SkipVerdict? SkipAdvice(string featureId, string groupId)
        => Current().Skips.TryGetValue((featureId, groupId), out var s) && s.Verdict.Of > 0 ? s.Verdict : null;

    public void ResetSkip(string featureId, string groupId) => Safe(() =>
    {
        _store.DeleteDecisions(featureId, groupId);
        _store.DeleteItemState(SkipId(featureId, groupId));
    });

    public void RecordDecision(string featureId, string groupId, LearningDecision decision, string? runId = null, string? groupTitle = null, string? example = null)
    {
        if (IsPaused || string.IsNullOrWhiteSpace(featureId) || string.IsNullOrWhiteSpace(groupId)) return;
        Safe(() => _store.AddDecision(new DecisionEvent(featureId, groupId, decision, _clock.UtcNow, runId, example, groupTitle)));
    }

    public void RecordUndo(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId)) return;
        // The undone moves never happened as far as rule suggestions go, even while paused.
        Safe(() => _store.DeleteMovesOfRun(runId));
        if (IsPaused) return;
        Safe(() =>
        {
            foreach (var (feature, group) in _store.AcceptedInRun(runId))
                _store.AddDecision(new DecisionEvent(feature, group, LearningDecision.Undo, _clock.UtcNow, runId));
        });
    }

    // ------------------------------------------------------------------ folders

    public void RecordFolderVisit(string folder)
    {
        if (IsPaused || !_privacy.MayRemember(folder)) return;
        Safe(() => _store.AddFolderVisit(new FolderVisit(PathUtil.Normalize(folder), _clock.UtcNow, VisitSource.PcTidy)));
    }

    public IReadOnlyList<FrequentFolder> FrequentFolders(int max = 10)
        => Current().Folders.Where(f => !IsOff(f.Id) && FolderExists(f.Folder)).Take(Math.Max(0, max)).ToList();

    // ------------------------------------------------------------------ suggestions and facts

    public IReadOnlyList<SuggestedRule> SuggestedRules()
    {
        var snap = Current();
        return snap.Rules.Where(c => snap.State(c.Key) is not { Off: true } and not { Dismissed: true } and not { MadeRule: true })
            .Select(c => Suggestion(c, snap)).ToList();
    }

    public IReadOnlyList<UnusedApp> UnusedApps() => Current().Apps.Apps.Where(a => !IsOff(a.Id)).ToList();

    /// <summary>The whole unused-apps result, including how many days of watching are still needed.</summary>
    public UnusedAppsReport UnusedAppsReport() => Current().Apps;

    public void ObserveInstalledApps(IEnumerable<WatchedApp> apps)
    {
        var list = apps.Where(a => !string.IsNullOrWhiteSpace(a.Key)).ToList();
        lock (_gate) _watchedApps = list;
    }

    public IReadOnlyList<DriveForecast> DriveForecasts() => Current().Drives.Where(d => !IsOff(d.Id)).ToList();

    public IdleState CheckIdle(TimeSpan? required = null)
        => _sources.Idle is { } signals
            ? Safe(() => new IdleDetector(signals).Check(required), new IdleState(false, TimeSpan.Zero, "Couldn't tell if the PC is in use."))
            : new IdleState(false, TimeSpan.Zero, "Idle detection isn't available here.");

    /// <summary>One idle check for the quiet-hours learner. Call from a background timer about every 30 minutes.</summary>
    public void SampleIdle()
    {
        if (IsPaused) return;
        // Every 30 minutes, also note which watched apps are running (started at sign-in, from the tray or another app).
        Step(SampleRunning);
        if (_sources.Idle is null) return;
        var state = CheckIdle();
        Safe(() => _store.AddIdleSample(_clock.UtcNow.ToLocalTime().Hour, state.IsIdle));
    }

    /// <summary>Apps PC Tidy already watches that are running now count as used now. Nothing new is added here.</summary>
    private void SampleRunning()
    {
        if (_sources.Running is null) return;
        var running = _sources.Running.RunningProgramPaths();
        if (running.Count == 0) return;
        var now = _clock.UtcNow;
        var seen = _store.Apps().Where(a => Path.IsPathFullyQualified(a.Key) && running.Contains(a.Key)).ToList();
        if (seen.Count == 0) return;
        _store.SaveApps(seen.Select(a => Learners.UnusedApps.Observe(a, new AppUsage(a.Key, a.Name, a.Source, a.LastCount, now), now)));
    }

    public QuietHours UsualQuietHours()
        => IsOff(QuietHours.ItemId) ? new QuietHours(Array.Empty<int>(), 0, "Turned off.") : Current().Quiet;

    // ------------------------------------------------------------------ the daily pass

    public async Task ObserveAsync(CancellationToken ct)
    {
        if (!await _observeGate.WaitAsync(0, ct).ConfigureAwait(false)) return; // one pass at a time
        try
        {
            if (!IsPaused)
            {
                if (_journal is not null) await MineJournalAsync(_journal, ct).ConfigureAwait(false);
                await Task.Run(() =>
                {
                    Step(SampleDrives);
                    Step(() => SampleApps(ct));
                    Step(SampleRunning);
                    Step(() => ReadRecent(ct));
                }, ct).ConfigureAwait(false);
            }
            await Task.Run(() => Step(() => _store.Maintain()), ct).ConfigureAwait(false);
        }
        finally
        {
            _observeGate.Release();
        }
    }

    private async Task MineJournalAsync(IRunJournal journal, CancellationToken ct)
    {
        IReadOnlyList<RunRecord> runs;
        try { runs = await journal.ListAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return; }
        var since = Safe(() => _store.StartedUtc, _clock.UtcNow);
        var withMoves = Safe(() => _store.Moves().Select(m => m.RunId).ToHashSet(StringComparer.Ordinal), new HashSet<string>());
        foreach (var run in runs)
        {
            ct.ThrowIfCancellationRequested();
            if (run.Status is RunStatus.Undone or RunStatus.RolledBack)
            {
                if (withMoves.Contains(run.RunId)) Step(() => _store.DeleteMovesOfRun(run.RunId));
                continue;
            }
            if (!JournalMiner.IsFinished(run) || (run.FinishedUtc ?? run.CreatedUtc) < since) continue;
            if (Safe(() => _store.IsRunMined(run.RunId), true)) continue;
            Step(() =>
            {
                var mined = _miner.Mine(run);
                _store.AddMinedRun(mined.Mark, mined.Moves, mined.Visits);
            });
        }
    }

    private void SampleDrives()
    {
        if (_sources.Drives is null) return;
        foreach (var s in _sources.Drives.Read(_clock.UtcNow)) _store.AddFreeSpace(s);
    }

    private void SampleApps(CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var userAssist = _sources.UserAssist?.Read(ct) ?? Array.Empty<AppUsage>();
        var usage = userAssist.Concat(_sources.Steam?.Read(ct) ?? Array.Empty<AppUsage>()).ToList();
        IReadOnlyList<WatchedApp> watched;
        lock (_gate) watched = _watchedApps;
        foreach (var app in watched)
        {
            var launches = app.InstallFolder is { Length: > 3 } folder
                ? userAssist.Where(u => Path.IsPathFullyQualified(u.Key) && PathUtil.IsUnder(u.Key, folder)).ToList()
                : new List<AppUsage>();
            usage.Add(new AppUsage(app.Key, app.Name, AppSource.Installed, launches.Sum(l => l.RunCount),
                launches.Select(l => l.LastRunUtc).Where(d => d is not null).DefaultIfEmpty().Max()));
        }
        if (usage.Count == 0) return;
        var existing = _store.Apps().ToDictionary(a => a.Key, StringComparer.OrdinalIgnoreCase);
        _store.SaveApps(usage.Select(u => Learners.UnusedApps.Observe(existing.GetValueOrDefault(u.Key), u, now)));
    }

    private void ReadRecent(CancellationToken ct)
    {
        if (_sources.Recent is null || !_store.ReadWindowsRecent) return;
        var visits = _sources.Recent.Read(_store.LatestVisit(VisitSource.WindowsRecent), ct).Where(v => _privacy.MayRemember(v.Folder)).ToList();
        if (visits.Count > 0) _store.AddFolderVisits(visits);
    }

    // ------------------------------------------------------------------ the learned page

    /// <summary>Every learned item, for the "What PC Tidy has learned" page (turned-off items included, marked Off).</summary>
    public IReadOnlyList<LearnedItem> LearnedItems()
    {
        var snap = Current();
        var items = new List<LearnedItem>();
        foreach (var c in snap.Rules)
        {
            var state = snap.State(c.Key);
            if (state is { Dismissed: true } or { MadeRule: true }) continue;
            var s = Suggestion(c, snap);
            items.Add(new LearnedItem
            {
                Id = c.Key, Kind = LearnedKind.Rule, Title = s.Title, Reason = s.Reason,
                Counts = $"{Plural(c.Moves, "move")} on {Plural(c.Days, "day")}, {c.Confidence:P0} to the same folder",
                Examples = s.Examples, SinceUtc = c.FirstUtc, Off = state?.Off == true,
                Effect = "A suggestion only: nothing moves until you make it a rule and confirm it in Organize.", Payload = c,
            });
        }
        foreach (var ((feature, group), s) in snap.Skips.OrderBy(k => k.Key.Item1).ThenBy(k => k.Key.Item2))
        {
            if (!s.Verdict.UntickByDefault) continue;
            var id = SkipId(feature, group);
            var section = _featureTitle(feature) ?? feature;
            items.Add(new LearnedItem
            {
                Id = id, Kind = LearnedKind.Skip, Title = $"{s.Title ?? group} starts unticked", Reason = s.Verdict.Reason,
                Counts = $"Skipped {Plural(s.Rejects, "time")}, undone {Plural(s.Undos, "time")}, kept {Plural(s.Accepts, "time")}",
                Examples = s.Examples, SinceUtc = s.FirstUtc, Off = IsOff(id, snap),
                Effect = $"In {section} it's still listed, just not ticked. You can tick it any time.", Payload = (feature, group),
            });
        }
        foreach (var f in snap.Folders.Where(f => FolderExists(f.Folder)).Take(FolderCards))
            items.Add(new LearnedItem
            {
                Id = f.Id, Kind = LearnedKind.Folder, Title = MoveFeatures.FolderName(f.Folder), Reason = $"You use {f.Folder} often.",
                Counts = $"{Plural(f.Visits, "visit")}, last on {Day(f.LastUtc)}", SinceUtc = f.FirstUtc, Off = IsOff(f.Id, snap),
                Effect = "Offered first in Favorites and folder suggestions.", Payload = f.Folder,
            });
        foreach (var a in snap.Apps.Apps)
            items.Add(new LearnedItem
            {
                Id = a.Id, Kind = LearnedKind.App, Title = a.Name, Reason = a.Reason,
                Counts = a.Source == AppSource.Steam ? "From Steam's last-played date" : "From Windows' record of programs you start, and what PC Tidy saw running",
                SinceUtc = Safe(() => _store.StartedUtc, _clock.UtcNow), Off = IsOff(a.Id, snap),
                Effect = "Flagged in Apps as \"not used lately\". Nothing is uninstalled.", Payload = a.Key,
            });
        foreach (var d in snap.Drives)
            items.Add(new LearnedItem
            {
                Id = d.Id, Kind = LearnedKind.Drive, Title = $"Drive {d.Drive.TrimEnd('\\')}", Reason = d.Reason,
                Counts = $"{Plural(d.Samples, "daily reading")}" + (d.Samples >= LearningRules.MinDriveSamples ? $", trend {Sizes.Format((long)Math.Abs(d.BytesPerDay))} a day" : ""),
                SinceUtc = d.FirstUtc, Off = IsOff(d.Id, snap),
                Effect = "Shown on Home and in Move to another drive. PC Tidy's own clean-ups don't count as the drive filling or emptying.", Payload = d.Drive,
            });
        if (snap.Quiet.Hours.Count > 0)
            items.Add(new LearnedItem
            {
                Id = QuietHours.ItemId, Kind = LearnedKind.QuietHours, Title = "When your PC is free", Reason = snap.Quiet.Reason,
                Counts = Plural(snap.Quiet.Samples, "check"), SinceUtc = Safe(() => _store.StartedUtc, _clock.UtcNow), Off = IsOff(QuietHours.ItemId, snap),
                Effect = "Used to suggest a time for scheduled Deep scans. Never during games or full-screen apps, never on battery.",
            });
        return items;
    }

    /// <summary>"Turn off" / "Turn on" one learned item.</summary>
    public void SetOff(LearnedItem item, bool off) => Safe(() =>
    {
        var s = Current().State(item.Id);
        _store.SetItemState((s ?? new LearnedItemState { ItemId = item.Id }) with { Off = off, Utc = _clock.UtcNow });
    });

    /// <summary>"Delete": forgets the evidence behind the item (it can come back only from new evidence).</summary>
    public void Delete(LearnedItem item) => Safe(() =>
    {
        switch (item.Kind)
        {
            case LearnedKind.Rule when item.Payload is RuleCandidate c:
                _store.DeleteRuleEvidence(c.MoveIds, c.Feature, c.Value, c.Destination);
                break;
            case LearnedKind.Skip when item.Payload is ValueTuple<string, string> key:
                _store.DeleteDecisions(key.Item1, key.Item2);
                break;
            case LearnedKind.Folder when item.Payload is string folder:
                _store.DeleteFolder(folder);
                break;
            case LearnedKind.App when item.Payload is string appKey:
                _store.DeleteApp(appKey);
                break;
            case LearnedKind.Drive when item.Payload is string drive:
                _store.DeleteFreeSpace(drive);
                break;
            case LearnedKind.QuietHours:
                _store.DeleteIdle();
                break;
        }
        _store.DeleteItemState(item.Id);
    });

    /// <summary>"Edit" on a suggested rule: its name and destination folder.</summary>
    public void EditRule(LearnedItem item, string? name, string? destination) => Safe(() =>
    {
        var s = Current().State(item.Id);
        _store.SetItemState((s ?? new LearnedItemState { ItemId = item.Id }) with
        {
            EditedName = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            EditedDestination = string.IsNullOrWhiteSpace(destination) ? null : destination.Trim(),
            Utc = _clock.UtcNow,
        });
    });

    /// <summary>The suggestion behind a rule card (with the user's edits).</summary>
    public SuggestedRule? RuleOf(LearnedItem item)
        => item.Payload is RuleCandidate c ? Suggestion(c, Current()) : null;

    /// <summary>"Make it a rule": hands the rule to Organize. Only after the user clicked it.</summary>
    public RuleHandOff MakeRule(LearnedItem item, IOrganizeRuleSink sink)
    {
        if (RuleOf(item) is not { } rule) return new RuleHandOff(false, "This suggestion is no longer available.", null);
        var result = sink.Add(rule);
        if (result.Added)
            Safe(() =>
            {
                var s = Current().State(item.Id);
                _store.SetItemState((s ?? new LearnedItemState { ItemId = item.Id }) with { MadeRule = true, Utc = _clock.UtcNow });
            });
        return result;
    }

    public void SetPaused(bool paused) => Safe(() => _store.Paused = paused);

    public bool ReadWindowsRecent
    {
        get => Safe(() => _store.ReadWindowsRecent, false);
        set => Safe(() => _store.ReadWindowsRecent = value);
    }

    /// <summary>Learning since (or since the last "Forget everything").</summary>
    public DateTime StartedUtc => Safe(() => _store.StartedUtc, _clock.UtcNow);

    /// <summary>"Forget everything": deletes the learning file and starts an empty one.</summary>
    public void ForgetEverything()
    {
        _store.ForgetEverything();
        Problem = null;
        Invalidate();
    }

    // ------------------------------------------------------------------ snapshot

    private sealed record SkipInfo(SkipVerdict Verdict, string? Title, IReadOnlyList<string> Examples, DateTime FirstUtc, int Accepts, int Rejects, int Undos);

    private sealed class Snapshot
    {
        public static readonly Snapshot Empty = new();
        public Dictionary<(string, string), SkipInfo> Skips { get; init; } = new();
        public IReadOnlyList<RuleCandidate> Rules { get; init; } = Array.Empty<RuleCandidate>();
        public IReadOnlyList<FrequentFolder> Folders { get; init; } = Array.Empty<FrequentFolder>();
        public UnusedAppsReport Apps { get; init; } = new(Array.Empty<UnusedApp>(), 0, (int)LearningRules.MinAppWatch.TotalDays);
        public IReadOnlyList<DriveForecast> Drives { get; init; } = Array.Empty<DriveForecast>();
        public QuietHours Quiet { get; init; } = new(Array.Empty<int>(), 0, "Not sure yet when your PC is usually free.");
        public IReadOnlyDictionary<string, LearnedItemState> States { get; init; } = new Dictionary<string, LearnedItemState>();
        public LearnedItemState? State(string id) => States.GetValueOrDefault(id);
    }

    private Snapshot Current()
    {
        lock (_gate)
        {
            if (_snapshot is not null) return _snapshot;
        }
        var built = Safe(Build, Snapshot.Empty);
        lock (_gate) return _snapshot ??= built;
    }

    private Snapshot Build()
    {
        var now = _clock.UtcNow;
        var decisions = _store.Decisions();
        var totals = _store.DecisionTotals().ToDictionary(t => (t.FeatureId, t.GroupId));
        var skips = new Dictionary<(string, string), SkipInfo>();
        foreach (var key in decisions.Select(d => (d.FeatureId, d.GroupId)).Concat(totals.Keys).Distinct())
        {
            var raw = decisions.Where(d => d.FeatureId == key.Item1 && d.GroupId == key.Item2).ToList(); // newest first
            var t = totals.GetValueOrDefault(key);
            var verdict = SkipCounter.Evaluate(raw.Select(d => d.Decision), t?.RecentVotes);
            var examples = raw.Select(d => d.Example).OfType<string>().Concat(RuleMiner.SplitExamples(t?.Examples))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();
            var first = raw.Select(d => d.Utc).Concat(t is null ? Array.Empty<DateTime>() : new[] { t.FirstUtc }).Min();
            skips[key] = new SkipInfo(verdict, raw.Select(d => d.GroupTitle).FirstOrDefault(x => x is not null) ?? t?.GroupTitle, examples, first,
                raw.Count(d => d.Decision == LearningDecision.Accept) + (t?.Accepts ?? 0),
                raw.Count(d => d.Decision == LearningDecision.Reject) + (t?.Rejects ?? 0),
                raw.Count(d => d.Decision == LearningDecision.Undo) + (t?.Undos ?? 0));
        }

        var samples = _store.FreeSpace();
        var marks = _store.RunMarks();
        var drives = samples.GroupBy(s => s.Drive, StringComparer.OrdinalIgnoreCase)
            .Select(g => DriveTrend.Evaluate(g.Key, g, marks, now)).OrderBy(d => d.Drive, StringComparer.OrdinalIgnoreCase).ToList();

        return new Snapshot
        {
            Skips = skips,
            Rules = RuleMiner.Mine(_store.Moves(), _store.MoveTotals()),
            Folders = Frecency.Rank(_store.FolderVisits(), _store.FolderTotals(), now, 50),
            Apps = WithoutStartupApps(Learners.UnusedApps.Evaluate(_store.Apps(), _store.StartedUtc, now)),
            Drives = drives,
            Quiet = QuietHoursLearner.Evaluate(_store.IdleHours()),
            States = _store.ItemStates(),
        };
    }

    /// <summary>
    /// An app that starts with Windows (Run key, Startup folder) is used without ever being opened from Start, so
    /// Windows' launch record can't tell; it's left out rather than called unused.
    /// </summary>
    private UnusedAppsReport WithoutStartupApps(UnusedAppsReport report)
    {
        if (_sources.Startup is null || report.Apps.Count == 0) return report;
        var (paths, names) = _sources.Startup.StartupPrograms();
        var kept = report.Apps.Where(a => !paths.Contains(a.Key) && !names.Contains(a.Name)).ToList();
        return kept.Count == report.Apps.Count ? report : report with { Apps = kept };
    }

    private void Invalidate()
    {
        lock (_gate) _snapshot = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private SuggestedRule Suggestion(RuleCandidate c, Snapshot snap)
    {
        var state = snap.State(c.Key);
        return SuggestedRule.From(c, state is null ? null : new LearnedNames(state.EditedName, state.EditedDestination));
    }

    private bool IsOff(string id) => IsOff(id, Current());
    private static bool IsOff(string id, Snapshot snap) => snap.State(id)?.Off == true;

    public static string SkipId(string featureId, string groupId) => $"skip:{featureId}/{groupId}".ToLowerInvariant();

    private bool FolderExists(string folder)
    {
        try { return _sources.FolderExists(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    private static string Plural(double n, string word)
    {
        var count = Math.Round(n);
        return $"{count.ToString("0", CultureInfo.CurrentCulture)} {word}{(count == 1 ? "" : "s")}";
    }

    private static string Day(DateTime utc) => utc.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture);

    // A damaged or locked learning file must never break the section that asked: answer "nothing learned" instead.
    private T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch (Exception ex) when (IsStoreProblem(ex))
        {
            Problem = ProblemText(ex.Message);
            return fallback;
        }
    }

    private static string ProblemText(string why)
        => "PC Tidy couldn't read what it learned (" + why.TrimEnd('.') + "). \"Forget everything\" starts a fresh, empty file.";

    private void Safe(Action write) => Safe(() => { write(); return true; }, false);

    private void Step(Action work) => Safe(work);

    private static bool IsStoreProblem(Exception ex)
        => ex is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException;
}
