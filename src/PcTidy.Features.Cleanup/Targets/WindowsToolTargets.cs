using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Cleanup.Scanning;

namespace PcTidy.Features.Cleanup.Targets;

/// <summary>§7 #11: Delivery Optimization cache, cleared with Windows' own command. Size measured read-only where readable.</summary>
internal sealed class DeliveryOptimizationTarget : ICleanupTarget
{
    public CleanupTargetInfo Info => CleanupTargets.DeliveryOptimization;

    public IReadOnlyList<ScanItem>? Collect(ScanSession s, CancellationToken ct)
    {
        var w = s.Locations.WindowsDir;
        // Windows 11 / newer Windows 10 keep it under SoftwareDistribution; older builds under the NetworkService profile.
        var roots = new[]
        {
            Path.Combine(w, "SoftwareDistribution", "DeliveryOptimization"),
            Path.Combine(w, "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache"),
        };
        var measures = roots.Select(r => (Root: r, M: s.Measure(r, ct))).Where(x => x.M.Exists).ToList();
        // Every Windows 10/11 PC has Delivery Optimization, but its folder may be unreadable without admin:
        // then the row is still offered and its size is measured when it runs. The sandbox simply has it or not.
        if (measures.Count == 0 && s.Locations.IsSandbox) return null;

        var bytes = measures.Sum(x => x.M.Bytes);
        var files = measures.Sum(x => x.M.Files);
        var unmeasured = measures.Count == 0 || measures.Any(x => x.M.PartlyUnreadable);
        var action = ItemFactory.ToolAction(Info, ActionKind.RunSystemTool, "Clear the Delivery Optimization cache with Windows' own command",
            ToolIds.DeliveryOptimizationClear, null, bytes, needsAdmin: true, isSystemChange: false);
        return new[]
        {
            ItemFactory.SystemTool(s, Info, new ItemSpec
            {
                Title = Info.Title,
                Detail = measures.FirstOrDefault().Root ?? roots[0],
                NeedsAdmin = true,
            }, action, bytes, files, unmeasured ? (bytes > 0 ? $"At least {Sizes.Format(bytes)}" : "Size measured when run") : null),
        };
    }
}

/// <summary>§7 #13: thumbnail and icon caches, via Windows' Thumbnail cleaner (Explorer keeps them open).</summary>
internal sealed class ThumbnailCacheTarget : ICleanupTarget
{
    public CleanupTargetInfo Info => CleanupTargets.ThumbnailCache;

    public IReadOnlyList<ScanItem>? Collect(ScanSession s, CancellationToken ct)
    {
        var explorer = Path.Combine(s.Locations.LocalAppData, "Microsoft", "Windows", "Explorer");
        var caches = s.ChildFiles(explorer, ct)
            .Where(f => f.Name.EndsWith(".db", StringComparison.OrdinalIgnoreCase)
                        && (f.Name.StartsWith("thumbcache_", StringComparison.OrdinalIgnoreCase)
                            || f.Name.StartsWith("iconcache_", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (caches.Count == 0) return null;

        var bytes = caches.Sum(f => f.Size);
        var action = ItemFactory.ToolAction(Info, ActionKind.RunSystemTool, "Clear thumbnail and icon caches with Windows' Thumbnail cleaner",
            ToolIds.CleanmgrHandler, new Dictionary<string, string> { ["handler"] = "Thumbnail Cache" }, bytes, needsAdmin: false, isSystemChange: false);
        return new[] { ItemFactory.SystemTool(s, Info, new ItemSpec { Title = Info.Title, Detail = explorer }, action, bytes, caches.Count, null) };
    }
}

/// <summary>§7 #14: the current user's Recycle Bin, emptied through the Recycle Bin API. Permanent.</summary>
internal sealed class RecycleBinTarget : ICleanupTarget
{
    public CleanupTargetInfo Info => CleanupTargets.RecycleBin;

    public IReadOnlyList<ScanItem>? Collect(ScanSession s, CancellationToken ct)
    {
        if (s.SystemInfo.QueryRecycleBin(ct) is not { } bin) return null;
        if (bin.Items == 0 && bin.Bytes == 0) return Array.Empty<ScanItem>();
        var action = ItemFactory.ToolAction(Info, ActionKind.EmptyRecycleBin, "Empty the Recycle Bin (can't be undone)",
            null, null, bin.Bytes, needsAdmin: false, isSystemChange: false);
        return new[]
        {
            ItemFactory.SystemTool(s, Info, new ItemSpec { Title = Info.Title, ExtraDetails = new Dictionary<string, string> { ["Items"] = bin.Items.ToString("N0") } },
                action, bin.Bytes, bin.Items, null),
        };
    }
}

/// <summary>§7 #20: Windows.old and the $Windows.~BT / ~WS setup folders, via Windows' "Previous Installations" cleaner.</summary>
internal sealed class WindowsOldTarget : ICleanupTarget
{
    public CleanupTargetInfo Info => CleanupTargets.WindowsOld;

    public IReadOnlyList<ScanItem>? Collect(ScanSession s, CancellationToken ct)
    {
        var drive = s.Locations.SystemDrive;
        var folders = new[] { "Windows.old", "$Windows.~BT", "$Windows.~WS" }
            .Select(n => (Path: Path.Combine(drive, n), M: s.Measure(Path.Combine(drive, n), ct)))
            .Where(x => x.M.Exists)
            .ToList();
        if (folders.Count == 0) return null;

        var bytes = folders.Sum(x => x.M.Bytes);
        var files = folders.Sum(x => x.M.Files);
        var partial = folders.Any(x => x.M.PartlyUnreadable);
        var action = ItemFactory.ToolAction(Info, ActionKind.RunSystemTool, "Remove the previous Windows installation with Windows' own cleaner (can't be undone)",
            ToolIds.CleanmgrHandler, new Dictionary<string, string> { ["handler"] = "Previous Installations" }, bytes, needsAdmin: true, isSystemChange: true);
        return new[]
        {
            ItemFactory.SystemTool(s, Info, new ItemSpec
            {
                Title = Info.Title,
                Detail = string.Join(", ", folders.Select(f => f.Path)),
                NeedsAdmin = true,
            }, action, bytes, files, partial ? $"At least {Sizes.Format(bytes)} (some parts need admin to measure)" : null),
        };
    }
}

/// <summary>§7 #21: DISM component cleanup (never /ResetBase). Its size is only known once DISM analyses it.</summary>
internal sealed class ComponentStoreTarget : ICleanupTarget
{
    public CleanupTargetInfo Info => CleanupTargets.ComponentStore;

    public IReadOnlyList<ScanItem>? Collect(ScanSession s, CancellationToken ct)
    {
        var winSxS = Path.Combine(s.Locations.WindowsDir, "WinSxS");
        if (!Directory.Exists(winSxS)) return null;
        var action = ItemFactory.ToolAction(Info, ActionKind.RunSystemTool, "Clean up replaced Windows components with DISM (can't be undone)",
            ToolIds.DismStartComponentCleanup, null, 0, needsAdmin: true, isSystemChange: true);
        return new[]
        {
            ItemFactory.SystemTool(s, Info, new ItemSpec { Title = Info.Title, Detail = winSxS, NeedsAdmin = true }, action, 0, 0, "Measured when run"),
        };
    }
}
