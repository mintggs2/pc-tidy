using System.Text;

namespace PcTidy.Features.Insights.Scripts;

/// <summary>Splits a command's arguments the way a shell would for reading purposes: quotes group words, | ; &amp;&amp; end the command.</summary>
public static class ArgTokens
{
    public static IReadOnlyList<string> Split(string text)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        char quote = '\0';
        bool inToken = false;
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0')
            {
                if (c == quote) { quote = '\0'; continue; }
                sb.Append(c);
                continue;
            }
            if (c is '"' or '\'')
            {
                quote = c;
                inToken = true;
                continue;
            }
            if (c is '|' or ';' || (c == '&' && i + 1 < text.Length && text[i + 1] == '&')) break;
            if (char.IsWhiteSpace(c))
            {
                if (inToken) tokens.Add(sb.ToString());
                sb.Clear();
                inToken = false;
                continue;
            }
            sb.Append(c);
            inToken = true;
        }
        if (inToken) tokens.Add(sb.ToString());
        return tokens;
    }

    /// <summary>Named PowerShell parameters (-Name value) plus the positional ones, matched by unique prefix.</summary>
    public sealed class PsArgs
    {
        private readonly Dictionary<string, string> _named = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Positional { get; } = new();
        public HashSet<string> Switches { get; } = new(StringComparer.OrdinalIgnoreCase);

        public static PsArgs Parse(IReadOnlyList<string> tokens, IReadOnlyCollection<string> valueParams)
        {
            var a = new PsArgs();
            for (int i = 0; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (t.Length > 1 && t[0] == '-' && !char.IsDigit(t[1]))
                {
                    var raw = t[1..];
                    string? inline = null;
                    var colon = raw.IndexOf(':');
                    if (colon > 0) { inline = raw[(colon + 1)..]; raw = raw[..colon]; }
                    var full = valueParams.FirstOrDefault(p => p.Equals(raw, StringComparison.OrdinalIgnoreCase))
                               ?? valueParams.FirstOrDefault(p => p.StartsWith(raw, StringComparison.OrdinalIgnoreCase));
                    if (full is null) { a.Switches.Add(raw); continue; }
                    if (inline is not null) a._named[full] = inline;
                    else if (i + 1 < tokens.Count) a._named[full] = tokens[++i];
                    continue;
                }
                a.Positional.Add(t);
            }
            return a;
        }

        public string? Get(string name) => _named.TryGetValue(name, out var v) ? v : null;

        /// <summary>Binds positional arguments to the parameters in <paramref name="order"/> that weren't given by name (as PowerShell does).</summary>
        public IReadOnlyDictionary<string, string?> Bind(params string[] order)
        {
            var bound = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            int next = 0;
            foreach (var p in order)
            {
                if (Get(p) is { } named) bound[p] = named;
                else bound[p] = next < Positional.Count ? Positional[next++] : null;
            }
            return bound;
        }
    }
}
