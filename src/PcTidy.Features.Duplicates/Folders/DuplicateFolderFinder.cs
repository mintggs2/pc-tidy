using System.IO.Hashing;
using System.Text;
using PcTidy.Features.Duplicates.Scanning;

namespace PcTidy.Features.Duplicates.Folders;

/// <summary>A folder whose whole content (names, sizes and hashes, at every depth) matches another folder.</summary>
public sealed record RawFolderCopy(string Path, long Bytes, int Files, DateTime WriteUtc);

public sealed record RawFolderGroup(string Signature, IReadOnlyList<RawFolderCopy> Folders);

/// <summary>
/// Duplicate folders: same set of names + sizes + content hashes. Built bottom-up from the walk and the exact
/// duplicate hashes (a duplicate folder's files are always duplicates themselves, so they're already hashed).
/// A folder the walk couldn't fully judge (links, online-only files, skipped subfolders, unhashed files) has no
/// signature and is never called a duplicate. Only the outermost matching folders are listed.
/// </summary>
public static class DuplicateFolderFinder
{
    private const string EmptyFileHash = "EMPTY";

    public static IReadOnlyList<RawFolderGroup> Find(IReadOnlyList<RootInventory> inventories, IReadOnlyDictionary<string, string> fullHashes, CancellationToken ct)
    {
        var all = new List<(string Sig, RawFolderCopy Copy, string? ParentPath)>();
        foreach (var inv in inventories)
        {
            ct.ThrowIfCancellationRequested();
            var dirs = inv.Dirs;
            var sig = new string?[dirs.Count];
            var bytes = new long[dirs.Count];
            var files = new int[dirs.Count];
            for (int i = dirs.Count - 1; i >= 0; i--)
            {
                var d = dirs[i];
                if (d.HasOpaqueContent || d.AllFiles != d.FileIndexes.Count) continue;
                var lines = new List<string>(d.FileIndexes.Count + d.Children.Count);
                bool ok = true;
                foreach (var fi in d.FileIndexes)
                {
                    var f = inv.Files[fi];
                    string? h = f.Size == 0 ? EmptyFileHash : fullHashes.TryGetValue(f.Path, out var fh) ? fh : null;
                    if (h is null) { ok = false; break; }
                    lines.Add($"F|{f.Name.ToUpperInvariant()}|{f.Size}|{h}");
                    bytes[i] += f.Size;
                    files[i]++;
                }
                if (!ok) continue;
                foreach (var c in d.Children)
                {
                    if (sig[c] is null) { ok = false; break; }
                    lines.Add($"D|{dirs[c].Name.ToUpperInvariant()}|{sig[c]}");
                    bytes[i] += bytes[c];
                    files[i] += files[c];
                }
                if (!ok || files[i] == 0) continue;
                lines.Sort(StringComparer.Ordinal);
                sig[i] = Convert.ToHexString(XxHash3.Hash(Encoding.UTF8.GetBytes(string.Join("\n", lines))));
            }
            // The walk's own root (Downloads, a drive...) is never offered as a duplicate folder.
            for (int i = 1; i < dirs.Count; i++)
                if (sig[i] is { } s)
                    all.Add((s, new RawFolderCopy(dirs[i].Path, bytes[i], files[i], dirs[i].WriteUtc), dirs[i].Parent > 0 ? dirs[dirs[i].Parent].Path : null));
        }

        var groups = all.GroupBy(x => x.Sig).Where(g => g.Count() > 1).ToList();
        var groupOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in groups)
            foreach (var x in g) groupOf[x.Copy.Path] = g.Key;

        var result = new List<RawFolderGroup>();
        foreach (var g in groups)
        {
            // Subfolders of matching folders are covered by their parents' group: of those, one stands in for all
            // (Album\raw for Album and "Album - Copy"); subfolders whose parent is unique stay as they are.
            var kept = g.Where(x => x.ParentPath is null || !groupOf.ContainsKey(x.ParentPath)).Select(x => x.Copy).ToList();
            kept.AddRange(g.Where(x => x.ParentPath is not null && groupOf.ContainsKey(x.ParentPath))
                .GroupBy(x => groupOf[x.ParentPath!])
                .Select(pg => pg.OrderBy(x => x.Copy.Path, StringComparer.OrdinalIgnoreCase).First().Copy));
            if (kept.Count > 1) result.Add(new RawFolderGroup(g.Key, kept));
        }
        return result.OrderByDescending(g => g.Folders[0].Bytes * (g.Folders.Count - 1)).ToList();
    }
}
