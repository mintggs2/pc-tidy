using System.Diagnostics;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Cleanup.SystemState;

namespace PcTidy.Features.Cleanup;

/// <summary>
/// "Big hidden Windows files" tab (Plan Â§8). Read-only report of hiberfil.sys, pagefile.sys, System Restore storage,
/// the search index, reserved storage and CompactOS. The only changes offered are the hibernation file type
/// (Reduced / Off), always unticked and needing admin. Never offers to turn off the pagefile or System Restore.
/// </summary>
public sealed class HiddenFilesScanner : IFeatureScanner
{
    public const string HibernationGroupId = "cleanup.hidden.hibernation";
    public const string PagefileGroupId = "cleanup.hidden.pagefile";
    public const string RestoreGroupId = "cleanup.hidden.restore";
    public const string SearchIndexGroupId = "cleanup.hidden.search-index";
    public const string ReservedGroupId = "cleanup.hidden.reserved";

    private const int HiberTypeReduced = 1;
    private const long LargeSearchIndexBytes = 5L * 1024 * 1024 * 1024;

    public string FeatureId => "cleanup.hidden";
    public string Title => "Big hidden Windows files";
    public Release Release => Release.V2;
    /// <summary>Situational changes only: never part of "Optimize everything".</summary>
    public bool IncludedInOptimizeAll => false;

    public Task<ScanResult> ScanAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
        => Task.Run(() => Scan(ctx, progress, ct), ct);

    private ScanResult Scan(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var system = SystemStateFactory.For(ctx.Locations, ctx.Walker);
        var notes = new List<string>();
        var rootFiles = DriveRootFiles(ctx, ct);
        var groups = new List<ScanGroup>();

        void Add(string title, Func<ScanGroup?> build)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ScanProgress($"Checking {title}…", (double)groups.Count / 5));
            try
            {
                if (build() is { } g) groups.Add(g);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                notes.Add($"{title} couldn't be checked ({ex.Message}).");
            }
        }

        Add("the hibernation file", () => Hibernation(system, rootFiles, ctx.Locations.SystemDrive));
        Add("the pagefile", () => Pagefile(system, rootFiles, ctx.Locations.SystemDrive));
        Add("System Restore storage", () => RestoreStorage(system, ct));
        Add("the search index", () => SearchIndex(ctx, ct));
        Add("reserved storage", () => ReservedAndCompact(system));
        progress?.Report(new ScanProgress("Done", 1));

        return new ScanResult
        {
            FeatureId = FeatureId,
            Title = Title,
            Depth = ctx.Depth,
            Duration = clock.Elapsed,
            Groups = groups,
            Notes = notes,
        };
    }

    private ScanGroup? Hibernation(ISystemState system, IReadOnlyDictionary<string, FileEntry> rootFiles, string systemDrive)
    {
        var power = system.ReadPower();
        rootFiles.TryGetValue("hiberfil.sys", out var file);
        var exists = rootFiles.ContainsKey("hiberfil.sys");
        var enabled = power.HibernateEnabled ?? exists;
        if (!enabled && !exists) return null; // off already: nothing to say

        var reduced = power.HiberFileType == HiberTypeReduced;
        var state = !enabled ? "Off" : reduced ? "Reduced" : "Full";
        var size = exists ? file.Size : 0;
        var items = new List<ScanItem>
        {
            Report("Hibernation file (hiberfil.sys)", Path.Combine(systemDrive, "hiberfil.sys"),
                "Lets your PC hibernate and powers Fast Startup. Full is about 40% of your memory; Reduced is about 20% and still keeps Fast Startup.",
                size, state),
        };
        if (enabled)
        {
            var laptop = power.HasBattery == true
                ? " This PC has a battery: if you use Hibernate, leave this alone."
                : "";
            if (!reduced)
            {
                // Reduced â‰ˆ 20% of RAM; estimate the saving from the current file when memory size is known.
                var reducedSize = power.TotalMemoryBytes is { } ram ? ram / 5 : size / 2;
                var saving = Math.Max(0, size - reducedSize);
                items.Add(HibernateChange("Switch the hibernation file to Reduced",
                    $"Keeps Fast Startup but drops the Hibernate option. Frees about {Sizes.Format(saving)}. Undo sets it back to Full.{laptop}",
                    "reduced", "full", saving));
            }
            items.Add(HibernateChange("Turn hibernation off",
                $"Removes the whole file, but also turns off Fast Startup and Hibernate. Only if you don't want those.{laptop}",
                "off", reduced ? "reduced" : "full", size));
        }
        return new ScanGroup
        {
            Id = HibernationGroupId,
            Title = "Hibernation file",
            Description = "Changing it needs admin. No restart needed, and it can be undone.",
            Badge = Badge.Check,
            Items = items,
        };
    }

    private ScanItem HibernateChange(string title, string reason, string type, string undoType, long saving) => new()
    {
        Title = title,
        Reason = reason,
        Badge = Badge.Check,
        DefaultSelected = false,
        SizeBytes = saving,
        NeedsAdmin = true,
        CurrentState = type == "off" ? "Would turn off" : "Would become Reduced",
        Actions = new[]
        {
            new PlanItem
            {
                Kind = ActionKind.RunSystemTool,
                FeatureId = FeatureId,
                Description = title,
                ToolId = ToolIds.HibernateType,
                // undoType tells the handler how to put it back (powercfg /h /type full|reduced).
                Args = new Dictionary<string, string> { ["type"] = type, ["undoType"] = undoType },
                ExpectedSize = saving,
                NeedsAdmin = true,
                Reversible = true,
            },
        },
    };

    private ScanGroup Pagefile(ISystemState system, IReadOnlyDictionary<string, FileEntry> rootFiles, string systemDrive)
    {
        rootFiles.TryGetValue("pagefile.sys", out var page);
        rootFiles.TryGetValue("swapfile.sys", out var swap);
        var lines = system.PagingFiles();
        var managed = lines is null ? (bool?)null : IsSystemManaged(lines);
        var state = managed switch
        {
            true => "Managed by Windows (best)",
            false => "Custom size",
            null => "Unknown",
        };
        var reason = managed == false
            ? "Windows needs it for memory and crash reports. Someone set a custom size: Windows usually does best managing it itself (System > About > Advanced system settings > Performance > Advanced > Virtual memory)."
            : "Windows needs it for memory and crash reports. It's fine as it is; PC Tidy never turns it off.";
        return new ScanGroup
        {
            Id = PagefileGroupId,
            Title = "Pagefile",
            Description = "Report only.",
            Badge = Badge.Safe,
            Items = new[]
            {
                Report("Pagefile (pagefile.sys + swapfile.sys)", Path.Combine(systemDrive, "pagefile.sys"), reason, page.Size + swap.Size, state),
            },
        };
    }

    private ScanGroup RestoreStorage(ISystemState system, CancellationToken ct)
    {
        var bytes = system.RestoreStorageBytes(ct);
        return new ScanGroup
        {
            Id = RestoreGroupId,
            Title = "System Restore storage",
            Description = "Report only.",
            Badge = Badge.Safe,
            Items = new[]
            {
                Report("Restore point storage", null,
                    "Room Windows keeps for restore points. PC Tidy makes one before system changes, so it never turns this off or shrinks it below a safe size.",
                    bytes ?? 0, bytes is null ? "Measured with admin" : Sizes.Format(bytes.Value), needsAdmin: bytes is null),
            },
        };
    }

    private ScanGroup SearchIndex(ScanContext ctx, CancellationToken ct)
    {
        var folder = Path.Combine(ctx.Locations.ProgramData, "Microsoft", "Search", "Data");
        long? bytes = null;
        if (Directory.Exists(folder))
        {
            var stats = ctx.Walker.Walk(folder, new WalkOptions { IncludeSystem = true }, _ => true, ct);
            if (stats.Inaccessible == 0) bytes = stats.Bytes;
        }
        var large = bytes > LargeSearchIndexBytes;
        var reason = large
            ? "Windows Search's index is large. To shrink it, open Indexing Options > Advanced > Rebuild. Never delete the file itself."
            : "Lets Windows Search find files fast. Its size looks normal.";
        return new ScanGroup
        {
            Id = SearchIndexGroupId,
            Title = "Search index",
            Description = "Report only.",
            Badge = Badge.Safe,
            Items = new[]
            {
                Report("Windows Search index", folder, reason, bytes ?? 0,
                    bytes is null ? "Measured with admin" : Sizes.Format(bytes.Value), needsAdmin: bytes is null),
            },
        };
    }

    private ScanGroup ReservedAndCompact(ISystemState system)
    {
        static string OnOff(bool? v) => v switch { true => "On", false => "Off", null => "Unknown" };
        return new ScanGroup
        {
            Id = ReservedGroupId,
            Title = "Reserved storage and CompactOS",
            Description = "Report only.",
            Badge = Badge.Safe,
            Items = new[]
            {
                Report("Reserved storage", null,
                    "Space (about 7 GB) Windows keeps so updates still install when your disk is full. Best left on.",
                    0, OnOff(system.ReservedStorageOn())),
                Report("CompactOS", null,
                    "Compresses Windows' own files. Only helps on very small drives, so it's usually off.",
                    0, OnOff(system.CompactOsOn())),
            },
        };
    }

    private static ScanItem Report(string title, string? detail, string reason, long bytes, string state, bool needsAdmin = false) => new()
    {
        Title = title,
        Detail = detail,
        Reason = reason,
        Badge = Badge.Safe,
        DefaultSelected = false,
        SizeBytes = bytes,
        NeedsAdmin = needsAdmin,
        CurrentState = state,
    };

    /// <summary>"?:\pagefile.sys" (all drives automatic) or "C:\pagefile.sys 0 0" (this drive automatic).</summary>
    private static bool IsSystemManaged(IReadOnlyList<string> lines)
        => lines.Where(l => !string.IsNullOrWhiteSpace(l)).All(l =>
        {
            var parts = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts[0].StartsWith("?:", StringComparison.Ordinal) || parts.Length == 1 || (parts.Length >= 3 && parts[1] == "0" && parts[2] == "0");
        });

    /// <summary>hiberfil.sys / pagefile.sys / swapfile.sys sizes, read from the drive's directory listing (never opened).</summary>
    private static IReadOnlyDictionary<string, FileEntry> DriveRootFiles(ScanContext ctx, CancellationToken ct)
    {
        var files = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        ctx.Walker.Walk(ctx.Locations.SystemDrive, new WalkOptions { Recurse = false, IncludeSystem = true, IncludeHidden = true }, e =>
        {
            if (!e.IsDirectory) files[e.Name] = e;
            return false;
        }, ct);
        return files;
    }
}
