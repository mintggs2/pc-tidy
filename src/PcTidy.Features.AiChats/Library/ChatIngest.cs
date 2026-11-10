using System.Security.Cryptography;
using System.Text;
using PcTidy.Features.AiChats.Model;
using PcTidy.Features.AiChats.Privacy;

namespace PcTidy.Features.AiChats.Library;

/// <summary>
/// The one door into the library: masks every text of an imported chat (title, messages, alternates, attachment text)
/// and works out the numbers the library shows (size, characters, word counts for topics, duplicate hash).
/// </summary>
public static class ChatIngest
{
    public static MaskedChat Prepare(ImportedChat chat, SecretMasker masker)
    {
        var secrets = new List<SecretFinding>();
        string M(string? text)
        {
            var r = masker.Mask(text);
            secrets.AddRange(r.Findings);
            return r.Text;
        }

        var title = M(chat.Title);
        var messages = chat.Messages.Select(m => m with { Text = M(m.Text) }).ToList();
        var alternates = chat.Alternates.Select(b => b with { Messages = b.Messages.Select(m => m with { Text = M(m.Text) }).ToList() }).ToList();
        var attachments = chat.Attachments.Select(a => a.ExtractedText is null ? a : a with { ExtractedText = M(a.ExtractedText) }).ToList();
        var chars = messages.Sum(m => m.Text.Length);
        long textBytes = messages.Sum(m => (long)Encoding.UTF8.GetByteCount(m.Text)) + alternates.Sum(b => b.Messages.Sum(m => (long)Encoding.UTF8.GetByteCount(m.Text)));

        return new MaskedChat
        {
            Id = chat.LibraryId,
            Source = chat.Source,
            SourceId = chat.SourceId,
            Title = title,
            CreatedUtc = chat.CreatedUtc,
            UpdatedUtc = chat.UpdatedUtc,
            Model = chat.Model,
            Project = chat.Project is null ? null : M(chat.Project),
            Messages = messages,
            Alternates = alternates,
            Attachments = attachments,
            Secrets = secrets,
            Terms = TopicLabeler.Terms(title, messages),
            OriginKind = chat.OriginKind,
            Origin = chat.Origin,
            // Agent chats: what the transcript takes on disk (what archiving copies). Web chats: the chat's own text.
            SizeBytes = chat.OriginBytes > 0 ? chat.OriginBytes : textBytes,
            CharCount = chars,
            ContentHash = Hash(messages),
        };
    }

    private static string Hash(IEnumerable<ChatMessage> messages)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var m in messages)
        {
            sha.AppendData(Encoding.UTF8.GetBytes(m.Role.ToString()));
            sha.AppendData(new byte[] { 0 });
            sha.AppendData(Encoding.UTF8.GetBytes(string.Join(' ', m.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))));
            sha.AppendData(new byte[] { 1 });
        }
        return Convert.ToHexString(sha.GetHashAndReset())[..32];
    }
}
