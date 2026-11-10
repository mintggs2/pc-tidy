using System.Text;
using PcTidy.Core;
using PcTidy.Core.Model;

namespace PcTidy.Features.AiChats;

/// <summary>
/// The plan items this section emits (documented Args only: WriteTextFile/EditTextFile "content"). Every one is
/// re-checked by the safety guard and run by the engine; this section never writes user files itself.
/// </summary>
internal static class Plans
{
    public const string Content = "content";

    /// <summary>A new note or text file. Skip on a name clash (never AutoNumber: other notes link to this exact name).</summary>
    public static PlanItem WriteText(string path, string content, string description) => new()
    {
        Kind = ActionKind.WriteTextFile,
        FeatureId = AiChatsIds.FeatureId,
        Description = description,
        DestinationPath = path,
        ExpectedSize = Encoding.UTF8.GetByteCount(content),
        ClashPolicy = NameClashPolicy.Skip,
        Args = new Dictionary<string, string> { [Content] = content },
    };

    /// <summary>Update a note PC Tidy wrote before (its pctidy-id matched). Skipped by the engine if it changed since the scan.</summary>
    public static PlanItem EditText(string path, string content, DateTime lastWriteUtc, string description) => new()
    {
        Kind = ActionKind.EditTextFile,
        FeatureId = AiChatsIds.FeatureId,
        Description = description,
        SourcePath = path,
        ExpectedLastWriteUtc = lastWriteUtc,
        ExpectedSize = Encoding.UTF8.GetByteCount(content),
        Args = new Dictionary<string, string> { [Content] = content },
    };

    public static PlanItem CreateFolder(string path) => new()
    {
        Kind = ActionKind.CreateDirectory,
        FeatureId = AiChatsIds.FeatureId,
        Description = $"Create the folder \"{Path.GetFileName(path)}\"",
        DestinationPath = path,
        IsDirectory = true,
    };

    /// <summary>Copy + verify; the original stays exactly where it is.</summary>
    public static PlanItem Copy(string source, string destination, long size, DateTime lastWriteUtc, string description) => new()
    {
        Kind = ActionKind.CopyPath,
        FeatureId = AiChatsIds.FeatureId,
        Description = description,
        SourcePath = source,
        DestinationPath = destination,
        ExpectedSize = size,
        ExpectedLastWriteUtc = lastWriteUtc,
        ClashPolicy = NameClashPolicy.AutoNumber,
    };

    /// <summary>Into quarantine (restorable for the quarantine period).</summary>
    public static PlanItem Quarantine(string path, long size, DateTime lastWriteUtc, string description) => new()
    {
        Kind = ActionKind.QuarantinePath,
        FeatureId = AiChatsIds.FeatureId,
        Description = description,
        SourcePath = path,
        ExpectedSize = size,
        ExpectedLastWriteUtc = lastWriteUtc,
    };

    public static string Quote(string path) => "\"" + Path.GetFileName(path) + "\"";

    public static string Size(long bytes) => Sizes.Format(bytes);
}
