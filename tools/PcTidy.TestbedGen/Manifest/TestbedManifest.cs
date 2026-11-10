using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcTidy.TestbedGen.Manifest;

public enum EntryKind { File, Directory, Junction, FileLink }

/// <summary>One file, folder or link in a manifest. Times are UTC with full 100 ns precision.
/// Links are recorded with their target and never followed; their own times are not recorded.</summary>
public sealed record ManifestEntry
{
    public required string Path { get; init; }
    public required EntryKind Kind { get; init; }
    public long Size { get; init; }
    /// <summary>XXH3-64 hex (same as Core FileHash.Full), or "!error: ..." when the file couldn't be read (e.g. locked).</summary>
    public string? Hash { get; init; }
    public FileAttributes Attributes { get; init; }
    public DateTime? CreationUtc { get; init; }
    public DateTime? LastWriteUtc { get; init; }
    /// <summary>Link target; "$ROOT\..." when it points inside the testbed, so manifests of copies compare equal.</summary>
    public string? Target { get; init; }
}

/// <summary>Everything under a testbed root, sorted by path (ordinal). Proves "undo restores everything exactly".</summary>
public sealed record TestbedManifest
{
    public int Version { get; init; } = 1;
    public required string Root { get; init; }
    public required IReadOnlyList<ManifestEntry> Entries { get; init; }
    [JsonIgnore] public int FileCount => Entries.Count(e => e.Kind == EntryKind.File);
    [JsonIgnore] public int DirectoryCount => Entries.Count(e => e.Kind == EntryKind.Directory);
    [JsonIgnore] public long TotalBytes => Entries.Where(e => e.Kind == EntryKind.File).Sum(e => e.Size);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public void Save(string path)
    {
        using var fs = File.Create(path);
        JsonSerializer.Serialize(fs, this, Json);
    }

    public static TestbedManifest Load(string path)
    {
        using var fs = File.OpenRead(path);
        return JsonSerializer.Deserialize<TestbedManifest>(fs, Json) ?? throw new InvalidDataException("Empty manifest: " + path);
    }
}
