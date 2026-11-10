using System.Text;

namespace PcTidy.TestbedGen.Manifest;

public sealed record ManifestCompareOptions
{
    /// <summary>Folder times change whenever something is moved in or out (even when undo puts it back), so they are
    /// ignored by default. Turn on to prove two generations are identical.</summary>
    public bool IncludeDirectoryTimes { get; init; }
    /// <summary>Attributes that don't count as a change. Default: Archive (copying sets it; nobody cares).</summary>
    public FileAttributes IgnoredAttributes { get; init; } = FileAttributes.Archive;
    public static ManifestCompareOptions Default { get; } = new();
    public static ManifestCompareOptions Strict { get; } = new() { IncludeDirectoryTimes = true, IgnoredAttributes = 0 };
}

public sealed record ManifestDifference(string Path, string Change, string? Before, string? After)
{
    public override string ToString() => Before is null && After is null ? $"{Change,-10} {Path}" : $"{Change,-10} {Path}  [{Before} -> {After}]";
}

public sealed record ManifestDiff(IReadOnlyList<ManifestDifference> Differences)
{
    public bool Identical => Differences.Count == 0;

    /// <summary>Readable report, capped so a broken run doesn't print 300k lines.</summary>
    public string Format(int maxLines = 200)
    {
        if (Identical) return "Identical.";
        var sb = new StringBuilder($"{Differences.Count} difference(s):\n");
        foreach (var d in Differences.Take(maxLines)) sb.Append("  ").Append(d).Append('\n');
        if (Differences.Count > maxLines) sb.Append($"  ... and {Differences.Count - maxLines} more\n");
        return sb.ToString();
    }
}

internal static class ManifestComparer
{
    public static ManifestDiff Compare(TestbedManifest before, TestbedManifest after, ManifestCompareOptions options)
    {
        var a = before.Entries.ToDictionary(e => e.Path, StringComparer.Ordinal);
        var b = after.Entries.ToDictionary(e => e.Path, StringComparer.Ordinal);
        var diffs = new List<ManifestDifference>();
        foreach (var (path, x) in a)
        {
            if (!b.TryGetValue(path, out var y)) { diffs.Add(new(path, "missing", null, null)); continue; }
            Diff(x, y, options, diffs);
        }
        foreach (var path in b.Keys.Where(p => !a.ContainsKey(p))) diffs.Add(new(path, "added", null, null));
        diffs.Sort((p, q) => string.CompareOrdinal(p.Path, q.Path));
        return new ManifestDiff(diffs);
    }

    private static void Diff(ManifestEntry x, ManifestEntry y, ManifestCompareOptions o, List<ManifestDifference> diffs)
    {
        void Check<T>(string what, T before, T after)
        {
            if (!EqualityComparer<T>.Default.Equals(before, after)) diffs.Add(new(x.Path, what, before?.ToString(), after?.ToString()));
        }
        Check("kind", x.Kind, y.Kind);
        if (x.Kind != y.Kind) return;
        Check("attributes", x.Attributes & ~o.IgnoredAttributes, y.Attributes & ~o.IgnoredAttributes);
        switch (x.Kind)
        {
            case EntryKind.File:
                Check("size", x.Size, y.Size);
                Check("content", x.Hash, y.Hash);
                Check("created", x.CreationUtc?.ToString("O"), y.CreationUtc?.ToString("O"));
                Check("modified", x.LastWriteUtc?.ToString("O"), y.LastWriteUtc?.ToString("O"));
                break;
            case EntryKind.Directory when o.IncludeDirectoryTimes:
                Check("created", x.CreationUtc?.ToString("O"), y.CreationUtc?.ToString("O"));
                Check("modified", x.LastWriteUtc?.ToString("O"), y.LastWriteUtc?.ToString("O"));
                break;
            case EntryKind.Junction or EntryKind.FileLink:
                Check("target", x.Target, y.Target);
                break;
        }
    }
}
