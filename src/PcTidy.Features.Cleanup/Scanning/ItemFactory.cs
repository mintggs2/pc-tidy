using PcTidy.Core;
using PcTidy.Core.Model;

namespace PcTidy.Features.Cleanup.Scanning;

/// <summary>How one result row should look. Unset values fall back to the target's Â§7 row.</summary>
internal sealed record ItemSpec
{
    public required string Title { get; init; }
    public string? Detail { get; init; }
    public Badge? Badge { get; init; }
    public bool? DefaultSelected { get; init; }
    public string? Reason { get; init; }
    public bool NeedsAdmin { get; init; }
    /// <summary>Process names that must be closed (lower-case, no .exe).</summary>
    public IReadOnlyList<string>? Apps { get; init; }
    public string? OwnerApp { get; init; }
    /// <summary>Only the opt-in History / Cookies rows set this.</summary>
    public bool PrivacyExtras { get; init; }
    /// <summary>How Deep-only finds in this row are described in the "What Deep found" note.</summary>
    public string? DeepLabel { get; init; }
    public IReadOnlyDictionary<string, string>? ExtraDetails { get; init; }
}

/// <summary>Builds result rows and their plan items. Never Reversible=false + ticked; never Check/Permanent + ticked.</summary>
internal static class ItemFactory
{
    public const string FeatureId = "cleanup";

    /// <summary>A row whose actions quarantine each pick. Null when nothing survives the never-touch guard.</summary>
    public static ScanItem? Quarantine(ScanSession s, CleanupTargetInfo target, ItemSpec spec, IEnumerable<Pick> picks)
    {
        var actions = new List<PlanItem>();
        long bytes = 0, deepBytes = 0;
        int files = 0, deepFiles = 0;
        DateTime? newest = null;

        foreach (var p in picks)
        {
            if (!s.Guard.Allows(p.Root, p.Entry.FullPath, spec.PrivacyExtras)) continue;
            actions.Add(new PlanItem
            {
                Kind = ActionKind.QuarantinePath,
                FeatureId = FeatureId,
                Description = $"Quarantine {Path.GetRelativePath(p.Root, p.Entry.FullPath)} ({spec.Title}, {Sizes.Format(p.Bytes)})",
                SourcePath = p.Entry.FullPath,
                IsDirectory = p.Entry.IsDirectory,
                ExpectedSize = p.Bytes,
                ExpectedLastWriteUtc = p.Entry.LastWriteUtc,
                CarveOutId = target.Id,
                NeedsAdmin = spec.NeedsAdmin,
                Reversible = true,
                RequiresClosedApps = spec.Apps,
            });
            bytes += p.Bytes;
            files += p.Files;
            if (p.DeepOnly) { deepBytes += p.Bytes; deepFiles += p.Files; }
            if (newest is null || p.Entry.LastWriteUtc > newest) newest = p.Entry.LastWriteUtc;
        }
        if (actions.Count == 0) return null;

        var details = new Dictionary<string, string>();
        if (spec.Detail is not null) details["Location"] = spec.Detail;
        details["Files"] = files.ToString("N0");
        if (deepFiles > 0)
        {
            details["Found by Deep scan"] = $"{deepFiles:N0} files, {Sizes.Format(deepBytes)}";
            s.Deep.Add(spec.DeepLabel ?? target.Title.ToLowerInvariant(), deepFiles, deepBytes);
        }
        AddExtras(details, spec);

        return BuildItem(s, target, spec, bytes, files, newest, details, actions, currentState: null);
    }

    /// <summary>A row that runs one of Windows' own tools (Reversible=false: never ticked).</summary>
    public static ScanItem SystemTool(ScanSession s, CleanupTargetInfo target, ItemSpec spec, PlanItem action,
        long estimatedBytes, long files, string? currentState)
    {
        var details = new Dictionary<string, string>();
        if (spec.Detail is not null) details["Location"] = spec.Detail;
        if (files > 0) details["Files"] = files.ToString("N0");
        details["Undo"] = "Can't be undone: Windows' own tool removes these for good.";
        AddExtras(details, spec);
        return BuildItem(s, target, spec with { DefaultSelected = false }, estimatedBytes, (int)Math.Min(int.MaxValue, files),
            null, details, new[] { action }, currentState);
    }

    /// <summary>Plan item for a Windows tool. Always not restorable.</summary>
    public static PlanItem ToolAction(CleanupTargetInfo target, ActionKind kind, string description, string? toolId,
        Dictionary<string, string>? args, long expectedSize, bool needsAdmin, bool isSystemChange)
        => new()
        {
            Kind = kind,
            FeatureId = FeatureId,
            Description = description,
            ToolId = toolId,
            Args = args,
            ExpectedSize = expectedSize,
            CarveOutId = target.Id,
            NeedsAdmin = needsAdmin,
            IsSystemChange = isSystemChange,
            Reversible = false,
        };

    /// <summary>Information-only row (no actions): sizes and guidance.</summary>
    public static ScanItem Info(ScanSession s, CleanupTargetInfo target, ItemSpec spec, long bytes, long files, string? currentState,
        string? disabledReason = null)
    {
        var details = new Dictionary<string, string>();
        if (spec.Detail is not null) details["Location"] = spec.Detail;
        if (files > 0) details["Files"] = files.ToString("N0");
        AddExtras(details, spec);
        return BuildItem(s, target, spec with { DefaultSelected = false }, bytes, (int)Math.Min(int.MaxValue, files), null, details,
            Array.Empty<PlanItem>(), currentState) with { DisabledReason = disabledReason };
    }

    private static ScanItem BuildItem(ScanSession s, CleanupTargetInfo target, ItemSpec spec, long bytes, int files, DateTime? newest,
        Dictionary<string, string> details, IReadOnlyList<PlanItem> actions, string? currentState)
    {
        var badge = spec.Badge ?? target.Badge;
        var reversible = actions.All(a => a.Reversible);
        // Hard rule: only Safe, fully reversible rows can be ticked by default.
        var selected = badge == Badge.Safe && reversible && actions.Count > 0 && (spec.DefaultSelected ?? target.DefaultSelected);
        return new ScanItem
        {
            Title = spec.Title,
            Detail = spec.Detail,
            Reason = s.WithOpenApps(spec.Reason ?? target.Reason, spec.Apps),
            Badge = badge,
            DefaultSelected = selected,
            SizeBytes = bytes,
            FileCount = files,
            NeedsAdmin = spec.NeedsAdmin || actions.Any(a => a.NeedsAdmin),
            RequiresClosedApps = spec.Apps,
            OwnerApp = spec.OwnerApp,
            LastModifiedUtc = newest,
            CurrentState = currentState,
            Details = details,
            Actions = actions,
        };
    }

    private static void AddExtras(Dictionary<string, string> details, ItemSpec spec)
    {
        if (spec.ExtraDetails is null) return;
        foreach (var (k, v) in spec.ExtraDetails) details[k] = v;
    }
}
