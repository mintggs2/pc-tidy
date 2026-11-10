namespace PcTidy.Features.Schedule.Reports;

/// <summary>
/// The optional copy of a report into a folder the user picked (e.g. an Obsidian vault's "40 Reports"). It touches a
/// user folder, so it is a <see cref="ActionKind.WriteTextFile"/> plan item (journaled, undoable), never a direct write.
/// It only ever creates a new file: if the name is taken by a file PC Tidy didn't write (no matching
/// <c>pctidy-id:</c>), it picks "name (2).md"; if PC Tidy already copied this exact report, nothing is planned.
/// </summary>
public static class ReportCopyPlanner
{
    public static PlanItem? Plan(ReportDocument report, string targetFolder, string markdown)
        => Plan(report.Id, report.Title, FileName(report), targetFolder, markdown);

    /// <summary>Copy of a report already saved in &lt;DataRoot&gt;\reports (its own data, read back as text).</summary>
    public static PlanItem? PlanSaved(string savedMarkdownPath, string targetFolder)
    {
        string markdown;
        try { markdown = File.ReadAllText(savedMarkdownPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        var lines = markdown.Split('\n');
        var id = lines.Take(12).FirstOrDefault(l => l.StartsWith(ReportDocument.IdMarker, StringComparison.Ordinal))?[ReportDocument.IdMarker.Length..].Trim();
        if (string.IsNullOrEmpty(id)) return null; // not a PC Tidy report
        var title = lines.FirstOrDefault(l => l.StartsWith("# ", StringComparison.Ordinal))?[2..].Trim().Replace("\\", "") ?? Path.GetFileNameWithoutExtension(savedMarkdownPath);
        return Plan(id, title, Path.GetFileName(savedMarkdownPath), targetFolder, markdown);
    }

    private static PlanItem? Plan(string id, string title, string fileName, string targetFolder, string markdown)
    {
        if (string.IsNullOrWhiteSpace(targetFolder) || !Path.IsPathFullyQualified(targetFolder)) return null;
        var desired = Path.Combine(targetFolder, fileName);
        var destination = desired;
        for (int n = 2; File.Exists(destination) || Directory.Exists(destination); n++)
        {
            if (File.Exists(destination) && WrittenByPcTidy(destination, id)) return null; // already copied
            if (n > 999) return null;
            destination = Path.Combine(targetFolder, $"{Path.GetFileNameWithoutExtension(desired)} ({n}).md");
        }
        return new PlanItem
        {
            Kind = ActionKind.WriteTextFile,
            FeatureId = Schedules.ScheduleIds.FeatureId,
            Description = $"Save a copy of the report \"{title}\" in {targetFolder}",
            DestinationPath = destination,
            ClashPolicy = NameClashPolicy.Skip,
            Reversible = true,
            Args = new Dictionary<string, string> { ["content"] = markdown },
        };
    }

    public static string FileName(ReportDocument report)
    {
        var stamp = report.TimeUtc.ToLocalTime().ToString("yyyy-MM-dd HHmm", System.Globalization.CultureInfo.InvariantCulture);
        var invalid = Path.GetInvalidFileNameChars();
        var slug = new string(report.Slug.Select(c => invalid.Contains(c) ? ' ' : c).ToArray()).Trim().TrimEnd('.');
        return $"{stamp} PC Tidy {(slug.Length == 0 ? "report" : slug)}.md";
    }

    /// <summary>Reads only the frontmatter (first lines) of an existing file to see if it is this report.</summary>
    internal static bool WrittenByPcTidy(string path, string id)
    {
        try
        {
            // An online-only cloud file is never opened (that would download it): treated as "not ours".
            if (CloudFile.IsPlaceholder(File.GetAttributes(path))) return false;
            foreach (var line in File.ReadLines(path).Take(12))
                if (line.StartsWith(ReportDocument.IdMarker, StringComparison.Ordinal))
                    return string.Equals(line[ReportDocument.IdMarker.Length..].Trim(), id, StringComparison.Ordinal);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
