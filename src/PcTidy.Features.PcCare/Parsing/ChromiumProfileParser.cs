using System.Text.Json;
using PcTidy.Features.PcCare.Model;

namespace PcTidy.Features.PcCare.Parsing;

/// <summary>What the extension's own manifest.json says (name, store update URL, declared permissions).</summary>
public sealed record ExtensionManifest(string? Name, string? UpdateUrl, IReadOnlyList<string> Permissions);

/// <summary>
/// Reads a Chromium (Chrome, Edge, Brave) profile's <c>Preferences</c> and <c>Secure Preferences</c> JSON, read-only:
/// home page, startup pages, default search engine and installed extensions
/// (<c>extensions.settings.&lt;id&gt;</c>: location, from_webstore, state/disable_reasons, active_permissions).
/// Browser files are never edited; fixes open the browser's own settings.
/// </summary>
public static class ChromiumProfileParser
{
    /// <summary>Chromium "Manifest::Location" values that are part of the browser itself (never shown).</summary>
    private static readonly HashSet<int> BuiltInLocations = new() { 5, 10 };

    private static readonly JsonDocumentOptions Options = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 256 };

    /// <param name="manifest">Reads an extension's manifest from its folder ("&lt;id&gt;\&lt;version&gt;" relative path); may return null.</param>
    public static BrowserProfile Parse(string browser, string profileName, string profileDir, string? preferencesJson, string? securePreferencesJson,
        Func<string, string?, ExtensionManifest?>? manifest = null)
    {
        using var prefs = TryParse(preferencesJson);
        using var secure = TryParse(securePreferencesJson);
        var p = prefs?.RootElement;
        var s = secure?.RootElement;

        // Some values live in Secure Preferences on newer builds; prefer the protected copy when it has one.
        string? homepage = Str(s, "homepage") ?? Str(p, "homepage");
        bool homeIsNtp = Bool(s, "homepage_is_newtabpage") ?? Bool(p, "homepage_is_newtabpage") ?? true;
        int? restore = Int(s, "session", "restore_on_startup") ?? Int(p, "session", "restore_on_startup");
        var startup = StrArray(s, "session", "startup_urls") ?? StrArray(p, "session", "startup_urls") ?? Array.Empty<string>();

        string? searchName = Str(s, "default_search_provider_data", "template_url_data", "short_name")
                             ?? Str(p, "default_search_provider_data", "template_url_data", "short_name")
                             ?? Str(p, "default_search_provider", "name");
        string? searchUrl = Str(s, "default_search_provider_data", "template_url_data", "url")
                            ?? Str(p, "default_search_provider_data", "template_url_data", "url")
                            ?? Str(p, "default_search_provider", "search_url");

        var extensions = new Dictionary<string, BrowserExtension>(StringComparer.Ordinal);
        foreach (var root in new[] { p, s })
        {
            if (root is not { } r || !TryGet(r, out var settings, "extensions", "settings") || settings.ValueKind != JsonValueKind.Object) continue;
            foreach (var ext in settings.EnumerateObject())
            {
                var parsed = ParseExtension(ext.Name, ext.Value, manifest);
                if (parsed is not null) extensions[ext.Name] = parsed; // Secure Preferences (second) wins
            }
        }

        return new BrowserProfile(browser, profileName, profileDir, homepage, homeIsNtp, restore, startup, searchName, searchUrl,
            extensions.Values.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
    }

    private static BrowserExtension? ParseExtension(string id, JsonElement e, Func<string, string?, ExtensionManifest?>? manifest)
    {
        if (e.ValueKind != JsonValueKind.Object || id.Length != 32 || !id.All(c => c is >= 'a' and <= 'p')) return null;
        int location = Int(e, "location") ?? 0;
        if (BuiltInLocations.Contains(location)) return null;
        if (Bool(e, "was_installed_by_default") == true || Bool(e, "was_installed_by_oem") == true) return null;

        var path = Str(e, "path");
        var fromFile = manifest?.Invoke(id, path);
        string? inlineName = Str(e, "manifest", "name");
        string? name = Clean(fromFile?.Name) ?? Clean(inlineName);
        if (name is null) return null; // no manifest left on disk: a stale entry, not an installed extension

        string? updateUrl = fromFile?.UpdateUrl ?? Str(e, "manifest", "update_url");
        bool fromStore = Bool(e, "from_webstore") == true || IsStoreUpdateUrl(updateUrl);

        var permissions = new List<string>();
        if (TryGet(e, out var active, "active_permissions"))
            foreach (var key in new[] { "api", "explicit_host", "scriptable_host", "manifest_permissions" })
                permissions.AddRange(StrArray(active, key) ?? Array.Empty<string>());
        if (permissions.Count == 0 && fromFile is not null) permissions.AddRange(fromFile.Permissions);

        return new BrowserExtension(id, name, fromStore, location, IsEnabled(e), permissions.Distinct(StringComparer.Ordinal).ToList());
    }

    /// <summary>Older builds: state 1 = enabled. Newer: no "state", and an empty/zero disable_reasons means enabled.</summary>
    private static bool IsEnabled(JsonElement e)
    {
        if (Int(e, "state") is { } state) return state == 1;
        if (!e.TryGetProperty("disable_reasons", out var r)) return true;
        return r.ValueKind switch
        {
            JsonValueKind.Number => r.TryGetInt32(out var n) && n == 0,
            JsonValueKind.Array => r.GetArrayLength() == 0,
            _ => true,
        };
    }

    public static bool IsStoreUpdateUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        return u.Host.Equals("clients2.google.com", StringComparison.OrdinalIgnoreCase)
               || u.Host.Equals("edge.microsoft.com", StringComparison.OrdinalIgnoreCase)
               || u.Host.EndsWith(".googleusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Unresolved "__MSG_name__" placeholders are left to the caller; empty names become null.</summary>
    private static string? Clean(string? name)
    {
        var n = name?.Trim();
        return string.IsNullOrEmpty(n) ? null : n;
    }

    /// <summary>Reads a manifest.json text (and an optional default-locale messages.json for "__MSG_x__" names).</summary>
    public static ExtensionManifest? ParseManifest(string? manifestJson, Func<string, string?>? messagesForLocale = null)
    {
        using var doc = TryParse(manifestJson);
        if (doc is null) return null;
        var m = doc.RootElement;
        var name = Str(m, "name");
        if (name is not null && name.StartsWith("__MSG_", StringComparison.Ordinal) && name.EndsWith("__", StringComparison.Ordinal))
        {
            var key = name[6..^2];
            var locale = Str(m, "default_locale") ?? "en";
            using var messages = TryParse(messagesForLocale?.Invoke(locale));
            string? resolved = null;
            if (messages?.RootElement is { ValueKind: JsonValueKind.Object } msgs)
                foreach (var prop in msgs.EnumerateObject())
                    if (string.Equals(prop.Name, key, StringComparison.OrdinalIgnoreCase)) { resolved = Str(prop.Value, "message"); break; }
            name = resolved ?? key;
        }
        var permissions = new List<string>();
        permissions.AddRange(StrArray(m, "permissions") ?? Array.Empty<string>());
        permissions.AddRange(StrArray(m, "host_permissions") ?? Array.Empty<string>());
        return new ExtensionManifest(name, Str(m, "update_url"), permissions);
    }

    // ------------------------------------------------------------------ JSON helpers (never throw)

    private static JsonDocument? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonDocument.Parse(json, Options); }
        catch (JsonException) { return null; }
    }

    private static bool TryGet(JsonElement root, out JsonElement value, params string[] path)
    {
        value = root;
        foreach (var key in path)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out value)) return false;
        }
        return true;
    }

    private static string? Str(JsonElement? root, params string[] path)
        => root is { } r && TryGet(r, out var v, path) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(JsonElement? root, params string[] path)
        => root is { } r && TryGet(r, out var v, path) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static int? Int(JsonElement? root, params string[] path)
        => root is { } r && TryGet(r, out var v, path) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    private static IReadOnlyList<string>? StrArray(JsonElement? root, params string[] path)
    {
        if (root is not { } r || !TryGet(r, out var v, path) || v.ValueKind != JsonValueKind.Array) return null;
        return v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList();
    }
}
