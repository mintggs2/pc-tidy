using PcTidy.Catalog.Apps;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Apps.Inventory;

namespace PcTidy.Features.Apps.Leftovers;

/// <summary>BCU-style rating of how sure PC Tidy is that a leftover belonged to the removed app.</summary>
public enum LeftoverConfidence { VeryGood, Good, Questionable, Bad }

public enum LeftoverKind { Folder, RegistryKey }

/// <summary>One thing the removed app probably left behind.</summary>
public sealed record LeftoverFinding
{
    public required LeftoverKind Kind { get; init; }
    /// <summary>Folder path, or "HKCU\Software\Publisher\App" for keys.</summary>
    public required string Path { get; init; }
    public required LeftoverConfidence Confidence { get; init; }
    /// <summary>Why it matched ("Same name as the app's install folder, inside its publisher's folder").</summary>
    public required string Why { get; init; }
    /// <summary>Why it can't be removed (another app uses it, PC Tidy's safety rules...). Null = removable.</summary>
    public string? NotRemovableReason { get; init; }
    /// <summary>Holds what looks like the user's own files (saves, documents): never pre-ticked.</summary>
    public bool MayHoldYourFiles { get; init; }
    public long SizeBytes { get; init; }
    public int FileCount { get; init; }
    public DateTime? LastWriteUtc { get; init; }
    /// <summary>What removing it would do (QuarantinePath / RegistryDeleteKey). Present even when not removable, for the record.</summary>
    public PlanItem? Action { get; init; }

    /// <summary>Only "Very good" findings that can actually be removed are ticked by default.</summary>
    public bool PreSelected => Confidence == LeftoverConfidence.VeryGood && NotRemovableReason is null && !MayHoldYourFiles;
}

/// <summary>
/// Finds what an uninstalled app left behind, strictly (Research §4 "Leftovers", Plan §8-4):
/// folders only on an EXACT name match under Program Files, ProgramData and AppData (or &lt;Publisher&gt;\&lt;App&gt; there),
/// registry keys only at the exact Software\&lt;Publisher&gt;\&lt;App&gt;. Rejected when another app still uses the place,
/// when it's a publisher folder other apps share, or when the name is short, generic or on the keeper list.
/// Every removal is a quarantine move (restorable); every item is re-checked by the SafetyGuard.
/// </summary>
public sealed class LeftoverScanner
{
    private readonly IKnownLocations _locations;
    private readonly ISafetyGuard _guard;
    private readonly IRegistryReader _registry;
    private readonly IFileWalker _walker;

    public LeftoverScanner(IKnownLocations locations, ISafetyGuard guard, IRegistryReader registry, IFileWalker walker)
    {
        _locations = locations;
        _guard = guard;
        _registry = registry;
        _walker = walker;
    }

    /// <summary>The places leftovers are looked for, with a friendly area name.</summary>
    public IReadOnlyList<(string Root, string Area)> Roots() => new[]
    {
        (_locations.ProgramFiles, "Program Files"),
        (_locations.ProgramFilesX86, "Program Files (x86)"),
        (_locations.ProgramData, "ProgramData"),
        (_locations.LocalAppData, "AppData (Local)"),
        (Path.Combine(_locations.LocalAppData, "Programs"), "AppData (Local) › Programs"),
        (Path.Combine(_locations.UserProfile, "AppData", "LocalLow"), "AppData (LocalLow)"),
        (_locations.RoamingAppData, "AppData (Roaming)"),
    };

    /// <param name="removed">The app as it was before it was uninstalled (or an orphaned entry).</param>
    /// <param name="stillInstalled">Every app still installed now (the removed one must not be in it).</param>
    public IReadOnlyList<LeftoverFinding> Scan(InstalledApp removed, IReadOnlyList<InstalledApp> stillInstalled, CancellationToken ct)
    {
        var names = MatchNames(removed);
        var others = stillInstalled.Where(a => a.Id != removed.Id).ToList();
        // A name another installed app also answers to (two versions side by side, a suite) is never matched.
        var otherNames = others.SelectMany(OwnNames).ToHashSet(StringComparer.OrdinalIgnoreCase);
        names.RemoveWhere(otherNames.Contains);

        var findings = new List<LeftoverFinding>();
        if (names.Count == 0) return findings;
        var publisher = SafePublisher(removed.Publisher);
        var publisherShared = publisher is not null && others.Any(o => AppNames.Same(AppNames.CleanPublisher(o.Publisher), publisher));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (root, area) in Roots())
        {
            ct.ThrowIfCancellationRequested();
            foreach (var name in names)
            {
                Consider(System.IO.Path.Combine(root, name), area, inPublisherFolder: false);
                if (publisher is not null && !AppNames.Same(publisher, name))
                    Consider(System.IO.Path.Combine(root, publisher, name), $"{area} › {publisher}", inPublisherFolder: true);
            }
        }
        // The app's own folder, if the uninstaller left it behind.
        if (removed.InstallLocation is { } old && IsInsideARoot(old)) Consider(old, AreaOf(old), inPublisherFolder: false);

        findings.AddRange(RegistryLeftovers(names, publisher));
        return findings.OrderBy(f => f.Confidence).ThenByDescending(f => f.SizeBytes).ToList();

        void Consider(string path, string area, bool inPublisherFolder)
        {
            string full;
            try { full = PathUtil.Normalize(path); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return; }
            if (!seen.Add(full) || !AppInventory.SafeDirectoryExists(full)) return;
            var finding = RateFolder(full, area, inPublisherFolder, removed, names, publisher, publisherShared, others, ct);
            if (finding is not null) findings.Add(finding);
        }
    }

    // ------------------------------------------------------------------ folders

    private LeftoverFinding? RateFolder(string folder, string area, bool inPublisherFolder, InstalledApp removed,
        HashSet<string> names, string? publisher, bool publisherShared, List<InstalledApp> others, CancellationToken ct)
    {
        var leaf = System.IO.Path.GetFileName(folder);
        // Keeper list and generic names: never even shown (they were never this app's to begin with).
        // The keeper list applies to every folder on the way too (NVIDIA Corporation\Installer2\..., Microsoft\jdk...).
        if (LeftoverNameRules.IsUnsafeName(leaf, out _) || SegmentsBelowRoot(folder).Any(LeftoverNameRules.IsKeeper)) return null;
        FileAttributes attributes;
        try { attributes = File.GetAttributes(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        if ((attributes & FileAttributes.ReparsePoint) != 0) return null; // links are never followed or moved

        var isOldInstall = removed.InstallLocation is { } loc && SamePath(loc, folder);
        var installLeaf = removed.InstallLocation is { } l ? System.IO.Path.GetFileName(l.TrimEnd('\\')) : null;
        var clean = AppNames.Clean(removed.Name);
        var why = new List<string>();
        int score = 0;
        if (isOldInstall) { score += 3; why.Add("It's the folder the app was installed in"); }
        var matchesInstall = AppNames.Same(leaf, installLeaf);
        var matchesName = AppNames.Same(leaf, clean);
        if (matchesInstall && matchesName) { score += 3; if (!isOldInstall) why.Add("Same name as the app and its install folder"); }
        else if (matchesInstall) { score += 2; if (!isOldInstall) why.Add("Same name as the app's install folder"); }
        else if (matchesName) { score += 2; why.Add("Same name as the app"); }
        if (inPublisherFolder) { score += 1; why.Add($"inside its publisher's folder ({publisher})"); }

        string? notRemovable = null;
        if (UsedByAnotherApp(folder, others) is { } user) notRemovable = $"{user} still uses this folder.";
        else if (publisherShared && AppNames.Same(leaf, publisher)) notRemovable = $"Other apps from {publisher} share this folder.";
        else if (ContainsAnotherApp(folder, others, names) is { } child) notRemovable = $"It holds files of another app ({child}).";

        var size = Measure(folder, ct);
        var mayHoldYourFiles = LooksLikeUserContent(folder, ct);
        var confidence = notRemovable is not null ? LeftoverConfidence.Bad : FromScore(score);
        // Roaming AppData and LocalLow carry your settings and sometimes your data (chat history, profiles):
        // a match there is at most "Good", so it's never ticked for you.
        if (IsSettingsArea(folder) && confidence < LeftoverConfidence.Good) confidence = LeftoverConfidence.Good;
        if (mayHoldYourFiles && confidence < LeftoverConfidence.Questionable) confidence = LeftoverConfidence.Questionable;
        if (confidence == LeftoverConfidence.Bad) notRemovable ??= $"PC Tidy isn't sure enough that this belonged to {removed.Name}.";

        var action = new PlanItem
        {
            Kind = ActionKind.QuarantinePath,
            FeatureId = AppsFeature.Id,
            Description = $"Move leftover folder '{leaf}' ({area}) to quarantine",
            SourcePath = folder,
            IsDirectory = true,
            ExpectedSize = size.Bytes,
            ExpectedLastWriteUtc = size.LastWriteUtc,
        };
        if (notRemovable is null)
        {
            var verdict = _guard.Check(action);
            if (!verdict.Allowed) notRemovable = verdict.Reason ?? "PC Tidy's safety rules keep this folder.";
        }

        return new LeftoverFinding
        {
            Kind = LeftoverKind.Folder,
            Path = folder,
            Confidence = confidence,
            Why = Sentence(why),
            NotRemovableReason = notRemovable,
            MayHoldYourFiles = mayHoldYourFiles,
            SizeBytes = size.Bytes,
            FileCount = size.Files,
            LastWriteUtc = size.LastWriteUtc,
            Action = action,
        };
    }

    private static LeftoverConfidence FromScore(int score) => score switch
    {
        >= 4 => LeftoverConfidence.VeryGood,
        3 => LeftoverConfidence.Good,
        2 => LeftoverConfidence.Questionable,
        _ => LeftoverConfidence.Bad,
    };

    /// <summary>Name of an installed app whose folder, uninstaller or icon is this folder, inside it, or around it.</summary>
    private string? UsedByAnotherApp(string folder, List<InstalledApp> others)
    {
        foreach (var o in others)
        {
            // A broken entry that names a whole area (InstallLocation = "C:\Program Files") says nothing about one folder.
            foreach (var p in PlacesOf(o).Where(p => !Roots().Any(r => SamePath(p, r.Root)) && !SamePath(p, _locations.UserProfile)))
                if (PathUtil.IsUnder(p, folder) || PathUtil.IsUnder(folder, p)) return o.Name;
        }
        return null;
    }

    private static IEnumerable<string> PlacesOf(InstalledApp app)
    {
        if (app.InstallLocation is { Length: > 3 } loc) yield return loc;
        if (UninstallCommand.ProgramPath(app.UninstallString) is { } program && System.IO.Path.GetDirectoryName(program) is { Length: > 3 } d1) yield return d1;
        if (UninstallCommand.IconPath(app.DisplayIcon) is { } icon && System.IO.Path.GetDirectoryName(icon) is { Length: > 3 } d2) yield return d2;
    }

    /// <summary>A direct subfolder named like another installed app means this is a shared (publisher) folder.</summary>
    private static string? ContainsAnotherApp(string folder, List<InstalledApp> others, HashSet<string> names)
    {
        string[] children;
        try { children = Directory.GetDirectories(folder).Select(System.IO.Path.GetFileName).OfType<string>().ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        foreach (var child in children)
        {
            if (names.Contains(child)) continue;
            var owner = others.FirstOrDefault(o => OwnNames(o).Contains(child, StringComparer.OrdinalIgnoreCase));
            if (owner is not null) return owner.Name;
        }
        return null;
    }

    private (long Bytes, int Files, DateTime? LastWriteUtc) Measure(string folder, CancellationToken ct)
    {
        long bytes = 0; int files = 0;
        DateTime? lastWrite = null;
        try
        {
            lastWrite = Directory.GetLastWriteTimeUtc(folder);
            _walker.Walk(folder, new WalkOptions { IncludeHidden = true, IncludeSystem = true }, e =>
            {
                if (!e.IsDirectory) { bytes += e.Size; files++; }
                return !e.IsReparsePoint;
            }, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* partial size is still shown */ }
        return (bytes, files, lastWrite);
    }

    /// <summary>SD Maid's KEEPER idea: saves, documents, pictures one or two levels down mean "check before removing".</summary>
    private bool LooksLikeUserContent(string folder, CancellationToken ct)
    {
        var found = false;
        try
        {
            _walker.Walk(folder, new WalkOptions { MaxDepth = 2, IncludeHidden = false }, e =>
            {
                if (found) return false;
                if (e.IsDirectory ? LeftoverNameRules.UserContentFolderNames.Contains(e.Name)
                                  : LeftoverNameRules.UserContentExtensions.Contains(System.IO.Path.GetExtension(e.Name)))
                    found = true;
                return !found && !e.IsReparsePoint;
            }, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* unreadable: treated as no */ }
        return found;
    }

    // ------------------------------------------------------------------ registry

    private IEnumerable<LeftoverFinding> RegistryLeftovers(HashSet<string> names, string? publisher)
    {
        // Only Software\<Publisher>\<App>: never Software\<App> alone, Classes, Installer or SharedDLLs.
        if (publisher is null) yield break;
        var places = new (RegistryHive Hive, bool View64, string Label)[]
        {
            (RegistryHive.CurrentUser, true, @"HKCU\Software"),
            (RegistryHive.LocalMachine, true, @"HKLM\SOFTWARE"),
            (RegistryHive.LocalMachine, false, @"HKLM\SOFTWARE\WOW6432Node"),
        };
        foreach (var name in names)
        {
            if (AppNames.Same(name, publisher)) continue;
            foreach (var (hive, view64, label) in places)
            {
                var keyPath = $@"Software\{publisher}\{name}";
                using var key = _registry.Open(hive, view64, keyPath);
                if (key is null) continue;
                var action = new PlanItem
                {
                    Kind = ActionKind.RegistryDeleteKey,
                    FeatureId = AppsFeature.Id,
                    Description = $"Remove leftover registry key {label}\\{publisher}\\{name} (a .reg backup is saved first)",
                    NeedsAdmin = hive == RegistryHive.LocalMachine,
                    Registry = new RegistryValueSpec { Hive = hive, KeyPath = keyPath, ValueName = null, View64 = view64 },
                };
                var verdict = _guard.Check(action);
                yield return new LeftoverFinding
                {
                    Kind = LeftoverKind.RegistryKey,
                    Path = $@"{label}\{publisher}\{name}",
                    Confidence = LeftoverConfidence.Good,
                    Why = $"Settings key with the app's exact name under its publisher ({publisher}).",
                    NotRemovableReason = verdict.Allowed ? null : "Shown so you know. " + (verdict.Reason ?? "PC Tidy's safety rules keep this key."),
                    Action = action,
                };
            }
        }
    }

    // ------------------------------------------------------------------ names

    /// <summary>Exact names the removed app's folders may have: its install folder's name and its clean display name.</summary>
    private static HashSet<string> MatchNames(InstalledApp app)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in OwnNames(app))
            if (!LeftoverNameRules.IsUnsafeName(n, out _)) set.Add(n);
        return set;
    }

    private static IEnumerable<string> OwnNames(InstalledApp app)
    {
        if (AppNames.Clean(app.Name) is { } clean) yield return clean;
        if (app.InstallLocation is { Length: > 3 } loc && System.IO.Path.GetFileName(loc.TrimEnd('\\')) is { Length: > 0 } leaf) yield return leaf;
    }

    private static string? SafePublisher(string? publisher)
    {
        var p = AppNames.CleanPublisher(publisher);
        if (p is null || LeftoverNameRules.IsUnsafeName(p, out _)) return null;
        // A publisher name must be usable as one folder name.
        return p.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 ? null : p;
    }

    private bool IsSettingsArea(string folder)
        => PathUtil.IsUnder(folder, _locations.RoamingAppData)
           || PathUtil.IsUnder(folder, System.IO.Path.Combine(_locations.UserProfile, "AppData", "LocalLow"));

    /// <summary>Folder names between the closest scan root and the folder itself, the folder included.</summary>
    private IEnumerable<string> SegmentsBelowRoot(string folder)
    {
        var root = Roots().Where(r => PathUtil.IsUnder(folder, r.Root)).OrderByDescending(r => r.Root.Length).Select(r => r.Root).FirstOrDefault();
        if (root is null) return new[] { System.IO.Path.GetFileName(folder) };
        var relative = System.IO.Path.GetRelativePath(root, folder);
        return relative.Split('\\', StringSplitOptions.RemoveEmptyEntries);
    }

    private bool IsInsideARoot(string path)
        => Roots().Any(r => PathUtil.IsUnder(path, r.Root) && !SamePath(path, r.Root));

    private string AreaOf(string path)
        => Roots().Where(r => PathUtil.IsUnder(path, r.Root)).OrderByDescending(r => r.Root.Length).Select(r => r.Area).FirstOrDefault() ?? "Folder";

    private static bool SamePath(string a, string b)
    {
        try { return string.Equals(PathUtil.Normalize(a), PathUtil.Normalize(b), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static string Sentence(List<string> parts)
    {
        if (parts.Count == 0) return "Exact name match.";
        var s = string.Join(", ", parts);
        return char.ToUpperInvariant(s[0]) + s[1..] + ".";
    }
}
