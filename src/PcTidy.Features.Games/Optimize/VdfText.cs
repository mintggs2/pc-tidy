using System.Text;

namespace PcTidy.Features.Games.Optimize;

/// <summary>
/// Lossless editing of Valve KeyValues text (Steam's localconfig.vdf). Finds a block by its key path and changes or adds
/// one string value, keeping every other character of the file exactly as Steam wrote it.
/// </summary>
internal static class VdfText
{
    private enum Kind { Text, Open, Close }

    private readonly record struct Token(Kind Kind, string Value, int Start, int End);

    private sealed class Node
    {
        public required string Key { get; init; }
        public Token? ValueToken { get; init; }
        public int OpenIndex { get; init; } = -1;
        public int CloseIndex { get; set; } = -1;
        public List<Node> Children { get; } = new();
        public bool IsBlock => OpenIndex >= 0;
    }

    /// <summary>Path from the file root to a Steam app's block in localconfig.vdf ("apps" is spelled "Apps" by some Steam versions).</summary>
    public static readonly string[] AppsPath = { "UserLocalConfigStore", "Software", "Valve", "Steam", "apps" };

    /// <summary>The string value at <paramref name="path"/> + <paramref name="key"/>, or null if any part is missing.</summary>
    public static string? GetValue(string text, IReadOnlyList<string> path, string key)
    {
        var (tokens, root) = Parse(text);
        var block = Find(root, path);
        var leaf = block?.Children.LastOrDefault(c => !c.IsBlock && Same(c.Key, key));
        return leaf?.ValueToken?.Value;
    }

    /// <summary>True when the block at <paramref name="path"/> exists.</summary>
    public static bool HasBlock(string text, IReadOnlyList<string> path) => Find(Parse(text).Root, path) is not null;

    /// <summary>
    /// New text with <paramref name="key"/> set to <paramref name="value"/> inside the block at <paramref name="path"/>.
    /// The last path segment is created as a new block if its parent exists (a game Steam hasn't stored settings for yet).
    /// Null when the structure isn't there (PC Tidy then only explains how to set it in Steam).
    /// </summary>
    public static string? WithValue(string text, IReadOnlyList<string> path, string key, string value)
    {
        var (tokens, root) = Parse(text);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var quoted = Quote(value);

        var block = Find(root, path);
        if (block is not null)
        {
            var leaf = block.Children.LastOrDefault(c => !c.IsBlock && Same(c.Key, key));
            if (leaf?.ValueToken is { } v) return text[..v.Start] + quoted + text[v.End..];
            if (block.CloseIndex < 0) return null; // truncated file: don't guess
            return InsertBeforeClose(text, tokens[block.CloseIndex], 1, Quote(key) + "\t\t" + quoted, newline);
        }

        var parent = path.Count > 1 ? Find(root, path.Take(path.Count - 1).ToList()) : null;
        if (parent is null || parent.CloseIndex < 0) return null;
        var close = tokens[parent.CloseIndex];
        var indent = IndentOf(text, close.Start) + "\t";
        var lines = new StringBuilder()
            .Append(Quote(path[^1])).Append(newline)
            .Append(indent).Append('{').Append(newline)
            .Append(indent).Append('\t').Append(Quote(key)).Append("\t\t").Append(quoted).Append(newline)
            .Append(indent).Append('}');
        return InsertBeforeClose(text, close, 1, lines.ToString(), newline);
    }

    /// <summary>Inserts a line (already indented after its first line) just above the closing brace of a block.</summary>
    private static string InsertBeforeClose(string text, Token close, int extraIndent, string content, string newline)
    {
        var braceIndent = IndentOf(text, close.Start);
        var lineStart = close.Start - braceIndent.Length;
        var childIndent = braceIndent + new string('\t', extraIndent);
        var atLineStart = lineStart == 0 || text[lineStart - 1] == '\n';
        if (atLineStart)
            return text[..lineStart] + childIndent + content + newline + text[lineStart..];
        // "{ }" on one line: break it open.
        return text[..close.Start] + newline + childIndent + content + newline + braceIndent + text[close.Start..];
    }

    /// <summary>Tabs/spaces between the start of the line and <paramref name="pos"/> (empty if other text precedes it).</summary>
    private static string IndentOf(string text, int pos)
    {
        int i = pos;
        while (i > 0 && text[i - 1] is ' ' or '\t') i--;
        return i == 0 || text[i - 1] == '\n' ? text[i..pos] : "";
    }

    public static string Quote(string s) => "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    private static Node? Find(Node root, IReadOnlyList<string> path)
    {
        var node = root;
        foreach (var segment in path)
        {
            node = node.Children.FirstOrDefault(c => c.IsBlock && Same(c.Key, segment));
            if (node is null) return null;
        }
        return node;
    }

    private static (List<Token> Tokens, Node Root) Parse(string text)
    {
        var tokens = Tokenize(text);
        var root = new Node { Key = "", OpenIndex = int.MaxValue };
        var stack = new Stack<Node>();
        stack.Push(root);
        string? pendingKey = null;
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            switch (t.Kind)
            {
                case Kind.Open:
                    var block = new Node { Key = pendingKey ?? "", OpenIndex = i };
                    stack.Peek().Children.Add(block);
                    stack.Push(block);
                    pendingKey = null;
                    break;
                case Kind.Close:
                    if (stack.Count > 1) stack.Pop().CloseIndex = i;
                    pendingKey = null;
                    break;
                default:
                    if (pendingKey is null) pendingKey = t.Value;
                    else
                    {
                        stack.Peek().Children.Add(new Node { Key = pendingKey, ValueToken = t });
                        pendingKey = null;
                    }
                    break;
            }
        }
        return (tokens, root);
    }

    private static List<Token> Tokenize(string s)
    {
        var list = new List<Token>();
        int pos = 0;
        while (pos < s.Length)
        {
            var c = s[pos];
            if (char.IsWhiteSpace(c)) { pos++; continue; }
            if (c == '/' && pos + 1 < s.Length && s[pos + 1] == '/') { while (pos < s.Length && s[pos] != '\n') pos++; continue; }
            if (c == '[') { while (pos < s.Length && s[pos] != ']') pos++; pos++; continue; } // [$WIN32] conditionals
            if (c == '{') { list.Add(new Token(Kind.Open, "{", pos, pos + 1)); pos++; continue; }
            if (c == '}') { list.Add(new Token(Kind.Close, "}", pos, pos + 1)); pos++; continue; }
            int start = pos;
            if (c == '"')
            {
                var sb = new StringBuilder();
                pos++;
                while (pos < s.Length && s[pos] != '"')
                {
                    if (s[pos] == '\\' && pos + 1 < s.Length)
                    {
                        var e = s[pos + 1];
                        if (e is 'n' or 't' or '\\' or '"') sb.Append(e switch { 'n' => '\n', 't' => '\t', _ => e });
                        else sb.Append('\\').Append(e);
                        pos += 2;
                        continue;
                    }
                    sb.Append(s[pos++]);
                }
                pos = Math.Min(pos + 1, s.Length);
                list.Add(new Token(Kind.Text, sb.ToString(), start, pos));
                continue;
            }
            while (pos < s.Length && !char.IsWhiteSpace(s[pos]) && s[pos] is not ('{' or '}' or '"')) pos++;
            list.Add(new Token(Kind.Text, s[start..pos], start, pos));
        }
        return list;
    }
}
