using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Engine.IO;

namespace PcTidy.Engine;

/// <summary>
/// Extra journal operations the executor uses when the journal offers them (the real <see cref="RunJournal"/> does):
/// several step records in one write, made durable with ONE forced disk flush.
/// </summary>
internal interface IStepBatchJournal
{
    /// <summary>
    /// Appends the records in order. <paramref name="durable"/>: they (and every record before them) are on the disk
    /// when this returns. Otherwise they reach the OS at once (an app crash loses nothing) and the disk with the next
    /// durable write.
    /// </summary>
    Task SaveStepsAsync(string runId, IReadOnlyList<StepRecord> steps, bool durable, CancellationToken ct);
}

/// <summary>
/// Write-ahead run journal. One folder per run under <c>&lt;DataRoot&gt;\runs\&lt;runId&gt;\</c>:
/// <list type="bullet">
/// <item><c>run.json</c>: header (atomic write: temp file flushed to disk, then replaced).</item>
/// <item><c>steps.jsonl</c>: append-only step records.</item>
/// <item><c>run.lock</c>: held open while a process runs it, so a crash is recognisable as "Interrupted".</item>
/// </list>
/// Durability: a "Started" record (the intent: what a step is about to do, including its prepared targets) is on the
/// disk before the change it describes. The executor writes intents for a batch of steps at once and forces them to
/// the disk with one flush (<see cref="IStepBatchJournal"/>); results ("Done", "Skipped"...) only need to reach the OS,
/// because the next intent's flush, or the run's status change, takes them to the disk too. After a power cut every
/// step whose intent is on the disk but whose result isn't is "Started", and recovery asks its handler what happened.
/// Loading = header + replay of the step records newer than it (the last record per step wins).
/// A torn last line (power cut mid-write) is ignored.
/// </summary>
public sealed class RunJournal : IRunJournal, IStepBatchJournal, IDisposable
{
    private const string HeaderName = "run.json";
    private const string StepsName = "steps.jsonl";
    private const string LockName = "run.lock";

    private static readonly JsonSerializerOptions Json = new(AppSettings.Json) { WriteIndented = false };
    private static readonly JsonSerializerOptions JsonIndented = new(AppSettings.Json);
    private static readonly string? AppVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString();

    private readonly string _runsRoot;
    private readonly ConcurrentDictionary<string, RunState> _states = new(StringComparer.OrdinalIgnoreCase);

    public RunJournal(string dataRoot) => _runsRoot = DataPaths.Runs(dataRoot);

    private sealed class RunState
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public long Seq;
        public bool Loaded;
        public FileStream? Lock;
        /// <summary>steps.jsonl, kept open for appending while the run is being written (no reopen per record).</summary>
        public FileStream? Steps;
    }

    private sealed record Header(int Format, long Seq, RunRecord Run);
    private sealed record StepLine(long Seq, StepRecord Step);

    private string RunDir(string runId) => Path.Combine(_runsRoot, RunIds.Validate(runId));

    // ------------------------------------------------------------------ writes

    public async Task<RunRecord> CreateAsync(ActionPlan plan, ExecutionMode mode, CancellationToken ct)
    {
        var dir = RunDir(plan.RunId);
        if (Directory.Exists(dir)) throw new IOException($"A run with id {plan.RunId} already exists.");
        Directory.CreateDirectory(dir);
        var run = new RunRecord
        {
            RunId = plan.RunId,
            ParentRunId = plan.ParentRunId,
            FeatureId = plan.FeatureId,
            Title = plan.Title,
            Mode = mode,
            CreatedUtc = DateTime.UtcNow,
            // Simulations are never "Running": a crash during one must not look like a resumable real run.
            Status = mode == ExecutionMode.Real ? RunStatus.Running : RunStatus.Planned,
            Steps = plan.Items.Select((item, i) => new StepRecord { Index = i, Item = item }).ToList(),
            AppVersion = AppVersion,
        };
        var state = _states.GetOrAdd(plan.RunId, _ => new RunState());
        await state.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            state.Loaded = true;
            state.Seq = 0;
            state.Steps?.Dispose();
            state.Steps = new FileStream(Path.Combine(dir, StepsName), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1);
            WriteHeader(dir, run, 0);
            UpdateLock(state, dir, run.Status);
        }
        finally { state.Gate.Release(); }
        return run;
    }

    /// <summary>One record. A "Started" record is forced to the disk (write-ahead); others reach the OS at once.</summary>
    public Task SaveStepAsync(string runId, StepRecord step, CancellationToken ct)
        => SaveStepsAsync(runId, new[] { step }, durable: step.Status == StepStatus.Started, ct);

    public async Task SaveStepsAsync(string runId, IReadOnlyList<StepRecord> steps, bool durable, CancellationToken ct)
    {
        if (steps.Count == 0 && !durable) return;
        var dir = RunDir(runId);
        var state = await EnsureStateAsync(runId, dir, ct).ConfigureAwait(false);
        await state.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false); // a started step must always be recorded
        try
        {
            var sb = new StringBuilder(steps.Count * 512);
            foreach (var step in steps)
                sb.Append(JsonSerializer.Serialize(new StepLine(++state.Seq, step), Json)).Append('\n');
            var stream = StepsStream(state, dir);
            using (EngineTimings.Measure(durable ? "journal.write+flush" : "journal.write"))
            {
                if (sb.Length > 0) stream.Write(Encoding.UTF8.GetBytes(sb.ToString()));
                stream.Flush(flushToDisk: durable);
            }
        }
        finally { state.Gate.Release(); }
    }

    /// <summary>Saves the header. <paramref name="run"/> must carry the caller's latest step states (it supersedes earlier step records).</summary>
    public async Task SaveRunAsync(RunRecord run, CancellationToken ct)
    {
        var dir = RunDir(run.RunId);
        var state = await EnsureStateAsync(run.RunId, dir, ct).ConfigureAwait(false);
        await state.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            try
            {
                // Status changes are durability points: every step record so far goes to the disk first.
                using (EngineTimings.Measure("journal.header"))
                {
                    if (state.Steps is not null) state.Steps.Flush(flushToDisk: true);
                    else if (File.Exists(Path.Combine(dir, StepsName))) StepsStream(state, dir).Flush(flushToDisk: true);
                    WriteHeader(dir, run, state.Seq);
                }
            }
            finally
            {
                // Even if the header couldn't be written, a finished run lets go of its lock (it then reads as Interrupted).
                UpdateLock(state, dir, run.Status);
            }
            if (run.Status is not (RunStatus.Running or RunStatus.Planned))
            {
                state.Steps?.Dispose(); // finished for now: let go of the file
                state.Steps = null;
            }
        }
        finally { state.Gate.Release(); }
    }

    private static void WriteHeader(string dir, RunRecord run, long seq)
        => FsOps.WriteAllTextAtomic(Path.Combine(dir, HeaderName), JsonSerializer.Serialize(new Header(1, seq, run), JsonIndented), flushToDisk: true);

    /// <summary>The open append stream; on first open of an existing file a torn last record gets its line ended.</summary>
    private static FileStream StepsStream(RunState state, string dir)
    {
        if (state.Steps is not null) return state.Steps;
        var fs = new FileStream(Path.Combine(dir, StepsName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 1);
        try
        {
            if (fs.Length > 0)
            {
                fs.Seek(-1, SeekOrigin.End);
                var endsWithNewline = fs.ReadByte() == '\n';
                fs.Seek(0, SeekOrigin.End);
                if (!endsWithNewline) fs.WriteByte((byte)'\n');
            }
        }
        catch
        {
            fs.Dispose();
            throw;
        }
        state.Steps = fs;
        return fs;
    }

    private void UpdateLock(RunState state, string dir, RunStatus status)
    {
        if (status == RunStatus.Running)
        {
            state.Lock ??= new FileStream(Path.Combine(dir, LockName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        else if (state.Lock is not null)
        {
            state.Lock.Dispose();
            state.Lock = null;
        }
    }

    private async Task<RunState> EnsureStateAsync(string runId, string dir, CancellationToken ct)
    {
        var state = _states.GetOrAdd(runId, _ => new RunState());
        if (state.Loaded) return state;
        await state.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!state.Loaded)
            {
                if (!Directory.Exists(dir)) throw new DirectoryNotFoundException($"Run {runId} not found.");
                var (header, lines) = ReadFiles(dir);
                state.Seq = Math.Max(header?.Seq ?? 0, lines.Count > 0 ? lines.Max(l => l.Seq) : 0);
                state.Loaded = true;
            }
        }
        finally { state.Gate.Release(); }
        return state;
    }

    // ------------------------------------------------------------------ reads

    public Task<RunRecord?> LoadAsync(string runId, CancellationToken ct)
    {
        var dir = RunDir(runId);
        return Task.Run(() => Load(runId, dir), ct);
    }

    private RunRecord? Load(string runId, string dir)
    {
        if (!Directory.Exists(dir)) return null;
        var (header, lines) = ReadFiles(dir);
        if (header is null) return null;

        var steps = header.Run.Steps.ToArray();
        foreach (var l in lines.Where(l => l.Seq > header.Seq).OrderBy(l => l.Seq))
            if (l.Step.Index >= 0 && l.Step.Index < steps.Length) steps[l.Step.Index] = l.Step;

        var run = header.Run with { Steps = steps };
        if (run.Status == RunStatus.Running && !IsHeldByAProcess(runId, dir))
            run = run with { Status = RunStatus.Interrupted };
        return run;
    }

    /// <summary>Every run, newest first. Runs off the caller's thread (it reads every run's files).</summary>
    public Task<IReadOnlyList<RunRecord>> ListAsync(CancellationToken ct) => Task.Run<IReadOnlyList<RunRecord>>(() =>
    {
        var list = new List<RunRecord>();
        foreach (var (runId, dir) in RunDirs(ct))
        {
            var r = TryLoad(runId, dir);
            if (r is not null) list.Add(r);
        }
        return list.OrderByDescending(r => r.CreatedUtc).ThenByDescending(r => r.RunId, StringComparer.Ordinal).ToList();
    }, ct);

    /// <summary>
    /// Real runs left "Running" by a crash or power cut. Only the start of each header is read (status and mode come
    /// before the steps), so this stays quick with a long history; just the interrupted runs are loaded in full.
    /// </summary>
    public Task<IReadOnlyList<RunRecord>> FindUnfinishedAsync(CancellationToken ct) => Task.Run<IReadOnlyList<RunRecord>>(() =>
    {
        var list = new List<RunRecord>();
        foreach (var (runId, dir) in RunDirs(ct))
        {
            var (status, mode) = PeekHeader(Path.Combine(dir, HeaderName));
            if (mode != ExecutionMode.Real) continue;
            // Running without a live owner = crash; Interrupted = the engine stopped it on an unexpected error.
            if (!(status == RunStatus.Interrupted || (status == RunStatus.Running && !IsHeldByAProcess(runId, dir)))) continue;
            if (TryLoad(runId, dir) is { Status: RunStatus.Interrupted, Mode: ExecutionMode.Real } r) list.Add(r);
        }
        return list.OrderByDescending(r => r.CreatedUtc).ToList();
    }, ct);

    private IEnumerable<(string RunId, string Dir)> RunDirs(CancellationToken ct)
    {
        if (!Directory.Exists(_runsRoot)) yield break;
        List<string> dirs;
        try { dirs = Directory.EnumerateDirectories(_runsRoot).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var dir in dirs)
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(dir);
            bool valid;
            try { RunIds.Validate(name); valid = true; }
            catch (ArgumentException) { valid = false; }
            if (valid) yield return (name, dir);
        }
    }

    private RunRecord? TryLoad(string runId, string dir)
    {
        try { return Load(runId, dir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; } // unreadable run: skip
    }

    /// <summary>Status and mode from the start of run.json (stops reading at the step list).</summary>
    private static (RunStatus? Status, ExecutionMode? Mode) PeekHeader(string path)
    {
        try
        {
            if (!File.Exists(path)) return (null, null);
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
            var buffer = new byte[Math.Min(16 * 1024, (int)Math.Min(int.MaxValue, fs.Length))];
            var read = fs.Read(buffer, 0, buffer.Length);
            var reader = new Utf8JsonReader(buffer.AsSpan(0, read), isFinalBlock: read == fs.Length, state: default);
            RunStatus? status = null;
            ExecutionMode? mode = null;
            int depth = 0;
            while (reader.Read())
            {
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) depth++;
                else if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray) depth--;
                else if (reader.TokenType == JsonTokenType.PropertyName && depth == 2)
                {
                    var name = reader.GetString();
                    if (name == "Steps") break;
                    if (name is "Status" or "Mode" && reader.Read() && reader.TokenType == JsonTokenType.String)
                    {
                        var value = reader.GetString();
                        if (name == "Status" && Enum.TryParse<RunStatus>(value, out var s)) status = s;
                        if (name == "Mode" && Enum.TryParse<ExecutionMode>(value, out var m)) mode = m;
                    }
                }
                if (status is not null && mode is not null) break;
            }
            // Mode is left out of the file when it's the default (Real).
            return (status, mode ?? ExecutionMode.Real);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return (null, null); }
    }

    private static (Header? Header, List<StepLine> Lines) ReadFiles(string dir)
    {
        Header? header = null;
        var headerPath = Path.Combine(dir, HeaderName);
        if (File.Exists(headerPath))
        {
            try { header = JsonSerializer.Deserialize<Header>(ReadShared(headerPath), Json); }
            catch (JsonException) { header = null; }
        }
        var lines = new List<StepLine>();
        var stepsPath = Path.Combine(dir, StepsName);
        if (File.Exists(stepsPath))
        {
            foreach (var raw in ReadShared(stepsPath).Split('\n'))
            {
                var text = raw.Trim();
                if (text.Length == 0) continue;
                try
                {
                    if (JsonSerializer.Deserialize<StepLine>(text, Json) is { Step: not null } l) lines.Add(l);
                }
                catch (JsonException) { /* torn record from a crash mid-write: ignore */ }
            }
        }
        return (header, lines);
    }

    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(fs, Encoding.UTF8);
        return r.ReadToEnd();
    }

    /// <summary>A run is live if this journal holds its lock, or another process does (the lock file can't be opened).</summary>
    private bool IsHeldByAProcess(string runId, string dir)
    {
        if (_states.TryGetValue(runId, out var s) && s.Lock is not null) return true;
        var lockPath = Path.Combine(dir, LockName);
        if (!File.Exists(lockPath)) return false;
        try
        {
            using var probe = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            return false; // nobody holds it: stale lock from a crash (removed by the probe)
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    /// <summary>Test hook: forget in-memory state and drop locks and open files, as if the process had died.</summary>
    internal void SimulateProcessDeath()
    {
        foreach (var s in _states.Values)
        {
            s.Lock?.Dispose(); s.Lock = null;
            s.Steps?.Dispose(); s.Steps = null;
            s.Loaded = false;
        }
        _states.Clear();
    }

    public void Dispose()
    {
        foreach (var s in _states.Values)
        {
            s.Lock?.Dispose();
            s.Steps?.Dispose();
        }
        _states.Clear();
    }
}
