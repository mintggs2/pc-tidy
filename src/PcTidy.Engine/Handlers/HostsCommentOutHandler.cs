using System.Text;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Engine.IO;

namespace PcTidy.Engine.Handlers;

/// <summary>
/// Turns off exact lines of Windows' hosts file by prefixing them with <see cref="Prefix"/>, keeping a byte-exact backup
/// (see <see cref="BackedUpTextEditHandler"/>); undo puts the original file back.
/// <para>
/// Only <c>%SystemRoot%\System32\drivers\etc\hosts</c> (from <see cref="IKnownLocations.WindowsDir"/>, so a sandbox uses
/// its own Windows folder) is ever changed, and only with admin rights (the elevated helper; in a sandbox the executor
/// runs it in-process). In the helper the backup goes to the admin-only quarantine, and undo uses the helper's own
/// record (ledger), so nothing the user can write decides what goes back into the hosts file.
/// </para>
/// Args: <c>lines</c> = the exact lines, "\n"-separated (every occurrence of each is turned off; encoding, BOM and line
/// breaks of the file are kept as they are). SourcePath = the hosts file.
/// </summary>
public sealed class HostsCommentOutHandler : BackedUpTextEditHandler
{
    public const string Prefix = "# PC Tidy: ";

    public override bool CanHandle(PlanItem item) => item.Kind == ActionKind.HostsCommentOut;

    /// <summary>Windows' own hosts file on this PC (or in the sandbox's Windows folder).</summary>
    public static string HostsPath(IKnownLocations locations) => Path.Combine(locations.WindowsDir, "System32", "drivers", "etc", "hosts");

    protected override string Target(PlanItem item, HandlerContext ctx) => Require(item.SourcePath ?? HostsPath(ctx.Locations), "hosts file");

    protected override string? Refusal(PlanItem item, HandlerContext ctx)
    {
        string target;
        try { target = Target(item, ctx); }
        catch (ArgumentException) { return "PC Tidy only changes Windows' own hosts file."; }
        if (!LongPath.Same(target, HostsPath(ctx.Locations))) return "PC Tidy only changes Windows' own hosts file.";
        if (!ctx.IsElevated && !ctx.Locations.IsSandbox) return "Needs admin rights, which aren't available here.";
        return null;
    }

    protected override string What(string target) => "the hosts file";

    protected override Edit Transform(byte[]? original, PlanItem item)
    {
        if (original is null) return new Edit(null, "The hosts file is missing, so nothing was changed.", "");
        var wanted = (item.Args is { } a && a.TryGetValue("lines", out var l) ? l : "")
            .Split('\n').Select(x => x.TrimEnd('\r')).Where(x => x.Trim().Length > 0).ToHashSet(StringComparer.Ordinal);
        if (wanted.Count == 0) return new Edit(null, "No lines were given, so nothing was changed.", "");

        var format = TextFormat.Detect(original);
        var text = format.Decode(original);
        var sb = new StringBuilder(text.Length + 64);
        int changed = 0, start = 0;
        while (start < text.Length)
        {
            var end = text.IndexOfAny(new[] { '\r', '\n' }, start);
            var lineEnd = end < 0 ? text.Length : end;
            var breakEnd = end < 0 ? text.Length : text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? end + 2 : end + 1;
            var line = text[start..lineEnd];
            if (wanted.Contains(line)) { sb.Append(Prefix); changed++; }
            sb.Append(text, start, breakEnd - start); // the line and its own line break, unchanged
            start = breakEnd;
        }
        if (changed == 0) return new Edit(null, "Those lines aren't in the hosts file any more, so nothing was changed.", "");
        var bytes = format.Encode(sb.ToString(), "asis");
        return bytes is null
            ? new Edit(null, "The hosts file couldn't be written back in its own encoding, so nothing was changed.", "")
            : new Edit(bytes, null, $"Turned off {changed} line(s) in the hosts file (they now start with \"{Prefix.TrimEnd()}\")", format.Name);
    }
}
