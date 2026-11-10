using PcTidy.Core.Model;

namespace PcTidy.Features.Drivers.Planning;

/// <summary>A plan ready for the standard confirm, or a plain reason why the picks can't run together.</summary>
public sealed record DriverPlanResult(ActionPlan? Plan, string? Problem);

/// <summary>
/// Turns the ticked Drivers rows into one plan with a fixed, safe order:
/// 1. one backup of all third-party drivers (whenever anything changes a driver),
/// 2. rollbacks, 3. Windows Update installs (merged into one step), 4. restores from a backup,
/// 5. driver-store removals, 6. at most ONE old-device removal, 7. pages to open.
/// </summary>
public static class DriverPlanBuilder
{
    /// <summary>Old devices are removed one at a time (never in bulk).</summary>
    public const int MaxDeviceRemovalsPerRun = 1;

    public static DriverPlanResult Build(string title, IEnumerable<ScanItem> picked, string exportFolder, string? parentRunId = null)
    {
        var actions = picked.Where(i => i.DisabledReason is null).SelectMany(i => i.Actions).ToList();
        if (actions.Count == 0) return new DriverPlanResult(null, "Nothing is ticked.");

        var removals = actions.Where(DriverActions.IsDeviceRemoval)
            .GroupBy(a => a.Args!["instanceId"], StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        if (removals.Count > MaxDeviceRemovalsPerRun)
            return new DriverPlanResult(null, "Old devices are removed one at a time, so you can check each one. Tick just one.");

        var exports = actions.Where(DriverActions.IsExport).ToList();
        var rest = actions.Where(a => !DriverActions.IsExport(a) && !DriverActions.IsDeviceRemoval(a)).ToList();

        var items = new List<PlanItem>();
        if (exports.Count > 0 || rest.Any(DriverActions.IsDriverChange))
        {
            var folder = exports.Select(e => e.Args?.GetValueOrDefault("folder")).FirstOrDefault(f => !string.IsNullOrWhiteSpace(f)) ?? exportFolder;
            items.Add(DriverActions.ExportAll(SeparateFromRestores(folder, rest)));
        }

        items.AddRange(Distinct(rest.Where(a => a.ToolId == ToolIds.DriverRollback), "instanceId"));

        var updateIds = rest.Where(DriverActions.IsWindowsUpdateInstall)
            .SelectMany(a => (a.Args?.GetValueOrDefault("updateIds") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (updateIds.Count > 0)
        {
            var single = rest.First(DriverActions.IsWindowsUpdateInstall);
            items.Add(updateIds.Count == 1
                ? single
                : DriverActions.InstallWindowsUpdates(updateIds, $"Install {updateIds.Count} driver updates from Windows Update"));
        }

        items.AddRange(Distinct(rest.Where(a => a.ToolId == ToolIds.PnpAddDrivers), "folder"));
        items.AddRange(Distinct(rest.Where(a => a.ToolId == ToolIds.PnpDeleteDriver), "inf"));
        items.AddRange(removals);
        items.AddRange(Distinct(rest.Where(a => a.Kind == ActionKind.OpenExternal), "target"));

        return new DriverPlanResult(new ActionPlan { FeatureId = DriverActions.FeatureId, Title = title, ParentRunId = parentRunId, Items = AfterBackup(items) }, null);
    }

    /// <summary>
    /// "Your drivers are backed up first" is enforced: every step that changes a driver or a device needs the backup step
    /// to have finished. If the backup fails or is skipped, they are all skipped too (most of them can't be undone).
    /// The rows' own backup steps were merged into this one, so each change is re-pointed at it.
    /// </summary>
    private static IReadOnlyList<PlanItem> AfterBackup(List<PlanItem> items)
    {
        var backup = items.FirstOrDefault(DriverActions.IsExport);
        if (backup is null) return items;
        return items.Select(i => DriverActions.IsDriverChange(i) || DriverActions.IsDeviceRemoval(i)
            ? DriverActions.NeedsBackup(i, backup)
            : i).ToList();
    }

    /// <summary>
    /// A backup made right before putting drivers back must not land in the folder being restored from
    /// (today's backup), or today's drivers would be restored along with it. Such a backup gets "-2", "-3"….
    /// </summary>
    private static string SeparateFromRestores(string folder, IReadOnlyList<PlanItem> rest)
    {
        var sources = rest.Where(a => a.ToolId == ToolIds.PnpAddDrivers).Select(a => a.Args?.GetValueOrDefault("folder") ?? "")
            .Select(f => f.TrimEnd('\\')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidate = folder;
        for (int n = 2; sources.Contains(candidate.TrimEnd('\\')); n++) candidate = folder.TrimEnd('\\') + "-" + n;
        return candidate;
    }

    private static IEnumerable<PlanItem> Distinct(IEnumerable<PlanItem> items, string argKey)
        => items.GroupBy(a => a.Args?.GetValueOrDefault(argKey) ?? a.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First());
}
