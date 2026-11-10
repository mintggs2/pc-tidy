using System.Globalization;
using System.Text;
using PcTidy.Features.AiChats.Model;

namespace PcTidy.Features.AiChats.Library;

/// <summary>
/// Offline topic labels for web chats (no AI, nothing leaves the PC): each chat's words are counted (title words
/// count three times), weighted by TF-IDF across the library, and the best word that the chat shares with at least one
/// other chat becomes its topic ("Python", "Garden"). Agent chats are grouped by their project folder instead.
/// A topic the user typed always wins over the computed one.
/// </summary>
public static class TopicLabeler
{
    public const string OtherTopic = "Other";
    private const int TitleWeight = 3;
    private const int MaxTerms = 40;
    private const int AssistantChars = 1500;

    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "the","and","for","you","your","are","was","were","with","that","this","have","has","had","not","but","can","could","would","should",
        "will","just","what","when","where","which","who","why","how","all","any","some","more","most","other","into","than","then","them",
        "they","their","there","here","about","also","from","been","being","its","it's","our","out","use","using","used","like","make","made",
        "get","got","one","two","three","new","way","want","need","does","did","doing","done","yes","please","thanks","thank","help","know",
        "let","may","might","must","very","much","many","each","such","only","own","same","both","few","now","over","under","again","off",
        "too","because","while","after","before","between","through","during","without","within","these","those","his","her","she","him",
        "hello","okay","sure","think","good","great","really","something","anything","thing","things","see","look","give","tell","show",
        "write","code","file","files","chat","question","answer","example","here's","i'm","don't","can't","it's","you're","let's","i've",
        "able","well","still","even","first","last","time","work","working","try","trying","change","changes","into","onto","via","per",
        "der","die","das","und","ist","nicht","ein","eine","les","des","une","est","pour","que","con","para","por","los","las","del",
    };

    /// <summary>Word counts for one chat (title ×3, user messages, the start of each answer), top <see cref="MaxTerms"/>.</summary>
    public static Dictionary<string, int> Terms(string title, IEnumerable<ChatMessage> messages)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var w in Words(title)) counts[w] = counts.GetValueOrDefault(w) + TitleWeight;
        foreach (var m in messages)
        {
            if (m.Role == ChatRole.Tool) continue;
            var text = m.Role == ChatRole.Assistant && m.Text.Length > AssistantChars ? m.Text[..AssistantChars] : m.Text;
            foreach (var w in Words(text)) counts[w] = counts.GetValueOrDefault(w) + 1;
        }
        return counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(MaxTerms)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }

    /// <summary>Lower-case words of 3–24 letters/digits that aren't stop words, pure numbers, or masked secrets.</summary>
    public static IEnumerable<string> Words(string? text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        var sb = new StringBuilder();
        foreach (var ch in text.Append(' '))
        {
            if (char.IsLetterOrDigit(ch) || ch == '\'' && sb.Length > 0) { sb.Append(char.ToLowerInvariant(ch)); continue; }
            if (sb.Length is >= 3 and <= 24)
            {
                var w = sb.ToString().Trim('\'');
                if (w.Length >= 3 && !Stop.Contains(w) && !w.All(char.IsDigit) && !LooksRandom(w)) yield return w;
            }
            sb.Clear();
        }
    }

    /// <summary>Hex ids, hashes and similar noise ("a3f9c2e1d0").</summary>
    private static bool LooksRandom(string w) => w.Length >= 10 && w.Count(char.IsDigit) >= 3 && w.Any(char.IsLetter);

    /// <summary>Topic per chat id, from every web chat's terms. Small libraries (under 3 chats) use each chat's top word.</summary>
    public static Dictionary<string, (string Topic, IReadOnlyList<string> Keywords)> Label(IReadOnlyDictionary<string, Dictionary<string, int>> docs)
    {
        var df = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var terms in docs.Values)
            foreach (var t in terms.Keys) df[t] = df.GetValueOrDefault(t) + 1;
        int n = docs.Count;
        var result = new Dictionary<string, (string, IReadOnlyList<string>)>(StringComparer.Ordinal);
        foreach (var (id, terms) in docs)
        {
            var scored = terms.Select(kv => (Term: kv.Key, Score: kv.Value * Idf(n, df[kv.Key]), Df: df[kv.Key]))
                .OrderByDescending(x => x.Score).ThenBy(x => x.Term, StringComparer.Ordinal).ToList();
            var keywords = scored.Take(5).Select(x => x.Term).ToList();
            // A topic is only useful if other chats share it; very common words (in most chats) say nothing.
            var shared = scored.FirstOrDefault(x => x.Df >= 2 && (n < 5 || x.Df <= n * 0.6));
            var label = shared.Term ?? (n < 3 ? scored.FirstOrDefault().Term : null);
            result[id] = (label is null ? OtherTopic : Display(label), keywords);
        }
        return result;
    }

    /// <summary>Cosine similarity of two term sets (for "related chats" links).</summary>
    public static double Similarity(IReadOnlyDictionary<string, int> a, IReadOnlyDictionary<string, int> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        double dot = 0;
        foreach (var (t, x) in a) if (b.TryGetValue(t, out var y)) dot += x * (double)y;
        if (dot == 0) return 0;
        static double Norm(IReadOnlyDictionary<string, int> v) => Math.Sqrt(v.Values.Sum(x => (double)x * x));
        return dot / (Norm(a) * Norm(b));
    }

    private static double Idf(int n, int df) => Math.Log((1.0 + n) / (1.0 + df)) + 1.0;

    private static string Display(string term) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(term);

    public static string EncodeTerms(IReadOnlyDictionary<string, int> terms)
        => string.Join(' ', terms.Select(kv => kv.Key + ":" + kv.Value.ToString(CultureInfo.InvariantCulture)));

    public static Dictionary<string, int> DecodeTerms(string? encoded)
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(encoded)) return d;
        foreach (var pair in encoded.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var cut = pair.LastIndexOf(':');
            if (cut > 0 && int.TryParse(pair[(cut + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var c)) d[pair[..cut]] = c;
        }
        return d;
    }
}
