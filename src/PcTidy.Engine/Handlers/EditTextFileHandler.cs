using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Engine.IO;

namespace PcTidy.Engine.Handlers;

/// <summary>
/// Replaces a config/text file's content (game INI, Steam's localconfig.vdf, a tool's config) with
/// <c>Args["content"]</c>, keeping a byte-exact backup in quarantine (see <see cref="BackedUpTextEditHandler"/>).
/// The file is written back the way it was stored: same encoding and BOM (UTF-8, UTF-8 with BOM, UTF-16 LE/BE, UTF-32,
/// or this PC's ANSI code page) and the same line breaks when the file used one style throughout.
/// <para>Args:</para>
/// <list type="bullet">
/// <item><c>content</c> (required): the complete new text.</item>
/// <item><c>encoding</c> (optional): utf-8 | utf-8-bom | utf-16le | utf-16be | ansi | keep (default).</item>
/// <item><c>lineEndings</c> (optional): keep (default) | crlf | lf | asis.</item>
/// <item><c>createIfMissing</c> = "true" (optional): create the file (UTF-8, no BOM) when it doesn't exist; undo removes it.</item>
/// </list>
/// Text the encoding can't hold (e.g. "é" for an ANSI file on a Greek PC) is never written lossily: the step is skipped.
/// The engine skips the step if <c>ExpectedLastWriteUtc</c> is set and the file changed since, or a "close first" app runs.
/// </summary>
public sealed class EditTextFileHandler : BackedUpTextEditHandler
{
    public override bool CanHandle(PlanItem item) => item.Kind == ActionKind.EditTextFile;

    protected override string Target(PlanItem item, HandlerContext ctx) => Require(item.SourcePath ?? item.DestinationPath, "file path");

    protected override bool CreatesWhenMissing(PlanItem item) => Arg(item, "createIfMissing") is { } v && v.Equals("true", StringComparison.OrdinalIgnoreCase);

    protected override string What(string target) => $"'{Name(target)}'";

    protected override Edit Transform(byte[]? original, PlanItem item)
    {
        var content = Arg(item, "content") ?? throw new ArgumentException("The plan item has no content.");
        var format = (original is null ? TextFormat.NewFile : TextFormat.Detect(original)).WithEncoding(Arg(item, "encoding"));
        var bytes = format.Encode(content, Arg(item, "lineEndings"));
        var name = Name(Require(item.SourcePath ?? item.DestinationPath, "file path"));
        return bytes is null
            ? new Edit(null, $"The new text has characters that '{name}' can't store ({format.Describe()}), so it was left alone.", "", format.Name)
            : new Edit(bytes, null, $"Changed '{name}' (kept as {format.Describe()})", format.Name);
    }

    private static string? Arg(PlanItem item, string key) => item.Args is { } a && a.TryGetValue(key, out var v) ? v : null;
}
