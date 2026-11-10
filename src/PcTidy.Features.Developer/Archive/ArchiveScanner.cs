using System.Diagnostics;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Developer.Common;
using PcTidy.Features.Developer.Projects;

namespace PcTidy.Features.Developer.Archive;

/// <summary>
/// "Archive old projects": projects untouched for X months (default 12) are offered to be zipped into the archive folder.
/// The engine zips, opens the zip and checks every file, and only then moves the original to quarantine (restorable).
/// "Archived projects" lists the zips PC Tidy made there; one click unzips one back to exactly where it was.
/// </summary>
public sealed class ArchiveScanner : IFeatureScanner
{
    public const string OldGroupId = "dev-archive-old";
    public const string ArchivedGroupId = "dev-archive-done";

    public string FeatureId => DevContext.FeatureId;
    public string Title => "Archive old projects";
    public Release Release => Release.V2;
    public bool IncludedInOptimizeAll => false;

    public Task<ScanResult> ScanAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
        => Task.Run(async () =>
        {
            var sw = Stopwatch.StartNew();
            var dev = DevContext.From(ctx);
            progress?.Report(new ScanProgress("Looking for your projects…", 0.02));
            var found = ProjectFinder.Find(dev, progress, ct);
            var old = BuildFoldersScanner.InspectAll(dev, found.Projects, progress, ct)
                .Where(r => IsOld(dev, r) && !IsArchiveFolderInside(dev, r.Project.Root))
                .ToList();
            progress?.Report(new ScanProgress("Checking for open files…", 0.85));
            var busy = await OpenProjectsAsync(dev, old, ct).ConfigureAwait(false);

            var groups = new List<ScanGroup>();
            var oldItems = old.Select(r => OldProjectItem(dev, r, busy, ct)).OrderByDescending(i => i.SizeBytes).ToList();
            if (oldItems.Count > 0)
                groups.Add(new ScanGroup
                {
                    Id = OldGroupId,
                    Title = $"Untouched for {dev.ArchiveMonths}+ months",
                    Description = $"Zipped into {dev.ArchiveFolder}. Every file in the zip is checked before the original goes to quarantine, so it can be put back.",
                    Badge = Badge.Check,
                    Items = oldItems,
                });
            progress?.Report(new ScanProgress("Reading your archive folder…", 0.95));
            var archived = ArchivedProjects.List(dev, dev.ArchiveFolder, ct).Select(a => ArchivedItem(dev, a)).ToList();
            if (archived.Count > 0)
                groups.Add(new ScanGroup
                {
                    Id = ArchivedGroupId,
                    Title = "Archived projects",
                    Description = "Zips PC Tidy made in your archive folder. Restoring unzips one back to where it was; the zip is kept.",
                    Badge = Badge.Safe,
                    Items = archived,
                });

            var notes = new List<string>(found.Notes);
            if (oldItems.Count == 0)
                notes.Add($"No project has gone {dev.ArchiveMonths} months without changes{(found.Projects.Count == 0 ? " (no code projects were found where PC Tidy looked)" : "")}.");
            progress?.Report(new ScanProgress("Done", 1));
            return new ScanResult { FeatureId = FeatureId, Title = Title, Depth = ctx.Depth, Duration = sw.Elapsed, Groups = groups, Notes = notes };
        }, ct);

    internal static bool IsOld(DevContext dev, ProjectReport r)
        => !r.Truncated && r.LastActivityUtc is { } last && Ages.MonthsBetween(last, dev.NowUtc) >= dev.ArchiveMonths;

    /// <summary>The archive folder is this project or inside it: zipping it there would zip into itself.</summary>
    private static bool IsArchiveFolderInside(DevContext dev, string projectRoot) => PathUtil.IsUnder(dev.ArchiveFolder, projectRoot);

    private static async Task<Dictionary<string, IReadOnlyList<string>>> OpenProjectsAsync(DevContext dev, IReadOnlyList<ProjectReport> old, CancellationToken ct)
    {
        var busy = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (old.Count == 0) return busy;
        var paths = old.Select(r => r.Project.Root).ToList();
        try
        {
            foreach (var info in await dev.Scan.InUse.CheckAsync(paths, ct).ConfigureAwait(false))
                if (info.InUse) busy[info.Path] = info.Apps;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { foreach (var p in paths) busy[p] = Array.Empty<string>(); }
        return busy;
    }

    internal static ScanItem OldProjectItem(DevContext dev, ProjectReport r, IReadOnlyDictionary<string, IReadOnlyList<string>> busy, CancellationToken ct)
    {
        var p = r.Project;
        var size = FolderMeasure.Measure(dev.Walker, p.Root, ct);
        var last = r.LastActivityUtc!.Value;
        var zip = ArchiveNaming.ZipPath(dev.ArchiveFolder, p.Name, dev.NowUtc.ToLocalTime());
        var reason = $"You last worked on it {Ages.Since(last, dev.NowUtc)} (by {r.ActivitySource}). It's zipped into your archive folder, every file in the zip is checked, "
                     + "and only then the original goes to quarantine. You can restore it from 'Archived projects' with one click.";
        if (r.CandidateBytes > 0)
            reason += $" Tip: {Sizes.Format(r.CandidateBytes)} of it is build folders; cleaning those first makes the zip much smaller.";

        string? disabled = null;
        IReadOnlyList<PlanItem> actions = Array.Empty<PlanItem>();
        if (busy.TryGetValue(p.Root, out var apps))
            disabled = apps.Count > 0 ? $"{string.Join(", ", apps)} has files open in this project. Close it and scan again." : "Some files in this project are open right now. Close the app using them and scan again.";
        else if (!size.Complete)
            disabled = "Part of this project couldn't be read, so it can't be zipped completely.";
        else if (size.Links > 0)
            disabled = "This project has links (junctions or symlinks) inside, which a zip can't keep. Archive it by hand if you want to.";
        else
        {
            var manifest = new ArchiveManifest
            {
                OriginalPath = p.Root, FolderName = p.Name, ArchivedUtc = dev.NowUtc, LastActivityUtc = last,
                SizeBytes = size.Bytes, FileCount = size.Files,
            };
            var action = new PlanItem
            {
                Kind = ActionKind.ArchiveToZip,
                FeatureId = DevContext.FeatureId,
                Description = $"Zip {p.Name} into {Path.GetFileName(zip)}, check it, then move the original to quarantine",
                SourcePath = p.Root,
                DestinationPath = zip,
                IsDirectory = true,
                ExpectedSize = size.Bytes,
                ExpectedLastWriteUtc = p.FolderWriteUtc > DateTime.MinValue ? p.FolderWriteUtc : null,
                Args = new Dictionary<string, string> { [ArchiveNaming.ManifestArg] = manifest.ToJson() },
            };
            var (allowed, refusal) = Plans.Vet(dev.Safety, new[] { action });
            actions = allowed;
            if (allowed.Count == 0) disabled = refusal?.Reason;
        }

        return new ScanItem
        {
            Id = "dev-archive-" + Ids.Of(p.Root),
            Title = p.Name,
            Detail = p.Root,
            Reason = reason,
            Badge = Badge.Check,
            DefaultSelected = false,
            SizeBytes = disabled is null ? size.Bytes : 0,
            FileCount = size.Files,
            LastModifiedUtc = last,
            CurrentState = Sizes.Format(size.Bytes),
            Details = new Dictionary<string, string>
            {
                ["Last worked on"] = Ages.Date(last),
                ["Zip"] = zip,
                ["Files"] = size.Files.ToString("N0"),
            },
            Actions = actions,
            DisabledReason = disabled,
        };
    }

    internal static ScanItem ArchivedItem(DevContext dev, ArchivedProject a)
    {
        var m = a.Manifest;
        string? disabled = a.Problem;
        IReadOnlyList<PlanItem> actions = Array.Empty<PlanItem>();
        if (disabled is null && m is not null)
        {
            if (FolderMeasure.DirectoryExists(m.OriginalPath) || FolderMeasure.FileExists(m.OriginalPath))
                disabled = $"Something is already at {m.OriginalPath}. Move or rename it first, so the project can go back exactly where it was.";
            else
            {
                var action = new PlanItem
                {
                    Kind = ActionKind.ExtractZip,
                    FeatureId = DevContext.FeatureId,
                    Description = $"Unzip {m.FolderName} back to {m.OriginalPath} (the zip is kept)",
                    SourcePath = a.ZipPath,
                    DestinationPath = m.OriginalPath,
                    IsDirectory = false,
                    ExpectedSize = a.ZipBytes,
                    ExpectedLastWriteUtc = a.ZipWriteUtc,
                    Args = new Dictionary<string, string> { [ArchiveNaming.UnwrapRootArg] = m.FolderName },
                };
                var (allowed, refusal) = Plans.Vet(dev.Safety, new[] { action });
                actions = allowed;
                if (allowed.Count == 0) disabled = refusal?.Reason;
            }
        }
        return new ScanItem
        {
            Id = "dev-archived-" + Ids.Of(a.ZipPath),
            Title = a.Name,
            Detail = a.ZipPath,
            Reason = m is null
                ? $"Archived on {a.ArchivedDate:d MMM yyyy}."
                : $"Archived on {Ages.Date(m.ArchivedUtc)} from {m.OriginalPath}. Restoring unzips it back there; the zip stays in your archive folder.",
            Badge = Badge.Safe,
            DefaultSelected = false,
            CurrentState = Sizes.Format(a.ZipBytes) + " zip",
            FileCount = m?.FileCount ?? 0,
            LastModifiedUtc = a.ZipWriteUtc,
            Actions = actions,
            DisabledReason = disabled,
        };
    }
}
