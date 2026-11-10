using System.Text;

namespace PcTidy.Safety.Detection;

/// <summary>A node of Valve's KeyValues text format (libraryfolders.vdf, appmanifest_*.acf).</summary>
public sealed class VdfNode
{
    private readonly List<KeyValuePair<string, VdfNode>> _children = new();

    /// <summary>Value for leaf nodes ("key" "value"); null for blocks ("key" { ... }).</summary>
    public string? Value { get; }

    public VdfNode(string? value = null) => Value = value;

    public IReadOnlyList<KeyValuePair<string, VdfNode>> Children => _children;

    internal void Add(string key, VdfNode node) => _children.Add(new(key, node));

    /// <summary>First child with this key (case-insensitive, as Steam itself treats keys).</summary>
    public VdfNode? this[string key] => _children.FirstOrDefault(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

    public string? GetString(string key) => this[key]?.Value;

    public long? GetLong(string key) => long.TryParse(GetString(key), out var v) ? v : null;
}

/// <summary>
/// Tolerant reader for Valve's KeyValues text format. Handles quoted and bare tokens, escapes, // comments,
/// [$WIN32]-style conditionals (ignored), and files that are truncated or have unbalanced braces: whatever
/// parsed before the damage is returned. Never throws on bad input.
/// </summary>
public static class Vdf
{
    public static VdfNode Parse(string text)
    {
        var root = new VdfNode();
        var stack = new Stack<VdfNode>();
        stack.Push(root);
        string? pendingKey = null;
        var pos = 0;
        while (NextToken(text, ref pos, out var token, out var kind))
        {
            switch (kind)
            {
                case TokenKind.Open:
                    var block = new VdfNode();
                    stack.Peek().Add(pendingKey ?? "", block);
                    stack.Push(block);
                    pendingKey = null;
                    break;
                case TokenKind.Close:
                    if (stack.Count > 1) stack.Pop();
                    pendingKey = null;
                    break;
                case TokenKind.Conditional:
                    break;
                default:
                    if (pendingKey is null) pendingKey = token;
                    else { stack.Peek().Add(pendingKey, new VdfNode(token)); pendingKey = null; }
                    break;
            }
        }
        return root;
    }

    /// <summary>Reads and parses a file; null if it's missing or unreadable.</summary>
    public static VdfNode? TryLoad(string path)
    {
        try { return File.Exists(path) ? Parse(File.ReadAllText(path)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private enum TokenKind { Text, Open, Close, Conditional }

    private static bool NextToken(string s, ref int pos, out string token, out TokenKind kind)
    {
        token = ""; kind = TokenKind.Text;
        while (pos < s.Length)
        {
            var c = s[pos];
            if (char.IsWhiteSpace(c)) { pos++; continue; }
            if (c == '/' && pos + 1 < s.Length && s[pos + 1] == '/')
            {
                while (pos < s.Length && s[pos] != '\n') pos++;
                continue;
            }
            if (c == '{') { pos++; kind = TokenKind.Open; return true; }
            if (c == '}') { pos++; kind = TokenKind.Close; return true; }
            if (c == '[')
            {
                while (pos < s.Length && s[pos] != ']') pos++;
                pos++; kind = TokenKind.Conditional; return true;
            }
            if (c == '"') { token = ReadQuoted(s, ref pos); return true; }
            var start = pos;
            while (pos < s.Length && !char.IsWhiteSpace(s[pos]) && s[pos] is not ('{' or '}' or '"')) pos++;
            token = s[start..pos];
            return true;
        }
        return false;
    }

    private static string ReadQuoted(string s, ref int pos)
    {
        pos++; // opening quote
        var sb = new StringBuilder();
        while (pos < s.Length && s[pos] != '"')
        {
            if (s[pos] == '\\' && pos + 1 < s.Length)
            {
                var e = s[pos + 1];
                // Unknown escapes keep their backslash (a stray single "\" in a hand-edited path stays a separator).
                if (e is 'n' or 't' or '\\' or '"') sb.Append(e switch { 'n' => '\n', 't' => '\t', _ => e });
                else sb.Append('\\').Append(e);
                pos += 2;
                continue;
            }
            sb.Append(s[pos++]);
        }
        pos++; // closing quote (or end of a truncated file)
        return sb.ToString();
    }
}
