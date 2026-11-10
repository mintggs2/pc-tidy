using System.Text;
using PcTidy.Core;

namespace PcTidy.Features.AiChats.Export;

/// <summary>A note PC Tidy exported earlier (it carries our pctidy-id marker in its frontmatter).</summary>
internal sealed record ExportedNote(string Path, string Id, string Content, DateTime LastWriteUtc);

/// <summary>
/// Read-only look at the export folder: which notes PC Tidy wrote before (by their "pctidy-id:" frontmatter line), and
/// which names are taken. Only notes with our marker are ever updated; everything else is left alone and its name avoided.
/// </summary>
internal sealed class ExistingNotes
{
    private const int HeadBytes = 8 * 1024;
    private const int MaxNoteBytes = 32 * 1024 * 1024;

    private readonly Dictionary<string, ExportedNote> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _taken = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, ExportedNote> ById => _byId;

    public bool IsTaken(string path) => _taken.Contains(path);

    public static ExistingNotes Scan(string folder, CancellationToken ct)
    {
        var notes = new ExistingNotes();
        if (!Directory.Exists(folder)) return notes;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = 8,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(folder, "*", options); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return notes; }
        foreach (var path in entries)
        {
            ct.ThrowIfCancellationRequested();
            notes._taken.Add(path);
            if (!path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) continue;
            if (Read(path) is { } note) notes._byId.TryAdd(note.Id, note);
        }
        return notes;
    }

    /// <summary>Reads a note only when its head has our marker. Online-only files are never opened.</summary>
    private static ExportedNote? Read(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || CloudFile.IsPlaceholder(info.Attributes) || info.Length > MaxNoteBytes) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var head = new byte[Math.Min(HeadBytes, (int)info.Length)];
            int read = 0, n;
            while (read < head.Length && (n = fs.Read(head, read, head.Length - read)) > 0) read += n;
            if (MarkerId(Encoding.UTF8.GetString(head, 0, read)) is not { } id) return null;
            fs.Position = 0;
            using var reader = new StreamReader(fs, new UTF8Encoding(false));
            return new ExportedNote(path, id, reader.ReadToEnd(), info.LastWriteTimeUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The pctidy-id value from a leading YAML frontmatter block, or null.</summary>
    internal static string? MarkerId(string text)
    {
        var t = text.TrimStart('﻿');
        if (!t.StartsWith("---", StringComparison.Ordinal)) return null;
        var lines = t.Split('\n');
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Trim() == "---") return null;
            if (!line.StartsWith(AiChatsIds.NoteMarker + ":", StringComparison.Ordinal)) continue;
            var value = line[(AiChatsIds.NoteMarker.Length + 1)..].Trim().Trim('"', '\'');
            return value.Length > 0 ? value : null;
        }
        return null;
    }
}
