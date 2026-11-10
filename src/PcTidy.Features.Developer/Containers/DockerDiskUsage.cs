using System.Text.Json;

namespace PcTidy.Features.Developer.Containers;

/// <summary>
/// Docker Engine's disk usage (<c>GET /system/df</c>, the data behind <c>docker system df</c>) in exact bytes, plus the
/// share each prune would free. The prune numbers match what the prunes remove:
/// images = dangling images only (<c>docker image prune</c>, no <c>-a</c>), containers = stopped containers,
/// builder = build cache not in use. Volumes are listed by name only and never offered (they hold data).
/// </summary>
public sealed record DockerDiskUsage(
    IReadOnlyList<DockerDiskUsage.Image> Images,
    IReadOnlyList<DockerDiskUsage.Container> Containers,
    IReadOnlyList<DockerDiskUsage.Volume> Volumes,
    IReadOnlyList<DockerDiskUsage.CacheRecord> BuildCache)
{
    public sealed record Image(string Id, IReadOnlyList<string> Tags, long Size, long SharedSize, int ContainerCount)
    {
        /// <summary>No tag at all (a leftover layer stack from an older build or pull).</summary>
        public bool IsDangling => Tags.Count == 0 || Tags.All(t => t == "<none>:<none>");
    }

    public sealed record Container(string Id, string Name, string State, long SizeRw)
    {
        public bool IsStopped => !State.Equals("running", StringComparison.OrdinalIgnoreCase)
                                 && !State.Equals("paused", StringComparison.OrdinalIgnoreCase)
                                 && !State.Equals("restarting", StringComparison.OrdinalIgnoreCase);
    }

    /// <param name="Size">Bytes, or -1 when Docker didn't measure it.</param>
    public sealed record Volume(string Name, long Size, int RefCount);

    public sealed record CacheRecord(string Id, long Size, bool InUse, bool Shared);

    public IReadOnlyList<Image> DanglingImages => Images.Where(i => i.IsDangling && i.ContainerCount <= 0).ToList();
    /// <summary>A dangling image's layers that other images share aren't freed, so only its unique part counts.</summary>
    public long DanglingImagesBytes => DanglingImages.Sum(i => Math.Max(0, i.Size - Math.Max(0, i.SharedSize)));
    public IReadOnlyList<Container> StoppedContainers => Containers.Where(c => c.IsStopped).ToList();
    public long StoppedContainersBytes => StoppedContainers.Sum(c => Math.Max(0, c.SizeRw));
    public IReadOnlyList<CacheRecord> UnusedBuildCache => BuildCache.Where(c => !c.InUse && !c.Shared).ToList();
    public long UnusedBuildCacheBytes => UnusedBuildCache.Sum(c => Math.Max(0, c.Size));

    /// <summary>Parses the Engine API JSON. Unknown or missing fields are tolerated; returns null for anything that isn't the expected shape.</summary>
    public static DockerDiskUsage? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            return new DockerDiskUsage(
                Array(root, "Images").Select(e => new Image(Str(e, "Id"), Strings(e, "RepoTags"), Long(e, "Size"), Long(e, "SharedSize"), (int)Long(e, "Containers"))).ToList(),
                Array(root, "Containers").Select(e => new Container(Str(e, "Id"), Strings(e, "Names").FirstOrDefault()?.TrimStart('/') ?? Short(Str(e, "Id")),
                    Str(e, "State"), Long(e, "SizeRw"))).ToList(),
                Array(root, "Volumes").Select(e => new Volume(Str(e, "Name"),
                    e.TryGetProperty("UsageData", out var u) && u.ValueKind == JsonValueKind.Object ? Long(u, "Size", -1) : -1,
                    e.TryGetProperty("UsageData", out var u2) && u2.ValueKind == JsonValueKind.Object ? (int)Long(u2, "RefCount", -1) : -1)).ToList(),
                Array(root, "BuildCache").Select(e => new CacheRecord(Str(e, "ID"), Long(e, "Size"), Bool(e, "InUse"), Bool(e, "Shared"))).ToList());
        }
        catch (JsonException) { return null; }
    }

    private static IEnumerable<JsonElement> Array(JsonElement e, string name)
        => e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object) : Enumerable.Empty<JsonElement>();

    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static IReadOnlyList<string> Strings(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : System.Array.Empty<string>();

    private static long Long(JsonElement e, string name, long fallback = 0)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : fallback;

    private static bool Bool(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string Short(string id) => id.StartsWith("sha256:", StringComparison.Ordinal) ? id[7..Math.Min(id.Length, 19)] : id[..Math.Min(id.Length, 12)];
}
