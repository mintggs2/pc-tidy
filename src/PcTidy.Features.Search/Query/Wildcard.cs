namespace PcTidy.Features.Search.Query;

/// <summary><c>*</c> and <c>?</c> over a whole name or path, ignoring case (ordinal, the same on every PC).</summary>
public static class Wildcard
{
    public static bool HasWildcards(ReadOnlySpan<char> text) => text.IndexOfAny('*', '?') >= 0;

    public static bool IsMatch(ReadOnlySpan<char> text, ReadOnlySpan<char> pattern)
    {
        int t = 0, p = 0, starP = -1, starT = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || Same(pattern[p], text[t]))) { t++; p++; }
            else if (p < pattern.Length && pattern[p] == '*') { starP = p++; starT = t; }
            else if (starP >= 0) { p = starP + 1; t = ++starT; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    private static bool Same(char a, char b) => a == b || char.ToUpperInvariant(a) == char.ToUpperInvariant(b);
}
