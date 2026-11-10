namespace PcTidy.Features.Games.Common;

/// <summary>
/// Minimal, lossless INI handling for game and app config files. Reading tolerates anything; writing changes exactly
/// one "Key=Value" line (or adds it) and keeps every other byte of the file, including comments, order and line endings.
/// Section and key names compare case-insensitively, as Windows' and Unreal's INI readers do.
/// </summary>
internal static class IniText
{
    /// <summary>Value of [section] key, or null.</summary>
    public static string? Get(string text, string section, string key)
    {
        string? current = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (IsSection(line, out var name)) { current = name; continue; }
            if (current is null || !current.Equals(section, StringComparison.OrdinalIgnoreCase)) continue;
            if (TrySplit(line, out var k, out var v) && k.Equals(key, StringComparison.OrdinalIgnoreCase)) return v;
        }
        return null;
    }

    /// <summary>Text with [section] key set to value. Adds the key (or the section) when missing.</summary>
    public static string Set(string text, string section, string key, string value)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = SplitKeepingEndings(text);
        int sectionStart = -1, lastContent = -1;
        string? current = null;
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Content.Trim();
            if (IsSection(line, out var name))
            {
                if (sectionStart >= 0) break; // left our section without finding the key
                current = name;
                if (name.Equals(section, StringComparison.OrdinalIgnoreCase)) { sectionStart = i; lastContent = i; }
                continue;
            }
            if (sectionStart < 0 || current is null) continue;
            if (line.Length > 0) lastContent = i;
            if (TrySplit(line, out var k, out _) && k.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                // Keep the file's own spelling of the key and its indentation.
                var indent = lines[i].Content[..(lines[i].Content.Length - lines[i].Content.TrimStart().Length)];
                lines[i] = lines[i] with { Content = indent + k + "=" + value };
                return Join(lines);
            }
        }

        if (sectionStart >= 0)
        {
            var at = lastContent;
            var ending = lines[at].Ending.Length > 0 ? lines[at].Ending : newline;
            if (lines[at].Ending.Length == 0) lines[at] = lines[at] with { Ending = newline };
            lines.Insert(at + 1, new Line(key + "=" + value, ending));
            return Join(lines);
        }

        var prefix = text.Length == 0 || text.EndsWith('\n') ? "" : newline;
        var gap = text.Length == 0 ? "" : newline;
        return text + prefix + gap + "[" + section + "]" + newline + key + "=" + value + newline;
    }

    private static bool IsSection(string line, out string name)
    {
        name = "";
        if (line.Length < 2 || line[0] != '[') return false;
        var close = line.IndexOf(']');
        if (close < 1) return false;
        name = line[1..close].Trim();
        return true;
    }

    private static bool TrySplit(string line, out string key, out string value)
    {
        key = value = "";
        if (line.Length == 0 || line[0] is ';' or '#') return false;
        var eq = line.IndexOf('=');
        if (eq <= 0) return false;
        key = line[..eq].Trim();
        value = line[(eq + 1)..].Trim();
        return key.Length > 0;
    }

    private readonly record struct Line(string Content, string Ending);

    private static List<Line> SplitKeepingEndings(string text)
    {
        var list = new List<Line>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            var end = i > start && text[i - 1] == '\r' ? i - 1 : i;
            list.Add(new Line(text[start..end], text[end..(i + 1)]));
            start = i + 1;
        }
        if (start < text.Length || list.Count == 0) list.Add(new Line(text[start..], ""));
        return list;
    }

    private static string Join(List<Line> lines) => string.Concat(lines.Select(l => l.Content + l.Ending));
}
