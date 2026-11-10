namespace PcTidy.Features.Speed.Common;

/// <summary>
/// File facts from a table (tests and screenshots): listed paths get the given facts; any other path is checked
/// for existence only and reported as unsigned with no publisher.
/// </summary>
public sealed class FixedFileFacts : IFileFactsProvider
{
    private readonly Dictionary<string, FileFacts> _known;

    public FixedFileFacts(IEnumerable<FileFacts>? known = null)
        => _known = (known ?? Array.Empty<FileFacts>()).Where(f => f.Path is not null)
            .ToDictionary(f => f.Path!, StringComparer.OrdinalIgnoreCase);

    public FileFacts Get(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return FileFacts.None(null);
        if (_known.TryGetValue(path, out var f)) return f;
        return File.Exists(path) ? new FileFacts(path, true, null, null, SignatureState.Unsigned, null) : FileFacts.None(path);
    }

    /// <summary>A trusted Microsoft-signed file.</summary>
    public static FileFacts Microsoft(string path, string? description = null)
        => new(path, true, "Microsoft Corporation", description, SignatureState.Verified, "Microsoft Corporation");

    /// <summary>A trusted third-party file.</summary>
    public static FileFacts Signed(string path, string company, string? description = null)
        => new(path, true, company, description, SignatureState.Verified, company);
}
