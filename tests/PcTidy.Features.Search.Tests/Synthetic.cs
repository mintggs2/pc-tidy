using PcTidy.Features.Search.Index;

namespace PcTidy.Features.Search.Tests;

/// <summary>In-memory index segments for query and benchmark tests (no disk involved).</summary>
internal static class Synthetic
{
    public static readonly DateTime Base = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>A small known tree under <paramref name="root"/>: (relative path, size, modified). Folders end with '\'.</summary>
    public static IndexSegment Tree(string root, params (string Path, long Size, DateTime Modified)[] entries)
    {
        var b = new IndexSegmentBuilder(root);
        var folders = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [""] = IndexSegment.RootParent };
        int FolderOf(string rel)
        {
            if (folders.TryGetValue(rel, out var i)) return i;
            var parent = Path.GetDirectoryName(rel) ?? "";
            var p = FolderOf(parent);
            i = b.Add(p, Path.GetFileName(rel), true, 0, Base, FileAttributes.Directory);
            folders[rel] = i;
            return i;
        }
        foreach (var (path, size, modified) in entries)
        {
            bool isDir = path.EndsWith('\\');
            var rel = path.TrimEnd('\\');
            var parent = FolderOf(Path.GetDirectoryName(rel) ?? "");
            if (isDir) { if (!folders.ContainsKey(rel)) folders[rel] = b.Add(parent, Path.GetFileName(rel), true, 0, modified, FileAttributes.Directory); }
            else b.Add(parent, Path.GetFileName(rel), false, size, modified, FileAttributes.Archive);
        }
        return b.Build(Base);
    }

    private static readonly string[] Words =
    {
        "holiday", "report", "invoice", "photo", "budget", "notes", "project", "summer", "family", "scan", "draft", "final",
        "meeting", "recipe", "travel", "music", "video", "backup", "letter", "contract", "camera", "school", "game", "screenshot",
    };
    private static readonly string[] Extensions = { "jpg", "png", "pdf", "docx", "txt", "mp4", "mp3", "zip", "xlsx", "json", "cs", "dll", "heic", "mov" };

    /// <summary>
    /// A realistic-looking tree of <paramref name="count"/> entries: ~8% folders, names like "holiday 2023 0412.jpg"
    /// (about 20 characters), sizes from bytes to gigabytes, dates over five years. Deterministic for a seed.
    /// </summary>
    public static IndexSegment Generate(string root, int count, int seed = 42)
    {
        var rnd = new Random(seed);
        var b = new IndexSegmentBuilder(root, count);
        var folders = new List<int> { IndexSegment.RootParent };
        var name = new char[64];
        for (int i = 0; i < count; i++)
        {
            int parent = folders[rnd.Next(folders.Count)];
            bool dir = rnd.Next(100) < 8;
            var w = Words[rnd.Next(Words.Length)];
            string text = dir
                ? $"{w} {2019 + rnd.Next(8)}"
                : $"{w} {2019 + rnd.Next(8)} {rnd.Next(10000):D4}.{Extensions[rnd.Next(Extensions.Length)]}";
            long size = dir ? 0 : (long)Math.Pow(2, rnd.NextDouble() * 31);
            var modified = Base.AddMinutes(-rnd.Next(5 * 365 * 24 * 60));
            int idx = b.Add(parent, text, dir, size, modified, dir ? FileAttributes.Directory : FileAttributes.Archive);
            if (dir) folders.Add(idx);
        }
        return b.Build(Base);
    }
}
