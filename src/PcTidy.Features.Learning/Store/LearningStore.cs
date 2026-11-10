using System.Globalization;
using Microsoft.Data.Sqlite;
using PcTidy.Features.Learning.Learners;
using PcTidy.Features.Learning.Model;

namespace PcTidy.Features.Learning.Store;

/// <summary>
/// PC Tidy's own learning database, <c>&lt;DataRoot&gt;\learning\learned.db</c> (SQLite). This is the ONLY class in the
/// Learning section that writes anything, and it only ever writes this file (v3 "own data" rule). It holds derived
/// facts only: decisions per feature group, paths PC Tidy itself moved, free-space readings, folder visits, app launch
/// counts. Never file contents, never secrets. Raw events are kept 90 days, then folded into totals
/// (<see cref="Maintain"/>). "Forget everything" deletes the file. Not encrypted (DPAPI was optional and is skipped:
/// the file lives in the user's own profile and holds no secrets).
/// Thread-safe: one gate around every operation. WAL journal with normal sync (few disk flushes, still crash-safe);
/// pooled connections are cleared before the file is deleted.
/// </summary>
public sealed class LearningStore
{
    public const string FolderName = "learning";
    public const string FileName = "learned.db";
    private const int SchemaVersion = 1;

    private readonly object _gate = new();
    private readonly ILearningClock _clock;
    private readonly string _connectionString;

    public LearningStore(string databasePath, ILearningClock? clock = null)
    {
        DatabasePath = Path.GetFullPath(databasePath);
        _clock = clock ?? SystemLearningClock.Instance;
        _connectionString = new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = true, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        try
        {
            lock (_gate) EnsureCreated();
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            // A damaged or unreadable file is never deleted silently: the page offers "Forget everything" instead.
            OpenProblem = ex.Message;
        }
    }

    /// <summary>Why the file couldn't be opened, or null. Every later call fails the same way until "Forget everything".</summary>
    public string? OpenProblem { get; private set; }

    /// <summary>The store in PC Tidy's data folder.</summary>
    public static LearningStore Open(string dataRoot, ILearningClock? clock = null) => new(PathFor(dataRoot), clock);

    public static string PathFor(string dataRoot) => Path.Combine(dataRoot, FolderName, FileName);

    public string DatabasePath { get; }

    /// <summary>Raised after every change (the page and the advisor's cache listen).</summary>
    public event EventHandler? Changed;

    // ------------------------------------------------------------------ settings kept with the data

    /// <summary>When learning started (or restarted after "Forget everything"). Runs before this are never mined.</summary>
    public DateTime StartedUtc => ReadMetaDate("started_utc") ?? _clock.UtcNow;

    /// <summary>"Pause learning": nothing new is recorded; what was learned still applies.</summary>
    public bool Paused
    {
        get => ReadMeta("paused") == "1";
        set => SetMeta("paused", value ? "1" : "0");
    }

    /// <summary>Optional, off by default: also learn folders from Windows' Recent items list (read-only).</summary>
    public bool ReadWindowsRecent
    {
        get => ReadMeta("read_recent") == "1";
        set => SetMeta("read_recent", value ? "1" : "0");
    }

    // ------------------------------------------------------------------ decisions (skip counter)

    public void AddDecision(DecisionEvent e) => Run((c, tx) => Exec(c, tx,
        "INSERT INTO decisions(feature, grp, decision, utc, run_id, example, title) VALUES($f, $g, $d, $u, $r, $e, $t)",
        ("$f", e.FeatureId), ("$g", e.GroupId), ("$d", (int)e.Decision), ("$u", Ticks(e.Utc)), ("$r", e.RunId), ("$e", Trim(e.Example, 200)), ("$t", Trim(e.GroupTitle, 200))));

    /// <summary>Raw decisions, newest first (optionally for one group).</summary>
    public IReadOnlyList<DecisionEvent> Decisions(string? featureId = null, string? groupId = null)
        => Query("SELECT feature, grp, decision, utc, run_id, example, title FROM decisions WHERE ($f IS NULL OR feature = $f) AND ($g IS NULL OR grp = $g) ORDER BY utc DESC, id DESC",
            r => new DecisionEvent(r.GetString(0), r.GetString(1), (LearningDecision)r.GetInt32(2), Date(r, 3), NullableString(r, 4), NullableString(r, 5), NullableString(r, 6)),
            ("$f", featureId), ("$g", groupId));

    public IReadOnlyList<DecisionTotals> DecisionTotals()
        => Query("SELECT feature, grp, accepts, rejects, undos, first_utc, last_utc, recent, examples, title FROM decision_totals",
            r => new DecisionTotals(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), Date(r, 5), Date(r, 6), r.GetString(7), r.GetString(8), NullableString(r, 9)));

    /// <summary>Groups the user accepted in a run (to record an undo against them).</summary>
    public IReadOnlyList<(string FeatureId, string GroupId)> AcceptedInRun(string runId)
        => Query("SELECT DISTINCT feature, grp FROM decisions WHERE run_id = $r AND decision = 0",
            r => (r.GetString(0), r.GetString(1)), ("$r", runId));

    /// <summary>Forgets every decision about one group (raw and totals).</summary>
    public void DeleteDecisions(string featureId, string groupId) => Run((c, tx) =>
    {
        Exec(c, tx, "DELETE FROM decisions WHERE feature = $f AND grp = $g", ("$f", featureId), ("$g", groupId));
        Exec(c, tx, "DELETE FROM decision_totals WHERE feature = $f AND grp = $g", ("$f", featureId), ("$g", groupId));
    });

    // ------------------------------------------------------------------ moves (rule suggestions) and mined runs

    public bool IsRunMined(string runId)
        => Query("SELECT 1 FROM mined_runs WHERE run_id = $r", r => true, ("$r", runId)).Count > 0;

    /// <summary>Saves the moves and visits found in one finished run and marks the run as mined (all or nothing).</summary>
    public void AddMinedRun(RunMark mark, IEnumerable<MoveEvent> moves, IEnumerable<FolderVisit> visits) => Run((c, tx) =>
    {
        Exec(c, tx, "INSERT OR IGNORE INTO mined_runs(run_id, utc, drives) VALUES($r, $u, $d)",
            ("$r", mark.RunId), ("$u", Ticks(mark.Utc)), ("$d", string.Join("|", mark.Drives)));
        foreach (var m in moves)
            Exec(c, tx, "INSERT INTO moves(run_id, utc, source, final, size, host) VALUES($r, $u, $s, $f, $z, $h)",
                ("$r", m.RunId), ("$u", Ticks(m.Utc)), ("$s", m.SourcePath), ("$f", m.FinalPath), ("$z", m.Size), ("$h", m.Host));
        foreach (var v in visits) InsertVisit(c, tx, v);
    });

    public IReadOnlyList<MoveEvent> Moves()
        => Query("SELECT id, run_id, utc, source, final, size, host FROM moves ORDER BY utc",
            r => new MoveEvent(r.GetString(1), Date(r, 2), r.GetString(3), r.GetString(4), r.GetInt64(5), NullableString(r, 6)) { Id = r.GetInt64(0) });

    public IReadOnlyList<MoveTotals> MoveTotals()
        => Query("SELECT feature, value, dest, weight, days, first_utc, last_utc, examples, common_source FROM move_totals",
            r => new MoveTotals((MoveFeature)r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetDouble(3), r.GetInt32(4), Date(r, 5), Date(r, 6), r.GetString(7), NullableString(r, 8)));

    /// <summary>Run marks (for the drive trend), newest first.</summary>
    public IReadOnlyList<RunMark> RunMarks()
        => Query("SELECT run_id, utc, drives FROM mined_runs ORDER BY utc DESC",
            r => new RunMark(r.GetString(0), Date(r, 1), r.GetString(2).Split('|', StringSplitOptions.RemoveEmptyEntries)));

    /// <summary>A run was undone: its moves never happened as far as learning goes.</summary>
    public void DeleteMovesOfRun(string runId) => Run((c, tx) => Exec(c, tx, "DELETE FROM moves WHERE run_id = $r", ("$r", runId)));

    /// <summary>Forgets the evidence behind one suggested rule.</summary>
    public void DeleteRuleEvidence(IEnumerable<long> moveIds, MoveFeature feature, string value, string destination) => Run((c, tx) =>
    {
        foreach (var id in moveIds) Exec(c, tx, "DELETE FROM moves WHERE id = $i", ("$i", id));
        Exec(c, tx, "DELETE FROM move_totals WHERE feature = $f AND value = $v AND dest = $d", ("$f", (int)feature), ("$v", value), ("$d", destination));
    });

    // ------------------------------------------------------------------ free space (drive fill trend)

    /// <summary>One reading per drive per day: a later reading the same day replaces the earlier one.</summary>
    public void AddFreeSpace(FreeSpaceSample s) => Run((c, tx) => Exec(c, tx,
        "INSERT OR REPLACE INTO free_space(drive, day, utc, total, free) VALUES($d, $day, $u, $t, $f)",
        ("$d", s.Drive), ("$day", DayNumber(s.Utc)), ("$u", Ticks(s.Utc)), ("$t", s.TotalBytes), ("$f", s.FreeBytes)));

    public IReadOnlyList<FreeSpaceSample> FreeSpace(string? drive = null)
        => Query("SELECT drive, utc, total, free FROM free_space WHERE ($d IS NULL OR drive = $d) ORDER BY utc",
            r => new FreeSpaceSample(r.GetString(0), Date(r, 1), r.GetInt64(2), r.GetInt64(3)), ("$d", drive));

    /// <summary>Monthly averages of readings older than 90 days.</summary>
    public IReadOnlyList<(string Drive, string Month, double AvgFree, int Samples)> FreeSpaceMonths()
        => Query("SELECT drive, month, avg_free, samples FROM free_space_months ORDER BY drive, month",
            r => (r.GetString(0), r.GetString(1), r.GetDouble(2), r.GetInt32(3)));

    public void DeleteFreeSpace(string drive) => Run((c, tx) =>
    {
        Exec(c, tx, "DELETE FROM free_space WHERE drive = $d", ("$d", drive));
        Exec(c, tx, "DELETE FROM free_space_months WHERE drive = $d", ("$d", drive));
    });

    // ------------------------------------------------------------------ folder visits (frecency)

    public void AddFolderVisit(FolderVisit v) => Run((c, tx) => InsertVisit(c, tx, v));

    public void AddFolderVisits(IEnumerable<FolderVisit> visits) => Run((c, tx) =>
    {
        foreach (var v in visits) InsertVisit(c, tx, v);
    });

    public IReadOnlyList<FolderVisit> FolderVisits()
        => Query("SELECT folder, utc, source FROM folder_visits ORDER BY utc",
            r => new FolderVisit(r.GetString(0), Date(r, 1), (VisitSource)r.GetInt32(2)));

    public IReadOnlyList<FolderTotals> FolderTotals()
        => Query("SELECT folder, score, score_utc, count, first_utc, last_utc FROM folder_totals",
            r => new FolderTotals(r.GetString(0), r.GetDouble(1), Date(r, 2), r.GetInt32(3), Date(r, 4), Date(r, 5)));

    public void DeleteFolder(string folder) => Run((c, tx) =>
    {
        Exec(c, tx, "DELETE FROM folder_visits WHERE folder = $f COLLATE NOCASE", ("$f", folder));
        Exec(c, tx, "DELETE FROM folder_totals WHERE folder = $f", ("$f", folder));
    });

    /// <summary>The newest Windows Recent visit already recorded (so the same Recent entries aren't counted twice).</summary>
    public DateTime? LatestVisit(VisitSource source)
        => Query("SELECT MAX(utc) FROM folder_visits WHERE source = $s", r => r.IsDBNull(0) ? (DateTime?)null : Date(r, 0), ("$s", (int)source)).FirstOrDefault();

    // ------------------------------------------------------------------ apps (unused apps)

    public void SaveApps(IEnumerable<AppObservation> observations) => Run((c, tx) =>
    {
        foreach (var o in observations)
            Exec(c, tx, "INSERT OR REPLACE INTO apps(key, name, source, first_seen, last_count, last_run, last_change) VALUES($k, $n, $s, $f, $c, $r, $ch)",
                ("$k", o.Key), ("$n", o.Name), ("$s", (int)o.Source), ("$f", Ticks(o.FirstSeenUtc)), ("$c", o.LastCount),
                ("$r", Ticks(o.LastRunUtc)), ("$ch", Ticks(o.LastCountChangeUtc)));
    });

    public IReadOnlyList<AppObservation> Apps()
        => Query("SELECT key, name, source, first_seen, last_count, last_run, last_change FROM apps",
            r => new AppObservation(r.GetString(0), r.GetString(1), (AppSource)r.GetInt32(2), Date(r, 3), r.GetInt32(4), NullableDate(r, 5), NullableDate(r, 6)));

    public void DeleteApp(string key) => Run((c, tx) => Exec(c, tx, "DELETE FROM apps WHERE key = $k", ("$k", key)));

    // ------------------------------------------------------------------ idle checks (quiet hours)

    public void AddIdleSample(int hour, bool idle) => Run((c, tx) => Exec(c, tx,
        "INSERT INTO idle_hours(hour, idle, total) VALUES($h, $i, 1) ON CONFLICT(hour) DO UPDATE SET idle = idle + $i, total = total + 1",
        ("$h", hour), ("$i", idle ? 1 : 0)));

    public IReadOnlyList<IdleHourCounts> IdleHours()
        => Query("SELECT hour, idle, total FROM idle_hours ORDER BY hour", r => new IdleHourCounts(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2)));

    public void DeleteIdle() => Run((c, tx) => Exec(c, tx, "DELETE FROM idle_hours"));

    // ------------------------------------------------------------------ per item state (Turn off / Edit / made a rule)

    public IReadOnlyDictionary<string, LearnedItemState> ItemStates()
        => Query("SELECT item_id, off, dismissed, made_rule, edited_name, edited_dest, utc FROM item_state",
            r => new LearnedItemState
            {
                ItemId = r.GetString(0), Off = r.GetInt32(1) != 0, Dismissed = r.GetInt32(2) != 0, MadeRule = r.GetInt32(3) != 0,
                EditedName = NullableString(r, 4), EditedDestination = NullableString(r, 5), Utc = Date(r, 6),
            }).ToDictionary(s => s.ItemId, StringComparer.OrdinalIgnoreCase);

    public void SetItemState(LearnedItemState s) => Run((c, tx) => Exec(c, tx,
        "INSERT OR REPLACE INTO item_state(item_id, off, dismissed, made_rule, edited_name, edited_dest, utc) VALUES($i, $o, $d, $m, $n, $e, $u)",
        ("$i", s.ItemId), ("$o", s.Off ? 1 : 0), ("$d", s.Dismissed ? 1 : 0), ("$m", s.MadeRule ? 1 : 0),
        ("$n", Trim(s.EditedName, 200)), ("$e", s.EditedDestination), ("$u", Ticks(s.Utc == default ? _clock.UtcNow : s.Utc))));

    public void DeleteItemState(string itemId) => Run((c, tx) => Exec(c, tx, "DELETE FROM item_state WHERE item_id = $i", ("$i", itemId)));

    // ------------------------------------------------------------------ retention

    /// <summary>What <see cref="Maintain"/> folded into totals.</summary>
    public sealed record MaintenanceReport(int Decisions, int Moves, int FreeSpaceSamples, int FolderVisits);

    /// <summary>
    /// Folds raw events older than 90 days into totals and deletes them (one transaction). Totals keep exactly what
    /// the learners need: decision counts + the newest votes, weighted move totals per feature value and destination,
    /// a fading score per folder, monthly free-space averages.
    /// </summary>
    public MaintenanceReport Maintain()
    {
        var now = _clock.UtcNow;
        var cutoff = Ticks(now - LearningRules.RawRetention);
        int decisions = 0, moves = 0, samples = 0, visits = 0;
        Run((c, tx) =>
        {
            decisions = FoldDecisions(c, tx, cutoff);
            moves = FoldMoves(c, tx, cutoff);
            samples = FoldFreeSpace(c, tx, cutoff);
            visits = FoldVisits(c, tx, cutoff, now);
        });
        return new MaintenanceReport(decisions, moves, samples, visits);
    }

    private int FoldDecisions(SqliteConnection c, SqliteTransaction tx, long cutoff)
    {
        var old = Query(c, tx, "SELECT feature, grp, decision, utc, example, title FROM decisions WHERE utc < $c ORDER BY utc DESC, id DESC",
            r => (F: r.GetString(0), G: r.GetString(1), D: (LearningDecision)r.GetInt32(2), U: Date(r, 3), E: NullableString(r, 4), T: NullableString(r, 5)), ("$c", cutoff));
        foreach (var group in old.GroupBy(o => (o.F, o.G)))
        {
            var existing = Query(c, tx, "SELECT accepts, rejects, undos, first_utc, last_utc, recent, examples, title FROM decision_totals WHERE feature = $f AND grp = $g",
                r => (A: r.GetInt32(0), R: r.GetInt32(1), U: r.GetInt32(2), First: Date(r, 3), Last: Date(r, 4), Recent: r.GetString(5), Ex: r.GetString(6), T: NullableString(r, 7)),
                ("$f", group.Key.F), ("$g", group.Key.G)).Select(x => ((int A, int R, int U, DateTime First, DateTime Last, string Recent, string Ex, string? T)?)x).FirstOrDefault();
            var newestFirst = group.ToList();
            var recent = SkipCounter.KeepRecent(SkipCounter.Votes(newestFirst.Select(d => d.D)), existing?.Recent ?? "");
            var examples = string.Join("|", newestFirst.Select(d => d.E).OfType<string>().Concat(RuleMiner.SplitExamples(existing?.Ex))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(3));
            var first = newestFirst.Min(d => d.U);
            var last = newestFirst.Max(d => d.U);
            var title = newestFirst.Select(d => d.T).FirstOrDefault(t => t is not null) ?? existing?.T;
            Exec(c, tx, "INSERT OR REPLACE INTO decision_totals(feature, grp, accepts, rejects, undos, first_utc, last_utc, recent, examples, title) VALUES($f, $g, $a, $r, $u, $fi, $la, $re, $ex, $ti)",
                ("$f", group.Key.F), ("$g", group.Key.G),
                ("$a", (existing?.A ?? 0) + newestFirst.Count(d => d.D == LearningDecision.Accept)),
                ("$r", (existing?.R ?? 0) + newestFirst.Count(d => d.D == LearningDecision.Reject)),
                ("$u", (existing?.U ?? 0) + newestFirst.Count(d => d.D == LearningDecision.Undo)),
                ("$fi", Ticks(existing is { } e1 && e1.First < first ? e1.First : first)),
                // Older events can't be newer than what totals already hold, but keep the max for safety.
                ("$la", Ticks(existing is { } e2 && e2.Last > last ? e2.Last : last)),
                ("$re", recent), ("$ex", examples), ("$ti", title));
        }
        Exec(c, tx, "DELETE FROM decisions WHERE utc < $c", ("$c", cutoff));
        return old.Count;
    }

    private int FoldMoves(SqliteConnection c, SqliteTransaction tx, long cutoff)
    {
        var old = Query(c, tx, "SELECT id, run_id, utc, source, final, size, host FROM moves WHERE utc < $c",
            r => new MoveEvent(r.GetString(1), Date(r, 2), r.GetString(3), r.GetString(4), r.GetInt64(5), NullableString(r, 6)) { Id = r.GetInt64(0) }, ("$c", cutoff));
        foreach (var t in RuleMiner.Fold(old))
        {
            var existing = Query(c, tx, "SELECT weight, days, first_utc, last_utc, examples, common_source FROM move_totals WHERE feature = $f AND value = $v AND dest = $d",
                r => new MoveTotals(t.Feature, t.Value, t.Destination, r.GetDouble(0), r.GetInt32(1), Date(r, 2), Date(r, 3), r.GetString(4), NullableString(r, 5)),
                ("$f", (int)t.Feature), ("$v", t.Value), ("$d", t.Destination)).FirstOrDefault();
            var merged = existing is null ? t : t with
            {
                Weight = t.Weight + existing.Weight,
                Days = t.Days + existing.Days,
                FirstUtc = t.FirstUtc < existing.FirstUtc ? t.FirstUtc : existing.FirstUtc,
                LastUtc = t.LastUtc > existing.LastUtc ? t.LastUtc : existing.LastUtc,
                Examples = string.Join("|", RuleMiner.SplitExamples(t.Examples).Concat(RuleMiner.SplitExamples(existing.Examples)).Distinct(StringComparer.OrdinalIgnoreCase).Take(3)),
                CommonSource = string.Equals(t.CommonSource, existing.CommonSource, StringComparison.OrdinalIgnoreCase) ? t.CommonSource : null,
            };
            Exec(c, tx, "INSERT OR REPLACE INTO move_totals(feature, value, dest, weight, days, first_utc, last_utc, examples, common_source) VALUES($f, $v, $d, $w, $n, $fi, $la, $ex, $cs)",
                ("$f", (int)merged.Feature), ("$v", merged.Value), ("$d", merged.Destination), ("$w", merged.Weight), ("$n", merged.Days),
                ("$fi", Ticks(merged.FirstUtc)), ("$la", Ticks(merged.LastUtc)), ("$ex", merged.Examples), ("$cs", merged.CommonSource));
        }
        Exec(c, tx, "DELETE FROM moves WHERE utc < $c", ("$c", cutoff));
        return old.Count;
    }

    private int FoldFreeSpace(SqliteConnection c, SqliteTransaction tx, long cutoff)
    {
        var old = Query(c, tx, "SELECT drive, utc, free FROM free_space WHERE utc < $c",
            r => (Drive: r.GetString(0), Utc: Date(r, 1), Free: r.GetInt64(2)), ("$c", cutoff));
        foreach (var g in old.GroupBy(o => (Drive: o.Drive.ToUpperInvariant(), Month: o.Utc.ToString("yyyy-MM", CultureInfo.InvariantCulture))))
        {
            var drive = g.First().Drive;
            var existing = Query(c, tx, "SELECT avg_free, samples FROM free_space_months WHERE drive = $d AND month = $m",
                r => (Avg: r.GetDouble(0), N: r.GetInt32(1)), ("$d", drive), ("$m", g.Key.Month)).Select(x => ((double Avg, int N)?)x).FirstOrDefault();
            int n = g.Count() + (existing?.N ?? 0);
            double avg = (g.Sum(o => (double)o.Free) + (existing is { } e ? e.Avg * e.N : 0)) / n;
            Exec(c, tx, "INSERT OR REPLACE INTO free_space_months(drive, month, avg_free, samples) VALUES($d, $m, $a, $n)",
                ("$d", drive), ("$m", g.Key.Month), ("$a", avg), ("$n", n));
        }
        Exec(c, tx, "DELETE FROM free_space WHERE utc < $c", ("$c", cutoff));
        return old.Count;
    }

    private int FoldVisits(SqliteConnection c, SqliteTransaction tx, long cutoff, DateTime now)
    {
        var old = Query(c, tx, "SELECT folder, utc, source FROM folder_visits WHERE utc < $c",
            r => new FolderVisit(r.GetString(0), Date(r, 1), (VisitSource)r.GetInt32(2)), ("$c", cutoff));
        foreach (var g in old.GroupBy(v => v.Folder, StringComparer.OrdinalIgnoreCase))
        {
            var existing = Query(c, tx, "SELECT folder, score, score_utc, count, first_utc, last_utc FROM folder_totals WHERE folder = $f",
                r => new FolderTotals(r.GetString(0), r.GetDouble(1), Date(r, 2), r.GetInt32(3), Date(r, 4), Date(r, 5)), ("$f", g.Key)).FirstOrDefault();
            var t = Frecency.Fold(existing?.Folder ?? g.Key, g, existing, now);
            Exec(c, tx, "INSERT OR REPLACE INTO folder_totals(folder, score, score_utc, count, first_utc, last_utc) VALUES($f, $s, $su, $n, $fi, $la)",
                ("$f", t.Folder), ("$s", t.Score), ("$su", Ticks(t.ScoreUtc)), ("$n", t.Count), ("$fi", Ticks(t.FirstUtc)), ("$la", Ticks(t.LastUtc)));
        }
        Exec(c, tx, "DELETE FROM folder_visits WHERE utc < $c", ("$c", cutoff));
        return old.Count;
    }

    // ------------------------------------------------------------------ forget everything

    /// <summary>
    /// Deletes the learning database file itself (PC Tidy's own data only) and starts a fresh, empty one. Runs from
    /// before this moment are never mined again.
    /// </summary>
    public void ForgetEverything()
    {
        lock (_gate)
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-journal", "-wal", "-shm" })
            {
                var file = DatabasePath + suffix;
                if (File.Exists(file)) File.Delete(file); // boundary-ok: own data in DataRoot (the learning database file)
            }
            EnsureCreated();
            OpenProblem = null;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ plumbing

    private void EnsureCreated()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!); // boundary-ok: own data in DataRoot (learning folder)
        using var c = Connect();
        Exec(c, null, "PRAGMA journal_mode=WAL;"); // must be outside a transaction; remembered in the file
        using var tx = c.BeginTransaction();
        Exec(c, tx, Schema);
        if (ReadMeta(c, tx, "started_utc") is null)
        {
            SetMeta(c, tx, "started_utc", Ticks(_clock.UtcNow).ToString(CultureInfo.InvariantCulture));
            SetMeta(c, tx, "schema_version", SchemaVersion.ToString(CultureInfo.InvariantCulture));
        }
        tx.Commit();
    }

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS decisions(id INTEGER PRIMARY KEY, feature TEXT NOT NULL, grp TEXT NOT NULL, decision INTEGER NOT NULL, utc INTEGER NOT NULL, run_id TEXT, example TEXT, title TEXT);
        CREATE INDEX IF NOT EXISTS ix_decisions_key ON decisions(feature, grp, utc);
        CREATE INDEX IF NOT EXISTS ix_decisions_run ON decisions(run_id);
        CREATE TABLE IF NOT EXISTS decision_totals(feature TEXT NOT NULL, grp TEXT NOT NULL, accepts INTEGER NOT NULL, rejects INTEGER NOT NULL, undos INTEGER NOT NULL,
            first_utc INTEGER NOT NULL, last_utc INTEGER NOT NULL, recent TEXT NOT NULL, examples TEXT NOT NULL, title TEXT, PRIMARY KEY(feature, grp));
        CREATE TABLE IF NOT EXISTS moves(id INTEGER PRIMARY KEY, run_id TEXT NOT NULL, utc INTEGER NOT NULL, source TEXT NOT NULL, final TEXT NOT NULL, size INTEGER NOT NULL, host TEXT);
        CREATE INDEX IF NOT EXISTS ix_moves_run ON moves(run_id);
        CREATE TABLE IF NOT EXISTS move_totals(feature INTEGER NOT NULL, value TEXT NOT NULL COLLATE NOCASE, dest TEXT NOT NULL COLLATE NOCASE, weight REAL NOT NULL,
            days INTEGER NOT NULL, first_utc INTEGER NOT NULL, last_utc INTEGER NOT NULL, examples TEXT NOT NULL, common_source TEXT, PRIMARY KEY(feature, value, dest));
        CREATE TABLE IF NOT EXISTS mined_runs(run_id TEXT PRIMARY KEY, utc INTEGER NOT NULL, drives TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS free_space(drive TEXT NOT NULL COLLATE NOCASE, day INTEGER NOT NULL, utc INTEGER NOT NULL, total INTEGER NOT NULL, free INTEGER NOT NULL, PRIMARY KEY(drive, day));
        CREATE TABLE IF NOT EXISTS free_space_months(drive TEXT NOT NULL COLLATE NOCASE, month TEXT NOT NULL, avg_free REAL NOT NULL, samples INTEGER NOT NULL, PRIMARY KEY(drive, month));
        CREATE TABLE IF NOT EXISTS folder_visits(id INTEGER PRIMARY KEY, folder TEXT NOT NULL, utc INTEGER NOT NULL, source INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_visits_utc ON folder_visits(utc);
        CREATE TABLE IF NOT EXISTS folder_totals(folder TEXT PRIMARY KEY COLLATE NOCASE, score REAL NOT NULL, score_utc INTEGER NOT NULL, count INTEGER NOT NULL, first_utc INTEGER NOT NULL, last_utc INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS apps(key TEXT PRIMARY KEY COLLATE NOCASE, name TEXT NOT NULL, source INTEGER NOT NULL, first_seen INTEGER NOT NULL, last_count INTEGER NOT NULL, last_run INTEGER, last_change INTEGER);
        CREATE TABLE IF NOT EXISTS idle_hours(hour INTEGER PRIMARY KEY, idle INTEGER NOT NULL, total INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS item_state(item_id TEXT PRIMARY KEY COLLATE NOCASE, off INTEGER NOT NULL, dismissed INTEGER NOT NULL, made_rule INTEGER NOT NULL, edited_name TEXT, edited_dest TEXT, utc INTEGER NOT NULL);
        """;

    private SqliteConnection Connect()
    {
        var c = new SqliteConnection(_connectionString);
        try
        {
            c.Open();
            Exec(c, null, "PRAGMA synchronous=NORMAL;");
            return c;
        }
        catch
        {
            c.Dispose(); // a damaged file fails here: never leave a handle open on it
            throw;
        }
    }

    /// <summary>Runs a write in one transaction under the gate, then raises <see cref="Changed"/>.</summary>
    private void Run(Action<SqliteConnection, SqliteTransaction> work)
    {
        lock (_gate)
        {
            if (!File.Exists(DatabasePath)) EnsureCreated(); // deleted from outside: start fresh rather than fail
            using var c = Connect();
            using var tx = c.BeginTransaction();
            work(c, tx);
            tx.Commit();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The single place SQL that changes the database is executed.</summary>
    private static void Exec(SqliteConnection c, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(c, tx, sql, args);
        cmd.ExecuteNonQuery(); // boundary-ok: own data in DataRoot (learned.db)
    }

    private IReadOnlyList<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] args)
    {
        lock (_gate)
        {
            if (!File.Exists(DatabasePath)) EnsureCreated();
            using var c = Connect();
            return Query(c, null, sql, map, args);
        }
    }

    private static List<T> Query<T>(SqliteConnection c, SqliteTransaction? tx, string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(c, tx, sql, args);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }

    private static SqliteCommand Command(SqliteConnection c, SqliteTransaction? tx, string sql, (string Name, object? Value)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private void InsertVisit(SqliteConnection c, SqliteTransaction tx, FolderVisit v) => Exec(c, tx,
        "INSERT INTO folder_visits(folder, utc, source) VALUES($f, $u, $s)", ("$f", v.Folder), ("$u", Ticks(v.Utc)), ("$s", (int)v.Source));

    private string? ReadMeta(string key)
        => Query("SELECT value FROM meta WHERE key = $k", r => r.GetString(0), ("$k", key)).FirstOrDefault();

    private static string? ReadMeta(SqliteConnection c, SqliteTransaction tx, string key)
        => Query(c, tx, "SELECT value FROM meta WHERE key = $k", r => r.GetString(0), ("$k", key)).FirstOrDefault();

    private DateTime? ReadMetaDate(string key)
        => long.TryParse(ReadMeta(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) ? new DateTime(t, DateTimeKind.Utc) : null;

    private void SetMeta(string key, string value) => Run((c, tx) => SetMeta(c, tx, key, value));

    private static void SetMeta(SqliteConnection c, SqliteTransaction tx, string key, string value)
        => Exec(c, tx, "INSERT OR REPLACE INTO meta(key, value) VALUES($k, $v)", ("$k", key), ("$v", value));

    private static long Ticks(DateTime utc) => (utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : utc).Ticks;
    private static long? Ticks(DateTime? utc) => utc is { } u ? Ticks(u) : null;
    private static long DayNumber(DateTime utc) => DateOnly.FromDateTime(utc).DayNumber;
    private static DateTime Date(SqliteDataReader r, int i) => new(r.GetInt64(i), DateTimeKind.Utc);
    private static DateTime? NullableDate(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : Date(r, i);
    private static string? NullableString(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static string? Trim(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}
