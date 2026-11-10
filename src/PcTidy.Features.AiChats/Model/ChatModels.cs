using System.Security.Cryptography;
using System.Text;

namespace PcTidy.Features.AiChats.Model;

/// <summary>Where a chat came from.</summary>
public enum ChatSource { ChatGpt, ClaudeWeb, ClaudeCode, Codex }

public enum ChatRole { User, Assistant, Tool }

/// <summary>How the original of a chat can be read again (for unmasked exports and archive copies).</summary>
public enum OriginKind { ExportZip, ExportFolder, TranscriptFile }

public static class ChatSources
{
    public static readonly IReadOnlyList<ChatSource> All = new[] { ChatSource.ChatGpt, ChatSource.ClaudeWeb, ChatSource.ClaudeCode, ChatSource.Codex };

    public static string Display(ChatSource s) => s switch
    {
        ChatSource.ChatGpt => "ChatGPT",
        ChatSource.ClaudeWeb => "Claude",
        ChatSource.ClaudeCode => "Claude Code",
        ChatSource.Codex => "Codex",
        _ => s.ToString(),
    };

    /// <summary>Stable lower-case key used in ids, frontmatter and the database.</summary>
    public static string Key(ChatSource s) => s switch
    {
        ChatSource.ChatGpt => "chatgpt",
        ChatSource.ClaudeWeb => "claude",
        ChatSource.ClaudeCode => "claude-code",
        ChatSource.Codex => "codex",
        _ => s.ToString().ToLowerInvariant(),
    };

    public static ChatSource FromKey(string key) => All.FirstOrDefault(s => Key(s) == key);

    /// <summary>Agent chats live on this PC (Claude Code, Codex); web chats come from export zips.</summary>
    public static bool IsAgent(ChatSource s) => s is ChatSource.ClaudeCode or ChatSource.Codex;

    public static string RoleName(ChatSource s, ChatRole role) => role switch
    {
        ChatRole.User => "You",
        ChatRole.Tool => "Tool",
        _ => Display(s),
    };
}

public sealed record ChatMessage(ChatRole Role, string Text, DateTime? CreatedUtc = null, string? Model = null);

/// <summary>
/// An alternate version of the chat (an edited question or a regenerated answer). It replaces the visible messages
/// from index <see cref="ForkAt"/> on.
/// </summary>
public sealed record ChatBranch(int ForkAt, IReadOnlyList<ChatMessage> Messages);

/// <summary>A file attached to a chat. Exactly one of the three ways to reach its content is set (or none: name only).</summary>
public sealed record ChatAttachment(string Name, long Size, string? ArchiveEntry = null, string? FilePath = null, string? ExtractedText = null);

/// <summary>A chat as read from its source, before masking. Never stored or shown in this form.</summary>
public sealed record ImportedChat
{
    public required ChatSource Source { get; init; }
    /// <summary>The source's own id (conversation id, session id).</summary>
    public required string SourceId { get; init; }
    public required string Title { get; init; }
    public DateTime? CreatedUtc { get; init; }
    public DateTime? UpdatedUtc { get; init; }
    public string? Model { get; init; }
    /// <summary>Agent chats: the project folder (from the records' cwd). Web chats: the Claude project name, if any.</summary>
    public string? Project { get; init; }
    public required IReadOnlyList<ChatMessage> Messages { get; init; }
    public IReadOnlyList<ChatBranch> Alternates { get; init; } = Array.Empty<ChatBranch>();
    public IReadOnlyList<ChatAttachment> Attachments { get; init; } = Array.Empty<ChatAttachment>();
    public required OriginKind OriginKind { get; init; }
    /// <summary>The export zip / folder or the transcript file it was read from.</summary>
    public required string Origin { get; init; }
    /// <summary>Agent chats: size of the transcript (and its sidecar folder) on disk.</summary>
    public long OriginBytes { get; init; }

    public string LibraryId => ChatIds.For(Source, SourceId);
}

public static class ChatIds
{
    /// <summary>"chatgpt-6650a…": stable across re-imports, safe in file names and frontmatter.</summary>
    public static string For(ChatSource source, string sourceId)
    {
        var clean = new StringBuilder(sourceId.Length);
        foreach (var ch in sourceId)
            if (char.IsAsciiLetterOrDigit(ch) || ch == '-' || ch == '_') clean.Append(char.ToLowerInvariant(ch));
        var id = clean.Length is > 0 and <= 80 ? clean.ToString() : Hash(sourceId);
        return ChatSources.Key(source) + "-" + id;
    }

    /// <summary>Id for a chat whose source gave none: derived from what is known about it.</summary>
    public static string Synthetic(params string?[] parts) => Hash(string.Join("\u001f", parts.Select(p => p ?? "")));

    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..24].ToLowerInvariant();
}

/// <summary>What an import found and skipped, in plain English.</summary>
public sealed class ImportReport
{
    private readonly List<string> _notes = new();
    public int Chats { get; set; }
    public int SkippedRecords { get; set; }
    public int SkippedChats { get; set; }
    public IReadOnlyList<string> Notes => _notes;

    public void Note(string text)
    {
        lock (_notes)
            if (_notes.Count < 50 && !_notes.Contains(text)) _notes.Add(text);
    }
}
