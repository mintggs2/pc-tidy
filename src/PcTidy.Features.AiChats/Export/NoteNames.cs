using System.Text;

namespace PcTidy.Features.AiChats.Export;

/// <summary>File and folder names that work in Windows and in Obsidian (no # ^ [ ] | which break wikilinks).</summary>
internal static class NoteNames
{
    private const int MaxLength = 100;
    private static readonly HashSet<char> Bad = new(Path.GetInvalidFileNameChars().Concat(new[] { '#', '^', '[', ']', '|', '%' }));
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string Safe(string? name, string fallback = "Untitled chat")
    {
        var sb = new StringBuilder();
        foreach (var ch in name ?? "")
        {
            if (char.IsControl(ch)) continue;
            sb.Append(Bad.Contains(ch) ? ' ' : ch);
        }
        var clean = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length > MaxLength) clean = clean[..MaxLength];
        clean = clean.Trim().TrimEnd('.', ' ').TrimStart('.');
        if (clean.Length == 0) clean = fallback;
        var stem = clean.Split('.')[0];
        if (Reserved.Contains(stem)) clean = "_" + clean;
        return clean;
    }

    /// <summary>"name.md" → "name (2).md" … skipping names taken on disk or already promised to another note of this export.</summary>
    public static string Free(string desired, Func<string, bool> taken)
    {
        if (!taken(desired)) return desired;
        var dir = Path.GetDirectoryName(desired)!;
        var stem = Path.GetFileNameWithoutExtension(desired);
        var ext = Path.GetExtension(desired);
        for (int i = 2; i < 10_000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!taken(candidate)) return candidate;
        }
        throw new IOException("No free name for " + desired);
    }
}
