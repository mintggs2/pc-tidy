using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using PcTidy.Core;
using PcTidy.Core.Model;

namespace PcTidy.Features.Assistant.Engine;

/// <summary>An item the model may point at, by its short handle ("i7").</summary>
public sealed record ItemRef(string Handle, string FeatureId, ScanResult Result, ScanGroup Group, ScanItem Item);

/// <summary>
/// The scan data for one question: a compact JSON document of PC Tidy's own results (drives, sections, groups,
/// items, biggest folders), plus the handle map used to check what the model answers.
/// <para>
/// Prompt-injection defence: file and folder names are attacker-controlled text, so they only ever appear as JSON
/// string values here, never in instructions. The JSON encoder escapes &lt; and &gt;, so no name can forge the
/// closing <see cref="EndMarker"/>. Items are referred to by handles that only this block hands out, and only
/// items the user could tick in a preview get one.
/// </para>
/// </summary>
public sealed class ScanDataBlock
{
    public const string BeginMarker = "<scan_data>";
    public const string EndMarker = "</scan_data>";

    /// <summary>Budget that keeps the whole prompt inside an 8k context on a small model.</summary>
    public const int MaxItemsTotal = 80;
    public const int MaxItemsPerGroup = 10;
    public const int MaxGroupsPerSection = 12;
    public const int MaxFolders = 15;
    public const int MaxTextLength = 160;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // Letters of every language stay readable to the model; HTML-sensitive characters (< > & ' ") are still
        // escaped by this encoder, which is what keeps the data block's end marker unforgeable.
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        WriteIndented = false,
    };

    private ScanDataBlock(string json, IReadOnlyDictionary<string, ItemRef> handles, IReadOnlySet<string> knownPaths, bool hasScanResults)
    {
        Json = json;
        Handles = handles;
        KnownPaths = knownPaths;
        HasScanResults = hasScanResults;
    }

    /// <summary>The JSON document (one line).</summary>
    public string Json { get; }
    /// <summary>Handle → the real scan item it stands for.</summary>
    public IReadOnlyDictionary<string, ItemRef> Handles { get; }
    /// <summary>Every path in the data and each of its parent folders (the only paths an answer may name).</summary>
    public IReadOnlySet<string> KnownPaths { get; }
    /// <summary>False when no section has been scanned this session (answers can only use drive space).</summary>
    public bool HasScanResults { get; }

    /// <summary>The JSON wrapped in its markers, exactly as it goes into the prompt.</summary>
    public string Delimited => BeginMarker + "\n" + Json + "\n" + EndMarker;

    /// <summary>
    /// Items the model may suggest for review: tickable in a preview (no DisabledReason, has actions), and
    /// fully undoable (never Permanent, every action reversible).
    /// </summary>
    public static bool IsReviewable(ScanItem item)
        => item.DisabledReason is null && item.Actions.Count > 0 && item.Badge != Badge.Permanent && item.Actions.All(a => a.Reversible);

    public static ScanDataBlock Build(IScanResultsCache cache, ISystemInfo? system, Func<string, string>? sectionTitle = null)
    {
        var handles = new Dictionary<string, ItemRef>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var root = new JsonObject();

        var drives = new JsonArray();
        foreach (var d in SafeDrives(system))
        {
            AddPath(paths, d.Root);
            drives.Add(new JsonObject
            {
                ["drive"] = Text(d.Root),
                ["label"] = Text(d.Label),
                ["isWindowsDrive"] = d.IsSystem,
                ["size"] = Sizes.Format(d.TotalBytes),
                ["free"] = Sizes.Format(d.FreeBytes),
                ["freeBytes"] = d.FreeBytes,
                ["totalBytes"] = d.TotalBytes,
            });
        }
        root["drives"] = drives;

        var results = cache.Latest;
        var sections = new JsonArray();
        int itemBudget = MaxItemsTotal;
        foreach (var result in results)
        {
            var groups = new JsonArray();
            foreach (var group in result.Groups.Where(g => g.Items.Count > 0).OrderByDescending(g => g.TotalBytes).Take(MaxGroupsPerSection))
            {
                var items = new JsonArray();
                var shown = group.Items.OrderByDescending(i => i.SizeBytes).Take(Math.Min(MaxItemsPerGroup, Math.Max(0, itemBudget))).ToList();
                foreach (var item in shown)
                {
                    var node = new JsonObject();
                    if (IsReviewable(item))
                    {
                        var handle = "i" + (handles.Count + 1);
                        handles[handle] = new ItemRef(handle, result.FeatureId, result, group, item);
                        node["id"] = handle;
                    }
                    node["name"] = Text(item.Title);
                    if (PathOf(item) is { } p)
                    {
                        node["path"] = Text(p);
                        AddPath(paths, p);
                    }
                    if (item.SizeBytes > 0) node["size"] = Sizes.Format(item.SizeBytes);
                    node["safety"] = item.Badge.ToString();
                    node["why"] = Text(item.DisabledReason ?? item.Reason);
                    if (item.DisabledReason is not null) node["notOffered"] = true;
                    items.Add(node);
                }
                itemBudget -= shown.Count;
                groups.Add(new JsonObject
                {
                    ["group"] = Text(group.Title),
                    ["total"] = Sizes.Format(group.TotalBytes),
                    ["itemCount"] = group.Items.Count,
                    ["items"] = items,
                });
            }
            sections.Add(new JsonObject
            {
                ["section"] = Text(sectionTitle?.Invoke(result.FeatureId) ?? result.Title),
                ["scannedAt"] = result.FinishedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                ["total"] = Sizes.Format(result.Groups.Sum(g => g.TotalBytes)),
                ["groups"] = groups,
            });
        }
        root["sections"] = sections;

        if (cache.Space is { } space && space.BiggestFolders.Count > 0)
        {
            var folders = new JsonArray();
            foreach (var f in space.BiggestFolders.OrderByDescending(f => f.Bytes).Take(MaxFolders))
            {
                AddPath(paths, f.Path);
                folders.Add(new JsonObject { ["path"] = Text(f.Path), ["size"] = Sizes.Format(f.Bytes) });
            }
            root["biggestFolders"] = folders;
            root["spaceMapAt"] = space.TakenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        }

        return new ScanDataBlock(root.ToJsonString(JsonOptions), handles, paths, results.Count > 0);
    }

    /// <summary>The item's own path (what its action works on), else its detail line.</summary>
    private static string? PathOf(ScanItem item)
        => item.Actions.Select(a => a.SourcePath).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p))
           ?? (LooksLikePath(item.Detail) ? item.Detail : null);

    private static bool LooksLikePath(string? s)
        => !string.IsNullOrWhiteSpace(s) && ((s.Length >= 3 && s[1] == ':' && s[2] == '\\') || s.StartsWith(@"\\", StringComparison.Ordinal));

    /// <summary>A path and each of its parent folders, so an answer may say "your Downloads folder" by path.</summary>
    private static void AddPath(HashSet<string> paths, string path)
    {
        for (var p = Canonical(path); p is not null; p = Parent(p))
            if (!paths.Add(p)) break; // the rest of the chain is already known
    }

    /// <summary>"C:" and "C:\" become "C:\"; other paths lose a trailing backslash.</summary>
    internal static string? Canonical(string? path)
    {
        var p = path?.Trim();
        if (string.IsNullOrEmpty(p)) return null;
        if (p.Length is 2 or 3 && p[1] == ':') return p[..2] + "\\";
        return p.TrimEnd('\\');
    }

    private static string? Parent(string p)
    {
        var parent = Path.GetDirectoryName(p);
        return string.IsNullOrEmpty(parent) || parent.Equals(p, StringComparison.OrdinalIgnoreCase) ? null : Canonical(parent);
    }

    /// <summary>Control characters (line breaks etc.) removed, length capped. Content is otherwise kept as data.</summary>
    private static string Text(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var clean = new string(s.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        return clean.Length > MaxTextLength ? clean[..MaxTextLength] + "…" : clean;
    }

    private static IReadOnlyList<DriveDetails> SafeDrives(ISystemInfo? system)
    {
        try { return system?.Drives ?? Array.Empty<DriveDetails>(); }
        catch (Exception) { return Array.Empty<DriveDetails>(); } // drive facts are a nice-to-have for an answer
    }
}
