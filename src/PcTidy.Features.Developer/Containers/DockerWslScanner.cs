using System.Diagnostics;
using System.Text.RegularExpressions;
using PcTidy.Catalog.Developer;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Developer.Caches;
using PcTidy.Features.Developer.Common;
using PcTidy.Features.Developer.Environment;

namespace PcTidy.Features.Developer.Containers;

/// <summary>
/// "Docker &amp; WSL": Docker's unused images, stopped containers and build cache (pruned by Docker itself), its
/// volumes by name only (they hold data, never offered), and the virtual disks of WSL distributions (stop WSL, then
/// set the disk to sparse so Windows gets unused space back). Docker Desktop's own disk gets guidance only.
/// Everything here is read-only until you confirm; none of it can be undone, so nothing is ticked for you.
/// </summary>
public sealed partial class DockerWslScanner : IFeatureScanner
{
    public const string DockerGroupId = "dev-docker";
    public const string VolumesGroupId = "dev-docker-volumes";
    public const string WslGroupId = "dev-wsl";
    public const string DockerDiskGroupId = "dev-docker-disk";

    internal static readonly CacheLocation[] DockerCliFolders = { new(CacheBase.ProgramFiles, @"Docker\Docker\resources\bin") };
    /// <summary>Docker Desktop's own WSL disks (newer single disk first, then the older per-distro ones).</summary>
    private static readonly string[] DockerDiskFiles = { @"Docker\wsl\disk\docker_data.vhdx", @"Docker\wsl\data\ext4.vhdx", @"Docker\wsl\main\ext4.vhdx", @"Docker\wsl\distro\ext4.vhdx" };
    private static readonly string[] WslRunningProcesses = { "vmmemwsl", "wslhost", "wslservice" };

    [GeneratedRegex(@"^[A-Za-z0-9._\-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex DistroNamePattern();

    public string FeatureId => DevContext.FeatureId;
    public string Title => "Docker & WSL";
    public Release Release => Release.V2;
    public bool IncludedInOptimizeAll => false;

    public Task<ScanResult> ScanAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
        => Task.Run(async () =>
        {
            var sw = Stopwatch.StartNew();
            var dev = DevContext.From(ctx);
            var groups = new List<ScanGroup>();
            var notes = new List<string>();

            progress?.Report(new ScanProgress("Asking Docker what it stores…", 0.1));
            var dockerCli = CacheToolDetector.FindExecutable(dev, new[] { "docker.exe" }, DockerCliFolders);
            if (dockerCli is not null)
            {
                var usage = DockerDiskUsage.Parse(await dev.Environment.DockerDiskUsageJsonAsync(ct).ConfigureAwait(false));
                if (usage is null) notes.Add("Docker isn't running, so PC Tidy can't see its images. Start Docker Desktop and scan again.");
                else groups.AddRange(DockerGroups(dev, usage));
            }

            progress?.Report(new ScanProgress("Measuring WSL disks…", 0.6));
            var distros = SafeDistros(dev.Environment);
            if (WslGroup(dev, distros, ct) is { } wsl) groups.Add(wsl);
            if (DockerDiskGroup(dev, distros, ct) is { } disk) groups.Add(disk);

            if (dockerCli is null && distros.Count == 0 && groups.Count == 0)
                notes.Add("Neither Docker nor WSL (Linux on Windows) was found on this PC.");
            progress?.Report(new ScanProgress("Done", 1));
            return new ScanResult { FeatureId = FeatureId, Title = Title, Depth = ctx.Depth, Duration = sw.Elapsed, Groups = groups, Notes = notes };
        }, ct);

    private static IReadOnlyList<WslDistro> SafeDistros(IDevEnvironment env)
    {
        try { return env.WslDistros(); } catch (Exception) { return Array.Empty<WslDistro>(); }
    }

    internal static IEnumerable<ScanGroup> DockerGroups(DevContext dev, DockerDiskUsage usage)
    {
        var items = new List<ScanItem>();
        var dangling = usage.DanglingImages;
        if (dangling.Count > 0)
            items.Add(DockerItem(dev, "images", "Unused image leftovers",
                $"{Ages.Plural(dangling.Count, "image")} without a name, left over from older builds and pulls. No container uses them. Docker removes them itself ('docker image prune'); this can't be undone.",
                usage.DanglingImagesBytes));
        var stopped = usage.StoppedContainers;
        if (stopped.Count > 0)
            items.Add(DockerItem(dev, "containers", "Stopped containers",
                $"{Ages.Plural(stopped.Count, "stopped container")} ({string.Join(", ", stopped.Take(5).Select(c => c.Name))}{(stopped.Count > 5 ? ", …" : "")}). Files written inside them are lost; volumes are kept. Docker removes them itself ('docker container prune'); this can't be undone.",
                usage.StoppedContainersBytes));
        var cache = usage.UnusedBuildCache;
        if (cache.Count > 0)
            items.Add(DockerItem(dev, "builder", "Build cache",
                $"Layers Docker kept to make builds faster ({Ages.Plural(cache.Count, "entry", "entries")}). The next build of an image takes longer. Docker removes them itself ('docker builder prune'); this can't be undone.",
                usage.UnusedBuildCacheBytes));
        if (items.Count > 0)
            yield return new ScanGroup
            {
                Id = DockerGroupId,
                Title = "Docker clean-up",
                Description = "Things Docker can remove with its own prune commands. Images you named and running containers are kept.",
                Badge = Badge.Permanent,
                Items = items,
            };

        if (usage.Volumes.Count > 0)
            yield return new ScanGroup
            {
                Id = VolumesGroupId,
                Title = "Docker volumes (your data)",
                Description = "Volumes hold your containers' data, such as databases. They're shown so you know what uses space; PC Tidy never removes them.",
                Badge = Badge.Check,
                Items = usage.Volumes.Select(v => new ScanItem
                {
                    Id = "dev-docker-volume-" + Projects.Ids.Of(v.Name),
                    Title = v.Name,
                    Detail = v.RefCount > 0 ? $"Used by {Ages.Plural(v.RefCount, "container")}" : "Not used by any container right now",
                    Reason = "A Docker volume: data your containers keep, like a database. If you're sure you don't need it, remove it in Docker Desktop.",
                    DisabledReason = "Volumes hold data, so PC Tidy never removes them.",
                    Badge = Badge.Check,
                    CurrentState = v.Size >= 0 ? Sizes.Format(v.Size) : "size not measured",
                }).ToList(),
            };
    }

    private static ScanItem DockerItem(DevContext dev, string what, string title, string reason, long bytes)
    {
        var action = Plans.Tool(ToolIds.DockerPrune, $"Docker: remove {title.ToLowerInvariant()}", new Dictionary<string, string> { ["what"] = what }, bytes);
        var (allowed, refusal) = Plans.Vet(dev.Safety, new[] { action });
        return new ScanItem
        {
            Id = "dev-docker-" + what,
            Title = title,
            Detail = $"docker {(what == "builder" ? "builder" : what == "images" ? "image" : "container")} prune",
            Reason = reason,
            Badge = Badge.Permanent,
            DefaultSelected = false,
            SizeBytes = allowed.Count > 0 ? bytes : 0,
            CurrentState = Sizes.Format(bytes),
            OwnerApp = "Docker",
            Actions = allowed,
            DisabledReason = allowed.Count == 0 ? refusal?.Reason : null,
        };
    }

    /// <summary>
    /// <c>wsl --manage &lt;distro&gt; --set-sparse</c> only exists in the WSL package (2.0 and later). Windows' built-in WSL
    /// (Windows 10 22H2's in-box wsl.exe) doesn't have it: there WSL would be stopped for nothing.
    /// </summary>
    internal static readonly Version WslSparseMinimum = new(2, 0, 0);

    internal const string WslTooOld = "This WSL can't make a disk sparse yet. Update WSL first: run 'wsl --update' in a terminal (or install WSL from the Microsoft Store), then scan again.";

    private static bool WslCanSetSparse(DevContext dev)
    {
        try { return dev.Environment.WslPackageVersion() is { } v && v >= WslSparseMinimum; }
        catch (Exception) { return false; }
    }

    internal static ScanGroup? WslGroup(DevContext dev, IReadOnlyList<WslDistro> distros, CancellationToken ct)
    {
        var running = WslRunningProcesses.Any(dev.IsRunning);
        var canSetSparse = WslCanSetSparse(dev);
        var items = new List<ScanItem>();
        foreach (var d in distros.Where(d => !IsDockerDistro(d.Name)))
        {
            if (d.Version < 2) continue; // WSL 1 keeps plain files, there's no disk to shrink
            var disk = Path.Combine(d.BasePath, d.VhdFileName);
            if (DiskEntry(dev, disk, ct) is not { } entry) continue;
            var reason = "Linux (WSL) keeps this distribution's files in a virtual disk that grows as you use it but doesn't shrink on its own. "
                         + "PC Tidy stops WSL, then sets the disk to sparse so Windows gets unused space back over time. "
                         + $"Back it up first: 'wsl --export {d.Name} {d.Name}-backup.tar' in a terminal."
                         + (running ? " WSL is running right now: open Linux terminals and apps close when it stops." : " Any Linux terminals or apps that are open close when it stops.");
            string? disabled = null;
            IReadOnlyList<PlanItem> actions = Array.Empty<PlanItem>();
            if ((entry.Attributes & FileAttributes.SparseFile) != 0)
                disabled = "This disk is already set to give unused space back to Windows.";
            else if (!canSetSparse)
                disabled = WslTooOld;
            else if (!DistroNamePattern().IsMatch(d.Name))
                disabled = "This distribution's name has characters PC Tidy can't pass to WSL safely. Use 'wsl --manage <name> --set-sparse true' yourself.";
            else
            {
                var plan = new[]
                {
                    Plans.Tool(ToolIds.WslShutdown, "Stop WSL (every running Linux distribution)"),
                    Plans.Tool(ToolIds.WslSetSparse, $"Let {d.Name}'s disk give unused space back to Windows", new Dictionary<string, string> { ["distro"] = d.Name }),
                };
                var (allowed, refusal) = Plans.Vet(dev.Safety, plan);
                if (allowed.Count == plan.Length) actions = allowed;
                else disabled = refusal?.Reason ?? "PC Tidy's safety rules don't allow this.";
            }
            items.Add(new ScanItem
            {
                Id = "dev-wsl-" + Projects.Ids.Of(d.Name),
                Title = $"{d.Name} (WSL disk)",
                Detail = disk,
                Reason = reason,
                Badge = Badge.Permanent,
                DefaultSelected = false,
                CurrentState = Sizes.Format(entry.Size),
                OwnerApp = "WSL",
                LastModifiedUtc = entry.LastWriteUtc,
                Actions = actions,
                DisabledReason = disabled,
            });
        }
        return items.Count == 0 ? null : new ScanGroup
        {
            Id = WslGroupId,
            Title = "WSL disks",
            Description = "Virtual disks of your Linux distributions. Making one sparse stops WSL first and can't be undone, but your Linux files stay as they are.",
            Badge = Badge.Permanent,
            Items = items,
        };
    }

    internal static ScanGroup? DockerDiskGroup(DevContext dev, IReadOnlyList<WslDistro> distros, CancellationToken ct)
    {
        var disks = DockerDiskFiles.Select(f => Path.Combine(dev.Locations.LocalAppData, f))
            .Concat(distros.Where(d => IsDockerDistro(d.Name)).Select(d => Path.Combine(d.BasePath, d.VhdFileName)))
            .Select(PathUtil.Normalize).Distinct(StringComparer.OrdinalIgnoreCase);
        var items = new List<ScanItem>();
        foreach (var disk in disks)
        {
            if (DiskEntry(dev, disk, ct) is not { } entry) continue;
            items.Add(new ScanItem
            {
                Id = "dev-docker-disk-" + Projects.Ids.Of(disk),
                Title = "Docker Desktop's disk",
                Detail = disk,
                Reason = "Docker Desktop keeps images, containers and volumes in this virtual disk. To make it smaller, remove what you don't need above, "
                         + "then use Docker Desktop's own settings (Resources → disk size, or Troubleshoot → Clean / Purge data).",
                DisabledReason = "Docker Desktop manages this disk, so it's shrunk from Docker Desktop itself.",
                Badge = Badge.Check,
                CurrentState = Sizes.Format(entry.Size),
                OwnerApp = "Docker Desktop",
                LastModifiedUtc = entry.LastWriteUtc,
            });
        }
        return items.Count == 0 ? null : new ScanGroup
        {
            Id = DockerDiskGroupId,
            Title = "Docker Desktop's disk",
            Description = "Shown so you know how big it is. Docker Desktop shrinks it with its own tools.",
            Badge = Badge.Check,
            Items = items,
        };
    }

    private static bool IsDockerDistro(string name) => name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase);

    /// <summary>The disk file's listing entry (size, attributes), read from its folder without opening it.</summary>
    private static FileEntry? DiskEntry(DevContext dev, string path, CancellationToken ct)
    {
        var folder = Path.GetDirectoryName(path);
        if (folder is null || !FolderMeasure.DirectoryExists(folder)) return null;
        var name = Path.GetFileName(path);
        foreach (var e in FolderMeasure.Children(dev.Walker, folder, ct))
            if (!e.IsDirectory && e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return e;
        return null;
    }
}
