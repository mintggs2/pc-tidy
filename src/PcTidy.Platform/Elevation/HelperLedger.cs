using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PcTidy.Core.Model;

namespace PcTidy.Platform;

/// <summary>
/// The elevated helper's own record of what it did, kept in its admin-only folder (<c>&lt;admin&gt;\ledger\&lt;runId&gt;.jsonl</c>).
/// Why: undo requests come from the normal app, whose journal the signed-in user can edit. The helper therefore undoes
/// only steps it ran itself, for exactly the plan item it ran (SHA-256 of the item must match), and uses ITS recorded
/// result (quarantine id, registry backup, prior values), never the undo data in the request.
/// Intents are flushed to disk (one flush per request) before the helper acts; results before it answers.
/// </summary>
public sealed class HelperLedger
{
    internal const string Started = "started";
    internal const string Finished = "finished";
    internal const string Undone = "undone";

    private static readonly JsonSerializerOptions Json = new(ElevationProtocol.Json);
    private readonly string _folder;
    private readonly Action _verifyFolder;
    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<int, Record>> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="adminRoot">The helper's admin-only folder.</param>
    /// <param name="verifyFolder">Re-checks that folder (owner, access list, not a link); throws if it's not safe.</param>
    public HelperLedger(string adminRoot, Action verifyFolder)
    {
        _folder = Path.Combine(adminRoot, "ledger");
        _verifyFolder = verifyFolder;
    }

    /// <summary>One line: the step as the helper saw (or finished) it, and the hash of its plan item.</summary>
    internal sealed record Record(string State, string ItemHash, StepRecord Step);

    /// <summary>Canonical SHA-256 of a plan item (arguments sorted, so the same item always hashes the same).</summary>
    public static string HashOf(PlanItem item)
    {
        var canonical = item with
        {
            Args = item.Args is null ? null : new Dictionary<string, string>(item.Args.OrderBy(a => a.Key, StringComparer.Ordinal)),
        };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical, Json)));
    }

    /// <summary>The latest record for a step of a run, or null if the helper never saw it.</summary>
    internal Record? Find(string runId, int index)
    {
        lock (_gate) return Load(runId).TryGetValue(index, out var r) ? r : null;
    }

    /// <summary>
    /// What this helper has already removed in a run: finished quarantine steps and their bytes. Used for the per-run
    /// delete cap, so splitting a big plan into many requests can't get around it.
    /// </summary>
    internal (int Items, long Bytes) RemovedSoFar(string runId)
    {
        lock (_gate)
        {
            var done = Load(runId).Values.Where(r => r.State == Finished && r.Step.Status == StepStatus.Done && r.Step.Item.Kind == ActionKind.QuarantinePath).ToList();
            return (done.Count, done.Sum(r => Math.Max(r.Step.BytesAffected, r.Step.Item.ExpectedSize)));
        }
    }

    /// <summary>True if this helper finished at least one quarantine step in the run (only then may its admin quarantine be purged).</summary>
    internal bool QuarantinedIn(string runId)
    {
        lock (_gate)
            return Load(runId).Values.Any(r => r.Step.Item.Kind == ActionKind.QuarantinePath && r.Step.Status is StepStatus.Done or StepStatus.Undone);
    }

    /// <summary>Appends records for one run with one forced disk flush.</summary>
    internal void Append(string runId, IEnumerable<(string State, StepRecord Step)> records)
    {
        lock (_gate)
        {
            var run = Load(runId);
            var sb = new StringBuilder();
            var added = new List<Record>();
            foreach (var (state, step) in records)
            {
                var r = new Record(state, HashOf(step.Item), step);
                sb.Append(JsonSerializer.Serialize(r, Json)).Append('\n');
                added.Add(r);
            }
            if (added.Count == 0) return;
            _verifyFolder();
            Directory.CreateDirectory(_folder);
            using (var fs = new FileStream(PathFor(runId), FileMode.Append, FileAccess.Write, FileShare.Read, 1))
            {
                fs.Write(Encoding.UTF8.GetBytes(sb.ToString()));
                fs.Flush(flushToDisk: true);
            }
            foreach (var r in added) run[r.Step.Index] = r;
        }
    }

    private Dictionary<int, Record> Load(string runId)
    {
        if (_cache.TryGetValue(runId, out var cached)) return cached;
        _verifyFolder();
        var map = new Dictionary<int, Record>();
        var path = PathFor(runId);
        if (File.Exists(path))
        {
            foreach (var line in File.ReadAllLines(path))
            {
                if (line.Length == 0) continue;
                try
                {
                    if (JsonSerializer.Deserialize<Record>(line, Json) is { Step: not null } r) map[r.Step.Index] = r;
                }
                catch (JsonException) { /* torn last line after a power cut */ }
            }
        }
        _cache[runId] = map;
        return map;
    }

    private string PathFor(string runId)
    {
        var safe = string.Concat(runId.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
        if (safe.Length == 0 || safe != runId) throw new ArgumentException("Invalid run id.", nameof(runId));
        return Path.Combine(_folder, safe + ".jsonl");
    }
}
