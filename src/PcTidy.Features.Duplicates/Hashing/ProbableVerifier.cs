using PcTidy.Features.Duplicates.Keep;
using PcTidy.Features.Duplicates.Model;

namespace PcTidy.Features.Duplicates.Hashing;

/// <summary>What "Check fully" found for a probable group.</summary>
/// <param name="Confirmed">Normal duplicate groups (full XXH3 match), ready for verified plan items. Never ticked by themselves.</param>
/// <param name="Different">Copies whose content turned out different from every other copy.</param>
/// <param name="Skipped">Copies that changed since the scan or couldn't be read (left alone).</param>
public sealed record ProbableCheck(IReadOnlyList<DuplicateGroup> Confirmed, IReadOnlyList<string> Different, IReadOnlyList<string> Skipped);

/// <summary>
/// "Check fully" for huge files that only matched by samples: reads every copy completely (XXH3, cached by path,
/// size and time), and only copies whose FULL hash matches become a normal duplicate group whose plan items carry
/// <c>verifyHash</c> = that full hash. Read-only, cancellable, runs off the UI thread.
/// </summary>
public static class ProbableVerifier
{
    public static Task<ProbableCheck> CheckFullyAsync(DuplicateGroup probable, HashCache cache, IProgress<double>? progress = null, CancellationToken ct = default)
        => Task.Run(() => Check(probable, cache, progress, ct), ct);

    private static ProbableCheck Check(DuplicateGroup probable, HashCache cache, IProgress<double>? progress, CancellationToken ct)
    {
        if (!probable.IsProbable) throw new ArgumentException("Only probable groups need a full check.", nameof(probable));
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var skipped = new List<string>();
        var copies = probable.Copies;
        for (int i = 0; i < copies.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var c = copies[i];
            // A hard link to another copy shares its data: its hash is that copy's hash.
            if (c.HardLinkOf is { Length: > 0 }) continue;
            if (FullHash(c, cache, ct) is { } h) hashes[c.Path] = h;
            else skipped.Add(c.Path);
            progress?.Report((i + 1.0) / copies.Count);
        }
        foreach (var c in copies.Where(c => c.HardLinkOf is { Length: > 0 }))
            if (hashes.TryGetValue(c.HardLinkOf!, out var h)) hashes[c.Path] = h;

        var confirmed = new List<DuplicateGroup>();
        var different = new List<string>();
        foreach (var byHash in copies.Where(c => hashes.ContainsKey(c.Path)).GroupBy(c => hashes[c.Path]))
        {
            var members = byHash.ToList();
            // Two or more real copies (not just two names of one file).
            if (members.Count(m => !m.IsHardLink || m.HardLinkOf == "") < 2) { different.AddRange(members.Select(m => m.Path)); continue; }
            var group = KeepRules.Decide("dup-" + byHash.Key + "-" + probable.Size, byHash.Key, probable.Size, KeepRules.Rank(members)) with
            {
                CheckedFully = true,
                // The user decides about huge files: nothing is ticked for them.
                AutoTicked = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            };
            if (probable.KeptByUser && members.Any(m => DuplicateGroup.PathEquals(m.Path, probable.KeptPath)))
                group = KeepRules.WithKeeper(group, probable.KeptPath) with { CheckedFully = true, AutoTicked = new HashSet<string>(StringComparer.OrdinalIgnoreCase) };
            confirmed.Add(group);
        }
        return new ProbableCheck(confirmed, different, skipped);
    }

    /// <summary>Full XXH3 of a copy, or null when it changed since the scan or can't be read.</summary>
    private static string? FullHash(DuplicateCopy c, HashCache cache, CancellationToken ct)
    {
        try
        {
            var info = new FileInfo(Win32Path.Raw(c.Path));
            if (!info.Exists || info.Length != c.Size || info.LastWriteTimeUtc != c.LastWriteUtc) return null;
            if (cache.TryGetFull(c.Path, c.Size, c.LastWriteUtc, out var cached)) return DuplicateFinder.Hex(cached);
            var hex = FileHash.Full(Win32Path.Raw(c.Path), ct);
            cache.PutFull(c.Path, c.Size, c.LastWriteUtc, DuplicateFinder.ParseHex(hex));
            return hex;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
