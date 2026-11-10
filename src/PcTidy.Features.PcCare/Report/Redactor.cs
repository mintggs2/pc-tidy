using System.Text.RegularExpressions;

namespace PcTidy.Features.PcCare.Report;

/// <summary>
/// Removes personal details from the exported health report: email addresses, the user name / profile folder name and the
/// PC's name (whole words, any case). The report is meant to be shareable when asking for help.
/// </summary>
public static partial class Redactor
{
    public const string Placeholder = "[hidden]";

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    /// <summary>Profile folders: "C:\Users\anything\" keeps the shape, hides the name.</summary>
    [GeneratedRegex(@"(?i)(\\Users\\)([^\\/<>""\r\n]+)")]
    private static partial Regex UserFolder();

    public static string Redact(string text, IEnumerable<string> terms)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var result = Email().Replace(text, Placeholder);
        result = UserFolder().Replace(result, m => IsPublicFolder(m.Groups[2].Value) ? m.Value : m.Groups[1].Value + Placeholder);
        foreach (var term in terms.Where(t => !string.IsNullOrWhiteSpace(t) && t.Trim().Length >= 3)
                                  .Select(t => t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(t => t.Length))
        {
            var pattern = @"(?<![A-Za-z0-9])" + Regex.Escape(term) + @"(?![A-Za-z0-9])";
            result = Regex.Replace(result, pattern, Placeholder, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        return result;
    }

    private static bool IsPublicFolder(string name)
        => name.Equals("Public", StringComparison.OrdinalIgnoreCase) || name.Equals("Default", StringComparison.OrdinalIgnoreCase)
           || name.Equals(Placeholder, StringComparison.Ordinal);
}
