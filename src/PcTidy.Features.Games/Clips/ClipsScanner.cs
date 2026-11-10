using System.Diagnostics;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Games.Common;

namespace PcTidy.Features.Games.Clips;

/// <summary>
/// Clips &amp; screenshots tab: exact duplicates (Safe, ticked: the identical copy stays), near-duplicates and old clips
/// (Check, never ticked), plus where each capture app keeps them. Removals are quarantine moves, undoable for the
/// quarantine period. Every row carries <see cref="ScanItem.PreviewPath"/> = the clip itself; the page shows its shell
/// thumbnail (cache only for online-only files, so nothing is ever downloaded).
/// </summary>
public sealed class ClipsScanner : IFeatureScanner
{
    public string FeatureId => GamesContext.FeatureId;
    public string Title => "Clips & screenshots";
    public Release Release => Release.V2;
    public bool IncludedInOptimizeAll => false;

    public Task<ScanResult> ScanAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
        => Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            var result = Scan(GamesContext.From(ctx), progress, ct);
            return result with { Duration = sw.Elapsed, Depth = ctx.Depth };
        }, ct);

    internal static ScanResult Scan(GamesContext g, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var sources = ClipSources.Detect(g);
        var notes = new List<string>();
        var groups = new List<ScanGroup>();
        if (sources.Count == 0)
        {
            notes.Add("No capture folders were found (Xbox Game Bar, NVIDIA, OBS, Medal, Steam, Snipping Tool, SteelSeries). Nothing to tidy here.");
            return Result(groups, notes);
        }

        progress?.Report(new ScanProgress("Looking in your capture folders…", 0.05));
        var perSource = new List<(ClipSource Source, List<ClipFile> Files)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        foreach (var source in sources)
        {
            var files = Collect(g, source, seen, ct);
            bytes += files.Sum(f => f.Size);
            perSource.Add((source, files));
            progress?.Report(new ScanProgress($"{source.Name}: {files.Count} files", 0.2, seen.Count, bytes));
        }

        var ours = perSource.Where(p => !p.Source.ManagedByLauncher).SelectMany(p => p.Files).ToList();
        progress?.Report(new ScanProgress("Looking for exact copies…", 0.4, seen.Count, bytes));
        var exact = ClipAnalyzer.ExactDuplicates(ours, ct);
        var exactPaths = exact.Select(d => d.Copy.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

        progress?.Report(new ScanProgress("Comparing clips of the same length…", 0.6, seen.Count, bytes));
        var near = ClipAnalyzer.NearDuplicates(ours.Where(f => !exactPaths.Contains(f.Path)), g.Media, ct);
        var flagged = new HashSet<string>(exactPaths.Concat(near.Select(n => n.Copy.Path)), StringComparer.OrdinalIgnoreCase);
        // The copies that stay are never offered as "old": "one copy always stays" must hold whatever the user ticks.
        var keepers = new HashSet<string>(exact.Concat(near).Select(d => d.Keeper.Path), StringComparer.OrdinalIgnoreCase);

        var cutoff = g.NowUtc.AddDays(-g.OldClipDays);
        var old = ours.Where(f => f.LastTouchedUtc < cutoff && !flagged.Contains(f.Path) && !keepers.Contains(f.Path))
            .OrderBy(f => f.LastTouchedUtc).ToList();

        int refused = 0;
        string? firstRefusal = null;
        ScanItem Track(ScanItem item)
        {
            if (item.DisabledReason is not null) { refused++; firstRefusal ??= item.DisabledReason; }
            return item;
        }

        if (exact.Count > 0)
            groups.Add(new ScanGroup
            {
                Id = "clip-duplicates",
                Title = "Exact copies",
                Description = "Byte-for-byte identical copies of another clip or screenshot. One copy always stays.",
                Badge = Badge.Safe,
                Items = exact.Select(d => Track(DuplicateItem(g, d))).ToList(),
            });
        if (near.Count > 0)
            groups.Add(new ScanGroup
            {
                Id = "clip-near-duplicates",
                Title = "Look like the same moment",
                Description = "Same length (within a second), same resolution and matching preview frames. The bigger, better-quality copy stays. Check the previews before you pick.",
                Badge = Badge.Check,
                Items = near.Select(d => Track(NearItem(g, d))).ToList(),
            });
        if (old.Count > 0)
            groups.Add(new ScanGroup
            {
                Id = "clip-old",
                Title = $"Older than {g.OldClipDays} days",
                Description = "Clips and screenshots you haven't touched in a while. Your call: they go to quarantine, so you can put them back.",
                Badge = Badge.Check,
                Items = old.Select(f => Track(OldItem(g, f))).ToList(),
            });

        var where = perSource.Where(p => p.Files.Count > 0).Select(p => SourceItem(p.Source, p.Files)).ToList();
        if (where.Count > 0)
            groups.Add(new ScanGroup
            {
                Id = "clip-sources",
                Title = "Where your clips are",
                Description = "Each capture app's folder with its size. Shown so you know where the space goes.",
                Badge = Badge.Safe,
                Items = where,
            });
        else if (groups.Count == 0)
            notes.Add("Your capture folders are empty. Nothing to tidy here.");

        if (refused > 0)
            notes.Add($"{refused} of these can't be picked yet: {firstRefusal}");
        progress?.Report(new ScanProgress("Done", 1, seen.Count, bytes));
        return Result(groups, notes);
    }

    private static ScanResult Result(List<ScanGroup> groups, List<string> notes)
        => new() { FeatureId = GamesContext.FeatureId, Title = "Clips & screenshots", Groups = groups, Notes = notes };

    private static List<ClipFile> Collect(GamesContext g, ClipSource source, HashSet<string> seen, CancellationToken ct)
    {
        var files = new List<ClipFile>();
        try
        {
            g.Scan.Walker.Walk(source.Folder, new WalkOptions { Recurse = source.Recursive, IncludeHidden = false, IncludeSystem = false }, e =>
            {
                if (e.IsDirectory)
                    // Steam keeps its own small copies in "thumbnails"; never walk through links.
                    return !e.IsReparsePoint && !e.Name.Equals("thumbnails", StringComparison.OrdinalIgnoreCase);
                if (e.IsReparsePoint || !ClipSources.IsMedia(e.Name)) return true;
                if (source.NameFilter is { } filter && !filter.IsMatch(e.Name)) return true;
                if (!seen.Add(e.FullPath)) return true;
                files.Add(new ClipFile(e.FullPath, e.Size, e.CreationUtc, e.LastWriteUtc, e.IsCloudPlaceholder, source));
                return true;
            }, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { /* folder vanished or unreadable: whatever was listed */ }
        return files;
    }

    // ---------------- rows ----------------

    /// <summary>Pre-ticked only because the engine re-checks both copies right before the move (see <see cref="Plans.VerifyAgainst"/>).</summary>
    private static ScanItem DuplicateItem(GamesContext g, DuplicateMatch d)
        => Row(g, d.Copy, Badge.Safe, defaultSelected: d.ContentHash is not null,
            reason: $"An exact copy of \"{d.Keeper.Name}\" (same bytes). That one stays, this copy goes to quarantine. Both are checked again just before.",
            extra: new() { ["Copy of"] = d.Keeper.Path },
            idPrefix: "dup",
            verify: d.ContentHash is { } hash ? Plans.VerifyAgainst(d.Keeper.Path, hash) : null);

    private static ScanItem NearItem(GamesContext g, DuplicateMatch d)
    {
        var details = new Dictionary<string, string> { ["Looks like"] = d.Keeper.Path };
        if (d.Media is { } m)
        {
            details["Length"] = $"{m.Duration:mm\\:ss}";
            details["Resolution"] = $"{m.Width} × {m.Height}";
        }
        return Row(g, d.Copy, Badge.Check, defaultSelected: false,
            reason: $"Looks like the same moment as \"{d.Keeper.Name}\" ({Sizes.Format(d.Keeper.Size)}), which is the better copy and stays. Check both previews first.",
            extra: details, idPrefix: "near");
    }

    private static ScanItem OldItem(GamesContext g, ClipFile f)
        => Row(g, f, Badge.Check, defaultSelected: false,
            reason: $"Last changed {Ages.Since(f.LastTouchedUtc, g.NowUtc)}. Your call: keep the ones you love.",
            extra: new(), idPrefix: "old");

    private static ScanItem Row(GamesContext g, ClipFile f, Badge badge, bool defaultSelected, string reason, Dictionary<string, string> extra, string idPrefix,
        Dictionary<string, string>? verify = null)
    {
        var action = Plans.Quarantine(f.Path, isDirectory: false, f.Size, f.LastWriteUtc, $"Move \"{f.Name}\" to quarantine", args: verify);
        var (allowed, refusal) = Plans.Vet(g.Safety, new[] { action });
        extra["App"] = f.Source.Name;
        extra["Folder"] = Path.GetDirectoryName(f.Path) ?? f.Source.Folder;
        if (f.IsPlaceholder) extra["OneDrive"] = "Online-only: PC Tidy shows Windows' cached preview and never downloads it.";
        return new ScanItem
        {
            Id = $"games.clip.{idPrefix}.{f.Path.ToLowerInvariant()}",
            Title = f.Name,
            Detail = f.Path,
            Reason = reason,
            Badge = badge,
            // A row that can't be picked is never ticked, whatever its badge.
            DefaultSelected = defaultSelected && badge == Badge.Safe && refusal is null,
            SizeBytes = f.Size,
            FileCount = 1,
            LastModifiedUtc = f.LastTouchedUtc,
            PreviewPath = f.Path,
            OwnerApp = f.Source.Name,
            Details = extra,
            Actions = allowed,
            DisabledReason = refusal,
        };
    }

    private static ScanItem SourceItem(ClipSource source, List<ClipFile> files)
    {
        var size = files.Sum(f => f.Size);
        var reason = source.ManagedByLauncher
            ? $"{files.Count:N0} files. Steam keeps these inside its own folder, which PC Tidy never changes: manage them in Steam (View → Screenshots, or Settings → Game Recording)."
            : $"{files.Count:N0} clips and screenshots, {Sizes.Format(size)}.";
        return new ScanItem
        {
            Id = $"games.clip.source.{source.Folder.ToLowerInvariant()}",
            Title = source.Name,
            Detail = source.Folder,
            Reason = reason,
            Badge = Badge.Safe,
            SizeBytes = size,
            FileCount = files.Count,
            OwnerApp = source.Name,
            PreviewPath = files.OrderByDescending(f => f.LastWriteUtc).FirstOrDefault()?.Path,
        };
    }
}
