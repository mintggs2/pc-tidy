using PcTidy.Features.AiChats.Local;
using PcTidy.Features.AiChats.Model;
using PcTidy.Features.AiChats.Privacy;

namespace PcTidy.Features.AiChats.Library;

/// <summary>
/// A chat ready for the library: every text already masked. Only <see cref="ChatIngest"/> creates these, so nothing
/// unmasked can reach the database or the search index.
/// </summary>
public sealed record MaskedChat
{
    internal MaskedChat() { }

    public required string Id { get; init; }
    public required ChatSource Source { get; init; }
    public required string SourceId { get; init; }
    public required string Title { get; init; }
    public DateTime? CreatedUtc { get; init; }
    public DateTime? UpdatedUtc { get; init; }
    public string? Model { get; init; }
    public string? Project { get; init; }
    public required IReadOnlyList<ChatMessage> Messages { get; init; }
    public required IReadOnlyList<ChatBranch> Alternates { get; init; }
    public required IReadOnlyList<ChatAttachment> Attachments { get; init; }
    public required IReadOnlyList<SecretFinding> Secrets { get; init; }
    public required Dictionary<string, int> Terms { get; init; }
    public required OriginKind OriginKind { get; init; }
    public required string Origin { get; init; }
    public required long SizeBytes { get; init; }
    public required int CharCount { get; init; }
    /// <summary>Hash of the visible messages: equal for two copies of the same conversation.</summary>
    public required string ContentHash { get; init; }
}

/// <summary>One row of the chat list.</summary>
public sealed record ChatSummary
{
    public required string Id { get; init; }
    public required ChatSource Source { get; init; }
    public required string Title { get; init; }
    public DateTime? CreatedUtc { get; init; }
    public DateTime? UpdatedUtc { get; init; }
    public string? Model { get; init; }
    public string? Project { get; init; }
    public string? Topic { get; init; }
    public string? Folder { get; init; }
    public bool Pinned { get; init; }
    public int MessageCount { get; init; }
    public int CharCount { get; init; }
    public long SizeBytes { get; init; }
    public int SecretCount { get; init; }
    public int AlternateCount { get; init; }
    public int AttachmentCount { get; init; }
    public OriginKind OriginKind { get; init; }
    public string Origin { get; init; } = "";
    public string ContentHash { get; init; } = "";
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Keywords { get; init; } = Array.Empty<string>();

    public bool IsAgent => ChatSources.IsAgent(Source);
    /// <summary>Agent chats: the project's short name. Web chats: the topic (or the Claude project).</summary>
    public string Group => GroupOf(Source, Project, Topic);

    /// <summary>The group a chat is listed under (the library's counts use the same rule).</summary>
    public static string GroupOf(ChatSource source, string? project, string? topic)
        => ChatSources.IsAgent(source) ? AgentProjects.ShortName(project) : project ?? topic ?? TopicLabeler.OtherTopic;
    public DateTime? Date => CreatedUtc ?? UpdatedUtc;
}

public sealed record ChatDetail(
    ChatSummary Summary,
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<ChatBranch> Alternates,
    IReadOnlyList<ChatAttachment> Attachments,
    IReadOnlyDictionary<string, int> Terms);

public enum LibraryView { All, Pinned, Biggest, NearEmpty, Duplicates, WithSecrets }

public sealed record LibraryQuery
{
    public LibraryView View { get; init; } = LibraryView.All;
    public ChatSource? Source { get; init; }
    /// <summary>Full-text search over the (masked) titles and messages.</summary>
    public string? Text { get; init; }
    /// <summary>Exact project (agent chats: cwd) or topic/project group name as shown.</summary>
    public string? Group { get; init; }
    public string? Tag { get; init; }
    public string? Folder { get; init; }
    public int Limit { get; init; } = 20_000;
}

public sealed record SourceCount(ChatSource Source, int Chats, long Bytes);

public sealed record GroupCount(string Name, int Chats, long Bytes);

/// <summary>One live-looking secret for the "Exposed secrets" report. Never contains the full key.</summary>
public sealed record SecretReportRow(
    string ChatId, string ChatTitle, ChatSource Source, DateTime? Date,
    string RuleTitle, string Masked, string Advice, string Fingerprint, int ChatsWithSameKey);

/// <summary>Chats near-empty below these: at most two messages and under 200 characters in all.</summary>
public static class LibraryRules
{
    public const int NearEmptyMaxMessages = 2;
    public const int NearEmptyMaxChars = 200;
    public const int BiggestCount = 100;
}
