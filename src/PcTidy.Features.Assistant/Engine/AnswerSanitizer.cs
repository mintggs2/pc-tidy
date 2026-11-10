using System.Text;
using System.Text.RegularExpressions;

namespace PcTidy.Features.Assistant.Engine;

/// <summary>
/// Cleans the model's plain-English answer before it is shown (as plain text, never as markup or links):
/// paths that aren't in this question's scan data are replaced, web links are removed, control and
/// text-direction characters are dropped, and the length is capped. The text is only ever displayed;
/// nothing in it is executed or followed.
/// </summary>
public static class AnswerSanitizer
{
    public const int MaxLength = 1500;
    public const string UnknownPath = "(a location that isn't in PC Tidy's scan)";
    public const string RemovedLink = "(link removed)";

    // From a path start up to the next whitespace (wildcards included). \G: anchored where we look.
    private static readonly Regex PathAt = new(@"\G(?:[A-Za-z]:\\|\\\\)[^\s""<>|]*", RegexOptions.CultureInvariant);
    private static readonly Regex Link = new(@"\b(?:https?|ftp|file|javascript|ms-[a-z\-]+):[^\s]*|\bwww\.[^\s]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string Clean(string? answer, IReadOnlySet<string> knownPaths)
    {
        if (string.IsNullOrWhiteSpace(answer)) return "";
        var text = StripControl(answer);
        text = Link.Replace(text, RemovedLink);
        text = ReplaceUnknownPaths(text, knownPaths);
        text = Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
        if (text.Length > MaxLength)
        {
            var cut = text.LastIndexOf(' ', MaxLength);
            text = text[..(cut > MaxLength / 2 ? cut : MaxLength)].TrimEnd() + "…";
        }
        return text;
    }

    /// <summary>
    /// Walks the text; wherever a path starts, keeps it only if it is exactly a known path (longest match first, so
    /// names with spaces work) that isn't followed by more path. "C:\Users\tester\Secret.txt" is NOT allowed just
    /// because "C:\Users\tester" is known. Everything else that looks like a path is replaced.
    /// </summary>
    private static string ReplaceUnknownPaths(string text, IReadOnlySet<string> knownPaths)
    {
        var known = knownPaths.Where(p => p.Length >= 3).OrderByDescending(p => p.Length).ToList();
        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            if (!StartsPath(text, i))
            {
                sb.Append(text[i++]);
                continue;
            }
            var match = known.FirstOrDefault(k => i + k.Length <= text.Length
                                                  && string.Compare(text, i, k, 0, k.Length, StringComparison.OrdinalIgnoreCase) == 0
                                                  && !ContinuesPath(text, i + k.Length));
            if (match is not null)
            {
                sb.Append(text, i, match.Length);
                i += match.Length;
                continue;
            }
            var m = PathAt.Match(text, i);
            sb.Append(UnknownPath);
            i += m.Success ? m.Length : 2;
        }
        return sb.ToString();
    }

    private static bool StartsPath(string t, int i)
    {
        if (i > 0 && (char.IsLetterOrDigit(t[i - 1]) || t[i - 1] == '\\')) return false;
        return (i + 2 < t.Length && char.IsAsciiLetter(t[i]) && t[i + 1] == ':' && t[i + 2] == '\\')
               || (i + 1 < t.Length && t[i] == '\\' && t[i + 1] == '\\');
    }

    /// <summary>More path follows: "\anything" (including wildcards), a letter or digit, or ".ext".</summary>
    private static bool ContinuesPath(string t, int j)
    {
        if (j >= t.Length) return false;
        var c = t[j];
        if (c == '\\') return j + 1 < t.Length && !char.IsWhiteSpace(t[j + 1]);
        bool nextIsWord = j + 1 < t.Length && (char.IsLetterOrDigit(t[j + 1]) || t[j + 1] is '_' or '$' or '~');
        return char.IsLetterOrDigit(c) || c is '_' or '$' or '~' or '*' or '?' || (c is '.' or '-' && nextIsWord);
    }

    private static string StripControl(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c == '\n') sb.Append(c);
            else if (c == '\t') sb.Append(' ');
            // Control characters, and bidi overrides that could make shown text read differently from what it is.
            else if (char.IsControl(c) || c is >= '\u202A' and <= '\u202E' || c is >= '\u2066' and <= '\u2069') continue;
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
