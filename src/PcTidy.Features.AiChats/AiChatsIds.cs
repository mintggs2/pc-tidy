using PcTidy.Core;

namespace PcTidy.Features.AiChats;

/// <summary>Ids and well-known places of the AI chats section.</summary>
public static class AiChatsIds
{
    public const string FeatureId = "aichats";

    /// <summary>PC Tidy's own chat library folder: &lt;DataRoot&gt;\aichats (the only place this section writes itself).</summary>
    public static string DataFolder(string dataRoot) => Path.Combine(dataRoot, FeatureId);

    /// <summary>Marker in every note PC Tidy exports. A note without it (or with another id) is never overwritten.</summary>
    public const string NoteMarker = "pctidy-id";

    /// <summary>A session file changed this recently may still be written to: treated as live and never moved.</summary>
    public static readonly TimeSpan LiveWindow = TimeSpan.FromMinutes(10);
}

/// <summary>
/// What the AI chats section remembers (stored with <c>AppSettings.SetFeature("aichats", …)</c>; the shell saves).
/// The Obsidian folder lives in <see cref="AppSettings.ObsidianExportFolder"/> so other sections can find it too.
/// </summary>
public sealed class AiChatsSettings
{
    /// <summary>Where "Archive a copy" puts copies of Claude Code / Codex transcripts. Null = not chosen yet.</summary>
    public string? ArchiveFolder { get; set; }

    /// <summary>
    /// Off by default. On: exported notes keep keys and passwords exactly as they were in the chat (read again from the
    /// original export). The library and search index always stay masked.
    /// </summary>
    public bool KeepSecretsInExports { get; set; }

    public static AiChatsSettings From(AppSettings settings)
    {
        try { return settings.GetFeature<AiChatsSettings>(AiChatsIds.FeatureId) ?? new AiChatsSettings(); }
        catch (System.Text.Json.JsonException) { return new AiChatsSettings(); }
    }

    public void Store(AppSettings settings) => settings.SetFeature(AiChatsIds.FeatureId, this);
}
