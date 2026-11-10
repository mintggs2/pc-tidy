using System.Globalization;
using PcTidy.Features.Schedule.DiskHealth;
using PcTidy.Features.Schedule.Drift;
using PcTidy.Features.Schedule.Metrics;
using PcTidy.Features.Schedule.Storage;

namespace PcTidy.Features.Schedule.Reports;

/// <summary>What one section's read-only scan found.</summary>
/// <param name="CleanBytes">Bytes its Safe, ticked-by-default items would clean (same counting as the bottom bar).</param>
/// <param name="SafeItems">Items that would be ticked by default.</param>
/// <param name="OtherItems">Other offered items, for you to look at (never counted as "can clean").</param>
/// <param name="Problem">Plain-English reason when the section couldn't finish.</param>
public sealed record SectionFinding(string FeatureId, string Title, long CleanBytes, int SafeItems, int OtherItems, IReadOnlyList<string> Notes, string? Problem);

/// <summary>Everything a scheduled scan or monthly checkup found. Nothing in here was changed.</summary>
public sealed record ScanFindings
{
    public required DateTime TimeUtc { get; init; }
    public required bool Checkup { get; init; }
    /// <summary>Started from the page ("Scan now"), not by a schedule.</summary>
    public bool Manual { get; init; }
    public required IReadOnlyList<SectionFinding> Sections { get; init; }
    public long CleanBytes => Sections.Sum(s => s.CleanBytes);
    public int OtherItems => Sections.Sum(s => s.OtherItems);
    public ScanSummary? Previous { get; init; }
    public DriftReport? Drift { get; init; }
    public IReadOnlyList<DiskReading> Disks { get; init; } = Array.Empty<DiskReading>();
    public IReadOnlyList<string> DiskWarnings { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, double>? Metrics { get; init; }
    public bool PracticeMode { get; init; }

    public string Headline => CleanBytes > 0
        ? $"PC Tidy found {Sizes.Format(CleanBytes)} you can clean"
        : "Nothing needs cleaning right now";

    public ScanSummary ToSummary(string? reportPath) => new()
    {
        TimeUtc = TimeUtc, Checkup = Checkup, CleanBytes = CleanBytes,
        BySection = Sections.Where(s => s.CleanBytes > 0).ToDictionary(s => s.FeatureId, s => s.CleanBytes),
        DriftCount = Drift?.Drifted.Count ?? 0, ReportPath = reportPath,
    };
}

/// <summary>The weekly scan / monthly checkup report. Report only: it says so at the top.</summary>
public static class ScanReportBuilder
{
    public static ReportDocument Build(ScanFindings f)
    {
        // Named for what started it: a scan you started yourself is never called "weekly".
        var name = f.Manual ? (f.Checkup ? "Checkup (you started it)" : "Scan (you started it)") : f.Checkup ? "Monthly checkup" : "Weekly scan";
        var blocks = new List<ReportBlock>
        {
            new ParagraphBlock($"{f.Headline}. This was a scan only: nothing on this PC was changed."
                               + (f.PracticeMode ? " (Practice PC: these numbers are from the practice folder, not this PC.)" : "")),
        };

        if (f.Checkup && f.Previous is { } prev)
        {
            var diff = f.CleanBytes - prev.CleanBytes;
            var since = prev.TimeUtc.ToLocalTime().ToString("d MMM", CultureInfo.CurrentCulture);
            var grew = f.Sections
                .Select(s => (s.Title, Delta: s.CleanBytes - (prev.BySection.TryGetValue(s.FeatureId, out var b) ? b : 0)))
                .Where(x => x.Delta > 1024 * 1024).OrderByDescending(x => x.Delta).Take(4)
                .Select(x => $"{x.Title} +{Sizes.Format(x.Delta)}").ToList();
            blocks.Add(new HeadingBlock("What built up"));
            blocks.Add(new ParagraphBlock(diff > 0
                ? $"Since the scan on {since}: {Sizes.Format(diff)} more to clean" + (grew.Count > 0 ? " (" + string.Join(", ", grew) + ")." : ".")
                : $"Nothing new built up since the scan on {since}."));
        }

        blocks.Add(new HeadingBlock("By section"));
        var rows = f.Sections.OrderByDescending(s => s.CleanBytes).ThenBy(s => s.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(s => (IReadOnlyList<string>)new[]
            {
                s.Title,
                s.Problem is not null ? "Couldn't finish" : s.CleanBytes > 0 ? Sizes.Format(s.CleanBytes) : "Nothing",
                s.OtherItems > 0 ? $"{s.OtherItems:N0} to look at" : "",
            }).ToList();
        blocks.Add(new TableBlock(new[] { "Section", "Can clean (Safe)", "Your call" }, rows));
        var problems = f.Sections.Where(s => s.Problem is not null).Select(s => $"{s.Title}: {s.Problem}").ToList();
        if (problems.Count > 0) blocks.Add(new BulletsBlock(problems));
        var notes = f.Sections.SelectMany(s => s.Notes.Select(n => $"{s.Title}: {n}")).Take(12).ToList();
        if (notes.Count > 0)
        {
            blocks.Add(new HeadingBlock("Notes from the scan"));
            blocks.Add(new BulletsBlock(notes));
        }

        if (f.Drift is { } drift)
        {
            blocks.Add(new HeadingBlock("Tweaks"));
            if (drift.Headline is { } headline)
            {
                blocks.Add(new ParagraphBlock(headline + " " + drift.Advice));
                blocks.Add(new TableBlock(new[] { "Tweak", "PC Tidy set", "Now" },
                    drift.Drifted.Select(d => (IReadOnlyList<string>)new[] { d.Description, d.AppliedText, d.CurrentText }).ToList()));
            }
            else blocks.Add(new ParagraphBlock(drift.Checked > 0
                ? $"All {drift.Checked} tweaks you applied are still in place."
                : "You haven't applied any tweaks with PC Tidy, so there's nothing to check."));
        }

        if (f.Disks.Count > 0 || f.DiskWarnings.Count > 0)
        {
            blocks.Add(new HeadingBlock("Disk health"));
            if (f.DiskWarnings.Count > 0) blocks.Add(new BulletsBlock(f.DiskWarnings));
            blocks.Add(new TableBlock(new[] { "Disk", "Health", "Temperature", "Wear" },
                f.Disks.Select(d => (IReadOnlyList<string>)new[]
                {
                    d.Name, DiskHealthAdvice.StatusText(d.Health),
                    d.TemperatureC is { } t ? $"{t:0} °C" : "Not readable",
                    d.WearPercent is { } w ? $"{w:0} %" : "Not readable",
                }).ToList()));
        }

        if (f.Metrics is { Count: > 0 } m)
        {
            blocks.Add(new HeadingBlock("This PC today"));
            blocks.Add(new BulletsBlock(MetricKeys.Ordered(m.Keys)
                .Select(k => MetricKeys.Describe(k) is { } info ? $"{info.Label}: {info.Format(m[k])}" : null)
                .OfType<string>().ToList()));
        }

        blocks.Add(new HeadingBlock("Next step"));
        blocks.Add(new ParagraphBlock(f.CleanBytes > 0
            ? "Open PC Tidy and press Review: you'll see everything first, untick what you want to keep, and confirm. Everything can be undone."
            : "Nothing to do. PC Tidy will look again next time."));

        return new ReportDocument
        {
            Id = (f.Checkup ? "checkup-" : "scan-") + f.TimeUtc.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture),
            Kind = f.Checkup ? ReportKind.Checkup : ReportKind.Scan,
            TimeUtc = f.TimeUtc,
            Slug = name,
            Title = $"PC Tidy {char.ToLowerInvariant(name[0]) + name[1..]}: {f.TimeUtc.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture)}",
            Blocks = blocks,
        };
    }
}
