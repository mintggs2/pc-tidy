namespace PcTidy.Features.Games.Optimize;

/// <summary>
/// Steam's "Launch Options" text: words separated by spaces, quotes group words, and "%command%" marks where Steam puts
/// the game itself (anything before it is a wrapper or environment variable and is never touched). Edits keep the rest
/// of the text exactly as the user typed it.
/// </summary>
internal static class LaunchOptionLine
{
    internal readonly record struct Word(string Text, int Start, int End);

    public static IReadOnlyList<Word> Words(string line)
    {
        var words = new List<Word>();
        int i = 0;
        while (i < line.Length)
        {
            if (char.IsWhiteSpace(line[i])) { i++; continue; }
            int start = i;
            bool quoted = false;
            while (i < line.Length && (quoted || !char.IsWhiteSpace(line[i])))
            {
                if (line[i] == '"') quoted = !quoted;
                i++;
            }
            words.Add(new Word(line[start..i].Replace("\"", "", StringComparison.Ordinal), start, i));
        }
        return words;
    }

    /// <summary>Index of the first game argument (after "%command%" when present).</summary>
    private static int FirstArgument(IReadOnlyList<Word> words)
    {
        for (int i = 0; i < words.Count; i++)
            if (words[i].Text.Equals("%command%", StringComparison.OrdinalIgnoreCase)) return i + 1;
        return 0;
    }

    /// <summary>True if the option's first word is already set (e.g. "+fps_max" in "+fps_max 400").</summary>
    public static bool Contains(string line, string option)
    {
        var name = Name(Words(option).FirstOrDefault().Text ?? "");
        var words = Words(line);
        return name.Length > 0 && words.Skip(FirstArgument(words)).Any(w => Name(w.Text).Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Adds the option at the end (unchanged if its first word is already there).</summary>
    public static string Add(string line, string option)
    {
        if (Contains(line, option)) return line;
        var trimmed = line.TrimEnd();
        return trimmed.Length == 0 ? option : trimmed + " " + option;
    }

    /// <summary>Removes every occurrence of <paramref name="token"/> (and its value word when it takes one).</summary>
    public static string Remove(string line, string token, bool takesValue)
    {
        var words = Words(line);
        var cuts = new List<(int Start, int End)>();
        for (int i = FirstArgument(words); i < words.Count; i++)
        {
            if (!Name(words[i].Text).Equals(token, StringComparison.OrdinalIgnoreCase)) continue;
            int start = words[i].Start, end = words[i].End;
            bool inlineValue = words[i].Text.Contains('=');
            if (takesValue && !inlineValue && i + 1 < words.Count && !IsOption(words[i + 1].Text)) { end = words[i + 1].End; i++; }
            cuts.Add((start, end));
        }
        var result = line;
        foreach (var (start, end) in cuts.OrderByDescending(c => c.Start))
            result = Cut(result, start, end);
        return result;
    }

    /// <summary>Removes [start, end) and the spaces it leaves behind, nothing else.</summary>
    private static string Cut(string s, int start, int end)
    {
        var before = s[..start];
        var after = s[end..].TrimStart();
        if (after.Length == 0) return before.TrimEnd();
        return before.Length > 0 && !char.IsWhiteSpace(before[^1]) ? before + " " + after : before + after;
    }

    /// <summary>"-cpuCount=8" → "-cpuCount".</summary>
    public static string Name(string word)
    {
        var eq = word.IndexOf('=');
        return eq > 0 ? word[..eq] : word;
    }

    private static bool IsOption(string word) => word.Length > 1 && word[0] is '-' or '+';
}
