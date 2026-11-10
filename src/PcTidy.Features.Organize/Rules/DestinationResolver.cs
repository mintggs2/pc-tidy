using System.Globalization;
using System.Text.RegularExpressions;
using PcTidy.Core;

namespace PcTidy.Features.Organize.Rules;

/// <summary>Turns a rule's destination template into a real folder path.</summary>
public static partial class DestinationResolver
{
    /// <summary>
    /// Appended when the Year/Month option is on: Pictures\2026\2026-11. ISO style, so folders sort correctly
    /// and are named the same in every language and region.
    /// </summary>
    public const string DateSuffix = @"{year}\{year}-{month:00}";

    [GeneratedRegex(@"\{(year|month(?::00)?|monthName|day(?::00)?|date)\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();

    public static bool HasDateTokens(string template) => TokenRegex().IsMatch(template);

    /// <summary>
    /// Replaces {year} {month} (= {month:00}, two digits) {monthName} {day} {date}. Only {monthName}, which
    /// user-made rules may choose, is localized via <paramref name="culture"/>; numbers are always culture-invariant.
    /// </summary>
    public static string ExpandTokens(string template, DateTime date, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        return TokenRegex().Replace(template, m => m.Groups[1].Value.ToLowerInvariant() switch
        {
            "year" => date.ToString("yyyy", CultureInfo.InvariantCulture),
            "month" or "month:00" => date.ToString("MM", CultureInfo.InvariantCulture),
            "monthname" => culture.TextInfo.ToTitleCase(culture.DateTimeFormat.GetMonthName(date.Month)),
            "day" or "day:00" => date.ToString("dd", CultureInfo.InvariantCulture),
            _ => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        });
    }

    /// <summary>
    /// Destination folder for a move rule. Relative templates are inside <paramref name="root"/>;
    /// absolute templates are used as they are (user-made rules only; defaults are always relative).
    /// Returns null when the template is empty or tries to climb out of the root with "..".
    /// </summary>
    public static string? ResolveFolder(RuleAction action, string root, DateTime date, bool useDateFolders, CultureInfo? culture = null)
    {
        var template = action.Destination?.Trim() ?? "";
        if (template.Length == 0) return null;
        if (useDateFolders && !HasDateTokens(template)) template = template.TrimEnd('\\', '/') + "\\" + DateSuffix;

        var expanded = ExpandTokens(template, date, culture);
        string full;
        try
        {
            if (Path.IsPathFullyQualified(expanded))
            {
                full = PathUtil.Normalize(expanded);
            }
            else
            {
                var segments = expanded.Split('\\', '/', StringSplitOptions.RemoveEmptyEntries).Select(WindowsNames.CleanFolderSegment).ToArray();
                if (segments.Length == 0 || segments.Any(s => s is "." or ".." or "")) return null;
                full = PathUtil.Normalize(Path.Combine(root, Path.Combine(segments)));
                if (!PathUtil.IsUnder(full, root)) return null;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        return full;
    }
}
