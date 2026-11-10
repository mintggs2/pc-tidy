using System.Text.RegularExpressions;

namespace PcTidy.Features.Apps.Leftovers;

/// <summary>
/// Turns display names and publishers into the plain names apps use for their folders
/// ("7-Zip 23.01 (x64)" → "7-Zip", "Contoso Software, Inc." → "Contoso Software"). Used for exact matching only.
/// </summary>
public static partial class AppNames
{
    [GeneratedRegex(@"\s*\((?:[^()]*)\)\s*$")]
    private static partial Regex TrailingParentheses();

    [GeneratedRegex(@"\s+(?:v(?:ersion)?\s*)?\d+(?:[.\-_]\d+)*[a-z]?$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingVersion();

    [GeneratedRegex(@"\s+(?:x64|x86|amd64|arm64|64-bit|32-bit|64 bit|32 bit|\(x64\)|\(x86\))$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingArchitecture();

    [GeneratedRegex(@"[,\s]+(?:Inc\.?|LLC|L\.L\.C\.|Ltd\.?|Limited|GmbH|Corporation|Corp\.?|Co\.|AB|AG|SA|S\.A\.|B\.V\.|BV|SAS|S\.r\.l\.|Pty|Oy|AS|ApS|KG)$", RegexOptions.IgnoreCase)]
    private static partial Regex LegalSuffix();

    /// <summary>Display name without version numbers, architecture and bracketed notes. Null if nothing is left.</summary>
    public static string? Clean(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return null;
        var s = displayName.Trim();
        for (int i = 0; i < 4; i++)
        {
            var before = s;
            s = TrailingParentheses().Replace(s, "");
            s = TrailingArchitecture().Replace(s, "");
            s = TrailingVersion().Replace(s, "");
            s = s.Trim().TrimEnd('-', '–', ',', ':').Trim();
            if (s == before) break;
        }
        return s.Length == 0 ? null : s;
    }

    /// <summary>Publisher without "Inc.", "Ltd", "GmbH"... Null if nothing is left.</summary>
    public static string? CleanPublisher(string? publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher)) return null;
        var s = publisher.Trim().TrimEnd('.');
        for (int i = 0; i < 3; i++)
        {
            var before = s;
            s = LegalSuffix().Replace(s, "").Trim().TrimEnd(',', '.').Trim();
            if (s == before) break;
        }
        return s.Length == 0 ? null : s;
    }

    public static bool Same(string? a, string? b)
        => a is not null && b is not null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
