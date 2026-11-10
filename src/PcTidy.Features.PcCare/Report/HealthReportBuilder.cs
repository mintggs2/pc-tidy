using System.Globalization;
using System.Net;
using System.Text;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.PcCare.Analysis;
using PcTidy.Features.PcCare.Model;
using PcTidy.Features.PcCare.Scanning;

namespace PcTidy.Features.PcCare.Report;

public sealed record ReportLine(string Label, string Value);

public sealed record ReportSection(string Heading, IReadOnlyList<ReportLine> Lines);

/// <summary>The health report's content, before it's rendered as HTML or Markdown.</summary>
public sealed record HealthReportModel(string Title, DateTime CreatedUtc, IReadOnlyList<ReportSection> Sections);

/// <summary>
/// Builds the exportable PC health report from the read-only snapshot: OS, uptime, antivirus, Windows Update, drives and
/// health, GPU temperature, startup count, app updates, browser flags, crashes, backups, restore points and recent PC Tidy
/// runs. Rendered text is always passed through <see cref="Redactor"/> (no user name, email or PC name).
/// </summary>
public static class HealthReportBuilder
{
    public static HealthReportModel Model(PcCareSnapshot s, PcCareBuildContext c, AntivirusVerdict? av)
    {
        var sections = new List<ReportSection>
        {
            new("This PC", ThisPc(s.Health, c.NowUtc)),
            new("Security", Security(s.Security, av)),
            new("Windows Update", Updates(s.Health, c.NowUtc)),
            new("Drives", Drives(s.Health, c.Drives)),
            new("Temperatures", Temps(s.Health)),
            new("Apps", Apps(s)),
            new("Browsers", Browsers(s.Browsers)),
            new($"Crashes (last {c.Options.CrashDays} days)", Crashes(s.Crashes, c)),
            new("Backups", Backups(s.Health)),
            new("Restore points", Restore(s.Restore)),
        };
        // Headings from other sections (e.g. Services), before the run list. A heading PC care already has is skipped.
        foreach (var extra in c.ExtraReportSections)
            if (!sections.Any(x => string.Equals(x.Heading, extra.Heading, StringComparison.OrdinalIgnoreCase))) sections.Add(extra);
        sections.Add(new("Recent PC Tidy runs", c.RecentRuns.Count == 0
            ? new[] { new ReportLine("Runs", "None yet") }
            : c.RecentRuns.OrderByDescending(r => r.StartedUtc).Take(10)
                .Select(r => new ReportLine(r.StartedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), $"{r.Title} ({r.Status})")).ToArray()));
        return new HealthReportModel("PC health report", c.NowUtc, sections.Where(x => x.Lines.Count > 0).ToList());
    }

    public static string Html(HealthReportModel m, IEnumerable<string> redactTerms)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>").Append(E(m.Title)).Append("</title>")
          .Append("<style>body{font-family:Segoe UI,Arial,sans-serif;max-width:860px;margin:32px auto;padding:0 16px;color:#1b1b1b}")
          .Append("h1{font-size:26px}h2{font-size:18px;margin-top:28px;border-bottom:1px solid #ddd;padding-bottom:4px}")
          .Append("table{border-collapse:collapse;width:100%}td{padding:4px 8px;vertical-align:top}td:first-child{color:#555;width:38%}")
          .Append("footer{margin-top:32px;color:#777;font-size:12px}</style></head><body>");
        sb.Append("<h1>").Append(E(m.Title)).Append("</h1><p>Made by PC Tidy on ")
          .Append(E(m.CreatedUtc.ToLocalTime().ToString("f", CultureInfo.CurrentCulture))).Append(". Read-only: making it changed nothing.</p>");
        foreach (var section in m.Sections)
        {
            sb.Append("<h2>").Append(E(section.Heading)).Append("</h2><table>");
            foreach (var line in section.Lines)
                sb.Append("<tr><td>").Append(E(line.Label)).Append("</td><td>").Append(E(line.Value)).Append("</td></tr>");
            sb.Append("</table>");
        }
        sb.Append("<footer>User names, email addresses and the PC's name are hidden so you can share this report.</footer></body></html>");
        return Redactor.Redact(sb.ToString(), redactTerms);
    }

    public static string Markdown(HealthReportModel m, IEnumerable<string> redactTerms)
    {
        static string Cell(string s) => s.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(m.Title).AppendLine()
          .Append("Made by PC Tidy on ").Append(m.CreatedUtc.ToLocalTime().ToString("f", CultureInfo.CurrentCulture)).AppendLine(". Read-only: making it changed nothing.");
        foreach (var section in m.Sections)
        {
            sb.AppendLine().Append("## ").AppendLine(section.Heading).AppendLine().AppendLine("| | |").AppendLine("|---|---|");
            foreach (var line in section.Lines) sb.Append("| ").Append(Cell(line.Label)).Append(" | ").Append(Cell(line.Value)).AppendLine(" |");
        }
        sb.AppendLine().AppendLine("_User names, email addresses and the PC's name are hidden so you can share this report._");
        return Redactor.Redact(sb.ToString(), redactTerms);
    }

    // ------------------------------------------------------------------ sections

    private static ReportLine[] ThisPc(HealthFacts? h, DateTime nowUtc)
    {
        if (h is null) return new[] { new ReportLine("Windows", "Not reported") };
        var list = new List<ReportLine>
        {
            new("Windows", string.Join(" ", new[] { h.OsName, h.DisplayVersion }.Where(x => !string.IsNullOrWhiteSpace(x))) + (h.Build > 0 ? $" (build {h.Build})" : "")),
        };
        if (h.LastBootUtc is { } boot)
        {
            var up = nowUtc - boot;
            list.Add(new("Running since last restart", up.TotalDays >= 1 ? $"{(int)up.TotalDays} days, {up.Hours} hours" : $"{up.Hours} hours, {up.Minutes} minutes"));
        }
        if (h.RebootPending) list.Add(new("Restart", "Windows is waiting for a restart to finish updates"));
        return list.ToArray();
    }

    private static ReportLine[] Security(SecurityFacts? sec, AntivirusVerdict? av)
    {
        if (sec is null || av is null) return new[] { new ReportLine("Antivirus", "Not reported") };
        var list = new List<ReportLine>
        {
            new("Antivirus", av.Situation switch
            {
                AvSituation.DefenderOn => "Microsoft Defender is on",
                AvSituation.ThirdPartyOn => string.Join(", ", av.ActiveThirdParty) + " is on",
                AvSituation.DefenderOffByPolicy => "Microsoft Defender is turned off by a policy; no other antivirus is on",
                AvSituation.NoActiveAntivirus => "No antivirus is on",
                _ => "Not reported",
            }),
        };
        if (sec.Defender?.SignaturesUpdatedUtc is { } sig) list.Add(new("Virus definitions updated", sig.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)));
        if (sec.Defender?.LastQuickScanUtc is { } q) list.Add(new("Last quick scan", q.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)));
        int vulnerable = sec.Drivers.Count(d => Catalog.PcCare.VulnerableDriverCatalog.Match(d.ImagePath) is not null);
        list.Add(new("Known vulnerable drivers", vulnerable == 0 ? "None found" : vulnerable.ToString(CultureInfo.CurrentCulture)));
        return list.ToArray();
    }

    private static ReportLine[] Updates(HealthFacts? h, DateTime nowUtc)
    {
        if (h is null) return Array.Empty<ReportLine>();
        var list = new List<ReportLine>();
        list.Add(new("Updates", h.UpdatesPausedUntilUtc switch
        {
            { } until when until > nowUtc => "Paused until " + until.ToLocalTime().ToString("d", CultureInfo.CurrentCulture),
            { } ended => "Not paused (the last pause ended on " + ended.ToLocalTime().ToString("d", CultureInfo.CurrentCulture) + ")",
            null => "Not paused",
        }));
        if (h.LastUpdateInstallUtc is { } last) list.Add(new("Last update installed", last.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)));
        return list.ToArray();
    }

    private static ReportLine[] Drives(HealthFacts? h, IReadOnlyList<DriveDetails> drives)
    {
        var list = drives.Select(d => new ReportLine($"{d.Root.TrimEnd('\\')} {d.Label}".Trim(),
            $"{Sizes.Format(d.FreeBytes)} free of {Sizes.Format(d.TotalBytes)}" + (d.Media is DriveMedia.Ssd ? " · SSD" : d.Media is DriveMedia.Hdd ? " · hard drive" : ""))).ToList();
        if (h is not null)
            list.AddRange(h.Disks.Select(d => new ReportLine("Disk " + d.Name, $"{d.Health} · {d.Media} · {Sizes.Format(d.SizeBytes)}")));
        return list.ToArray();
    }

    private static ReportLine[] Temps(HealthFacts? h)
        => h is null || h.Gpus.Count == 0
            ? new[] { new ReportLine("Graphics card", "Not readable on this PC") }
            : h.Gpus.Select(g => new ReportLine(g.Name, $"{g.TemperatureC} °C" + (g.FanPercent is { } f ? $" · fan {f}%" : ""))).ToArray();

    private static ReportLine[] Apps(PcCareSnapshot s)
    {
        var list = new List<ReportLine>();
        if (s.Health?.StartupItems is { } n) list.Add(new("Apps that start with Windows", n.ToString(CultureInfo.CurrentCulture)));
        list.Add(new("App updates (winget)", s.Winget?.State switch
        {
            WingetState.Ready => s.Winget.Upgrades.Count == 0 ? "Up to date" : $"{s.Winget.Upgrades.Count} available",
            WingetState.NotInstalled => "winget isn't installed",
            null => "Not checked",
            _ => "Couldn't be read",
        }));
        return list.ToArray();
    }

    private static ReportLine[] Browsers(BrowserFacts? b)
    {
        if (b is null) return Array.Empty<ReportLine>();
        return new[]
        {
            new ReportLine("Browser profiles checked", b.Profiles.Count.ToString(CultureInfo.CurrentCulture)),
            new ReportLine("Extensions worth a look", BrowserAudit.Extensions(b).Count.ToString(CultureInfo.CurrentCulture)),
            new ReportLine("Changed search/home/startup pages", BrowserAudit.Settings(b).Count.ToString(CultureInfo.CurrentCulture)),
            new ReportLine("Programs often flagged as unwanted", BrowserAudit.UnwantedPrograms(b).Count.ToString(CultureInfo.CurrentCulture)),
        };
    }

    private static ReportLine[] Crashes(CrashFacts? cr, PcCareBuildContext c)
    {
        if (cr is null || !cr.EventLogAvailable) return new[] { new ReportLine("Event log", "Couldn't be read") };
        var since = c.NowUtc.AddDays(-Math.Max(1, c.Options.CrashDays));
        var ev = cr.Events.Where(e => e.TimeUtc >= since).ToList();
        return new[]
        {
            new ReportLine("Blue screens", ev.Count(e => e.Kind == CrashKind.BlueScreen).ToString(CultureInfo.CurrentCulture)),
            new ReportLine("Unexpected restarts", ev.Count(e => e.Kind == CrashKind.UnexpectedShutdown).ToString(CultureInfo.CurrentCulture)),
            new ReportLine("Hardware errors", ev.Count(e => e.Kind == CrashKind.HardwareError).ToString(CultureInfo.CurrentCulture)),
            new ReportLine("App crashes and hangs", ev.Count(e => e.Kind is CrashKind.AppCrash or CrashKind.AppHang).ToString(CultureInfo.CurrentCulture)),
        };
    }

    private static ReportLine[] Backups(HealthFacts? h)
    {
        if (h is null) return Array.Empty<ReportLine>();
        return new[]
        {
            new ReportLine("OneDrive folder backup", h.OneDriveBackedUpFolders is { } n and > 0 ? $"{n} folder{(n == 1 ? "" : "s")} backed up" : "Not set up"),
            new ReportLine("File History", h.FileHistoryConfigured switch { true => "Set up", false => "Not set up", _ => "Unknown" }),
            new ReportLine("Reminder", "A synced folder isn't a backup on its own: deleting a file deletes the cloud copy too."),
        };
    }

    private static ReportLine[] Restore(RestoreFacts? r)
    {
        if (r is null) return Array.Empty<ReportLine>();
        if (r.Access == ReadAccess.NeedsAdmin) return new[] { new ReportLine("Restore points", "Needs admin to list") };
        var list = new List<ReportLine> { new("Restore points", r.Points.Count.ToString(CultureInfo.CurrentCulture)) };
        if (r.Points.Count > 0) list.Add(new("Newest", r.Points.Max(p => p.CreatedUtc).ToLocalTime().ToString("d", CultureInfo.CurrentCulture)));
        if (r.ProtectionOn == false) list.Add(new("System Protection", "Off"));
        if (r.Storage is { } st) list.Add(new("Space limit", $"{st.MaxPercent:0}% of the system drive"));
        return list.ToArray();
    }
}
