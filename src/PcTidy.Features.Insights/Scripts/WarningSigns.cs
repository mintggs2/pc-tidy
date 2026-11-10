using System.Text.RegularExpressions;

namespace PcTidy.Features.Insights.Scripts;

/// <summary>
/// Whole-file warning signs for "Scan this script": how a script behaves, rather than one setting it changes.
/// Like every other rule these only read the file's text (lines matched against bounded patterns with a match timeout);
/// nothing is run, imported or evaluated.
/// <list type="bullet">
/// <item>an overall "score" (870/1000, "97% optimized") with no per-change breakdown;</item>
/// <item>an "apply all" / one-click step;</item>
/// <item>a restore point made after the system changes, or announced and never made;</item>
/// <item>scrambled (Base64) data kept in pieces that are joined later;</item>
/// <item>text that walks you past the SmartScreen warning.</item>
/// </list>
/// </summary>
internal static partial class WarningSigns
{
    /// <summary>Lines longer than this are data blobs (the hidden-code rules flag those), not wording to read.</summary>
    private const int MaxLine = 20_000;

    /// <summary>System changes (registry, services, boot) a script may make before "no restore point first" is mentioned.</summary>
    internal const int ManyChanges = 5;

    /// <summary>Above this many lines the call-order reading (functions, batch labels) is skipped and file order is used.</summary>
    private const int MaxFlowLines = 20_000;

    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(CommandRules.RegexTimeoutMs);

    public static IReadOnlyList<ScriptFinding> Scan(IReadOnlyList<string> rawLines, IReadOnlyList<(int Line, string Text)> code, ScriptLanguage language, IReadOnlyList<ScriptFinding> found)
    {
        var signs = new List<ScriptFinding>();
        void Run(Func<ScriptFinding?> rule)
        {
            // A pattern that runs out of time means "couldn't read", never "nothing there".
            try { if (rule() is { } f) signs.Add(f); }
            catch (RegexMatchTimeoutException) { signs.Add(Unreadable(1, rawLines.Count > 0 ? rawLines[0] : "")); }
        }
        Run(() => SmartScreenCoaching(rawLines));
        if (language != ScriptLanguage.RegFile)
        {
            Run(() => OpaqueScore(code));
            Run(() => ApplyAll(code));
            Run(() => RestorePointOrder(code, language, found));
            Run(() => DataInPieces(code));
        }
        return signs.GroupBy(s => s.What).Select(g => g.First()).ToList();
    }

    /// <summary>A line (or part of the file) PC Tidy gave up on because a pattern took too long.</summary>
    internal static ScriptFinding Unreadable(int line, string evidence) => new()
    {
        Line = line, Area = ScriptArea.Other, What = "Part of the file was too tangled to read", Verdict = ScriptVerdict.Unknown,
        Why = "PC Tidy stops reading a line that takes too long to check, so it can't say what this part does. Very long, tangled lines are typical of scripts that hide their real work.",
        Evidence = CatalogMatcher.Shorten(evidence),
    };

    private static ScriptFinding Sign(int line, string evidence, string what, string why, ScriptVerdict verdict = ScriptVerdict.Unknown, bool red = true, ScriptArea area = ScriptArea.WarningSign)
        => new() { Line = line, Area = area, What = what, Verdict = verdict, Why = why, Evidence = CatalogMatcher.Shorten(evidence), RedFlag = red };

    // ================================================================== SmartScreen coaching (comments count: that's where it's usually written)

    private static ScriptFinding? SmartScreenCoaching(IReadOnlyList<string> raw)
    {
        for (int i = 0; i < raw.Count; i++)
            if (raw[i].Length <= MaxLine && SmartScreenText().IsMatch(raw[i]))
                return Sign(i + 1, raw[i], "Tells you how to click past the SmartScreen warning",
                    "\"Windows protected your PC\" means SmartScreen hasn't seen this program often enough to trust it yet. Text that walks you through More info → Run anyway "
                    + "asks you to skip the one check that happens before anything starts. If you didn't expect the warning, choose Don't run.");
        return null;
    }

    // ================================================================== an overall score with no breakdown

    private static ScriptFinding? OpaqueScore(IReadOnlyList<(int Line, string Text)> code)
    {
        (int Line, string? Text) score = (0, null);
        int itemLines = 0;
        foreach (var (line, text) in code)
        {
            if (text.Length > MaxLine || !Prints().IsMatch(text)) continue;
            bool isScore = ScoreWord().IsMatch(text) && ScoreValue().IsMatch(text);
            if (isScore) { score.Text ??= text; if (score.Line == 0) score.Line = line; }
            else if (PointsItem().IsMatch(text)) itemLines++;
        }
        // Two or more printed "+5 points"-style lines count as a breakdown: the number can be checked against them.
        if (score.Text is null || itemLines >= 2) return null;

        bool random = RandomSource().IsMatch(score.Text) || ScoreVar().Matches(score.Text).Select(m => m.Groups["v"].Value).Distinct(StringComparer.OrdinalIgnoreCase)
            .Any(v => code.Any(c => c.Text.Length <= MaxLine && RandomSource().IsMatch(c.Text)
                                    && Regex.IsMatch(c.Text, @"[$%!{]" + Regex.Escape(v) + @"(?!\w)", RegexOptions.IgnoreCase, Timeout)));
        bool literal = !random && LiteralScore().IsMatch(score.Text) && !ScoreVar().IsMatch(score.Text);
        var source = random ? "The number is picked at random, so it measures nothing. "
            : literal ? "The number is typed into the script, so it's the same on every PC. " : "";
        return Sign(score.Line, score.Text, "Shows an overall score with no breakdown of the changes",
            source + "One number like 870/1000 or \"97% optimized\" doesn't say which changes it counts or how anything was measured, so nobody can check it. "
            + "Optimizer packs show one to make their changes look worthwhile. PC Tidy lists each change on its own instead.");
    }

    // ================================================================== "apply all" / one click

    private static ScriptFinding? ApplyAll(IReadOnlyList<(int Line, string Text)> code)
    {
        foreach (var (line, text) in code)
            if (text.Length <= MaxLine && ApplyAllText().IsMatch(text))
                return Sign(line, text, "Has an \"apply all\" step that makes every change at once",
                    "Everything is changed in one go, so you can't choose, check or undo the changes one at a time, and if something breaks you won't know which change did it. "
                    + "PC Tidy's Tweaks lets you pick each change and undo it on its own.");
        return null;
    }

    // ================================================================== restore point order

    private static ScriptFinding? RestorePointOrder(IReadOnlyList<(int Line, string Text)> code, ScriptLanguage language, IReadOnlyList<ScriptFinding> found)
    {
        // Registry, service and boot changes, minus the ones that only prepare System Restore (the 24-hour frequency value, VSS).
        var changes = found.Where(f => !f.FromDecodedPayload && f.Area is ScriptArea.Registry or ScriptArea.Service or ScriptArea.BootSettings
                                       && !RestorePrep().IsMatch(f.What + " " + f.Evidence)).ToList();
        if (changes.Count == 0) return null;
        var flow = new Flow(code, language);
        var first = changes.Select(c => (Finding: c, At: flow.Earliest(c.Line))).OrderBy(x => x.At).ThenBy(x => x.Finding.Line).First();

        var creates = code.Where(c => c.Text.Length <= MaxLine && RestorePointCreate().IsMatch(c.Text)).ToList();
        if (creates.Count > 0)
        {
            var make = creates.Select(c => (c.Line, c.Text, At: flow.Earliest(c.Line))).OrderBy(x => x.At).ThenBy(x => x.Line).First();
            if (make.At < first.At || (make.At == first.At && make.Line < first.Finding.Line)) return null;
            // A menu script ("1. Restore point  2. Tweaks") lets you pick the order: PC Tidy can't tell, so it doesn't guess.
            if (flow.Interactive && (flow.InRoutine(make.Line) || flow.InRoutine(first.Finding.Line))) return null;
            return Sign(make.Line, make.Text, "Makes the restore point only after changing the system",
                $"Line {first.Finding.Line} changes Windows before this line makes the restore point. A restore point keeps things as they are at that moment, "
                + "so one made afterwards saves the changed settings and going back to it doesn't undo them. A careful script makes it first.");
        }

        var claim = code.FirstOrDefault(c => c.Text.Length <= MaxLine && Prints().IsMatch(c.Text) && RestorePointClaim().IsMatch(c.Text));
        if (claim.Text is not null)
            return Sign(claim.Line, claim.Text, "Says it makes a restore point, but nothing in it does",
                "It prints that a restore point is being made, yet no line in the file creates one, so there's nothing to go back to if a change goes wrong. "
                + "Make one yourself first (search Start for \"Create a restore point\"), or use Tweaks, which makes one before every system change.");

        int changed = changes.Select(c => c.Line).Distinct().Count();
        if (changed >= ManyChanges)
            return Sign(first.Finding.Line, first.Finding.Evidence, $"Changes {changed} system settings without making a restore point first",
                "Nothing in the file makes a restore point, so there's no simple way back if one of these changes causes trouble. "
                + "Make one yourself before running it (search Start for \"Create a restore point\"); Tweaks makes one before every system change.",
                ScriptVerdict.Situational, red: false);
        return null;
    }

    /// <summary>
    /// Where a line can first run: a line inside a PowerShell function or under a batch label runs where that routine is
    /// first called (or reached), not where it's written. Text only, approximate, bounded; outside routines it's the line itself.
    /// </summary>
    private sealed class Flow
    {
        private sealed record Routine(string Name, int Start, int End);

        private readonly List<Routine> _routines = new();
        private readonly Dictionary<string, List<int>> _callers = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<Routine, int> _memo = new();
        private readonly bool _batch;

        public bool Interactive { get; }

        public Flow(IReadOnlyList<(int Line, string Text)> code, ScriptLanguage language)
        {
            _batch = language == ScriptLanguage.Batch;
            Interactive = code.Any(c => c.Text.Length <= MaxLine && InputPrompt().IsMatch(c.Text));
            if (code.Count == 0 || code.Count > MaxFlowLines) return;
            if (language == ScriptLanguage.PowerShell) ReadFunctions(code);
            else if (_batch) ReadLabels(code);
            if (_routines.Count == 0) return;

            var names = new HashSet<string>(_routines.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var (line, text) in code)
            {
                if (text.Length > MaxLine) continue;
                var hits = _batch ? CallTarget().Matches(text).Select(m => m.Groups["n"].Value) : Word().Matches(text).Select(m => m.Value);
                foreach (var name in hits.Where(names.Contains).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!_callers.TryGetValue(name, out var list)) _callers[name] = list = new List<int>();
                    list.Add(line);
                }
            }
        }

        private void ReadFunctions(IReadOnlyList<(int Line, string Text)> code)
        {
            for (int i = 0; i < code.Count; i++)
            {
                if (code[i].Text.Length > MaxLine || FunctionStart().Match(code[i].Text) is not { Success: true } m) continue;
                int depth = 0, end = code[^1].Line;
                bool opened = false;
                for (int j = i; j < code.Count; j++)
                {
                    var t = code[j].Text.Length > MaxLine ? "" : Strings().Replace(code[j].Text, "");
                    foreach (var c in t)
                    {
                        if (c == '{') { depth++; opened = true; }
                        else if (c == '}') depth--;
                    }
                    if (opened && depth <= 0) { end = code[j].Line; break; }
                }
                _routines.Add(new Routine(m.Groups["name"].Value, code[i].Line, end));
            }
        }

        private void ReadLabels(IReadOnlyList<(int Line, string Text)> code)
        {
            var labels = code.Where(c => c.Text.Length <= MaxLine).Select(c => (c.Line, Match: BatchLabel().Match(c.Text))).Where(x => x.Match.Success).ToList();
            for (int k = 0; k < labels.Count; k++)
                _routines.Add(new Routine(labels[k].Match.Groups["name"].Value, labels[k].Line, k + 1 < labels.Count ? labels[k + 1].Line - 1 : code[^1].Line));
        }

        private Routine? Innermost(int line) => _routines.Where(r => r.Start <= line && line <= r.End).OrderByDescending(r => r.Start).FirstOrDefault();

        public bool InRoutine(int line) => Innermost(line) is not null;

        public int Earliest(int line) => Earliest(line, 0);

        private int Earliest(int line, int depth)
        {
            var r = Innermost(line);
            if (r is null) return line;
            if (_memo.TryGetValue(r, out var known)) return known;
            if (depth > 8) return r.Start;
            // A batch label is also reached by falling through to it; a PowerShell function only runs where it's called.
            int best = _batch ? r.Start : int.MaxValue;
            if (_callers.TryGetValue(r.Name, out var calls))
                foreach (var call in calls)
                    if (call < r.Start || call > r.End) best = Math.Min(best, Earliest(call, depth + 1));
            if (best == int.MaxValue) best = r.Start; // never called: where it's written
            _memo[r] = best;
            return best;
        }
    }

    // ================================================================== Base64 in pieces

    private static ScriptFinding? DataInPieces(IReadOnlyList<(int Line, string Text)> code)
    {
        int pieces = 0, total = 0, firstLine = 0;
        string firstText = "";
        bool joins = false;
        foreach (var (line, text) in code)
        {
            if (text.Length > MaxLine) continue;
            if (!joins && PieceJoin().IsMatch(text)) joins = true;
            foreach (Match m in Base64Literal().Matches(text))
            {
                var b = m.Groups["b"].Value;
                // Hashes and ids are hex; scrambled data mixes upper and lower case letters with digits.
                if (!b.Any(char.IsAsciiLetterUpper) || !b.Any(char.IsAsciiLetterLower) || !b.Any(char.IsAsciiDigit) || b.All(char.IsAsciiHexDigit)) continue;
                if (pieces++ == 0) { firstLine = line; firstText = text; }
                total += b.Length;
            }
        }
        if (pieces < 3 || total < 300 || !joins) return null;
        return Sign(firstLine, firstText, "Keeps scrambled data in pieces that are joined later",
            $"{pieces} blocks of Base64 text are glued together while the script runs. Splitting hidden code into pieces is a way to stop antivirus recognising it; "
            + "PC Tidy can't see what the joined result does.", area: ScriptArea.HiddenCode);
    }

    // ================================================================== patterns (text only, bounded, with a match timeout)

    [GeneratedRegex(@"\brun\s{1,3}anyway\b|windows\s{1,3}protected\s{1,3}your\s{1,3}pc|\bkeep\s{1,3}anyway\b|smart\s?screen[^\r\n]{0,80}?\b(?:ignore|bypass|click|anyway|more\s{1,3}info)\b|\bmore\s{1,3}info\b[^\r\n]{0,80}?\b(?:smart\s?screen|anyway)\b", RegexOptions.IgnoreCase, CommandRules.RegexTimeoutMs)]
    private static partial Regex SmartScreenText();

    [GeneratedRegex(@"(?<![\w$-])(?:Write-Host|Write-Output|Write-Information|echo|Out-Host|MsgBox|title)(?![\w-])|\.Popup\b|WScript\.Echo|console\.log|\[Console\]::Write|^\s{0,20}[""'][^\r\n]{0,400}[""']\s{0,5}$", RegexOptions.IgnoreCase, CommandRules.RegexTimeoutMs)]
    private static partial Regex Prints();

    [GeneratedRegex(@"\b(?:score|rating|pc\s{0,3}health|system\s{0,3}health|health\s{0,3}(?:score|index)|optimi[sz](?:ed|ation)|boost(?:ed)?|performance\s{0,3}(?:index|score|rating|boost|gain)|faster|fps\s{0,3}(?:gain|boost|increase))\b", RegexOptions.IgnoreCase, CommandRules.RegexTimeoutMs)]
    private static partial Regex ScoreWord();

    [GeneratedRegex(@"(?<![\w.])(?:\d{1,4}(?:\.\d{1,2})?|\$\{?[A-Za-z_]\w{0,60}\}?|\$\([^)\r\n]{1,120}\)|%[A-Za-z_]\w{0,60}%|![A-Za-z_]\w{0,60}!)\s{0,3}(?:/\s{0,3}(?:10|100|1000)(?!\d)|%%?|\s?percent\b|\s?(?:pts|points)\b)|\b(?:score|rating)\b\s{0,3}[:=]\s{0,3}(?:\d{1,4}|\$\{?[A-Za-z_]\w{0,60}|%[A-Za-z_]\w{0,60}%|![A-Za-z_]\w{0,60}!)", RegexOptions.IgnoreCase, CommandRules.RegexTimeoutMs)]
    private static partial Regex ScoreValue();

    [GeneratedRegex(@"(?<![\w.])[+-]\s{0,2}\d{1,4}\s{0,2}(?:pts|points|%)", RegexOptions.IgnoreCase, CommandRules.RegexTimeoutMs)]
    private static partial Regex PointsItem();

    [GeneratedRegex(@"Get-Random|%RANDOM%|!RANDOM!|\bRnd\b|Math\.random|\[random\]|System\.Random", RegexOptions.IgnoreCase, CommandRules.RegexTimeoutMs)]
    private static partial Regex RandomSource();

    [GeneratedRegex(@"(?<![\w.$%!])\d{1,4}(?:\.\d{1,2})?\s{0,3}(?:/\s{0,3}(?:10|100|1000)(?!\d)|%%?|\s?percent\b|\s?(?:pts|points)\b)|\b(?:score|rating)\b\s{0,3}[:=]\s{0,3}\d{1,4}", RegexOptions.IgnoreCase, CommandRules.RegexTimeoutMs)]
    private static partial Regex LiteralScore();

    [GeneratedRegex(@"\$\{?(?<v>[A-Za-z_]\w{0,60})\}?|[%!](?<v>[A-Za-z_]\w{0,60})[%!]", RegexOptions.None, CommandRules.RegexTimeoutMs)]
    private static partial Regex ScoreVar();

    [GeneratedRegex(@"\b(?:apply|run|enable|execute|install)[\s_-]{0,3}(?:all|every(?:thing)?)[\s_-]{0,3}(?:the[\s_-]{1,3})?(?:recommended[\s_-]{1,3})?(?:tweaks?|optimi[sz]ations?|boosts?|debloat\w{0,10}|fps)\b|\b(?:one|1)[\s-]?click[\s_-]{0,3}(?:optimi\w{0,12}|boost\w{0,6}|tweak\w{0,6}|fix\w{0,6}|fps|debloat\w{0,10}|tun\w{0,6}|clean\w{0,6})|\b(?:optimi[sz]e|boost|tweak|debloat)[\s_-]{1,3}(?:everything|it[\s_-]all)\b|\ball[\s_-]{1,3}(?:tweaks?|optimi[sz]ations?)[\s_-]{1,3}at[\s_-]{1,3}once\b", RegexOptions.IgnoreCase, CommandRules.RegexTimeoutMs)]
    private static partial Regex ApplyAllText();

    [GeneratedRegex(@"(?<![\w-])Checkpoint-Computer(?![\w-])|CreateRestorePoint|SRSetRestorePoint", RegexOptions.IgnoreCase, CommandRules.RegexTimeoutMs)]
    private static partial Regex RestorePointCreate();

    [GeneratedRegex(@"SystemRestore|ComputerRestore|\bVSS\b|\bswprv\b|\bsrservice\b", RegexOptions.IgnoreCase, CommandRules.RegexTimeoutMs)]
    private static partial Regex RestorePrep();

    [GeneratedRegex(@"\b(?:creat|mak|sav)ing\s{1,3}(?:an?\s{1,3})?(?:new\s{1,3})?(?:system\s{1,3})?restore\s{1,3}point|\brestore\s{1,3}point\s{1,3}(?:created|made|saved)\s{0,3}(?:successfully|[.!]|[""']|$)", RegexOptions.IgnoreCase, CommandRules.RegexTimeoutMs)]
    private static partial Regex RestorePointClaim();

    [GeneratedRegex(@"(?<![\w-])(?:Read-Host|choice|PromptForChoice|Out-GridView|InputBox)(?![\w-])|\bset\s{1,3}/p\b", RegexOptions.IgnoreCase, CommandRules.RegexTimeoutMs)]
    private static partial Regex InputPrompt();

    [GeneratedRegex(@"^\s{0,20}(?:function|filter)\s{1,5}(?:global:|script:|local:|private:)?(?<name>[\w-]{1,80})", RegexOptions.IgnoreCase, CommandRules.RegexTimeoutMs)]
    private static partial Regex FunctionStart();

    [GeneratedRegex(@"'[^'\r\n]{0,2000}'|""[^""\r\n]{0,2000}""", RegexOptions.None, CommandRules.RegexTimeoutMs)]
    private static partial Regex Strings();

    [GeneratedRegex(@"(?<![\w$-])[A-Za-z_][\w-]{0,80}", RegexOptions.None, CommandRules.RegexTimeoutMs)]
    private static partial Regex Word();

    [GeneratedRegex(@"^\s{0,10}:(?<name>[A-Za-z_][\w.-]{0,60})\s{0,10}$", RegexOptions.None, CommandRules.RegexTimeoutMs)]
    private static partial Regex BatchLabel();

    [GeneratedRegex(@"(?<![\w-])(?:call|goto)\s{1,5}:?(?<n>[A-Za-z_][\w.-]{0,60})", RegexOptions.IgnoreCase, CommandRules.RegexTimeoutMs)]
    private static partial Regex CallTarget();

    [GeneratedRegex(@"(?<q>['""])(?<b>[A-Za-z0-9+/]{40,4000}={0,2})\k<q>", RegexOptions.None, CommandRules.RegexTimeoutMs)]
    private static partial Regex Base64Literal();

    [GeneratedRegex(@"\+=|-join\b|\[string\]::(?:Concat|Join)|FromBase64String|['""]\s{0,3}\+\s{0,3}\$\w|\$\w{1,60}\s{0,3}\+\s{0,3}\$\w|\.join\(", RegexOptions.IgnoreCase, CommandRules.RegexTimeoutMs)]
    private static partial Regex PieceJoin();
}
