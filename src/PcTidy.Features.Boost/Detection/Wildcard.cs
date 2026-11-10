namespace PcTidy.Features.Boost.Detection;

/// <summary>Case-insensitive name patterns where "*" means "any characters" (e.g. "*CrashReporter*", "unins*").</summary>
internal static class Wildcard
{
    public static bool IsMatch(string text, string pattern)
    {
        var parts = pattern.Split('*');
        if (parts.Length == 1) return text.Equals(pattern, StringComparison.OrdinalIgnoreCase);
        int pos = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 0) continue;
            if (i == 0)
            {
                if (!text.StartsWith(part, StringComparison.OrdinalIgnoreCase)) return false;
                pos = part.Length;
                continue;
            }
            if (i == parts.Length - 1)
                return text.Length - pos >= part.Length && text.EndsWith(part, StringComparison.OrdinalIgnoreCase);
            var found = text.IndexOf(part, pos, StringComparison.OrdinalIgnoreCase);
            if (found < 0) return false;
            pos = found + part.Length;
        }
        return true;
    }

    public static bool MatchesAny(string text, IEnumerable<string> patterns) => patterns.Any(p => IsMatch(text, p));
}
