using System.Text;
using System.Xml;
using System.Xml.Linq;
using PcTidy.Catalog.DriveMove;

namespace PcTidy.Features.DriveMove.Caches;

/// <summary>
/// Reads and changes ONE setting in a tool's config text, keeping everything else exactly as it was (comments, order,
/// other settings). Pure string work: the engine writes the result (EditTextFile, with a backup). Returns null when the
/// text can't be understood safely, so nothing is offered rather than a guess.
/// </summary>
public static class ConfigText
{
    public static string? Get(ConfigFormat format, string text, string key, string? section = null) => format switch
    {
        ConfigFormat.NpmRc => KeyValueGet(text, key),
        ConfigFormat.PipIni => IniGet(text, section ?? "global", key),
        ConfigFormat.NuGetConfig => NuGetGet(text, key),
        ConfigFormat.MavenSettings => MavenGet(text, key),
        _ => null,
    };

    /// <summary>The whole new text with <paramref name="key"/> set to <paramref name="value"/> (text "" = a new file).</summary>
    public static string? Set(ConfigFormat format, string text, string key, string value, string? section = null) => format switch
    {
        ConfigFormat.NpmRc => KeyValueSet(text, key, value),
        ConfigFormat.PipIni => IniSet(text, section ?? "global", key, value),
        ConfigFormat.NuGetConfig => NuGetSet(text, key, value),
        ConfigFormat.MavenSettings => MavenSet(text, key, value),
        _ => null,
    };

    // ------------------------------------------------------------------ .npmrc: key=value lines

    private static bool IsComment(string trimmed) => trimmed.StartsWith('#') || trimmed.StartsWith(';');

    private static string? KeyValueGet(string text, string key)
    {
        foreach (var line in Lines(text))
        {
            var t = line.Trim();
            if (IsComment(t)) continue;
            var eq = t.IndexOf('=');
            if (eq > 0 && t[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) return Unquote(t[(eq + 1)..].Trim());
        }
        return null;
    }

    private static string KeyValueSet(string text, string key, string value)
    {
        var lines = Lines(text).ToList();
        var newline = text.Contains("\r\n") || text.Length == 0 ? "\r\n" : "\n";
        bool done = false;
        for (int i = 0; i < lines.Count; i++)
        {
            var t = lines[i].Trim();
            if (IsComment(t)) continue;
            var eq = t.IndexOf('=');
            if (eq <= 0 || !t[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            if (!done) { lines[i] = $"{key}={value}"; done = true; }
            else lines.RemoveAt(i--); // a later duplicate would win over ours
        }
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        if (!done) lines.Add($"{key}={value}");
        return string.Join(newline, lines) + newline;
    }

    // ------------------------------------------------------------------ pip.ini: [section] key = value

    private static string? IniGet(string text, string section, string key)
    {
        string? current = null;
        foreach (var line in Lines(text))
        {
            var t = line.Trim();
            if (t.Length == 0 || IsComment(t)) continue;
            if (t.StartsWith('[') && t.EndsWith(']')) { current = t[1..^1].Trim(); continue; }
            if (!string.Equals(current, section, StringComparison.OrdinalIgnoreCase)) continue;
            var sep = t.IndexOfAny(new[] { '=', ':' });
            if (sep > 0 && NormalizeIniKey(t[..sep]) == NormalizeIniKey(key)) return Unquote(t[(sep + 1)..].Trim());
        }
        return null;
    }

    /// <summary>pip treats "cache-dir" and "cache_dir" alike.</summary>
    private static string NormalizeIniKey(string k) => k.Trim().Replace('_', '-').ToLowerInvariant();

    private static string IniSet(string text, string section, string key, string value)
    {
        var lines = Lines(text).ToList();
        var newline = text.Contains("\r\n") || text.Length == 0 ? "\r\n" : "\n";
        int sectionStart = -1, sectionEnd = lines.Count;
        for (int i = 0; i < lines.Count; i++)
        {
            var t = lines[i].Trim();
            if (!(t.StartsWith('[') && t.EndsWith(']'))) continue;
            if (sectionStart >= 0) { sectionEnd = i; break; }
            if (t[1..^1].Trim().Equals(section, StringComparison.OrdinalIgnoreCase)) sectionStart = i;
        }
        var entry = $"{key} = {value}";
        if (sectionStart < 0)
        {
            while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
            if (lines.Count > 0) lines.Add("");
            lines.Add($"[{section}]");
            lines.Add(entry);
            return string.Join(newline, lines) + newline;
        }
        bool done = false;
        for (int i = sectionStart + 1; i < sectionEnd; i++)
        {
            var t = lines[i].Trim();
            if (t.Length == 0 || IsComment(t)) continue;
            var sep = t.IndexOfAny(new[] { '=', ':' });
            if (sep <= 0 || NormalizeIniKey(t[..sep]) != NormalizeIniKey(key)) continue;
            if (!done) { lines[i] = entry; done = true; }
            else { lines.RemoveAt(i--); sectionEnd--; }
        }
        if (!done)
        {
            // Right after the section's last setting (before trailing blank lines).
            int at = sectionEnd;
            while (at - 1 > sectionStart && lines[at - 1].Trim().Length == 0) at--;
            lines.Insert(at, entry);
        }
        return string.Join(newline, lines).TrimEnd('\r', '\n') + newline;
    }

    // ------------------------------------------------------------------ NuGet.Config / Maven settings.xml

    private static XDocument? ParseXml(string text)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException) { return null; }
    }

    private static string Serialize(XDocument doc)
    {
        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(new StringWriter(sb), new XmlWriterSettings { OmitXmlDeclaration = doc.Declaration is null, Indent = false, NewLineHandling = NewLineHandling.None }))
            doc.Save(w);
        // XmlWriter over a StringWriter always says utf-16: these files are UTF-8 on disk.
        return sb.ToString().Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");
    }

    private static string? NuGetGet(string text, string key)
    {
        var doc = ParseXml(text);
        return doc?.Root?.Element("config")?.Elements("add")
            .LastOrDefault(e => string.Equals((string?)e.Attribute("key"), key, StringComparison.OrdinalIgnoreCase))?.Attribute("value")?.Value;
    }

    private static string? NuGetSet(string text, string key, string value)
    {
        if (text.Trim().Length == 0)
            return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<configuration>\r\n  <config>\r\n" +
                   $"    <add key=\"{Escape(key)}\" value=\"{Escape(value)}\" />\r\n  </config>\r\n</configuration>\r\n";
        var doc = ParseXml(text);
        if (doc?.Root is not { } root || root.Name.LocalName != "configuration") return null;
        var config = root.Element("config");
        if (config is null)
        {
            config = new XElement("config");
            root.AddFirst(config);
        }
        var adds = config.Elements("add").Where(e => string.Equals((string?)e.Attribute("key"), key, StringComparison.OrdinalIgnoreCase)).ToList();
        if (adds.Count == 0) config.Add(new XElement("add", new XAttribute("key", key), new XAttribute("value", value)));
        else
        {
            adds[0].SetAttributeValue("value", value);
            foreach (var extra in adds.Skip(1)) extra.Remove();
        }
        return Serialize(doc);
    }

    private static string? MavenGet(string text, string key)
    {
        var doc = ParseXml(text);
        return doc?.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == key)?.Value.Trim();
    }

    private static string? MavenSet(string text, string key, string value)
    {
        if (text.Trim().Length == 0)
            return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<settings xmlns=\"http://maven.apache.org/SETTINGS/1.2.0\">\r\n" +
                   $"  <{key}>{Escape(value)}</{key}>\r\n</settings>\r\n";
        var doc = ParseXml(text);
        if (doc?.Root is not { } root || root.Name.LocalName != "settings") return null;
        var existing = root.Elements().FirstOrDefault(e => e.Name.LocalName == key);
        if (existing is not null) existing.Value = value;
        else root.AddFirst(new XElement(root.Name.Namespace + key, value));
        return Serialize(doc);
    }

    private static string Escape(string s) => System.Security.SecurityElement.Escape(s) ?? "";

    // ------------------------------------------------------------------ helpers

    private static IEnumerable<string> Lines(string text)
        => text.Length == 0 ? Array.Empty<string>() : text.Replace("\r\n", "\n").Split('\n');

    private static string Unquote(string v) => v.Length >= 2 && (v[0] == '"' && v[^1] == '"' || v[0] == '\'' && v[^1] == '\'') ? v[1..^1] : v;
}
