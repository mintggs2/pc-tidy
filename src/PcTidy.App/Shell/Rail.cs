using PcTidy.Core;
using PcTidy.Core.Model;

namespace PcTidy.App.Shell;

public enum RailEntryKind { Header, Module, More }

/// <summary>One row of the left rail: a group header, a section, or the "More" expander with its sections.</summary>
public sealed record RailEntry(RailEntryKind Kind, string Text, IFeatureModule? Module = null, IReadOnlyList<IFeatureModule>? Children = null)
{
    public IReadOnlyList<IFeatureModule> MoreChildren => Children ?? Array.Empty<IFeatureModule>();
}

/// <summary>
/// The rail's rows (layout spec §4.2): Home, then the groups (CLEAN UP, SPEED UP, ORGANIZE, YOU) with their sections,
/// then one "More" expander at the end holding the rarely used sections, so the rail starts at about 16 rows.
/// The footer (History, Quarantine, Settings) isn't part of this list. Data-driven: new sections just appear.
/// </summary>
public static class RailLayout
{
    public const string MoreText = "More";

    /// <summary>
    /// Rarely used sections (layout spec §4.2): Duplicates, Developer cleanup, Drivers, Photos, Desktop tidy, Search,
    /// Move to drive, AI chats, Notifications, Ask PC Tidy and Learned. In rail order. With these under More the rail
    /// is about 15 section rows, so "More" itself is on screen above the footer at the smallest window (1024×700).
    /// </summary>
    public static readonly IReadOnlyList<string> MoreIds = new[]
    {
        "duplicates", "developer", "drivers", "photos", "desktoptidy", "search", "drivemove", "aichats", "notifications", "assistant", "learned",
    };

    public static bool IsInMore(string moduleId) => MoreIds.Contains(moduleId, StringComparer.OrdinalIgnoreCase);

    /// <param name="modules">The visible modules, in rail order.</param>
    public static IReadOnlyList<RailEntry> Build(IReadOnlyList<IFeatureModule> modules)
    {
        var rows = new List<RailEntry>();
        var body = modules.Where(m => m.Group != NavGroup.Footer).ToList();
        foreach (var (group, header) in NavGroups.Body)
        {
            var inGroup = body.Where(m => m.Group == group && !IsInMore(m.Id)).ToList();
            if (inGroup.Count == 0) continue;
            if (header.Length > 0) rows.Add(new RailEntry(RailEntryKind.Header, header));
            rows.AddRange(inGroup.Select(m => new RailEntry(RailEntryKind.Module, m.Title, m)));
        }
        var more = body.Where(m => IsInMore(m.Id))
                       .OrderBy(m => MoreIds.ToList().FindIndex(id => id.Equals(m.Id, StringComparison.OrdinalIgnoreCase)))
                       .ToList();
        if (more.Count > 0) rows.Add(new RailEntry(RailEntryKind.More, MoreText, Children: more));
        return rows;
    }

    /// <summary>
    /// Rows the rail shows: Home, headers, sections, "More" (plus its sections when open) and the footer's
    /// <paramref name="footerRows"/> (History, Quarantine, Settings and any footer sections).
    /// </summary>
    public static int VisibleRows(IReadOnlyList<RailEntry> rows, bool moreOpen, int footerRows = 3)
        => 1 + rows.Sum(r => r.Kind == RailEntryKind.More && moreOpen ? 1 + r.MoreChildren.Count : 1) + footerRows;

    /// <summary>"More" opens by itself when the open section is inside it.</summary>
    public static bool ShouldOpenMore(bool remembered, string? currentModuleId)
        => remembered || (currentModuleId is not null && IsInMore(currentModuleId));
}

/// <summary>
/// The rail's small counters (layout spec §4.2): each <see cref="INavBadgeSource"/> section's own text, fetched off the
/// UI thread with a time limit. A failure, a time-out or an empty text shows nothing. No "NEW" badges, no dots.
/// </summary>
public sealed class NavBadgeFetcher
{
    /// <summary>Longest counter text shown (a longer one is cut, it's a hint, not a report).</summary>
    public const int MaxLength = 24;

    private readonly TimeSpan _timeout;

    public NavBadgeFetcher(TimeSpan? timeout = null) => _timeout = timeout ?? TimeSpan.FromSeconds(5);

    /// <summary>Counter text by module id; modules whose source failed, timed out or said nothing get null.</summary>
    public async Task<IReadOnlyDictionary<string, string?>> FetchAsync(IEnumerable<IFeatureModule> modules, CancellationToken ct)
    {
        var sources = modules.Where(m => m is INavBadgeSource).ToList();
        var tasks = sources.Select(m => FetchOneAsync((INavBadgeSource)m, ct)).ToList();
        var texts = await Task.WhenAll(tasks).ConfigureAwait(false);
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < sources.Count; i++) result[sources[i].Id] = texts[i];
        return result;
    }

    private async Task<string?> FetchOneAsync(INavBadgeSource source, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        try
        {
            // Task.Run: a source that blocks before its first await still never holds up the UI thread.
            var text = await Task.Run(() => source.GetNavBadgeAsync(cts.Token), cts.Token).WaitAsync(_timeout, ct).ConfigureAwait(false);
            return Clean(text);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null; // failed or too slow: show nothing
        }
    }

    /// <summary>One short line, or null for nothing to show. Never "NEW".</summary>
    public static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (t.Equals("new", StringComparison.OrdinalIgnoreCase)) return null;
        return t.Length <= MaxLength ? t : t[..(MaxLength - 1)].TrimEnd() + "…";
    }
}

/// <summary>History's rail counter: undo-able runs since History was last opened.</summary>
public static class HistoryBadge
{
    /// <summary>
    /// Runs (an Optimize-everything run counts once) made after <paramref name="lastViewedUtc"/> that can still be undone:
    /// real, finished, not undone, with something reversible done. Null last-viewed = nothing counts.
    /// </summary>
    public static int Count(IReadOnlyList<RunRecord> runs, DateTime? lastViewedUtc)
    {
        if (lastViewedUtc is not { } since) return 0;
        return runs.Where(r => r.Mode == ExecutionMode.Real && r.CreatedUtc > since)
                   .GroupBy(r => r.ParentRunId ?? r.RunId)
                   .Count(g => g.Any(r => (r.Status is RunStatus.Completed or RunStatus.CompletedWithSkips or RunStatus.Failed or RunStatus.PartiallyUndone)
                                          && r.Steps.Any(s => s.Status == StepStatus.Done && s.Item.Reversible)));
    }

    /// <summary>The counter text ("2"), or null for none.</summary>
    public static string? Text(int count) => count > 0 ? count.ToString(System.Globalization.CultureInfo.CurrentCulture) : null;

    public static string AccessibleText(int count) => count == 1 ? "1 run you can undo since you last opened History" : $"{count} runs you can undo since you last opened History";
}
