using System.Text.Json;
using System.Text.RegularExpressions;
using PcTidy.Core.Model;

namespace PcTidy.Features.Assistant.Engine;

/// <summary>
/// A suggestion that survived validation: real items from one section's current scan, all of them items the user
/// could tick in that preview. The only thing it can do is open that preview with them pre-selected.
/// </summary>
public sealed record ValidatedSuggestion(string FeatureId, string SectionTitle, ScanResult Result, IReadOnlyList<ScanItem> Items)
{
    public long TotalBytes => Items.Sum(i => i.SizeBytes);
    public string ButtonText => $"Review in {SectionTitle} →";
    public SectionReviewRequest ToRequest() => new(FeatureId, SectionTitle, Items.Select(i => i.Id).ToList(), Result.FinishedUtc);

    /// <summary>Checked again on click: the section hasn't been rescanned since the answer.</summary>
    public bool IsCurrent(IScanResultsCache cache) => ReferenceEquals(cache.Get(FeatureId), Result);
}

public sealed record ValidationReport(IReadOnlyList<ValidatedSuggestion> Suggestions, IReadOnlyList<string> Rejected);

/// <summary>
/// Checks the model's suggestions against the allow-list and the REAL cached scan, never trusting the text:
/// the action id must be on <see cref="AssistantActions.AllowList"/> (exact match); every item id must be a handle
/// this question's <see cref="ScanDataBlock"/> handed out (so raw paths, scan GUIDs and invented ids all fail);
/// the item must still be in the section's current result and still reviewable. Anything else is dropped, and
/// a malformed reply never throws.
/// </summary>
public static class ReplyValidator
{
    private static readonly Regex HandlePattern = new("^i[0-9]{1,4}$", RegexOptions.CultureInvariant);

    public static ValidationReport Validate(JsonElement suggestions, ScanDataBlock data, IScanResultsCache cache, Func<string, string>? sectionTitle = null)
    {
        var rejected = new List<string>();
        var picked = new Dictionary<string, (ScanResult Result, List<ScanItem> Items)>(StringComparer.OrdinalIgnoreCase);
        if (suggestions.ValueKind == JsonValueKind.Undefined || suggestions.ValueKind == JsonValueKind.Null)
            return new ValidationReport(Array.Empty<ValidatedSuggestion>(), rejected);
        if (suggestions.ValueKind != JsonValueKind.Array)
            return new ValidationReport(Array.Empty<ValidatedSuggestion>(), new[] { "suggestions is not a list" });

        int seen = 0;
        foreach (var s in suggestions.EnumerateArray())
        {
            if (++seen > AssistantPrompt.MaxSuggestions) { rejected.Add("too many suggestions"); break; }
            if (s.ValueKind != JsonValueKind.Object) { rejected.Add("suggestion is not an object"); continue; }
            var action = s.TryGetProperty("actionId", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
            if (action is null || !AssistantActions.AllowList.Contains(action)) { rejected.Add($"action not allowed: {action ?? "(none)"}"); continue; }
            if (!s.TryGetProperty("itemIds", out var ids) || ids.ValueKind != JsonValueKind.Array) { rejected.Add("itemIds is not a list"); continue; }

            int count = 0;
            foreach (var idElement in ids.EnumerateArray())
            {
                if (++count > AssistantPrompt.MaxItemsPerSuggestion) { rejected.Add("too many items in one suggestion"); break; }
                var id = idElement.ValueKind == JsonValueKind.String ? idElement.GetString() : null;
                if (id is null || !HandlePattern.IsMatch(id) || !data.Handles.TryGetValue(id, out var item)) { rejected.Add($"unknown item: {Short(idElement)}"); continue; }
                if (!IsCurrent(item, cache)) { rejected.Add($"item no longer in the latest scan: {id}"); continue; }
                if (!ScanDataBlock.IsReviewable(item.Item)) { rejected.Add($"item can't be reviewed: {id}"); continue; }

                if (!picked.TryGetValue(item.FeatureId, out var bucket))
                    picked[item.FeatureId] = bucket = (item.Result, new List<ScanItem>());
                if (!bucket.Items.Any(x => x.Id == item.Item.Id)) bucket.Items.Add(item.Item);
            }
        }

        var list = picked
            .Where(p => p.Value.Items.Count > 0)
            .Select(p => new ValidatedSuggestion(p.Key, sectionTitle?.Invoke(p.Key) ?? p.Value.Result.Title, p.Value.Result, p.Value.Items))
            .ToList();
        return new ValidationReport(list, rejected);
    }

    /// <summary>
    /// The handle's item must belong to the result the cache holds NOW for that section (the same object), so
    /// a suggestion can't point into a scan that has since been replaced, and the item must still be in it.
    /// </summary>
    public static bool IsCurrent(ItemRef item, IScanResultsCache cache)
        => cache.Get(item.FeatureId) is { } now && ReferenceEquals(now, item.Result) && now.AllItems.Any(i => ReferenceEquals(i, item.Item));

    private static string Short(JsonElement e)
    {
        var t = e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.GetRawText();
        return t.Length > 40 ? t[..40] + "…" : t;
    }
}
