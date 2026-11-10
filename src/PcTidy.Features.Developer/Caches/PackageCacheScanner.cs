using System.Diagnostics;
using PcTidy.Catalog.Developer;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Developer.Common;

namespace PcTidy.Features.Developer.Caches;

/// <summary>
/// "Package caches": npm, pnpm, Yarn, pip, NuGet and Gradle download caches, each cleaned only by the tool's OWN
/// command (<see cref="ToolIds.DevCacheClean"/>), only when the tool exists on this PC. Sizes are read, never guessed.
/// These runs can't be undone (the tool deletes), so they're never ticked for you. NuGet's global packages folder
/// (every .NET project's packages) is only offered in Expert mode, as a quarantine move, with Visual Studio closed.
/// </summary>
public sealed class PackageCacheScanner : IFeatureScanner
{
    public const string CachesGroupId = "dev-caches";
    public const string NuGetPackagesGroupId = "dev-nuget-packages";

    public string FeatureId => DevContext.FeatureId;
    public string Title => "Package caches";
    public Release Release => Release.V2;
    public bool IncludedInOptimizeAll => false;

    public Task<ScanResult> ScanAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
        => Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            var dev = DevContext.From(ctx);
            var items = new List<ScanItem>();
            for (int i = 0; i < DevCacheTools.All.Count; i++)
            {
                var tool = DevCacheTools.All[i];
                progress?.Report(new ScanProgress($"Measuring the {tool.Name} cache…", (double)i / (DevCacheTools.All.Count + 1)));
                if (ToolItem(dev, CacheToolDetector.Find(dev, tool, ct)) is { } item) items.Add(item);
            }
            progress?.Report(new ScanProgress("Measuring NuGet's packages folder…", 0.9));
            var nuget = NuGetPackagesItem(dev, ct);

            var groups = new List<ScanGroup>();
            if (items.Count > 0)
                groups.Add(new ScanGroup
                {
                    Id = CachesGroupId,
                    Title = "Package caches",
                    Description = "Copies of packages your tools downloaded once. Each is cleaned by the tool's own command, which can't be undone: what's needed is downloaded again.",
                    Badge = Badge.Permanent,
                    Items = items.OrderByDescending(x => x.SizeBytes).ToList(),
                });
            if (nuget is not null)
                groups.Add(new ScanGroup
                {
                    Id = NuGetPackagesGroupId,
                    Title = "NuGet packages (every .NET project)",
                    Description = "The packages all your .NET projects build from. Removing them makes every project download them again.",
                    Badge = Badge.Check,
                    Items = new[] { nuget },
                });
            var notes = groups.Count == 0
                ? new[] { "No package managers (npm, pnpm, Yarn, pip, NuGet, Gradle) or their caches were found on this PC." }
                : Array.Empty<string>();
            progress?.Report(new ScanProgress("Done", 1));
            return new ScanResult { FeatureId = FeatureId, Title = Title, Depth = ctx.Depth, Duration = sw.Elapsed, Groups = groups, Notes = notes };
        }, ct);

    internal static ScanItem? ToolItem(DevContext dev, CacheFinding f)
    {
        if (f.Folders.Count == 0) return null; // no cache on this PC: nothing to show
        var tool = f.Tool;
        var bytes = f.Bytes;
        var freed = tool.FreesEverything ? bytes : 0;
        var reason = $"{tool.Redownload} PC Tidy asks {tool.Name} to do it with its own command ('{tool.Command}'); this can't be undone.";
        string? disabled = null;
        IReadOnlyList<PlanItem> actions = Array.Empty<PlanItem>();
        if (f.Executable is null)
            disabled = $"{tool.Name} itself wasn't found on this PC, and PC Tidy only cleans this cache with {tool.Name}'s own command.";
        else
        {
            var action = Plans.Tool(ToolIds.DevCacheClean, $"Clean the {tool.Name} cache ('{tool.Command}')",
                new Dictionary<string, string> { ["tool"] = tool.Id }, freed, tool.CloseApps);
            var (allowed, refusal) = Plans.Vet(dev.Safety, new[] { action });
            actions = allowed;
            if (allowed.Count == 0) disabled = refusal?.Reason;
        }
        return new ScanItem
        {
            Id = "dev-cache-" + tool.Id,
            Title = $"{tool.Name} cache",
            Detail = string.Join("; ", f.Folders.Select(x => x.Path)),
            Reason = reason,
            Badge = Badge.Permanent,
            DefaultSelected = false,
            SizeBytes = disabled is null ? freed : 0,
            FileCount = f.Folders.Sum(x => x.Size.Files),
            CurrentState = tool.FreesEverything ? Sizes.Format(bytes) : $"{Sizes.Format(bytes)} (the tool removes only what's unused)",
            RequiresClosedApps = tool.CloseApps.Count > 0 ? tool.CloseApps : null,
            OwnerApp = tool.Name,
            Details = new Dictionary<string, string>
            {
                ["Command"] = tool.Command,
                ["Cache size"] = Sizes.Format(bytes),
                ["Program"] = f.Executable ?? "not found",
            },
            Actions = actions,
            DisabledReason = disabled,
        };
    }

    internal static ScanItem? NuGetPackagesItem(DevContext dev, CancellationToken ct)
    {
        var folder = CacheToolDetector.Resolve(dev, DevCacheTools.NuGetGlobalPackages).FirstOrDefault();
        if (folder is null || !FolderMeasure.DirectoryExists(folder)) return null;
        var size = FolderMeasure.Measure(dev.Walker, folder, ct);
        var self = FolderMeasure.Self(dev.Walker, folder, ct);
        const string reason = "Every .NET project on this PC builds from these packages. Removing them makes each project download all of its packages again on the next build. It goes to quarantine, so it can be put back.";
        string? disabled = null;
        IReadOnlyList<PlanItem> actions = Array.Empty<PlanItem>();
        if (!dev.Scan.Settings.ExpertMode)
            disabled = "Only offered in Expert mode (Settings), because every .NET project has to download its packages again.";
        else if (self is null || self.Value.IsReparsePoint)
            disabled = "This folder is a link to somewhere else, so PC Tidy leaves it alone.";
        else
        {
            var action = Plans.Quarantine(folder, size.Bytes, self.Value.LastWriteUtc,
                "Move NuGet's global packages folder to quarantine", DevCacheTools.NuGetGlobalPackagesCloseApps);
            var (allowed, refusal) = Plans.Vet(dev.Safety, new[] { action });
            actions = allowed;
            if (allowed.Count == 0) disabled = refusal?.Reason;
        }
        return new ScanItem
        {
            Id = "dev-nuget-global-packages",
            Title = "NuGet global packages",
            Detail = folder,
            Reason = reason,
            Badge = Badge.Check,
            DefaultSelected = false,
            SizeBytes = disabled is null ? size.Bytes : 0,
            FileCount = size.Files,
            CurrentState = Sizes.Format(size.Bytes),
            RequiresClosedApps = DevCacheTools.NuGetGlobalPackagesCloseApps,
            OwnerApp = "NuGet",
            Actions = actions,
            DisabledReason = disabled,
        };
    }
}
