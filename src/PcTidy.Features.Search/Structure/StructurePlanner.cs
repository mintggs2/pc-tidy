using PcTidy.Core;
using PcTidy.Core.Model;

namespace PcTidy.Features.Search.Structure;

/// <summary>Where a suggested structure would live: a drive's root, or Documents on the Windows drive.</summary>
public sealed record StructureRoot(string Label, string Path, bool IsDocuments);

/// <summary>One folder of the proposed tree and whether it's there already.</summary>
public sealed record TreeFolder(string RelativePath, string FullPath, string Purpose, bool Exists);

/// <summary>What would happen to one existing top-level folder.</summary>
public sealed record FolderMove(FolderProfile Folder, string? Destination, string Reason, string? DisabledReason, bool BecomesCategory);

/// <summary>A preset applied to one root: the tree and each folder's suggested place.</summary>
public sealed record StructureProposal(StructureRoot Root, PresetDefinition Preset, IReadOnlyList<TreeFolder> Tree, IReadOnlyList<FolderMove> Moves);

/// <summary>
/// Suggests a folder structure per drive and builds the guided move as plan items (CreateDirectory + MovePath of
/// whole top-level folders). Read-only: it only lists and sizes folders. Every row is "Check" and unticked: the
/// user ticks what fits. Never touches anything inside a folder, never a protected, cloud, project/app or Windows
/// user folder, and every action is pre-checked with the safety guard (a blocked one is shown with the reason).
/// </summary>
public sealed class StructurePlanner
{
    private readonly IKnownLocations _k;
    private readonly ISafetyGuard _safety;
    private readonly IFileWalker _walker;
    private readonly TimeProvider _clock;

    public StructurePlanner(IKnownLocations locations, ISafetyGuard safety, IFileWalker walker, TimeProvider? clock = null)
    {
        _k = locations;
        _safety = safety;
        _walker = walker;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Every fixed drive: Documents on the Windows drive, the drive itself elsewhere. Missing ones are left out.</summary>
    public IReadOnlyList<StructureRoot> DefaultRoots()
    {
        var roots = new List<StructureRoot>();
        foreach (var drive in _k.FixedDrives)
        {
            bool system = Same(drive, _k.SystemDrive);
            var path = system ? _k.Documents : drive;
            if (!Directory.Exists(path)) continue;
            roots.Add(new StructureRoot(system ? $"Documents ({DriveLabel(drive)})" : DriveLabel(drive), Norm(path), system));
        }
        return roots;
    }

    public StructureProposal Propose(StructureRoot root, StructurePreset preset, CancellationToken ct)
    {
        var def = StructurePresets.Get(preset);
        var topNames = StructurePresets.TopLevelNames(preset);
        var now = _clock.GetUtcNow().UtcDateTime;
        var moves = new List<FolderMove>();

        foreach (var child in TopLevelFolders(root.Path, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (topNames.Contains(child.Name)) continue; // already part of the structure
            if (child.IsReparsePoint)
            {
                moves.Add(Kept(child.FullPath, child.Name, "It's a link to another place, so it's left alone."));
                continue;
            }
            var verdict = _safety.ClassifyPath(child.FullPath);
            if (!verdict.Allowed && verdict.Protection == PathProtection.Never) continue; // Windows, apps, games, PC Tidy's data
            if (WhyNotMovable(child.FullPath) is { } why)
            {
                moves.Add(Kept(child.FullPath, child.Name, why));
                continue;
            }
            var profile = FolderProfiler.Profile(child.FullPath, _walker, ct);
            var place = StructurePresets.Place(preset, profile, now, root.IsDocuments);
            if (place.RelativeFolder is null)
            {
                moves.Add(new FolderMove(profile, null, place.Reason, place.Reason, false));
                continue;
            }
            var category = Path.Combine(root.Path, place.RelativeFolder);
            // "D:\Photos" becomes "D:\Media\Photos" itself instead of "D:\Media\Photos\Photos" when that's free.
            bool becomes = string.Equals(Path.GetFileName(category), child.Name, StringComparison.OrdinalIgnoreCase)
                           && !Directory.Exists(category)
                           && !moves.Any(m => m.BecomesCategory && string.Equals(m.Destination, category, StringComparison.OrdinalIgnoreCase));
            var dest = becomes ? category : Path.Combine(category, child.Name);
            var free = becomes ? dest : PathUtil.NextFreeName(dest);
            var reason = place.Reason + (free != dest ? $" A folder called '{child.Name}' is already there, so it's named '{Path.GetFileName(free)}'." : "");
            moves.Add(new FolderMove(profile, free, reason, null, becomes));
        }

        var becoming = moves.Where(m => m.BecomesCategory).Select(m => m.Destination!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tree = def.Tree
            .SelectMany(f => Chain(root.Path, f.RelativePath).Select(p => (Path: p, f.Purpose, Rel: Path.GetRelativePath(root.Path, p))))
            .DistinctBy(t => t.Path, StringComparer.OrdinalIgnoreCase)
            .Where(t => !becoming.Contains(t.Path))
            .Select(t => new TreeFolder(t.Rel, t.Path, t.Purpose, Directory.Exists(t.Path)))
            .ToList();
        // Folders that become a category move first, so a sibling's parent-creation can't take their place.
        var ordered = moves.OrderByDescending(m => m.BecomesCategory).ThenBy(m => m.Folder.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return new StructureProposal(root, def, tree, ordered);
    }

    /// <summary>The proposals as a scan result: per root, "create the folders", "move folders into place", "left where they are".</summary>
    public ScanResult Plan(StructurePreset preset, IReadOnlyList<StructureRoot>? roots, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var started = _clock.GetUtcNow();
        var proposals = ProposeAll(preset, roots, progress, ct);
        return ToScanResult(preset, proposals, _clock.GetUtcNow() - started);
    }

    public IReadOnlyList<StructureProposal> ProposeAll(StructurePreset preset, IReadOnlyList<StructureRoot>? roots, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        roots ??= DefaultRoots();
        var list = new List<StructureProposal>();
        for (int n = 0; n < roots.Count; n++)
        {
            progress?.Report(new ScanProgress($"Looking at {roots[n].Label}", (double)n / roots.Count));
            list.Add(Propose(roots[n], preset, ct));
        }
        return list;
    }

    /// <summary>The proposals as a scan result: per root, "create the folders", "move folders into place", "left where they are".</summary>
    public ScanResult ToScanResult(StructurePreset preset, IReadOnlyList<StructureProposal> proposals, TimeSpan duration)
    {
        var groups = new List<ScanGroup>();
        var notes = new List<string>();
        for (int n = 0; n < proposals.Count; n++)
        {
            var p = proposals[n];
            groups.AddRange(Groups(p, n));
            if (p.Moves.Any(m => m.Folder.Sampled)) notes.Add($"Some folders in {p.Root.Label} are very big; their contents were sampled to decide what they hold.");
            if (IsCloud(p.Root.Path))
                notes.Add($"Nothing to move in {p.Root.Label}: it's synced by OneDrive, so PC Tidy leaves its layout alone (moving folders there would make OneDrive upload everything again).");
        }
        if (proposals.Count == 0) notes.Add("No drive folders were found to suggest a structure for.");
        return new ScanResult
        {
            FeatureId = SearchIds.FeatureId,
            Title = $"Folder structure: {StructurePresets.Get(preset).Title}",
            Groups = groups,
            Notes = notes,
            Duration = duration,
        };
    }

    // ------------------------------------------------------------------ groups and plan items

    private IEnumerable<ScanGroup> Groups(StructureProposal p, int index)
    {
        var def = p.Preset;
        var missing = p.Tree.Where(t => !t.Exists).ToList();
        if (missing.Count > 0)
        {
            var actions = missing.Select(t => CreateDir(t.FullPath)).ToList();
            yield return new ScanGroup
            {
                Id = $"structure-{index}-tree",
                Title = $"Create the {def.Title} folders in {p.Root.Label}",
                Description = def.Summary,
                Badge = Badge.Check,
                Items = new[]
                {
                    Guarded(new ScanItem
                    {
                        Title = $"Create {missing.Count} empty folder{(missing.Count == 1 ? "" : "s")}",
                        Detail = string.Join(" · ", missing.Select(t => t.RelativePath)),
                        Reason = "Only empty folders are made; nothing is moved. Undo removes them again if they're still empty.",
                        Badge = Badge.Check,
                        Details = missing.ToDictionary(t => t.RelativePath, t => t.Purpose),
                        Actions = actions,
                    }),
                },
            };
        }

        var moves = p.Moves.Where(m => m.Destination is not null && m.DisabledReason is null).Select(m => MoveItem(p, m)).ToList();
        if (moves.Count > 0)
            yield return new ScanGroup
            {
                Id = $"structure-{index}-moves",
                Title = $"Move folders into place in {p.Root.Label}",
                Description = "Each folder moves as a whole (nothing inside it is changed). Tick only the ones that fit; Undo puts them back.",
                Badge = Badge.Check,
                Items = moves,
            };

        var kept = p.Moves.Where(m => m.Destination is null || m.DisabledReason is not null).Select(m => new ScanItem
        {
            Title = m.Folder.Name,
            Detail = m.Folder.Path,
            Reason = m.Reason,
            Badge = Badge.Check,
            SizeBytes = m.Folder.Bytes,
            FileCount = m.Folder.Files,
            LastModifiedUtc = m.Folder.NewestUtc,
            DisabledReason = m.DisabledReason ?? m.Reason,
        }).ToList();
        if (kept.Count > 0)
            yield return new ScanGroup
            {
                Id = $"structure-{index}-kept",
                Title = $"Folders left where they are in {p.Root.Label}",
                Description = "Not moved, and why: each folder says what keeps it in place.",
                Badge = Badge.Check,
                Items = kept,
            };
    }

    private ScanItem MoveItem(StructureProposal p, FolderMove m)
    {
        var dest = m.Destination!;
        var parentChain = Chain(p.Root.Path, Path.GetRelativePath(p.Root.Path, Path.GetDirectoryName(dest)!))
            .Where(c => !string.Equals(c, p.Root.Path, StringComparison.OrdinalIgnoreCase))
            // A folder another tick turns into (D:\Photos → D:\Media\Photos) is never pre-created; the move makes it.
            .Where(c => !p.Moves.Any(o => o.BecomesCategory && string.Equals(o.Destination, c, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var actions = parentChain.Select(CreateDir).ToList();
        actions.Add(new PlanItem
        {
            Kind = ActionKind.MovePath,
            FeatureId = SearchIds.FeatureId,
            SourcePath = m.Folder.Path,
            DestinationPath = dest,
            IsDirectory = true,
            ExpectedSize = m.Folder.Bytes,
            ClashPolicy = NameClashPolicy.AutoNumber,
            Description = $"Move folder '{m.Folder.Name}' to {Path.GetRelativePath(p.Root.Path, dest)}",
        });
        return Guarded(new ScanItem
        {
            Title = m.Folder.Name,
            Detail = $"{m.Folder.Path} → {dest}",
            Reason = m.Reason,
            Badge = Badge.Check,
            DefaultSelected = false,
            SizeBytes = m.Folder.Bytes,
            FileCount = m.Folder.Files,
            LastModifiedUtc = m.Folder.NewestUtc,
            Actions = actions,
        });
    }

    /// <summary>Pre-checks every action with the guard; a blocked row stays visible but can't be ticked.</summary>
    private ScanItem Guarded(ScanItem item)
    {
        foreach (var a in item.Actions)
        {
            var v = _safety.Check(a);
            if (!v.Allowed) return item with { DisabledReason = CantDoHere(v.Reason) };
        }
        return item;
    }

    /// <summary>"PC Tidy can't do this here because it's in a cloud-synced folder…": one sentence, not "here: This is…".</summary>
    public static string CantDoHere(string? reason)
    {
        var why = string.IsNullOrWhiteSpace(reason) ? "it's a protected place." : reason.Trim();
        var first = why.Split(' ', 2)[0];
        // Lower-case only a plain opening word ("This", "It's"…), never a name like OneDrive or Windows.
        string[] plain = { "This", "It", "It's", "Its", "That", "The", "These", "A", "An" };
        if (plain.Contains(first, StringComparer.Ordinal)) why = char.ToLowerInvariant(why[0]) + why[1..];
        if (why.StartsWith("this is ", StringComparison.Ordinal)) why = "it's " + why["this is ".Length..];
        return "PC Tidy can't do this here because " + why;
    }

    private bool IsCloud(string folder) => Safe(() => _safety.IsInCloudRoot(Path.Combine(folder, "pc-tidy-probe")) || _k.CloudRoots.Any(c => PathUtil.IsUnder(folder, c)));

    private static PlanItem CreateDir(string path) => new()
    {
        Kind = ActionKind.CreateDirectory,
        FeatureId = SearchIds.FeatureId,
        DestinationPath = path,
        IsDirectory = true,
        Description = $"Create the folder '{Path.GetFileName(path)}' in {Path.GetDirectoryName(path)}",
    };

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Folders that apps and games create in Documents (or a drive) and expect to stay where they are: game saves,
    /// Office, PowerShell, Visual Studio... Moving them loses saves and settings, so they're never suggested.
    /// </summary>
    private static readonly string[] AppOwnedNames =
    {
        "My Games", "Saved Games", "WindowsPowerShell", "PowerShell", "Custom Office Templates", "OneNote Notebooks", "Outlook Files",
        "Fax", "Scanned Documents", "Sound recordings", "IISExpress", "My Web Sites", "Zoom", "Adobe", "Rockstar Games",
        "Electronic Arts", "EA Games", "Ubisoft", "Paradox Interactive", "Klei", "Euro Truck Simulator 2", "Battlefield*", "Call of Duty*",
        "Visual Studio*", "SQL Server Management Studio*", "Snagit", "Camtasia*", "FeedbackHub", "My Music", "My Pictures", "My Videos",
    };

    internal static bool IsAppOwned(string name)
        => AppOwnedNames.Any(n => n.EndsWith('*')
            ? name.StartsWith(n[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(name, n, StringComparison.OrdinalIgnoreCase));

    /// <summary>Why a top-level folder can't be part of the suggestion (Windows/app folders, OneDrive, projects), or null.</summary>
    private string? WhyNotMovable(string folder)
    {
        if (IsAppOwned(Path.GetFileName(folder)))
            return "Apps or games keep their saves and settings here and expect it to stay put.";
        foreach (var (path, what) in KnownFolders())
            if (Safe(() => PathUtil.IsUnder(path, folder)))
                return $"Windows uses it as your {what} folder. To move it, use Move to drive → User folders, which tells Windows too.";
        foreach (var dir in _k.GameInstallDirs.Concat(_k.SteamLibraries).Concat(_k.AppInstallDirs))
            if (Safe(() => PathUtil.IsUnder(dir, folder)))
                return "Games or apps are installed in it; moving it would break them.";
        if (Safe(() => PathUtil.IsUnder(_k.DataRoot, folder))) return "PC Tidy keeps its own data in it.";
        if (_safety.IsInCloudRoot(folder) || _k.CloudRoots.Any(c => Safe(() => PathUtil.IsUnder(c, folder))))
            return "It's in OneDrive (or holds it): moving it would make OneDrive upload and download everything again.";
        if (_safety.IsInsideProjectOrAppFolder(folder, out var why))
            return $"{(why ?? "It looks like a code project or an app").TrimEnd('.')}. Folders like this only move as a whole with Move to drive, which leaves a link so nothing breaks.";
        return null;
    }

    private IEnumerable<(string Path, string What)> KnownFolders() => new[]
    {
        (_k.UserProfile, "user"), (_k.Desktop, "Desktop"), (_k.Documents, "Documents"), (_k.Downloads, "Downloads"),
        (_k.Pictures, "Pictures"), (_k.Videos, "Videos"), (_k.Music, "Music"),
    };

    private IEnumerable<FileEntry> TopLevelFolders(string root, CancellationToken ct)
    {
        var list = new List<FileEntry>();
        _walker.Walk(root, new WalkOptions { Recurse = false, IncludeHidden = false, IncludeSystem = false }, e =>
        {
            if (e.IsDirectory) list.Add(e);
            return false;
        }, ct);
        return list.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>root\a, root\a\b for "a\b".</summary>
    private static IEnumerable<string> Chain(string root, string relative)
    {
        var current = root;
        foreach (var part in relative.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            current = Path.Combine(current, part);
            yield return current;
        }
    }

    private static FolderMove Kept(string path, string name, string why)
        => new(new FolderProfile(path, name, FolderKind.Mixed, 0, 0, null, false, ""), null, why, why, false);

    private static bool Same(string a, string b) => Safe(() => string.Equals(PathUtil.Normalize(a), PathUtil.Normalize(b), StringComparison.OrdinalIgnoreCase));

    private static string Norm(string p) => PathUtil.Normalize(p);

    /// <summary>"D:" for a drive root. A practice PC's drives are folders named after the letter (…\pc\D), shown the same way.</summary>
    public static string DriveLabel(string drive)
    {
        var trimmed = drive.TrimEnd('\\', '/');
        if (trimmed.Length == 2 && trimmed[1] == ':') return trimmed.ToUpperInvariant();
        var name = Path.GetFileName(trimmed);
        return name.Length == 1 && char.IsLetter(name[0]) ? char.ToUpperInvariant(name[0]) + ":" : name.Length > 0 ? name : drive;
    }

    private static bool Safe(Func<bool> f)
    {
        try { return f(); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}
