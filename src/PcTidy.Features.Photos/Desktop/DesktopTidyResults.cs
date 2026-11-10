using PcTidy.Core.Model;
using PcTidy.Features.Organize.Planning;

namespace PcTidy.Features.Photos.Desktop;

/// <summary>Turns a <see cref="DesktopTidyPreview"/> into the shell's standard groups and items.</summary>
public static class DesktopTidyResults
{
    public const string SkippedGroupId = "desktoptidy.skipped";

    public static ScanResult Build(DesktopTidyPreview preview)
    {
        var roots = new[] { preview.Desktop };
        var groups = preview.Preview.Actions
            .GroupBy(a => a.Group, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ScanGroup
            {
                Id = "desktoptidy.move." + g.Key.ToLowerInvariant().Replace('\\', '.'),
                Title = g.Key,
                Description = OrganizeResultBuilder.DescribeMoves(g, roots),
                Badge = Badge.Safe,
                Items = g.Select(a => Item(a, preview)).ToList(),
            })
            .ToList();

        if (preview.Preview.Skipped.Count > 0)
            groups.Add(new ScanGroup
            {
                Id = SkippedGroupId,
                Title = "Stays put",
                Description = "These aren't moved. Each one says why.",
                Badge = Badge.Safe,
                Items = preview.Preview.Skipped.Select(s => new ScanItem
                {
                    Id = "desktoptidy:skip:" + s.Path,
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
            FeatureId = DesktopTidyModule.FeatureId,
            Title = "Desktop tidy",
            Depth = preview.Preview.Depth,
            Duration = preview.Preview.Duration,
            Groups = groups,
            Notes = preview.Preview.Notes,
        };
    }

    public static ScanItem Item(PlannedAction a, DesktopTidyPreview preview)
    {
        var roots = new[] { preview.Desktop };
        var from = DisplayPaths.Short(a.SourcePath, roots);
        var to = DisplayPaths.Short(a.DestinationPath!, roots);
        return new ScanItem
        {
            Id = "desktoptidy:" + a.SourcePath,
            Title = a.Name,
            Detail = $"{from} → {to}",
            Reason = a.Reason,
            Badge = a.Badge,
            DefaultSelected = a.Badge == Badge.Safe && a.DefaultSelected && !preview.NeedsPermission,
            SizeBytes = a.Size,
            FileCount = 1,
            LastModifiedUtc = a.LastWriteUtc,
            Details = new Dictionary<string, string> { ["From"] = from, ["To"] = to },
            Actions = a.Items,
            DisabledReason = preview.NeedsPermission ? DesktopTidyPlanner.PermissionReason : null,
        };
    }
}
