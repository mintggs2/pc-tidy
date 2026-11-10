using System.Text;
using System.Text.RegularExpressions;

namespace PcTidy.Features.Insights.Scripts;

/// <summary>
/// "Scan this script": reads a downloaded .ps1 / .bat / .cmd / .reg / .vbs / .js as TEXT and lists what it would change,
/// each change matched against the Tweaks research (proven / situational / placebo / harmful / unknown).
/// It never runs, imports, dot-sources or evaluates anything: the whole analysis is string matching on the file's text.
/// </summary>
public static partial class ScriptAnalyzer
{
    /// <summary>File types the scanner reads.</summary>
    public static readonly IReadOnlySet<string> SupportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".ps1", ".psm1", ".bat", ".cmd", ".reg", ".vbs", ".js",
    };

    public static ScriptLanguage? LanguageOf(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".ps1" or ".psm1" => ScriptLanguage.PowerShell,
        ".bat" or ".cmd" => ScriptLanguage.Batch,
        ".reg" => ScriptLanguage.RegFile,
        ".vbs" => ScriptLanguage.VbScript,
        ".js" => ScriptLanguage.JScript,
        _ => null,
    };

    /// <summary>Analyses the text of a script. <paramref name="fileName"/> picks the language (by extension).</summary>
    public static ScriptReport Analyze(string fileName, string text, bool truncated = false)
    {
        var language = LanguageOf(fileName) ?? ScriptLanguage.PowerShell;
        var rawLines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var findings = new List<ScriptFinding>();

        if (language == ScriptLanguage.RegFile)
        {
            var reg = RegFileParser.Parse(rawLines);
            findings.AddRange(reg.Writes.Where(w => w.Op != RegistryOp.CreateKey).Select(w => CatalogMatcher.ForRegistry(w)));
            findings.AddRange(WarningSigns.Scan(rawLines, Array.Empty<(int, string)>(), language, findings));
        }
        else
        {
            var payloads = new List<CommandRules.Payload>();
            findings.AddRange(ScanCode(rawLines, language, decoded: false, payloads));
            // One level of -EncodedCommand payloads, decoded as text and read the same way (never run).
            foreach (var p in payloads)
                findings.AddRange(ScanCode(p.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'), ScriptLanguage.PowerShell, decoded: true, new List<CommandRules.Payload>())
                    .Select(f => f with { Line = p.Line, What = f.What + " (hidden)" }));
            var code = LogicalLines(rawLines, language).ToList();
            findings = UpgradeDownloadThenRun(findings, code, language);
            findings = UpgradeDownloadThenStart(findings, text);
            findings = SafeModeRoundTrip(findings);
            findings.AddRange(WarningSigns.Scan(rawLines, code, language, findings));
        }

        findings = findings
            .GroupBy(f => (f.Line, f.What, f.FromDecodedPayload))
            .Select(g => g.OrderByDescending(f => f.Verdict).ThenByDescending(f => f.RedFlag).First())
            .OrderBy(f => f.Line)
            .ToList();

        var overall = OverallOf(findings);
        var (headline, advice) = Wording(overall, findings, fileName);
        return new ScriptReport
        {
            FileName = Path.GetFileName(fileName), Language = language, Findings = findings, Overall = overall,
            Headline = headline, Advice = advice, LinesRead = rawLines.Length, Truncated = truncated,
        };
    }

    private static IEnumerable<ScriptFinding> ScanCode(IReadOnlyList<string> rawLines, ScriptLanguage language, bool decoded, List<CommandRules.Payload> payloads)
    {
        var logical = LogicalLines(rawLines, language).ToList();
        bool mentionsHosts = logical.Any(l => l.Text.Contains("hosts", StringComparison.OrdinalIgnoreCase));
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (line, raw) in logical)
        {
            var text = raw;
            if (language == ScriptLanguage.PowerShell)
            {
                text = Substitute(text, variables);
                if (PsStringAssignment().Match(text) is { Success: true } a) variables[a.Groups["name"].Value] = a.Groups["value"].Value;
            }
            else if (language == ScriptLanguage.Batch)
            {
                text = SubstituteBatch(text, variables);
                if (BatchSet().Match(text) is { Success: true } s) variables[s.Groups["name"].Value] = s.Groups["value"].Value.Trim('"');
            }
            IReadOnlyList<ScriptFinding> found;
            // The newer patterns have a match timeout: a line that takes too long is reported as unreadable, never as clean.
            try { found = CommandRules.Scan(line, text, decoded, mentionsHosts, payloads).ToList(); }
            catch (RegexMatchTimeoutException) { found = new[] { WarningSigns.Unreadable(line, text) with { FromDecodedPayload = decoded } }; }
            foreach (var f in found) yield return f;
        }
    }

    /// <summary>
    /// Code downloaded into a variable on one line and run as text on a later one ($s = irm …; iex $s): the download is the
    /// same red flag as "irm … | iex" on one line.
    /// </summary>
    private static List<ScriptFinding> UpgradeDownloadThenRun(List<ScriptFinding> findings, IReadOnlyList<(int Line, string Text)> code, ScriptLanguage language)
    {
        try
        {
            var runs = new Dictionary<int, int>(); // download line → line that runs it
            for (int i = 0; i < code.Count; i++)
            {
                var (line, text) = code[i];
                if (text.Length > 20_000 || !CommandRules.Download().IsMatch(text) || CommandRules.RunText().IsMatch(text)) continue;
                var assign = language == ScriptLanguage.PowerShell ? PsAssignment().Match(text) : OtherAssignment().Match(text);
                if (!assign.Success) continue;
                var name = assign.Groups["name"].Value;
                var use = new Regex((language == ScriptLanguage.PowerShell ? @"\$\{?" : @"(?<![\w.])") + Regex.Escape(name) + @"(?!\w)",
                    RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(CommandRules.RegexTimeoutMs));
                for (int j = i + 1; j < code.Count; j++)
                {
                    if (code[j].Text.Length > 20_000) continue;
                    var later = CommandRules.Unbroken(code[j].Text); // i`e`x $s counts too
                    if (language != ScriptLanguage.PowerShell && OtherAssignment().Match(later) is { Success: true } re
                        && re.Groups["name"].Value.Equals(name, StringComparison.OrdinalIgnoreCase)) break; // reassigned
                    if (language == ScriptLanguage.PowerShell && PsAssignment().Match(later) is { Success: true } rp
                        && rp.Groups["name"].Value.Equals(name, StringComparison.OrdinalIgnoreCase)) break;
                    if (CommandRules.RunText().IsMatch(later) && use.IsMatch(later)) { runs[line] = code[j].Line; break; }
                }
            }
            if (runs.Count == 0) return findings;
            return findings.Select(f => f.Area == ScriptArea.Download && !f.RedFlag && !f.FromDecodedPayload && runs.TryGetValue(f.Line, out var at)
                ? f with
                {
                    Verdict = ScriptVerdict.Harmful, RedFlag = true,
                    What = f.What.Replace("Downloads a file", "Downloads code", StringComparison.Ordinal) + $" and runs it (line {at})",
                    Why = "It keeps the downloaded text and later runs it as code, so you can't see what it runs, and it can change between today and tomorrow. "
                          + "This is the most common way \"FPS boost\" and \"debloat\" scripts deliver malware.",
                }
                : f).ToList();
        }
        catch (RegexMatchTimeoutException)
        {
            return findings;
        }
    }

    /// <summary>
    /// Safe Mode switched on and switched off again in the same file is a planned trip (driver cleaners do this), not a
    /// flag left behind: still worth knowing, but not harmful.
    /// </summary>
    private static List<ScriptFinding> SafeModeRoundTrip(List<ScriptFinding> findings)
    {
        var off = findings.FirstOrDefault(f => f.Area == ScriptArea.BootSettings && f.What.StartsWith("Removes the boot setting 'safeboot'", StringComparison.OrdinalIgnoreCase));
        if (off is null) return findings;
        return findings.Select(f => f is { MatchedId: "boot.safeboot-left-on", Verdict: ScriptVerdict.Harmful } && !f.What.StartsWith("Removes", StringComparison.Ordinal)
            ? f with
            {
                Verdict = ScriptVerdict.Situational,
                Why = $"It also removes the Safe Mode setting again (line {off.Line}), so this looks like a planned restart into Safe Mode, for example to clean out a graphics driver. "
                      + "If the script stops before that line, every restart goes back to Safe Mode: press Win+R, type msconfig and untick Safe boot on the Boot tab.",
            }
            : f).ToList();
    }

    /// <summary>Joins continuation lines (PowerShell backtick, batch caret) and drops comments, keeping the first line number.</summary>
    internal static IEnumerable<(int Line, string Text)> LogicalLines(IReadOnlyList<string> lines, ScriptLanguage language)
    {
        var sb = new StringBuilder();
        int start = 0;
        bool inBlock = false;
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var t = line.Trim();
            // Block comments: <# … #> (PowerShell), /* … */ (JScript).
            if (language == ScriptLanguage.PowerShell || language == ScriptLanguage.JScript)
            {
                var open = language == ScriptLanguage.PowerShell ? "<#" : "/*";
                var close = language == ScriptLanguage.PowerShell ? "#>" : "*/";
                if (inBlock)
                {
                    var end = t.IndexOf(close, StringComparison.Ordinal);
                    if (end < 0) continue;
                    inBlock = false;
                    t = t[(end + close.Length)..].Trim();
                    line = t;
                }
                var startBlock = t.IndexOf(open, StringComparison.Ordinal);
                if (startBlock >= 0)
                {
                    var endBlock = t.IndexOf(close, startBlock + 2, StringComparison.Ordinal);
                    if (endBlock < 0) { inBlock = true; t = t[..startBlock].Trim(); }
                    else t = (t[..startBlock] + " " + t[(endBlock + close.Length)..]).Trim();
                    line = t;
                }
            }
            if (sb.Length == 0 && IsComment(t, language)) continue;
            if (sb.Length == 0) start = i + 1;

            char cont = language switch { ScriptLanguage.PowerShell => '`', ScriptLanguage.Batch => '^', _ => '\0' };
            var trimmedEnd = line.TrimEnd();
            if (cont != '\0' && trimmedEnd.EndsWith(cont))
            {
                sb.Append(trimmedEnd[..^1]).Append(' ');
                continue;
            }
            // VBScript continues with " _" at the end of a line.
            if (language == ScriptLanguage.VbScript && trimmedEnd.EndsWith(" _", StringComparison.Ordinal))
            {
                sb.Append(trimmedEnd[..^2]).Append(' ');
                continue;
            }
            sb.Append(line);
            var joined = sb.ToString();
            sb.Clear();
            if (joined.Trim().Length > 0) yield return (start, joined);
        }
        if (sb.Length > 0) yield return (start, sb.ToString());
    }

    private static bool IsComment(string t, ScriptLanguage language) => language switch
    {
        ScriptLanguage.PowerShell => t.StartsWith('#'),
        ScriptLanguage.Batch => t.StartsWith("::", StringComparison.Ordinal) || BatchRem().IsMatch(t),
        ScriptLanguage.VbScript => t.StartsWith('\'') || t.StartsWith("rem ", StringComparison.OrdinalIgnoreCase),
        ScriptLanguage.JScript => t.StartsWith("//", StringComparison.Ordinal),
        _ => false,
    };

    /// <summary>Replaces $name / ${name} with string values assigned earlier in the file (so "-Path $key" can be read).</summary>
    private static string Substitute(string text, IReadOnlyDictionary<string, string> vars)
    {
        if (vars.Count == 0 || !text.Contains('$')) return text;
        return PsVariable().Replace(text, m =>
        {
            var name = m.Groups["braced"].Success ? m.Groups["braced"].Value : m.Groups["plain"].Value;
            // Don't rewrite the assignment target itself.
            if (!vars.TryGetValue(name, out var v) || IsAssignmentTarget(text, m)) return m.Value;
            // Inside "…" the value is spliced in as text; on its own it stays one argument (paths have spaces).
            return InsideDoubleQuotes(text, m.Index) ? v : "'" + v + "'";
        });
    }

    private static bool InsideDoubleQuotes(string text, int index)
    {
        bool inside = false;
        for (int i = 0; i < index; i++)
            if (text[i] == '"' && (i == 0 || text[i - 1] != '`')) inside = !inside;
        return inside;
    }

    private static bool IsAssignmentTarget(string text, Match m)
        => text[..m.Index].Trim().Length == 0 && text[(m.Index + m.Length)..].TrimStart().StartsWith('=') && !text[(m.Index + m.Length)..].TrimStart().StartsWith("==", StringComparison.Ordinal);

    private static string SubstituteBatch(string text, IReadOnlyDictionary<string, string> vars)
        => vars.Count == 0 || !text.Contains('%') ? text : BatchVariable().Replace(text, m => vars.TryGetValue(m.Groups["name"].Value, out var v) ? v : m.Value);

    /// <summary>A script that downloads something and also starts a program anywhere: the download is the red flag.</summary>
    private static List<ScriptFinding> UpgradeDownloadThenStart(List<ScriptFinding> findings, string text)
    {
        if (!StartsProgram().IsMatch(text)) return findings;
        return findings.Select(f => f.Area == ScriptArea.Download && !f.RedFlag
            ? f with
            {
                Verdict = ScriptVerdict.Harmful, RedFlag = true, What = f.What.Replace("Downloads a file", "Downloads a program", StringComparison.Ordinal) + " and starts a program",
                Why = "It fetches a program from the internet and starts it, so what really runs isn't in this file. This is how fake \"FPS boost\" packs install malware.",
            }
            : f).ToList();
    }

    public static ScriptOverall OverallOf(IReadOnlyList<ScriptFinding> findings)
    {
        if (findings.Count == 0) return ScriptOverall.NothingRecognised;
        if (findings.Any(f => f.RedFlag || f.Verdict == ScriptVerdict.Harmful)) return ScriptOverall.Dangerous;
        if (findings.Any(f => f.Verdict == ScriptVerdict.Unknown)) return ScriptOverall.Unclear;
        // A non-red warning sign (no restore point first) isn't a change, so it doesn't tip "mostly placebo".
        var changes = findings.Where(f => f.Area != ScriptArea.WarningSign).ToList();
        int placebo = changes.Count(f => f.Verdict == ScriptVerdict.Placebo);
        return placebo > 0 && placebo >= changes.Count - placebo ? ScriptOverall.MostlyPlacebo : ScriptOverall.KnownSettings;
    }

    private const string FpsWarning =
        "Downloaded \"FPS boost\" and \"optimizer\" packs are a known way malware spreads (for example the \"Tweaks\" stealer pushed through YouTube videos). Nothing in them is needed for a fast PC.";

    private static (string Headline, string Advice) Wording(ScriptOverall overall, IReadOnlyList<ScriptFinding> findings, string fileName)
    {
        int n = findings.Count(f => f.Area != ScriptArea.WarningSign);
        int signs = findings.Count - n;
        string changes = n == 1 ? "1 change" : $"{n} changes";
        string found = signs == 0 ? changes : n == 0 ? Signs(signs) : $"{changes} and {Signs(signs)}";
        bool looksLikePack = OptimizerName().IsMatch(Path.GetFileNameWithoutExtension(fileName));
        string pack = looksLikePack || overall == ScriptOverall.Dangerous ? " " + FpsWarning : "";
        // The only warning sign that isn't a red flag: no restore point before many changes.
        string noRestorePoint = findings.Any(f => f.Area == ScriptArea.WarningSign && !f.RedFlag)
            ? " It doesn't make a restore point first, so make one yourself if you run it." : "";
        return overall switch
        {
            ScriptOverall.Dangerous => (
                $"Don't run this. PC Tidy found {found}; {findings.Count(f => f.RedFlag || f.Verdict == ScriptVerdict.Harmful)} of them harmful, hidden or a warning sign.",
                "Delete the file (or leave it unopened) and don't run it as administrator. If you already ran it, use Tweaks → Find bad tweaks to put Windows' defaults back, and run a Defender scan." + pack),
            ScriptOverall.Unclear => (
                $"PC Tidy can't vouch for this. It found {changes}; {findings.Count(f => f.Verdict == ScriptVerdict.Unknown)} aren't covered by its research.",
                "Only run it if you trust where it came from and know what each change does. Settings PC Tidy knows are in Tweaks, with one-click undo." + noRestorePoint + pack),
            ScriptOverall.MostlyPlacebo => (
                $"Mostly placebo. {changes} found; most of them make no measurable difference.",
                "There's little to gain from running it. The settings that do help are in Tweaks, with one-click undo." + noRestorePoint + pack),
            ScriptOverall.KnownSettings => (
                $"Known settings only. {changes} found, all covered by PC Tidy's research.",
                "You can make the same changes in Tweaks instead: same result, but PC Tidy records the old values so you can undo them." + noRestorePoint + pack),
            _ => (
                "PC Tidy didn't recognise any changes in this file.",
                "That doesn't mean it's safe: PC Tidy only knows common registry, service, boot, network and download commands. If you don't know what it does, don't run it." + pack),
        };
    }

    private static string Signs(int n) => n == 1 ? "1 warning sign" : $"{n} warning signs";

    [GeneratedRegex(@"^\s*\$(?<name>\w+)\s*=\s*['""](?<value>[^'""$]*)['""]\s*;?\s*$")]
    private static partial Regex PsStringAssignment();

    [GeneratedRegex(@"^\s{0,20}\$\{?(?<name>[A-Za-z_]\w{0,60})\}?\s{0,10}=(?!=)", RegexOptions.None, CommandRules.RegexTimeoutMs)]
    private static partial Regex PsAssignment();

    [GeneratedRegex(@"^\s{0,20}(?:(?:var|let|const|set|dim)\s{1,10})?(?<name>[A-Za-z_]\w{0,60})\s{0,10}=(?!=)", RegexOptions.IgnoreCase, CommandRules.RegexTimeoutMs)]
    private static partial Regex OtherAssignment();

    [GeneratedRegex(@"\$\{(?<braced>[^}]+)\}|\$(?<plain>[A-Za-z_]\w*)")]
    private static partial Regex PsVariable();

    [GeneratedRegex(@"^\s*@?set\s+""?(?<name>[A-Za-z_][\w]*)=(?<value>[^""]*)""?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex BatchSet();

    [GeneratedRegex(@"%(?<name>[A-Za-z_]\w*)%")]
    private static partial Regex BatchVariable();

    [GeneratedRegex(@"^@?rem(\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex BatchRem();

    [GeneratedRegex(@"Start-Process|\bsaps\b|\.Run\s*\(?\s*""|(?<![\w-])start\s+(?:""[^""]*""\s+)?[^\s]+\.exe|\.ShellExecute|cmd(?:\.exe)?\s+/c\s+[^\s]+\.exe", RegexOptions.IgnoreCase)] // boundary-ok: a text pattern matched against the script, nothing is called
    private static partial Regex StartsProgram();

    [GeneratedRegex(@"fps|boost|optimi[sz]|tweak|debloat|latency|lag|ping|booster", RegexOptions.IgnoreCase)]
    private static partial Regex OptimizerName();
}
