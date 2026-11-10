using System.Text.RegularExpressions;
using PcTidy.Features.PcCare.Model;

namespace PcTidy.Features.PcCare.Parsing;

/// <summary>
/// Reads the table printed by <c>winget upgrade</c>. winget has no machine-readable output for upgrades and its column
/// headers are translated, so the header text is never used. Instead each row after the dashed separator is read from the
/// right, where the parts are never translated: Source ("winget"/"msstore"), Available, Version, Id (no spaces), then the
/// name. A row that doesn't fit (a cut-off "…" id, an unknown source) is counted as unreadable rather than guessed at;
/// a table that can't be read at all returns <see cref="WingetState.Unreadable"/> so the page says "Open winget" instead.
/// </summary>
public static partial class WingetTableParser
{
    private static readonly HashSet<string> Sources = new(StringComparer.Ordinal) { "winget", "msstore" };

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.\-_+]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^-{10,}\s*$")]
    private static partial Regex Separator();

    /// <param name="output">winget's standard output.</param>
    /// <param name="exitCode">winget's exit code (non-zero with no table = failed).</param>
    public static WingetFacts Parse(string? output, int exitCode)
    {
        var lines = (output ?? "").Replace("\r\n", "\n").Split('\n').Select(CleanLine).ToList();
        int sep = lines.FindIndex(l => Separator().IsMatch(l));
        if (sep < 0)
            return exitCode == 0 && !string.IsNullOrWhiteSpace(output)
                ? new WingetFacts(WingetState.Ready, Array.Empty<WingetUpgrade>())
                : new WingetFacts(WingetState.Failed, Array.Empty<WingetUpgrade>());

        var upgrades = new List<WingetUpgrade>();
        int unreadable = 0;
        for (int i = sep + 1; i < lines.Count; i++)
        {
            var line = lines[i];
            // The table ends at the first blank line (a summary line or a second "pinned" table may follow).
            if (string.IsNullOrWhiteSpace(line)) break;
            var row = TryParseRow(line);
            if (row is not null) upgrades.Add(row);
            else if (LooksLikeRow(line)) unreadable++;
        }

        if (upgrades.Count == 0 && unreadable > 0)
            return new WingetFacts(WingetState.Unreadable, Array.Empty<WingetUpgrade>(), unreadable);
        return new WingetFacts(WingetState.Ready, upgrades, unreadable);
    }

    internal static WingetUpgrade? TryParseRow(string line)
    {
        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (tokens.Count < 5) return null;

        string source = tokens[^1];
        if (!Sources.Contains(source)) return null;
        int at = tokens.Count - 2;
        string? available = TakeVersion(tokens, ref at);
        string? installed = TakeVersion(tokens, ref at);
        if (available is null || installed is null || at < 1) return null;

        string id = tokens[at];
        if (id.Contains('…') || id.EndsWith("...", StringComparison.Ordinal) || !IdPattern().IsMatch(id)) return null;
        // winget-source ids are Publisher.Package; Store ids are 12-character product ids.
        if (source == "winget" && !id.Contains('.')) return null;

        var name = string.Join(' ', tokens.Take(at)).Trim();
        if (name.Length == 0) return null;
        return new WingetUpgrade(name, id, installed, available, source);
    }

    /// <summary>A version token, joined with a leading "&lt;" or "&gt;" token winget sometimes prints ("&lt; 1.2").</summary>
    private static string? TakeVersion(List<string> tokens, ref int at)
    {
        if (at < 0) return null;
        var v = tokens[at];
        if (v.Contains('…') || !v.Any(char.IsLetterOrDigit)) return null;
        at--;
        if (at >= 0 && tokens[at] is "<" or ">")
        {
            v = tokens[at] + " " + v;
            at--;
        }
        return v;
    }

    private static bool LooksLikeRow(string line)
    {
        var t = line.TrimEnd();
        return t.EndsWith(" winget", StringComparison.Ordinal) || t.EndsWith(" msstore", StringComparison.Ordinal) || t.Contains('…');
    }

    /// <summary>winget draws a progress spinner with carriage returns and backspaces: keep only what's left on the line.</summary>
    private static string CleanLine(string raw)
    {
        var line = raw;
        int cr = line.LastIndexOf('\r');
        if (cr >= 0) line = cr == line.Length - 1 ? line[..cr] : line[(cr + 1)..];
        if (line.IndexOf('\b') >= 0) line = new string(line.Where(c => c != '\b').ToArray());
        return line.Replace(' ', ' ').TrimEnd();
    }
}
