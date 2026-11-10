using PcTidy.Catalog.Insights;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Insights.Model;
using PcTidy.Features.Insights.Planning;

namespace PcTidy.Features.Insights.Cards;

/// <summary>Storage cards: OneDrive "Free up space", compressing old folders, and the Dev Drive advisor.</summary>
internal static class StorageCards
{
    private const FileAttributes Pinned = (FileAttributes)0x00080000;   // "Always keep on this device"
    private const FileAttributes Unpinned = (FileAttributes)0x00100000; // already set to free up space

    /// <summary>Thresholds (shared with the vault page so the numbers shown match what is offered).</summary>
    public const long FreeUpMinBytes = 50L << 20;
    public const int FreeUpMinAgeDays = 180;
    public const int FreeUpMaxRows = 100;
    public const long CompressMinBytes = 256L << 20;
    public const int CompressMinAgeDays = 365;
    /// <summary>Rough share of compressible data LZX saves (documents, logs, old projects). Shown as "about".</summary>
    public const double CompressRatio = 0.4;

    // ------------------------------------------------------------------ OneDrive "Free up space"

    public static InsightCard OneDriveFreeUp(CardContext c)
    {
        const string title = "OneDrive: free up space";
        if (c.Locations.CloudRoots.Count == 0)
            return InsightCard.NotAvailable(InsightsIds.OneDriveFreeUp, InsightCategory.Storage, title, "CloudArrowDown24", "OneDrive isn't set up on this PC.");

        var cutoff = c.NowUtc.AddDays(-FreeUpMinAgeDays);
        var found = new List<FileEntry>();
        int pinnedSkipped = 0;
        foreach (var root in c.Locations.CloudRoots)
        {
            if (!Directory.Exists(root)) continue;
            c.Scan.Walker.Walk(root, new WalkOptions { IncludeHidden = false }, e =>
            {
                if (e.IsDirectory) return !e.IsReparsePoint;
                if (e.IsCloudPlaceholder || (e.Attributes & Unpinned) != 0 || e.Size < FreeUpMinBytes || e.LastTouchedUtc > cutoff) return true;
                if ((e.Attributes & Pinned) != 0) { pinnedSkipped++; return true; }
                found.Add(e);
                return true;
            }, c.Token);
        }

        var top = found.OrderByDescending(f => f.Size).Take(FreeUpMaxRows).ToList();
        var items = top.Select(f => FreeUpRow(c, f)).ToList();
        long bytes = items.Where(i => i.DisabledReason is null).Sum(i => i.SizeBytes);
        int offered = items.Count(i => i.DisabledReason is null);
        var steps = new[]
        {
            "In File Explorer, open your OneDrive folder.",
            "Right-click a file or folder and choose \"Free up space\".",
            "OneDrive keeps it in the cloud; it downloads again when you open it (you need to be online for that).",
        };
        const string explanation =
            "Files you haven't opened in a long time don't need a copy on this PC as well as in OneDrive. \"Free up space\" is Windows' own way to keep them in the cloud only: nothing is deleted. "
            + "Files you set to \"Always keep on this device\" are left alone.";
        if (offered == 0)
            return new InsightCard
            {
                Id = InsightsIds.OneDriveFreeUp, Category = InsightCategory.Storage, Title = title, Icon = "CloudArrowDown24", Status = InsightStatus.Good,
                Headline = "No big, old OneDrive files are stored on this PC", Explanation = explanation, WhatToDo = "Nothing to do.",
                Facts = pinnedSkipped > 0 ? new[] { new InsightFact("Kept on this device by you", Words.Plural(pinnedSkipped, "file", "files")) } : Array.Empty<InsightFact>(),
                Items = items,
            };
        return new InsightCard
        {
            Id = InsightsIds.OneDriveFreeUp, Category = InsightCategory.Storage, Title = title, Icon = "CloudArrowDown24", Status = InsightStatus.Info,
            Headline = $"{Words.Plural(offered, "file", "files")} ({Sizes.Format(bytes)}) could be online-only",
            Explanation = explanation,
            WhatToDo = "Tick the files you rarely need and confirm, or do it yourself in Explorer. Undo puts each file back the way it was (it downloads again when opened).",
            Steps = steps, Items = items,
            Facts = new[]
            {
                new InsightFact("Rule", $"Bigger than {Sizes.Format(FreeUpMinBytes)}, not opened or changed for {FreeUpMinAgeDays} days"),
                new InsightFact("Space you'd get back", Sizes.Format(bytes)),
            }.Concat(pinnedSkipped > 0 ? new[] { new InsightFact("Kept on this device by you", Words.Plural(pinnedSkipped, "file", "files")) } : Array.Empty<InsightFact>()).ToList(),
        };
    }

    private static ScanItem FreeUpRow(CardContext c, FileEntry f)
    {
        var plan = InsightPlans.FreeUpSpace(f.FullPath, f.Size, f.LastWriteUtc);
        var (allowed, refusal) = c.Vet(plan);
        return new ScanItem
        {
            Title = f.Name, Detail = f.FullPath, SizeBytes = f.Size, FileCount = 1, LastModifiedUtc = f.LastTouchedUtc,
            Reason = $"Last used {Words.Ago(f.LastTouchedUtc, c.NowUtc)}. Stays in OneDrive; downloads again when you open it.",
            Badge = Badge.Check, DefaultSelected = false, Actions = allowed,
            DisabledReason = allowed.Count == 0 ? refusal ?? "PC Tidy's safety rules don't allow this one." : null,
        };
    }

    // ------------------------------------------------------------------ compress old folders (opt-in)

    public static InsightCard CompressFolders(CardContext c)
    {
        const string title = "Compress old folders";
        var fileSystems = c.Source.ReadFileSystems();
        var roots = (c.Scan.Roots is { Count: > 0 } r ? r : new[] { c.Locations.Documents, c.Locations.Desktop, c.Locations.Downloads })
            .Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!fileSystems.Values.Any(fs => fs.Equals("NTFS", StringComparison.OrdinalIgnoreCase)))
            return InsightCard.NotAvailable(InsightsIds.CompressFolders, InsightCategory.Storage, title, "FolderZip24", "Windows' file compression needs an NTFS drive, and PC Tidy found none here.");

        var cutoff = c.NowUtc.AddDays(-CompressMinAgeDays);
        var rows = new List<ScanItem>();
        foreach (var root in roots)
        {
            IEnumerable<string> folders;
            try { folders = Directory.EnumerateDirectories(root).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var folder in folders)
            {
                c.Token.ThrowIfCancellationRequested();
                if (Candidate(c, folder, fileSystems, cutoff) is { } row) rows.Add(row);
            }
        }
        rows = rows.OrderByDescending(x => x.SizeBytes).ToList();
        long saving = rows.Where(x => x.DisabledReason is null).Sum(x => x.SizeBytes);
        const string explanation =
            "Windows can compress folders you no longer change (old documents, logs, finished projects) with its own NTFS compression (\"compact /exe:lzx\"). "
            + "Files stay where they are and open normally, just a little slower to read. It does nothing for photos, videos or zip files, which are already compressed.";
        if (rows.Count == 0)
            return new InsightCard
            {
                Id = InsightsIds.CompressFolders, Category = InsightCategory.Storage, Title = title, Icon = "FolderZip24", Status = InsightStatus.Good,
                Headline = "No old folders worth compressing", Explanation = explanation, WhatToDo = "Nothing to do.",
                Links = new[] { new InsightLink("Read about compact (Microsoft)", InsightsLinks.CompactDocs) },
            };
        return new InsightCard
        {
            Id = InsightsIds.CompressFolders, Category = InsightCategory.Storage, Title = title, Icon = "FolderZip24", Status = InsightStatus.Info,
            Headline = $"{Words.Plural(rows.Count, "old folder", "old folders")} could save about {Sizes.Format(saving)}",
            Explanation = explanation,
            WhatToDo = "Optional. Tick only folders you won't change again and confirm; nothing is ticked for you. Undo uncompresses them.",
            Items = rows, IsHeuristic = true, // the saving is an estimate
            Facts = new[]
            {
                new InsightFact("Rule", $"Not changed for {CompressMinAgeDays / 365} year or more, at least {Sizes.Format(CompressMinBytes)} of compressible files"),
                new InsightFact("Estimate", $"About {CompressRatio:P0} of the compressible files (a rough guess; the real saving shows after)"),
            },
            Links = new[] { new InsightLink("Read about compact (Microsoft)", InsightsLinks.CompactDocs) },
        };
    }

    private static ScanItem? Candidate(CardContext c, string folder, IReadOnlyDictionary<string, string> fileSystems, DateTime cutoff)
    {
        if (c.Locations.CloudRoots.Any(r => PathUtil.IsUnder(folder, r))) return null; // OneDrive files: "Free up space" is the better tool
        if (c.Scan.Safety.IsInsideProjectOrAppFolder(folder, out _)) return null;     // code, apps and games stay as they are
        if (c.Scan.Safety.ClassifyPath(folder).Protection == PathProtection.Never) return null;
        var drive = Words.DriveRootOf(folder, c.Locations.FixedDrives);
        if (drive is null || !fileSystems.TryGetValue(drive, out var fs) || !fs.Equals("NTFS", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }

        long total = 0, compressible = 0, compressed = 0;
        int files = 0;
        DateTime newest = DateTime.MinValue;
        bool hasProject = false;
        c.Scan.Walker.Walk(folder, new WalkOptions(), e =>
        {
            if (e.IsDirectory)
            {
                if (e.Name.Equals(".git", StringComparison.OrdinalIgnoreCase) || e.Name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) hasProject = true;
                return !e.IsReparsePoint;
            }
            if (e.IsCloudPlaceholder) return true;
            files++;
            total += e.Size;
            if (e.LastTouchedUtc > newest) newest = e.LastTouchedUtc;
            if ((e.Attributes & FileAttributes.Compressed) != 0) compressed += e.Size;
            else if (!InsightsData.AlreadyCompressedExtensions.Contains(Path.GetExtension(e.Name))) compressible += e.Size;
            return true;
        }, c.Token);

        if (hasProject || files == 0 || newest > cutoff || compressible < CompressMinBytes) return null;
        if (compressed * 2 > total || compressible * 2 < total) return null; // already compressed, or mostly media/archives

        long saving = (long)(compressible * CompressRatio);
        var plan = InsightPlans.CompressFolder(folder, saving);
        var (allowed, refusal) = c.Vet(plan);
        return new ScanItem
        {
            Title = Path.GetFileName(folder.TrimEnd('\\')), Detail = folder, SizeBytes = saving, FileCount = files, LastModifiedUtc = newest,
            Reason = $"Not changed since {Words.Date(newest)}. {Sizes.Format(total)} now, about {Sizes.Format(saving)} saved. Undo uncompresses it.",
            CurrentState = Sizes.Format(total),
            Badge = Badge.Check, DefaultSelected = false, Actions = allowed,
            DisabledReason = allowed.Count == 0 ? refusal ?? "PC Tidy's safety rules don't allow this folder." : null,
        };
    }

    // ------------------------------------------------------------------ Dev Drive advisor

    public static InsightCard DevDrive(CardContext c)
    {
        const string title = "Dev Drive";
        var id = c.Identity;
        int build = id.Build > 0 ? id.Build : c.WindowsBuild;
        bool supported = build > 22621 || (build == 22621 && id.Ubr >= 2338);
        if (!supported)
            return InsightCard.NotAvailable(InsightsIds.DevDrive, InsightCategory.Storage, title, "Code24",
                "Dev Drive needs Windows 11 22H2 (with updates from September 2023) or newer.");

        // What the developer has: package caches (exact folders from Microsoft's Dev Drive page) and git repositories.
        var caches = new List<(string Name, long Bytes, string Variable)>();
        foreach (var (name, relative, variable) in InsightsData.DevCaches)
        {
            var path = Path.Combine(c.Locations.UserProfile, relative);
            if (!Directory.Exists(path)) continue;
            var stats = c.Scan.Walker.Walk(path, new WalkOptions(), _ => true, c.Token);
            if (stats.Bytes > 0) caches.Add((name, stats.Bytes, variable));
        }
        var repos = FindRepositories(c);
        long cacheBytes = caches.Sum(x => x.Bytes);
        if (repos.Count == 0 && cacheBytes < (1L << 30))
            return InsightCard.NotAvailable(InsightsIds.DevDrive, InsightCategory.Storage, title, "Code24",
                "PC Tidy found no code projects or big package caches, so a Dev Drive wouldn't help here.");

        var refs = c.Source.ReadFileSystems().Where(kv => kv.Value.Equals("ReFS", StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key.TrimEnd('\\')).ToList();
        var facts = new List<InsightFact> { new("Code projects found", repos.Count.ToString(System.Globalization.CultureInfo.CurrentCulture)) };
        facts.AddRange(caches.OrderByDescending(x => x.Bytes).Select(x => new InsightFact(x.Name, $"{Sizes.Format(x.Bytes)} (move with {x.Variable})")));
        if (refs.Count > 0) facts.Add(new InsightFact("ReFS drives", string.Join(", ", refs)));

        const string explanation =
            "A Dev Drive is a separate ReFS drive for code, package caches and build output. Microsoft Defender scans it in \"performance mode\" (still on, just not in the way), "
            + "so builds and installs get faster. It needs at least 50 GB, can't be C:, and an existing drive can't be converted.";
        var steps = new[]
        {
            "Open Settings → System → Storage → Advanced storage settings → Disks & volumes.",
            "Choose \"Create dev drive\" (from free space, by shrinking a drive, or as a virtual disk) and give it 50 GB or more.",
            "Move your code folders there, and point package caches at it with the variables listed above.",
            "Keep Defender on. Never use the option that removes antivirus from a Dev Drive: Microsoft calls it a security risk.",
        };
        var links = new[]
        {
            new InsightLink("Open Disks & volumes", InsightsLinks.SettingsDisksAndVolumes),
            new InsightLink("Read about Dev Drive (Microsoft)", InsightsLinks.DevDriveDocs),
        };
        if (refs.Count > 0)
            return new InsightCard
            {
                Id = InsightsIds.DevDrive, Category = InsightCategory.Storage, Title = title, Icon = "Code24", Status = InsightStatus.Good,
                Headline = $"You already have a ReFS drive ({string.Join(", ", refs)})", Explanation = explanation,
                WhatToDo = "If it's a Dev Drive, keep code and package caches there. Check with Settings → Disks & volumes.", Facts = facts, Links = links,
            };
        return new InsightCard
        {
            Id = InsightsIds.DevDrive, Category = InsightCategory.Storage, Title = title, Icon = "Code24", Status = InsightStatus.Info,
            // "and 0 B of package caches" read like a bug: name only what was found.
            Headline = (repos.Count, cacheBytes) switch
            {
                ( > 0, > 0) => $"{Words.Plural(repos.Count, "code project", "code projects")} and {Sizes.Format(cacheBytes)} of package caches could live on a Dev Drive",
                ( > 0, _) => $"{Words.Plural(repos.Count, "code project", "code projects")} could live on a Dev Drive",
                _ => $"{Sizes.Format(cacheBytes)} of package caches could live on a Dev Drive",
            },
            Explanation = explanation,
            WhatToDo = "Optional. PC Tidy never creates or formats drives: follow the steps if you want one.",
            Facts = facts, Steps = steps, Links = links,
        };
    }

    /// <summary>Git repositories in the user's folders (up to 4 levels deep, never in AppData or inside node_modules).</summary>
    private static IReadOnlyList<string> FindRepositories(CardContext c)
    {
        var repos = new List<string>();
        var appData = Path.Combine(c.Locations.UserProfile, "AppData");
        if (!Directory.Exists(c.Locations.UserProfile)) return repos;
        c.Scan.Walker.Walk(c.Locations.UserProfile, new WalkOptions { MaxDepth = 4, IncludeHidden = true }, e =>
        {
            if (!e.IsDirectory) return true;
            if (e.IsReparsePoint || PathUtil.IsUnder(e.FullPath, appData)) return false;
            if (e.Name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) return false;
            if (e.Name.Equals(".git", StringComparison.OrdinalIgnoreCase))
            {
                repos.Add(Path.GetDirectoryName(e.FullPath)!);
                return false;
            }
            return repos.Count < 500;
        }, c.Token);
        return repos;
    }
}
