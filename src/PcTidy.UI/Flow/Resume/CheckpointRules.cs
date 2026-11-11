using System.IO;
using PcTidy.Core.Model;

namespace PcTidy.UI.Flow.Resume;

/// <summary>A finished section that will be scanned again instead of reused, and why (plain English).</summary>
public sealed record RescanReason(int Index, string FeatureId, string Title, string Reason);

/// <summary>What <see cref="CheckpointRules.Evaluate"/> decided about a saved scan.</summary>
public sealed record ResumeDecision
{
    /// <summary>False: throw the checkpoint away and scan from the start (<see cref="DiscardReason"/> says why).</summary>
    public bool CanResume { get; init; }
    public string? DiscardReason { get; init; }
    /// <summary>Finished sections to show as they were, by scanner position.</summary>
    public IReadOnlyDictionary<int, CheckpointSection> Reuse { get; init; } = new Dictionary<int, CheckpointSection>();
    /// <summary>Finished sections that are scanned again anyway.</summary>
    public IReadOnlyList<RescanReason> Rescan { get; init; } = Array.Empty<RescanReason>();

    public static ResumeDecision Discard(string reason) => new() { CanResume = false, DiscardReason = reason };
}

/// <summary>
/// THE staleness rules for resuming a scan, all in one place. A resumed section is only a starting point for the
/// preview: before anything changes, the engine still re-checks every file and setting exactly as after a fresh scan.
/// <list type="number">
/// <item>Older than <see cref="MaxAge"/> (24 h), or from the future (the clock moved): discard.</item>
/// <item>PC Tidy version changed, or any option changed (flow, scanners and their order, depth, preset, folders,
/// data folder or practice PC): discard.</item>
/// <item>Windows build or update revision (CurrentBuild.UBR) changed, so an update was installed in between: keep
/// only sections about the user's own files (<see cref="OwnFilesPrefixes"/>); every section that reads Windows
/// settings or state is scanned again.</item>
/// <item>Folders changed a lot: for each kept section, its sampled files are looked at again. More than
/// <see cref="ChangedFilesLimit"/> (20%) missing or changed (size or last-write time) and that section is scanned again.</item>
/// <item>A section whose results were too big to save is scanned again.</item>
/// </list>
/// If no finished section is left to reuse, the checkpoint is discarded.
/// </summary>
public static class CheckpointRules
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);
    /// <summary>Allowed clock skew before a checkpoint counts as "from the future".</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(10);
    public const double ChangedFilesLimit = 0.20;
    /// <summary>Files sampled per finished section.</summary>
    public const int SampleSize = 200;
    /// <summary>One section's results are not saved beyond this (it is scanned again on resume instead).</summary>
    public const int MaxSectionBytes = 8 << 20;

    /// <summary>
    /// Sections about the user's own files, which a Windows update doesn't change (FeatureId equal to one of these, or
    /// starting with it and a dot). Checked against the real ids: organize, duplicates, photos, desktoptidy, developer,
    /// spacemap, drivemove, aichats, search. Everything else (cleanup, tweaks, services, drivers, pccare, privacy,
    /// notifications, speed, apps, games, insights…) is scanned again after an update.
    /// </summary>
    public static readonly IReadOnlyList<string> OwnFilesPrefixes = new[]
    {
        "organize", "duplicates", "photos", "desktoptidy", "developer", "spacemap", "drivemove", "aichats", "search",
    };

    public static bool IsAboutOwnFiles(string featureId)
        => OwnFilesPrefixes.Any(p => featureId.Equals(p, StringComparison.OrdinalIgnoreCase)
                                     || featureId.StartsWith(p + ".", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// How long looking at one section's samples again may take. Past it the section counts as "changed a lot" and is
    /// scanned again, which is always safe (a disconnected share or a removed USB drive can block for seconds a file).
    /// </summary>
    public static readonly TimeSpan SampleTimeLimit = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Whether a sampled file (or folder) is still there with the same size and last-write time. Only paths on a
    /// local fixed drive are looked at: a network path, a mapped, removable or missing drive counts as changed
    /// (the section is scanned again), so a check never waits on a drive that has gone away.
    /// </summary>
    public static bool StillSame(FileSample s)
    {
        try
        {
            if (!OnLocalFixedDrive(s.Path)) return false;
            if (s.IsFolder)
            {
                var d = new DirectoryInfo(s.Path);
                return d.Exists && Math.Abs((d.LastWriteTimeUtc - s.LastWriteUtc).TotalSeconds) < 2;
            }
            var f = new FileInfo(s.Path);
            return f.Exists && f.Length == s.Size && Math.Abs((f.LastWriteTimeUtc - s.LastWriteUtc).TotalSeconds) < 2;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>"C:\…" on a fixed drive. UNC paths (\\server\share, \\?\…) and every other drive type are not.</summary>
    public static bool OnLocalFixedDrive(string path)
    {
        try
        {
            if (path.Length < 3 || path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) return false;
            if (!char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/')) return false;
            return new DriveInfo(path[..1]).DriveType == DriveType.Fixed; // GetDriveType: never waits on the drive itself
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when more than 20% of the samples are missing or changed, or when looking at them takes longer than
    /// <paramref name="limit"/> (default <see cref="SampleTimeLimit"/>): an answer that slow is treated as "changed".
    /// </summary>
    public static bool ChangedALot(IReadOnlyList<FileSample> samples, Func<FileSample, bool> stillSame, TimeSpan? limit = null)
    {
        if (samples.Count == 0) return false;
        double allowed = samples.Count * ChangedFilesLimit;
        var work = Task.Run(() =>
        {
            int changed = 0;
            foreach (var s in samples)
            {
                bool same;
                try { same = stillSame(s); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { same = false; }
                if (!same && ++changed > allowed) break; // already too many: no need to look further
            }
            return changed;
        });
        try
        {
            if (!work.Wait(limit ?? SampleTimeLimit)) return true; // too slow (an offline share?): scan it again, which is safe
            return work.Result > allowed;
        }
        catch (AggregateException) { return true; }
    }

    /// <summary>The rules that need only the header (age, clock, version, options): a discard decision, or null to go on.</summary>
    public static ResumeDecision? PreCheck(ScanCheckpoint cp, ScanCheckpointKey current, DateTime nowUtc)
    {
        var age = nowUtc - cp.StartedUtc;
        if (age > MaxAge) return ResumeDecision.Discard("it is more than a day old");
        if (age < -ClockSkew) return ResumeDecision.Discard("the PC's clock changed since");
        if (!string.Equals(cp.Key.AppVersion, current.AppVersion, StringComparison.Ordinal)) return ResumeDecision.Discard("PC Tidy was updated since");
        if (!cp.Key.SameOptions(current)) return ResumeDecision.Discard("the scan options or sections changed");
        return null;
    }

    /// <param name="current">The key the scan would have now (with the saved depth and preset).</param>
    /// <param name="currentBuild">Windows' CurrentBuild.UBR now (null if unreadable: treated as changed when the saved one was known).</param>
    /// <param name="stillSame">Looks at one sampled file again; null = <see cref="StillSame"/>.</param>
    public static ResumeDecision Evaluate(ScanCheckpoint cp, ScanCheckpointKey current, DateTime nowUtc, string? currentBuild, Func<FileSample, bool>? stillSame = null)
    {
        stillSame ??= StillSame;
        if (PreCheck(cp, current, nowUtc) is { } discard) return discard;

        bool windowsUpdated = !string.Equals(cp.WindowsBuild ?? "", currentBuild ?? "", StringComparison.OrdinalIgnoreCase);
        var reuse = new Dictionary<int, CheckpointSection>();
        var rescan = new List<RescanReason>();
        foreach (var s in cp.Sections.OrderBy(s => s.Index))
        {
            if (s.Index < 0 || s.Index >= current.ScannerIds.Count || !string.Equals(current.ScannerIds[s.Index], s.FeatureId, StringComparison.OrdinalIgnoreCase))
                return ResumeDecision.Discard("the scan options or sections changed");
            if (reuse.ContainsKey(s.Index)) continue;
            string? why =
                s.Result is null ? s.NotSavedReason ?? "its results weren't saved"
                : windowsUpdated && !IsAboutOwnFiles(s.FeatureId) ? "Windows was updated since"
                : ChangedALot(s.Samples, stillSame) ? "many of its files changed since"
                : null;
            if (why is null) reuse[s.Index] = s;
            else rescan.Add(new RescanReason(s.Index, s.FeatureId, s.Title, why));
        }
        if (reuse.Count == 0)
            return ResumeDecision.Discard(rescan.Count > 0 ? $"none of the finished sections could be reused ({rescan[0].Reason})" : "no section had finished");
        return new ResumeDecision { CanResume = true, Reuse = reuse, Rescan = rescan };
    }

    /// <summary>
    /// Up to <see cref="SampleSize"/> existing files and folders the section's items point at, spread over the whole
    /// list. Folders are sampled by their last-write time (3.1.1 review L3), so a section of only folders is checked too.
    /// </summary>
    public static IReadOnlyList<FileSample> Sample(ScanResult result)
    {
        var paths = new List<(string Path, bool? Folder)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in result.AllItems)
        {
            foreach (var a in item.Actions)
                if (a.SourcePath is { Length: > 3 } p && Path.IsPathFullyQualified(p) && seen.Add(p)) paths.Add((p, a.IsDirectory));
            if (item.Actions.Count == 0 && item.Detail is { Length: > 3 } d && Path.IsPathFullyQualified(d) && seen.Add(d)) paths.Add((d, null));
        }
        if (paths.Count == 0) return Array.Empty<FileSample>();
        var picked = new List<FileSample>();
        // Every k-th path, so a long list is sampled from start to end (files that are already gone are skipped).
        double step = Math.Max(1.0, paths.Count / (double)SampleSize);
        for (double i = 0; i < paths.Count && picked.Count < SampleSize; i += step)
        {
            var (p, folder) = paths[(int)i];
            try
            {
                if (folder != true)
                {
                    var f = new FileInfo(p);
                    if (f.Exists) { picked.Add(new FileSample(p, f.Length, f.LastWriteTimeUtc)); continue; }
                }
                if (folder != false)
                {
                    var dir = new DirectoryInfo(p);
                    if (dir.Exists) picked.Add(new FileSample(p, 0, dir.LastWriteTimeUtc) { IsFolder = true });
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException) { }
        }
        return picked;
    }
}
