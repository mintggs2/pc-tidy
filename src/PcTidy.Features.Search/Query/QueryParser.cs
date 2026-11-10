using System.Text;

namespace PcTidy.Features.Search.Query;

/// <summary>
/// Everything-style query syntax (a subset), parsed the same on every PC whatever its language:
/// <list type="bullet">
/// <item>space = AND, <c>|</c> = OR (binds tighter than space, like Everything), <c>!</c> = NOT, <c>&lt; &gt;</c> = grouping</item>
/// <item><c>"quoted text"</c> keeps spaces and stops <c>|</c>, <c>!</c> and <c>:</c> having a meaning</item>
/// <item><c>*</c>/<c>?</c> wildcards match the whole name; a word with <c>\</c> matches the full path</item>
/// <item><c>ext:jpg;png</c> · <c>size:&gt;1gb</c> · <c>dm:lastweek</c> · <c>path:</c> · <c>folder:</c> · <c>file:</c> · <c>dupe:</c> (same name and size)</item>
/// </list>
/// Filter names are matched ignoring case with ordinal rules (no Turkish dotless-i surprises).
/// </summary>
public static class QueryParser
{
    private enum TokenKind { Word, Or, Not, Open, Close }

    /// <param name="ColonAt">Position of the first colon outside quotes in <paramref name="Text"/> (-1 = none).</param>
    private readonly record struct Token(TokenKind Kind, string Text = "", int ColonAt = -1, bool Quoted = false);

    public static ParsedQuery Parse(string? text, TimeProvider? clock = null)
    {
        clock ??= TimeProvider.System;
        text ??= "";
        var warnings = new List<string>();
        var tokens = Lex(text);
        int pos = 0;
        bool dupe = false;
        var root = ParseAnd(tokens, ref pos, depth: 0, clock, warnings, ref dupe);
        if (root is null && dupe) root = MatchAllNode.Instance;
        return new ParsedQuery(text, root, dupe, warnings);
    }

    // ------------------------------------------------------------------ lexer

    private static List<Token> Lex(string s)
    {
        var tokens = new List<Token>();
        int i = 0, depth = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '|') { tokens.Add(new Token(TokenKind.Or)); i++; continue; }
            if (c == '!') { tokens.Add(new Token(TokenKind.Not)); i++; continue; }
            if (c == '<') { tokens.Add(new Token(TokenKind.Open)); depth++; i++; continue; }
            if (c == '>' && depth > 0) { tokens.Add(new Token(TokenKind.Close)); depth--; i++; continue; }

            var word = new StringBuilder();
            bool inQuotes = false, quoted = false;
            int colonAt = -1;
            while (i < s.Length)
            {
                c = s[i];
                if (c == '"') { inQuotes = !inQuotes; quoted = true; i++; continue; }
                if (!inQuotes)
                {
                    if (char.IsWhiteSpace(c) || c == '|') break;
                    // '>' closes a group unless it's a comparison right after "size:" / "dm:" or another operator.
                    if (c == '>' && depth > 0 && !(word.Length > 0 && word[^1] is ':' or '<' or '>' or '=')) break;
                    if (c == ':' && colonAt < 0) colonAt = word.Length;
                }
                word.Append(c);
                i++;
            }
            tokens.Add(new Token(TokenKind.Word, word.ToString(), colonAt, quoted));
        }
        return tokens;
    }

    // ------------------------------------------------------------------ grammar

    private static QueryNode? ParseAnd(List<Token> t, ref int pos, int depth, TimeProvider clock, List<string> warnings, ref bool dupe)
    {
        var items = new List<QueryNode>();
        while (pos < t.Count)
        {
            if (t[pos].Kind == TokenKind.Close)
            {
                pos++;
                if (depth > 0) break;
                continue; // stray '>' outside a group: ignored
            }
            if (t[pos].Kind == TokenKind.Or) { pos++; continue; } // "|a" or "a | | b": nothing to join
            var node = ParseOr(t, ref pos, depth, clock, warnings, ref dupe);
            if (node is not null) items.Add(node);
        }
        return items.Count switch { 0 => null, 1 => items[0], _ => new AndNode(items) };
    }

    private static QueryNode? ParseOr(List<Token> t, ref int pos, int depth, TimeProvider clock, List<string> warnings, ref bool dupe)
    {
        var first = ParseUnary(t, ref pos, depth, clock, warnings, ref dupe);
        var items = first is null ? new List<QueryNode>() : new List<QueryNode> { first };
        while (pos < t.Count && t[pos].Kind == TokenKind.Or)
        {
            pos++;
            if (pos >= t.Count || t[pos].Kind is TokenKind.Or or TokenKind.Close) continue;
            var next = ParseUnary(t, ref pos, depth, clock, warnings, ref dupe);
            if (next is not null) items.Add(next);
        }
        return items.Count switch { 0 => null, 1 => items[0], _ => new OrNode(items) };
    }

    private static QueryNode? ParseUnary(List<Token> t, ref int pos, int depth, TimeProvider clock, List<string> warnings, ref bool dupe)
    {
        if (pos >= t.Count) return null;
        var token = t[pos];
        switch (token.Kind)
        {
            case TokenKind.Not:
                pos++;
                var inner = ParseUnary(t, ref pos, depth, clock, warnings, ref dupe);
                return inner is null or MatchAllNode ? null : new NotNode(inner);
            case TokenKind.Open:
                pos++;
                return ParseAnd(t, ref pos, depth + 1, clock, warnings, ref dupe);
            case TokenKind.Word:
                pos++;
                return Term(token, clock, warnings, ref dupe);
            default:
                pos++;
                return null;
        }
    }

    // ------------------------------------------------------------------ terms

    private static QueryNode? Term(Token token, TimeProvider clock, List<string> warnings, ref bool dupe)
    {
        var text = token.Text;
        if (text.Length == 0) return null;
        // "name:value" with a known name (2+ letters: "C:\Users" is a path, not a filter).
        if (token.ColonAt >= 2 && IsLetters(text.AsSpan(0, token.ColonAt)))
        {
            var name = text[..token.ColonAt].ToLowerInvariant();
            var value = text[(token.ColonAt + 1)..];
            var node = Filter(name, value, clock, warnings, ref dupe, out bool known);
            if (known) return node;
            warnings.Add($"\"{text[..token.ColonAt]}:\" isn't a filter PC Tidy knows, so it's searched as text.");
        }
        return Word(text);
    }

    private static QueryNode Word(string text)
        => new TextNode(text, Wildcard.HasWildcards(text), text.Contains('\\'));

    private static QueryNode? Filter(string name, string value, TimeProvider clock, List<string> warnings, ref bool dupe, out bool known)
    {
        known = true;
        switch (name)
        {
            case "ext":
                var exts = value.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(e => e.TrimStart('.').ToLowerInvariant()).Where(e => e.Length > 0).Distinct(StringComparer.Ordinal).ToList();
                return new ExtNode(exts);
            case "size":
                if (SizeSpec.TryParse(value, out var min, out var max)) return new SizeNode(min, max);
                warnings.Add($"\"size:{value}\" isn't a size PC Tidy understands. Try size:>1gb or size:10mb..50mb.");
                return null;
            case "dm":
            case "datemodified":
                if (DateSpec.TryParse(value, clock, out var from, out var to)) return new DateNode(from, to);
                warnings.Add($"\"dm:{value}\" isn't a date PC Tidy understands. Try dm:today, dm:lastweek or dm:2026-11.");
                return null;
            case "path":
                if (value.Length == 0) return null;
                return new PathNode(value, Wildcard.HasWildcards(value));
            case "folder":
            case "file":
                return new KindNode(name == "folder", value.Length == 0 ? null : Word(value));
            case "dupe":
                dupe = true;
                if (value.Length > 0) warnings.Add("dupe: always means \"same name and size\"; the text after it was ignored.");
                return null;
            default:
                known = false;
                return null;
        }
    }

    private static bool IsLetters(ReadOnlySpan<char> s)
    {
        foreach (var c in s) if (!char.IsAsciiLetter(c)) return false;
        return true;
    }
}
