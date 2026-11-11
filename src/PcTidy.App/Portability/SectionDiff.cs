using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PcTidy.App.Portability;

/// <summary>One step to a setting inside a section's settings: a property, or the item of a list that has a given key.</summary>
internal sealed record Step(string Name, string? KeyName = null, string? Label = null)
{
    public bool IsItem => KeyName is not null;
    public static Step Prop(string name) => new(name);
    /// <summary>The list item whose <paramref name="keyName"/> property equals <paramref name="keyValue"/>.</summary>
    public static Step Item(string keyName, string keyValue, string label) => new(keyValue, keyName, label);
}

/// <summary>One changed setting of a section, with how to apply it to that section's JSON.</summary>
internal sealed record SectionRow(string Name, string Before, string After, bool Automatic, bool Loosens, string? Note,
    IReadOnlyList<string> Paths, Action<JsonNode> Apply);

/// <summary>
/// Compares one section's settings (a JSON blob in <c>AppSettings.Features</c>) setting by setting, so an import shows
/// every changed value as its own row (EXP-1): nested settings by name ("Watch › Enabled"), lists of things with an id
/// (rules, games, apps) item by item, lists of plain values (folders) value by value. A new list item is added with its
/// "do this on its own" switches OFF; each such switch that the file has on becomes its own, flagged row.
/// <para>Rows that would let PC Tidy act without asking (watch mode, automatic rules, automatic quarantine, Boost closing
/// apps, anything named "Auto…") are <see cref="SectionRow.Automatic"/>; an address PC Tidy would contact (any "…Url"
/// setting; the updater's own address is fixed in code and can't be imported) <see cref="SectionRow.Loosens"/>. Both start unticked.</para>
/// </summary>
internal static class SectionDiff
{
    private static readonly string[] KeyCandidates = { "Id", "Key", "ProcessName", "Path", "Name" };
    private static readonly string[] AutoSwitches = { "RunAutomatically", "AllowAutomaticQuarantine" };
    private static readonly string[] ItemNames = { "Name", "DisplayName", "Title" };
    /// <summary>The id given to an incoming item of a list whose items have no unique id of their own.</summary>
    private const string GeneratedKey = "Id";

    /// <summary>Settings that describe this PC rather than preferences (never imported, never shown).</summary>
    private static bool ThisPcOnly(string section, IReadOnlyList<Step> path)
        => path.Count == 1 && (path[0].Name == "Version" || (section == "organize" && path[0].Name == "Ledger"));

    public static List<SectionRow> Rows(string section, JsonElement? mine, JsonElement theirs)
    {
        var rows = new List<SectionRow>();
        Diff(section, new List<Step>(), mine, theirs, rows);
        return rows;
    }

    private static void Diff(string section, List<Step> path, JsonElement? mine, JsonElement theirs, List<SectionRow> rows)
    {
        if (ThisPcOnly(section, path)) return;
        if (theirs.ValueKind == JsonValueKind.Object && mine is null or { ValueKind: JsonValueKind.Object })
        {
            foreach (var p in theirs.EnumerateObject())
            {
                JsonElement? child = mine is { } m && m.TryGetProperty(p.Name, out var v) ? v : null;
                Diff(section, With(path, Step.Prop(p.Name)), child, p.Value, rows);
            }
            return;
        }
        if (theirs.ValueKind == JsonValueKind.Array && mine is null or { ValueKind: JsonValueKind.Array })
        {
            var t = theirs.EnumerateArray().ToList();
            var m = mine?.EnumerateArray().ToList() ?? new List<JsonElement>();
            if (KeyOf(t, m) is { } key) { DiffKeyed(section, path, key, m, t, rows); return; }
            if (t.Concat(m).All(IsScalar)) { DiffValues(section, path, m, t, rows); return; }
            if (t.Count > 0 && t.All(e => e.ValueKind == JsonValueKind.Object)) { DiffUnkeyed(section, path, m, t, rows); return; }
        }
        if (mine is { } same && Text(same) == Text(theirs)) return;
        rows.Add(Changed(section, path, mine, theirs));
    }

    // ---------------- lists of things without a unique id ----------------

    /// <summary>
    /// A list of things that can't be matched item by item (no id, or ids that repeat): never ONE "whole list" row that
    /// would apply the file's list as it is (IMP-1). Every incoming item this PC doesn't already have exactly is added as a
    /// new item under a freshly generated id, with its "on its own" switches off and one flagged row per switch.
    /// Items only this PC has are kept: without an id nothing can be matched safely enough to remove it.
    /// </summary>
    private static void DiffUnkeyed(string section, List<Step> path, List<JsonElement> mine, List<JsonElement> theirs, List<SectionRow> rows)
    {
        var unmatched = mine.Select(m => JsonNode.Parse(m.GetRawText())).ToList();
        foreach (var t in theirs)
        {
            var node = JsonNode.Parse(t.GetRawText())!.AsObject();
            int same = unmatched.FindIndex(m => JsonNode.DeepEquals(m, node));
            if (same >= 0) { unmatched.RemoveAt(same); continue; }
            // A non-text id (a number) can't be replaced without breaking the section's settings: such an item is added
            // as it is, with every switch inside it off, and no switch rows (there is no way to point at it later).
            if (node[GeneratedKey] is JsonNode existingId && existingId.GetValueKind() is not (JsonValueKind.String or JsonValueKind.Null))
            {
                ForceOffDeep(node);
                var raw = node.ToJsonString();
                rows.Add(new SectionRow($"{Label(path)} › \"{ItemLabel(t, "Item")}\" added", "—", Summary(t), IsAutomaticList(section, path), false,
                    "Anything in it that runs on its own was switched off.", PathsIn(t).ToList(), root => AppendItem(root, path, raw)));
                continue;
            }
            var id = Guid.NewGuid().ToString("N");
            node[GeneratedKey] = id;
            using var doc = JsonDocument.Parse(node.ToJsonString());
            rows.AddRange(Added(section, path, GeneratedKey, Step.Item(GeneratedKey, id, ItemLabel(t, "Item")), doc.RootElement.Clone()));
        }
    }

    // ---------------- lists of things with an id ----------------

    private static void DiffKeyed(string section, List<Step> path, string key, List<JsonElement> mine, List<JsonElement> theirs, List<SectionRow> rows)
    {
        foreach (var t in theirs)
        {
            var k = t.GetProperty(key).GetString()!;
            var itemStep = Step.Item(key, k, ItemLabel(t, k));
            var existing = mine.FirstOrDefault(x => x.GetProperty(key).GetString() == k);
            if (existing.ValueKind == JsonValueKind.Object) { Diff(section, With(path, itemStep), existing, t, rows); continue; }
            rows.AddRange(Added(section, path, key, itemStep, t));
        }
        foreach (var m in mine)
        {
            var k = m.GetProperty(key).GetString()!;
            if (theirs.Any(x => x.GetProperty(key).GetString() == k)) continue;
            var label = ItemLabel(m, k);
            rows.Add(new SectionRow($"{Label(path)} › \"{label}\" removed", Summary(m), "—", false, false, null, Array.Empty<string>(),
                root => RemoveItem(root, path, key, k)));
        }
    }

    /// <summary>A new item: added with its "on its own" switches off, plus one flagged row per switch the file has on.</summary>
    private static IEnumerable<SectionRow> Added(string section, List<Step> path, string key, Step itemStep, JsonElement item)
    {
        var node = JsonNode.Parse(item.GetRawText())!.AsObject();
        var switchesOn = AutoSwitches.Where(s => node[s] is JsonValue v && v.TryGetValue<bool>(out var on) && on).ToList();
        ForceOffDeep(node); // the top-level switches above and any nested ones: off unless their own row is ticked
        var plain = node.ToJsonString();
        var paths = PathsIn(item).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var automatic = IsAutomaticList(section, path);
        yield return new SectionRow($"{Label(path)} › \"{itemStep.Label}\" added", "—", Summary(item), automatic, false,
            paths.Count > 0 ? "Folders in it: " + string.Join(", ", paths.Take(4)) + (paths.Count > 4 ? ", …" : "") : null,
            paths, root => AddItem(root, path, key, itemStep.Name, plain));
        foreach (var s in switchesOn)
            yield return new SectionRow($"{Label(With(path, itemStep))} › {Words(s)}", "Off", "On", true, false,
                "Only together with the item above. It lets PC Tidy do this without asking each time.", Array.Empty<string>(),
                root => Set(root, With(With(path, itemStep), Step.Prop(s)), "true"));
    }

    // ---------------- lists of plain values (folders, names) ----------------

    private static void DiffValues(string section, List<Step> path, List<JsonElement> mine, List<JsonElement> theirs, List<SectionRow> rows)
    {
        var mineText = mine.Select(Text).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var theirsText = theirs.Select(Text).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var t in theirs.Where(t => !mineText.Contains(Text(t))).DistinctBy(Text, StringComparer.OrdinalIgnoreCase))
        {
            var raw = t.GetRawText();
            rows.Add(new SectionRow($"{Label(path)} added", "—", Show(t), IsAutomaticList(section, path), false, null, PathsIn(t).ToList(),
                root => AddValue(root, path, raw)));
        }
        foreach (var m in mine.Where(m => !theirsText.Contains(Text(m))).DistinctBy(Text, StringComparer.OrdinalIgnoreCase))
        {
            var raw = m.GetRawText();
            rows.Add(new SectionRow($"{Label(path)} removed", Show(m), "—", false, false, null, Array.Empty<string>(),
                root => RemoveValue(root, path, raw)));
        }
    }

    // ---------------- one changed value ----------------

    private static SectionRow Changed(string section, List<Step> path, JsonElement? mine, JsonElement theirs)
    {
        var raw = theirs.GetRawText();
        var last = path.LastOrDefault(s => !s.IsItem)?.Name ?? "";
        bool automatic = TurnsOn(theirs) && (AutoSwitches.Contains(last) || IsAutoName(last) || IsAutomaticSetting(section, path));
        // A whole object or list arriving in one row: whatever inside it would run on its own is switched off (IMP-1),
        // judged by its content, not by the setting's name.
        bool switchedOff = false;
        if (theirs.ValueKind is JsonValueKind.Object or JsonValueKind.Array && JsonNode.Parse(raw) is { } whole && ForceOffDeep(whole))
        {
            raw = whole.ToJsonString();
            switchedOff = true;
        }
        bool contacts = last.EndsWith("Url", StringComparison.OrdinalIgnoreCase) && theirs.ValueKind == JsonValueKind.String && theirs.GetString() is { Length: > 0 };
        var note = contacts
            ? "PC Tidy would contact this address, so only tick it if you trust whoever runs it."
            : automatic ? "It lets PC Tidy act on its own, without asking each time."
            : switchedOff ? "Anything in it that runs on its own was switched off. Switch it on in that section yourself if you want it." : null;
        return new SectionRow(Label(path), mine is { } m ? Show(m) : "Default", Show(theirs), automatic, contacts, note, PathsIn(theirs).ToList(),
            root => Set(root, path, raw));
    }

    // ---------------- which changes let PC Tidy act on its own ----------------

    /// <summary>Settings that switch on something that runs by itself (on top of the generic "Auto…" names).</summary>
    private static bool IsAutomaticSetting(string section, List<Step> path)
    {
        var names = path.Where(s => !s.IsItem).Select(s => s.Name).ToList();
        return section switch
        {
            "organize" => names.FirstOrDefault() == "Watch",
            "boost" => names.Count >= 1 && names[0] is "Enabled" or "PauseOneDrive" or "Games" or "AppsToClose",
            _ => false,
        };
    }

    /// <summary>Lists whose new entries widen what runs by itself (watched folders, games Boost reacts to, apps it closes).</summary>
    private static bool IsAutomaticList(string section, List<Step> path)
    {
        var names = path.Where(s => !s.IsItem).Select(s => s.Name).ToList();
        return section switch
        {
            "organize" => names.FirstOrDefault() == "Watch",
            "boost" => names.FirstOrDefault() is "Games" or "AppsToClose",
            _ => false,
        };
    }

    private static bool IsAutoName(string name)
        => name.StartsWith("Auto", StringComparison.Ordinal) || name.Contains("Automatic", StringComparison.Ordinal) || name.StartsWith("Watch", StringComparison.Ordinal);

    /// <summary>
    /// Sets every "on its own" switch (<c>RunAutomatically</c>, <c>AllowAutomaticQuarantine</c>, any other true
    /// <c>Auto…</c>/<c>…Automatic…</c>/<c>Watch…</c> flag) anywhere inside <paramref name="node"/> to false.
    /// True when it changed something.
    /// </summary>
    private static bool ForceOffDeep(JsonNode? node)
    {
        bool changed = false;
        switch (node)
        {
            case JsonObject o:
                foreach (var (name, value) in o.ToList())
                {
                    if (value is JsonValue v && v.GetValueKind() == JsonValueKind.True && (AutoSwitches.Contains(name) || IsAutoName(name)))
                    {
                        o[name] = false;
                        changed = true;
                    }
                    else changed |= ForceOffDeep(value);
                }
                break;
            case JsonArray a:
                foreach (var x in a) changed |= ForceOffDeep(x);
                break;
        }
        return changed;
    }

    private static bool TurnsOn(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.String => v.GetString() is { Length: > 0 },
        JsonValueKind.Array => v.GetArrayLength() > 0,
        JsonValueKind.Object => true,
        _ => false,
    };

    // ---------------- applying to the section's JSON ----------------

    /// <summary>The node <paramref name="count"/> steps down <paramref name="path"/>; creates missing objects/lists when asked.</summary>
    private static JsonNode? Walk(JsonNode root, IReadOnlyList<Step> path, int count, bool create)
    {
        JsonNode? node = root;
        for (int i = 0; i < count && node is not null; i++)
        {
            var s = path[i];
            if (!s.IsItem)
            {
                if (node is not JsonObject o) return null;
                if (o[s.Name] is null && create) o[s.Name] = i + 1 < path.Count && path[i + 1].IsItem ? new JsonArray() : new JsonObject();
                node = o[s.Name];
            }
            else
            {
                if (node is not JsonArray a) return null;
                node = a.FirstOrDefault(e => e is JsonObject eo && eo[s.KeyName!] is JsonValue v && v.TryGetValue<string>(out var k) && k == s.Name);
            }
        }
        return node;
    }

    private static void Set(JsonNode root, IReadOnlyList<Step> path, string rawValue)
    {
        if (path.Count == 0 || path[^1].IsItem) return;
        if (Walk(root, path, path.Count - 1, create: true) is JsonObject parent) parent[path[^1].Name] = JsonNode.Parse(rawValue);
    }

    private static JsonArray? ListAt(JsonNode root, IReadOnlyList<Step> path)
    {
        if (path.Count == 0 || path[^1].IsItem) return null;
        if (Walk(root, path, path.Count - 1, create: true) is not JsonObject parent) return null;
        if (parent[path[^1].Name] is not JsonArray list)
        {
            list = new JsonArray();
            parent[path[^1].Name] = list;
        }
        return list;
    }

    private static void AddItem(JsonNode root, IReadOnlyList<Step> path, string key, string keyValue, string rawItem)
    {
        if (ListAt(root, path) is not { } list) return;
        RemoveWhere(list, e => e is JsonObject o && o[key] is JsonValue v && v.TryGetValue<string>(out var k) && k == keyValue);
        list.Add(JsonNode.Parse(rawItem));
    }

    private static void AppendItem(JsonNode root, IReadOnlyList<Step> path, string rawItem)
    {
        if (ListAt(root, path) is { } list) list.Add(JsonNode.Parse(rawItem));
    }

    private static void RemoveItem(JsonNode root, IReadOnlyList<Step> path, string key, string keyValue)
    {
        if (Walk(root, path, path.Count, create: false) is JsonArray list)
            RemoveWhere(list, e => e is JsonObject o && o[key] is JsonValue v && v.TryGetValue<string>(out var k) && k == keyValue);
    }

    private static void AddValue(JsonNode root, IReadOnlyList<Step> path, string rawValue)
    {
        if (ListAt(root, path) is not { } list) return;
        var text = Text(JsonDocument.Parse(rawValue).RootElement);
        if (list.Any(e => e is not null && string.Equals(Text(JsonDocument.Parse(e.ToJsonString()).RootElement), text, StringComparison.OrdinalIgnoreCase))) return;
        list.Add(JsonNode.Parse(rawValue));
    }

    private static void RemoveValue(JsonNode root, IReadOnlyList<Step> path, string rawValue)
    {
        if (Walk(root, path, path.Count, create: false) is not JsonArray list) return;
        var text = Text(JsonDocument.Parse(rawValue).RootElement);
        RemoveWhere(list, e => e is not null && string.Equals(Text(JsonDocument.Parse(e.ToJsonString()).RootElement), text, StringComparison.OrdinalIgnoreCase));
    }

    private static void RemoveWhere(JsonArray list, Func<JsonNode?, bool> match)
    {
        for (int i = list.Count - 1; i >= 0; i--)
            if (match(list[i])) list.RemoveAt(i);
    }

    // ---------------- text ----------------

    private static List<Step> With(List<Step> path, Step next) => new(path) { next };

    /// <summary>The key property every item of both lists has (a non-empty, unique string), or null.</summary>
    private static string? KeyOf(List<JsonElement> theirs, List<JsonElement> mine)
    {
        var all = theirs.Concat(mine).ToList();
        if (all.Count == 0 || all.Any(e => e.ValueKind != JsonValueKind.Object)) return null;
        foreach (var key in KeyCandidates)
        {
            bool Unique(List<JsonElement> list)
            {
                var values = list.Select(e => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null).ToList();
                return values.All(v => !string.IsNullOrEmpty(v)) && values.Distinct(StringComparer.Ordinal).Count() == values.Count;
            }
            if (Unique(theirs) && Unique(mine)) return key;
        }
        return null;
    }

    private static string ItemLabel(JsonElement item, string key)
    {
        foreach (var n in ItemNames)
            if (item.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s) return s;
        return key;
    }

    private static string Label(IReadOnlyList<Step> path)
        => path.Count == 0 ? "Settings" : string.Join(" › ", path.Select(s => s.IsItem ? $"\"{s.Label}\"" : Words(s.Name)));

    /// <summary>"RunAutomatically" → "Run automatically".</summary>
    internal static string Words(string name)
    {
        var spaced = Regex.Replace(name, "(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");
        return spaced.Length == 0 ? name : char.ToUpperInvariant(spaced[0]) + spaced[1..].ToLowerInvariant();
    }

    private static bool IsScalar(JsonElement e) => e.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False;

    private static string Text(JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.GetRawText();

    private static string Show(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.True => "On",
        JsonValueKind.False => "Off",
        JsonValueKind.Null or JsonValueKind.Undefined => "Not set",
        JsonValueKind.String => e.GetString() is { Length: > 0 } s ? s : "Not set",
        JsonValueKind.Number => e.GetRawText(),
        JsonValueKind.Array when e.GetArrayLength() == 0 => "None",
        _ => Summary(e),
    };

    /// <summary>A short one-line description of an object or list (its name first, then its settings).</summary>
    private static string Summary(JsonElement e)
    {
        var name = e.ValueKind == JsonValueKind.Object ? ItemNames.Select(n => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null).FirstOrDefault(s => !string.IsNullOrEmpty(s)) : null;
        var json = e.GetRawText();
        json = Regex.Replace(json, @"\s+", " ");
        if (json.Length > 160) json = json[..160] + "…";
        return name is null ? json : $"{name}: {json}";
    }

    /// <summary>Every string in a JSON value that looks like a folder path (drive or network).</summary>
    internal static IEnumerable<string> PathsIn(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                var v = e.GetString();
                if (v is { Length: >= 3 } && ((char.IsAsciiLetter(v[0]) && v[1] == ':' && v[2] is '\\' or '/') || v.StartsWith(@"\\", StringComparison.Ordinal))) yield return v;
                break;
            case JsonValueKind.Array:
                foreach (var x in e.EnumerateArray()) foreach (var p in PathsIn(x)) yield return p;
                break;
            case JsonValueKind.Object:
                foreach (var x in e.EnumerateObject()) foreach (var p in PathsIn(x.Value)) yield return p;
                break;
        }
    }
}
