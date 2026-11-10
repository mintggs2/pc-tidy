using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace PcTidy.Features.Organize.Rules;

/// <summary>Outcome of a pattern match. <see cref="TimedOut"/> means the pattern was too slow and counts as "no match".</summary>
public readonly record struct PatternResult(bool IsMatch, bool TimedOut, string? Error)
{
    public static readonly PatternResult No = new(false, false, null);
    public static readonly PatternResult Yes = new(true, false, null);
}

/// <summary>
/// Glob ("Screenshot*;Snip*", '?' and '*', ';' separates alternatives) and regex name matching.
/// Every regex runs with a match timeout so a user pattern like "(a+)+$" can never hang a scan.
/// </summary>
public static class NamePattern
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(200);

    private static readonly ConcurrentDictionary<(string, PatternSyntax, TimeSpan), Regex?> Cache = new();

    public static PatternResult Match(string fileName, string? pattern, PatternSyntax syntax, TimeSpan? timeout = null)
    {
        if (string.IsNullOrEmpty(pattern)) return PatternResult.No;
        var regex = Compile(pattern, syntax, timeout ?? DefaultTimeout, out var error);
        if (regex is null) return new PatternResult(false, false, error);
        try
        {
            return regex.IsMatch(fileName) ? PatternResult.Yes : PatternResult.No;
        }
        catch (RegexMatchTimeoutException)
        {
            return new PatternResult(false, true, "The pattern took too long, so it was treated as no match.");
        }
    }

    /// <summary>Checks a pattern for the rule editor. Returns null when valid.</summary>
    public static string? Validate(string? pattern, PatternSyntax syntax)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return "Type a name pattern.";
        Compile(pattern, syntax, DefaultTimeout, out var error);
        return error;
    }

    private static Regex? Compile(string pattern, PatternSyntax syntax, TimeSpan timeout, out string? error)
    {
        error = null;
        var key = (pattern, syntax, timeout);
        if (Cache.TryGetValue(key, out var cached))
        {
            if (cached is null) error = "That pattern isn't valid.";
            return cached;
        }
        Regex? regex;
        try
        {
            var source = syntax == PatternSyntax.Glob ? GlobToRegex(pattern) : pattern;
            regex = new Regex(source, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, timeout);
        }
        catch (ArgumentException)
        {
            regex = null;
            error = "That pattern isn't valid.";
        }
        if (Cache.Count < 512) Cache[key] = regex;
        return regex;
    }

    /// <summary>"Screenshot*;Snip?" → ^(?:Screenshot.*|Snip.)$ (whole-name match, like Explorer's search).</summary>
    internal static string GlobToRegex(string glob)
    {
        var parts = glob.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sb = new StringBuilder("^(?:");
        for (int p = 0; p < parts.Length; p++)
        {
            if (p > 0) sb.Append('|');
            foreach (var ch in parts[p])
            {
                sb.Append(ch switch
                {
                    '*' => ".*",
                    '?' => ".",
                    _ => Regex.Escape(ch.ToString()),
                });
            }
        }
        sb.Append(")$");
        return sb.ToString();
    }
}
