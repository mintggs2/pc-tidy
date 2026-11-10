using PcTidy.Core;

namespace PcTidy.Features.Cleanup.Scanning;

/// <summary>Counts what a Deep scan found that a Quick scan would have skipped, for the "What Deep found" note.</summary>
internal sealed class DeepTally
{
    private readonly Dictionary<string, (int Files, long Bytes)> _byKind = new();

    public void Add(string kind, int files, long bytes)
    {
        _byKind.TryGetValue(kind, out var t);
        _byKind[kind] = (t.Files + files, t.Bytes + bytes);
    }

    public int TotalFiles => _byKind.Values.Sum(v => v.Files);
    public long TotalBytes => _byKind.Values.Sum(v => v.Bytes);

    public string Summary()
    {
        if (_byKind.Count == 0)
            return "Deep scan looked in more places but found nothing extra to clean.";
        var kinds = string.Join("; ", _byKind.Select(kv => $"{kv.Key} ({Sizes.Format(kv.Value.Bytes)})"));
        return $"Deep scan found {TotalFiles:N0} more files ({Sizes.Format(TotalBytes)}) that Quick skips: {kinds}.";
    }
}
