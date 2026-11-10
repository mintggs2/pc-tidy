using PcTidy.Core.Model;
using PcTidy.Features.Cleanup.Scanning;

namespace PcTidy.Features.Cleanup.Targets;

/// <summary>One §7 row. Read-only: lists what could be cleaned and describes it as plan items.</summary>
internal interface ICleanupTarget
{
    CleanupTargetInfo Info { get; }

    /// <summary>
    /// The rows for this target, or null when it doesn't apply to this PC (app not installed, folder missing):
    /// the group is then hidden, not shown as an error. An empty list = present, but nothing to clean right now.
    /// </summary>
    IReadOnlyList<ScanItem>? Collect(ScanSession s, CancellationToken ct);
}

internal static class TargetExtensions
{
    /// <summary>Drops the nulls ItemFactory returns when nothing survived the guard.</summary>
    public static List<ScanItem> Present(this IEnumerable<ScanItem?> items) => items.Where(i => i is not null).Select(i => i!).ToList();
}
