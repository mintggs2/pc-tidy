using System.Diagnostics;
using PcTidy.Catalog.Developer;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Developer.Common;

namespace PcTidy.Features.Developer.Projects;

/// <summary>
/// "Build folders": node_modules, bin/obj, .vs, target, __pycache__, .venv and build folders, only inside real
/// projects you haven't worked on for a while (default 90 days), never in a project with open files, each moved to
/// quarantine with its exact size so it can be put back. Projects in cloud folders are only offered once you allowed
/// that folder, with a warning: removing something there removes it from the cloud too.
/// </summary>
public sealed class BuildFoldersScanner : IFeatureScanner
{
    public const string SkippedGroupId = "dev-build-left-alone";
    /// <summary>In-use checks go to Restart Manager in batches like this (it slows down on very big sessions).</summary>
    private const int InUseBatch = 40;

    public string FeatureId => DevContext.FeatureId;
    public string Title => "Build folders";
    public Release Release => Release.V2;
    public bool IncludedInOptimizeAll => false;

    public Task<ScanResult> ScanAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
        => Task.Run(async () =>
        {
            var sw = Stopwatch.StartNew();
            var dev = DevContext.From(ctx);
            progress?.Report(new ScanProgress("Looking for your projects…", 0.02));
            var found = ProjectFinder.Find(dev, progress, ct);
            var reports = InspectAll(dev, found.Projects, progress, ct);
            progress?.Report(new ScanProgress("Checking for open files…", 0.9));
            var busy = await OpenFilesAsync(dev, reports, ct).ConfigureAwait(false);
            var groups = BuildGroups(dev, reports, busy);
            var notes = new List<string>(found.Notes);
            if (found.Projects.Count == 0)
                notes.Add("No code projects were found in the folders PC Tidy looked in. You can pick other folders to look in.");
            progress?.Report(new ScanProgress("Done", 1));
            return new ScanResult
            {
                FeatureId = FeatureId, Title = Title, Depth = ctx.Depth, Duration = sw.Elapsed,
                Groups = groups, Notes = notes,
            };
        }, ct);

    internal static IReadOnlyList<ProjectReport> InspectAll(DevContext dev, IReadOnlyList<DevProject> projects, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var reports = new List<ProjectReport>(projects.Count);
        for (int i = 0; i < projects.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ScanProgress($"Looking inside {projects[i].Name}…", 0.1 + 0.8 * i / Math.Max(1, projects.Count)));
            try { reports.Add(ProjectInspector.Inspect(dev, projects[i], ct)); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* a project that vanished or can't be read is simply not offered */ }
        }
        return reports;
    }

    /// <summary>Candidate folders some app has files open in (Restart Manager, in batches). Path → apps.</summary>
    private static async Task<Dictionary<string, IReadOnlyList<string>>> OpenFilesAsync(DevContext dev, IReadOnlyList<ProjectReport> reports, CancellationToken ct)
    {
        var paths = reports.Where(r => IsIdle(dev, r)).SelectMany(r => r.Candidates).Select(c => c.Path).ToList();
        var busy = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < paths.Count; i += InUseBatch)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                foreach (var info in await dev.Scan.InUse.CheckAsync(paths.Skip(i).Take(InUseBatch).ToList(), ct).ConfigureAwait(false))
                    if (info.InUse) busy[info.Path] = info.Apps;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                // Can't tell: treat the whole batch as busy. The engine checks again anyway, but we never offer a guess.
                foreach (var p in paths.Skip(i).Take(InUseBatch)) busy[p] = Array.Empty<string>();
            }
        }
        return busy;
    }

    internal static bool IsIdle(DevContext dev, ProjectReport r)
        => !r.Truncated && r.LastActivityUtc is { } last && dev.NowUtc - last >= TimeSpan.FromDays(dev.IdleDays);

    internal static IReadOnlyList<ScanGroup> BuildGroups(DevContext dev, IReadOnlyList<ProjectReport> reports, IReadOnlyDictionary<string, IReadOnlyList<string>> busy)
    {
        var groups = new List<ScanGroup>();
        var leftAlone = new List<ScanItem>();
        foreach (var r in reports.Where(r => r.Candidates.Count > 0))
        {
            var why = LeftAloneReason(dev, r, busy);
            if (why is not null)
            {
                leftAlone.Add(new ScanItem
                {
                    Id = "dev-skip-" + Ids.Of(r.Project.Root),
                    Title = r.Project.Name,
                    Detail = r.Project.Root,
                    Reason = why,
                    DisabledReason = why,
                    Badge = Badge.Check,
                    CurrentState = Sizes.Format(r.CandidateBytes) + " in build folders",
                    LastModifiedUtc = r.LastActivityUtc,
                });
                continue;
            }
            var items = r.Candidates.OrderByDescending(c => c.Size.Bytes).Select(c => Item(dev, r, c)).ToList();
            groups.Add(new ScanGroup
            {
                Id = "dev-build-" + Ids.Of(r.Project.Root),
                Title = r.Project.Name,
                Description = $"{r.Project.Root}. Last worked on {Ages.Since(r.LastActivityUtc!.Value, dev.NowUtc)} (by {r.ActivitySource}). "
                              + "Only folders your tools rebuild are listed; your code and its history (.git) are never touched.",
                Badge = items.All(i => i.Badge == Badge.Safe) ? Badge.Safe : Badge.Check,
                Items = items,
            });
        }
        groups.Sort((a, b) => b.TotalBytes.CompareTo(a.TotalBytes));
        if (leftAlone.Count > 0)
            groups.Add(new ScanGroup
            {
                Id = SkippedGroupId,
                Title = "Left alone",
                Description = "Projects you worked on recently or that have files open right now. Their build folders aren't offered.",
                Badge = Badge.Check,
                Items = leftAlone,
            });
        return groups;
    }

    private static string? LeftAloneReason(DevContext dev, ProjectReport r, IReadOnlyDictionary<string, IReadOnlyList<string>> busy)
    {
        if (r.Truncated) return "This project is too big for PC Tidy to check every file, so it's left alone to be safe.";
        if (r.LastActivityUtc is not { } last) return "PC Tidy couldn't tell when you last worked on this project, so it's left alone.";
        if (!IsIdle(dev, r))
            return $"You worked on it {Ages.Since(last, dev.NowUtc)} (by {r.ActivitySource}). Build folders are only offered after {dev.IdleDays} days without changes.";
        var open = r.Candidates.Where(c => busy.ContainsKey(c.Path)).ToList();
        if (open.Count > 0)
        {
            var apps = open.SelectMany(c => busy[c.Path]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return apps.Count > 0
                ? $"{string.Join(", ", apps)} has files open in this project. Close it and scan again."
                : "Some files in this project are open right now. Close the app using them and scan again.";
        }
        return null;
    }

    private static ScanItem Item(DevContext dev, ProjectReport r, RebuildCandidate c)
    {
        var inCloud = SafeIsInCloud(dev.Safety, c.Path);
        var reason = c.Rule.Reason;
        if (c.Rule.Kind == RebuildKind.PythonVenv)
            reason += c.HasDependencyList
                ? " The project lists its packages, so 'pip install -r requirements.txt' (or your tool's install) rebuilds it."
                : " This project has no requirements or lock file, so you'd have to reinstall its packages by hand. Have a look first.";
        if (c.Confidence == RebuildConfidence.Likely)
            reason += " It sits next to the project file but has none of the tool's usual files inside, so have a look first.";
        if (c.Size.Links > 0)
            reason += $" It has {Ages.Plural(c.Size.Links, "link")} inside (pnpm uses them); links are moved as they are, never what they point to.";
        if (inCloud)
            reason = "This project is in a cloud-synced folder: removing this here also removes it from the cloud and your other PCs. " + reason;

        var safe = c.Confidence == RebuildConfidence.Certain && !c.Rule.AlwaysCheck && !inCloud && c.Size.Complete;
        var action = Plans.Quarantine(c.Path, c.Size.Bytes, c.FolderWriteUtc,
            $"Move {c.Rule.FolderName} of {r.Project.Name} to quarantine ({c.Rule.Label})", c.Rule.CloseApps);
        var (allowed, refusal) = Plans.Vet(dev.Safety, new[] { action });
        string? disabled = null;
        if (allowed.Count == 0)
            disabled = inCloud
                ? "It's in a cloud-synced folder, where removing it also removes it online. To clean it, first allow this folder in Settings."
                : refusal?.Reason ?? "PC Tidy's safety rules don't allow moving this.";
        else if (!c.Size.Complete)
            reason += " Part of it couldn't be read, so its size may be bigger than shown.";

        return new ScanItem
        {
            Id = "dev-folder-" + Ids.Of(c.Path),
            Title = c.Rule.Label,
            Detail = c.Path,
            Reason = reason,
            Badge = safe ? Badge.Safe : Badge.Check,
            DefaultSelected = safe && disabled is null,
            SizeBytes = disabled is null ? c.Size.Bytes : 0,
            FileCount = c.Size.Files,
            LastModifiedUtc = c.Size.NewestWriteUtc > DateTime.MinValue ? c.Size.NewestWriteUtc : c.FolderWriteUtc,
            RequiresClosedApps = c.Rule.CloseApps.Count > 0 ? c.Rule.CloseApps : null,
            OwnerApp = r.Project.Name,
            CurrentState = Sizes.Format(c.Size.Bytes),
            Details = new Dictionary<string, string>
            {
                ["Project"] = r.Project.Root,
                ["Last worked on"] = Ages.Date(r.LastActivityUtc!.Value),
                ["Files"] = c.Size.Files.ToString("N0"),
            },
            Actions = allowed,
            DisabledReason = disabled,
        };
    }

    private static bool SafeIsInCloud(ISafetyGuard safety, string path)
    {
        try { return safety.IsInCloudRoot(path); } catch (Exception) { return true; }
    }
}

/// <summary>Short stable ids from paths (row ids must not change between scans of the same folder).</summary>
internal static class Ids
{
    public static string Of(string text)
        => Convert.ToHexString(System.IO.Hashing.XxHash3.Hash(System.Text.Encoding.UTF8.GetBytes(text.ToUpperInvariant())));
}
