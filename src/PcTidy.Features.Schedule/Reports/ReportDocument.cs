using System.Globalization;
using System.Net;
using System.Text;
using PcTidy.Features.Schedule.Storage;

namespace PcTidy.Features.Schedule.Reports;

/// <summary>A block of a report. Rendered to Markdown and HTML from the same content, so both always agree.</summary>
public abstract record ReportBlock;
public sealed record HeadingBlock(string Text) : ReportBlock;
public sealed record ParagraphBlock(string Text) : ReportBlock;
public sealed record BulletsBlock(IReadOnlyList<string> Items) : ReportBlock;
public sealed record TableBlock(IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows) : ReportBlock;

/// <summary>
/// A report as data. <see cref="Id"/> goes into the Markdown frontmatter as <c>pctidy-id:</c>, which is how PC Tidy
/// recognises a file it wrote (it never overwrites a file without that marker).
/// </summary>
public sealed record ReportDocument
{
    public const string IdMarker = "pctidy-id:";

    public required string Id { get; init; }
    public required ReportKind Kind { get; init; }
    public required DateTime TimeUtc { get; init; }
    public required string Title { get; init; }
    /// <summary>Short name used in the file name ("Clean", "Weekly scan").</summary>
    public required string Slug { get; init; }
    public required IReadOnlyList<ReportBlock> Blocks { get; init; }

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append(IdMarker).Append(' ').Append(Id).Append('\n');
        sb.Append("pctidy-kind: ").Append(Kind.ToString().ToLowerInvariant()).Append('\n');
        sb.Append("date: ").Append(TimeUtc.ToLocalTime().ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("tags: [pc-tidy]\n");
        sb.Append("---\n\n");
        sb.Append("# ").Append(Md(Title)).Append("\n\n");
        foreach (var block in Blocks)
        {
            switch (block)
            {
                case HeadingBlock h:
                    sb.Append("## ").Append(Md(h.Text)).Append("\n\n");
                    break;
                case ParagraphBlock p:
                    sb.Append(Md(p.Text)).Append("\n\n");
                    break;
                case BulletsBlock b when b.Items.Count > 0:
                    foreach (var item in b.Items) sb.Append("- ").Append(Md(item)).Append('\n');
                    sb.Append('\n');
                    break;
                case TableBlock t when t.Rows.Count > 0:
                    sb.Append("| ").Append(string.Join(" | ", t.Headers.Select(Cell))).Append(" |\n");
                    sb.Append('|').Append(string.Concat(t.Headers.Select(_ => " --- |"))).Append('\n');
                    foreach (var row in t.Rows) sb.Append("| ").Append(string.Join(" | ", row.Select(Cell))).Append(" |\n");
                    sb.Append('\n');
                    break;
            }
        }
        sb.Append("*Written by PC Tidy. Reports only describe what happened; opening one never changes anything.*\n");
        return sb.ToString();
    }

    public string ToHtml()
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html>\n<html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append("<meta name=\"pctidy-id\" content=\"").Append(H(Id)).Append("\">");
        sb.Append("<title>").Append(H(Title)).Append("</title><style>");
        sb.Append(":root{color-scheme:light dark;--fg:#1b1b1b;--muted:#5f6368;--line:#d9dde1;--bg:#fff;--accent:#2BB673}");
        sb.Append("@media(prefers-color-scheme:dark){:root{--fg:#ececec;--muted:#a8adb3;--line:#3a3f44;--bg:#1f2123;--accent:#3CCB86}}");
        sb.Append("body{font-family:'Segoe UI',system-ui,sans-serif;background:var(--bg);color:var(--fg);max-width:880px;margin:32px auto;padding:0 16px;line-height:1.5}");
        sb.Append("h1{font-size:1.6em;border-bottom:3px solid var(--accent);padding-bottom:6px}h2{font-size:1.15em;margin-top:28px}");
        sb.Append("table{border-collapse:collapse;width:100%}th,td{text-align:left;padding:6px 8px;border-bottom:1px solid var(--line)}th{color:var(--muted);font-weight:600}");
        sb.Append("footer{color:var(--muted);font-size:.85em;margin-top:32px}</style></head><body>\n");
        sb.Append("<h1>").Append(H(Title)).Append("</h1>\n");
        sb.Append("<p style=\"color:var(--muted)\">").Append(H(TimeUtc.ToLocalTime().ToString("f", CultureInfo.CurrentCulture))).Append("</p>\n");
        foreach (var block in Blocks)
        {
            switch (block)
            {
                case HeadingBlock h: sb.Append("<h2>").Append(H(h.Text)).Append("</h2>\n"); break;
                case ParagraphBlock p: sb.Append("<p>").Append(H(p.Text)).Append("</p>\n"); break;
                case BulletsBlock b when b.Items.Count > 0:
                    sb.Append("<ul>");
                    foreach (var item in b.Items) sb.Append("<li>").Append(H(item)).Append("</li>");
                    sb.Append("</ul>\n");
                    break;
                case TableBlock t when t.Rows.Count > 0:
                    sb.Append("<table><thead><tr>");
                    foreach (var head in t.Headers) sb.Append("<th>").Append(H(head)).Append("</th>");
                    sb.Append("</tr></thead><tbody>");
                    foreach (var row in t.Rows)
                    {
                        sb.Append("<tr>");
                        foreach (var cell in row) sb.Append("<td>").Append(H(cell)).Append("</td>");
                        sb.Append("</tr>");
                    }
                    sb.Append("</tbody></table>\n");
                    break;
            }
        }
        sb.Append("<footer>Written by PC Tidy. Reports only describe what happened; opening one never changes anything.</footer>\n</body></html>\n");
        return sb.ToString();
    }

    private static string H(string s) => WebUtility.HtmlEncode(s);

    /// <summary>
    /// Escapes Markdown so file names and app names (data, not formatting) show as typed: no accidental bold, links,
    /// headings, HTML or wikilinks in a vault.
    /// </summary>
    internal static string Md(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (var c in s.Replace("\r", " ").Replace("\n", " "))
        {
            if (c is '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '>' or '#' or '|' or '!' or '{' or '}' or '~' or '=')
                sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string Cell(string s) => Md(s);
}
