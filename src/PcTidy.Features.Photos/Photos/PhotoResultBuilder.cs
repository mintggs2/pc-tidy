using PcTidy.Core.Model;
using PcTidy.Features.Organize.Planning;

namespace PcTidy.Features.Photos.Photos;

/// <summary>Turns a <see cref="PhotoPreview"/> into the shell's standard groups and items.</summary>
public static class PhotoResultBuilder
{
    public const string BurstGroupId = "photos.bursts";
    public const string BlurryGroupId = "photos.blurry";
    public const string SkippedGroupId = "photos.skipped";

    public static string MoveGroupId(string year) => "photos.move." + year;

    /// <summary>
    /// Flag groups come first: if a flagged photo's quarantine and its move are both ticked, the quarantine runs first
    /// and the move is then skipped (the file is gone), so nothing is half-done.
    /// </summary>
    public static ScanResult Build(PhotoPreview preview, bool tickMoves = true)
    {
        var roots = preview.Roots.Select(r => r.Root).ToList();
        var groups = new List<ScanGroup>();

        var bursts = preview.Flags.Where(f => f.Kind == PhotoFlagKind.BurstExtra).ToList();
        if (bursts.Count > 0)
            groups.Add(new ScanGroup
            {
                Id = BurstGroupId, Title = "Burst shots", Badge = Badge.Check,
                Description = "Shots taken within 2 seconds that look almost the same. PC Tidy keeps the sharpest of each burst; the others are listed here for you to decide.",
                Items = bursts.Select(f => FlagItem(f, roots)).ToList(),
            });
        var blurry = preview.Flags.Where(f => f.Kind == PhotoFlagKind.Blurry).ToList();
        if (blurry.Count > 0)
            groups.Add(new ScanGroup
            {
                Id = BlurryGroupId, Title = "Blurry shots", Badge = Badge.Check,
                Description = "These look soft or shaken. It's a best guess (night shots and fog can look blurry too), so check each preview.",
                Items = blurry.Select(f => FlagItem(f, roots)).ToList(),
            });

        foreach (var year in preview.Moves.GroupBy(m => m.Year).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var months = year.Select(m => DisplayPaths.Short(Path.GetDirectoryName(m.DestinationPath)!, roots)).Distinct().ToList();
            groups.Add(new ScanGroup
            {
                Id = MoveGroupId(year.Key), Title = $"Sort into {year.Key}", Badge = Badge.Safe,
                Description = "Moves to " + string.Join(", ", months.Take(3)) + (months.Count > 3 ? $" and {months.Count - 3} more" : "") + ". Undoable from History.",
                Items = year.Select(m => MoveItem(m, roots, tickMoves)).ToList(),
            });
        }

        if (preview.Skipped.Count > 0)
            groups.Add(new ScanGroup
            {
                Id = SkippedGroupId, Title = "Stays put", Badge = Badge.Safe,
                Description = "These aren't moved. Each one says why.",
                Items = preview.Skipped.Select(s => new ScanItem
                {
                    Id = "photos:skip:" + s.Path,
                    Title = s.Name,
                    Detail = DisplayPaths.Short(s.Path, roots),
                    Reason = s.Reason,
                    Badge = Badge.Safe,
                    SizeBytes = s.Size,
                    FileCount = s.IsDirectory ? 0 : 1,
                    DisabledReason = s.Reason,
                }).ToList(),
            });

        return new ScanResult
        {
            FeatureId = PhotosModule.FeatureId,
            Title = "Photos",
            Depth = preview.Depth,
            Duration = preview.Duration,
            Groups = groups,
            Notes = preview.Notes,
        };
    }

    public static ScanItem MoveItem(PhotoMove m, IReadOnlyList<string> roots, bool tick = true) => new()
    {
        Id = "photos:move:" + m.SourcePath,
        Title = m.Name,
        Detail = $"{DisplayPaths.Short(m.SourcePath, roots)} → {DisplayPaths.Short(m.DestinationPath, roots)}",
        Reason = m.Reason,
        Badge = Badge.Safe,
        DefaultSelected = tick && m.DisabledReason is null,
        SizeBytes = m.Size,
        FileCount = 1,
        LastModifiedUtc = m.LastWriteUtc,
        // Placeholders never get this far, so a preview never downloads anything.
        PreviewPath = m.IsVideo ? null : m.SourcePath,
        Details = new Dictionary<string, string> { ["Date"] = m.Date.Describe(), ["From"] = DisplayPaths.Short(m.SourcePath, roots), ["To"] = DisplayPaths.Short(m.DestinationPath, roots) },
        Actions = m.Items,
        DisabledReason = m.DisabledReason,
    };

    public static ScanItem FlagItem(PhotoFlag f, IReadOnlyList<string> roots)
    {
        var details = new Dictionary<string, string>
        {
            ["Where"] = DisplayPaths.Short(f.SourcePath, roots),
            ["Sharpness"] = f.Metrics.Sharpness.ToString("0"),
        };
        if (f.KeptPath is not null) details["Kept"] = DisplayPaths.Short(f.KeptPath, roots);
        return new ScanItem
        {
            Id = "photos:flag:" + f.SourcePath,
            Title = f.Name,
            Detail = DisplayPaths.Short(f.SourcePath, roots),
            Reason = f.Reason,
            // Brief rule 8: Check items are never ticked by default.
            Badge = Badge.Check,
            DefaultSelected = false,
            SizeBytes = f.Size,
            FileCount = 1,
            LastModifiedUtc = f.LastWriteUtc,
            PreviewPath = f.SourcePath,
            Details = details,
            Actions = new[] { f.Item },
            DisabledReason = f.DisabledReason,
        };
    }
}
