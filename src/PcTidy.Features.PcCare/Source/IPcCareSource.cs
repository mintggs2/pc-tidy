using System.Text.Json;
using System.Text.Json.Serialization;
using PcTidy.Core;
using PcTidy.Features.PcCare.Model;
using PcTidy.Features.PcCare.Scanning;

namespace PcTidy.Features.PcCare.Source;

/// <summary>Where PC care's facts come from. Implementations are READ-ONLY.</summary>
public interface IPcCareSource
{
    /// <summary>Reads everything (each area on its own: one failing area never stops the others).</summary>
    Task<PcCareSnapshot> ReadAsync(PcCareScanOptions options, IProgress<string>? progress, CancellationToken ct);

    /// <summary>Times DNS lookups against the current servers and three public ones (read-only, sends a few UDP packets).</summary>
    Task<NetworkFacts?> TestDnsAsync(CancellationToken ct);
}

/// <summary>Picks the source: the real PC, or the sandbox snapshot (real tools and network never run in a sandbox).</summary>
public static class PcCareSourceFactory
{
    public static IPcCareSource For(IKnownLocations locations)
    {
        if (!locations.IsSandbox) return new WindowsPcCareSource(locations);
        var root = locations is SandboxKnownLocations s ? s.Root : locations.DataRoot;
        return new FixturePcCareSource(Path.Combine(root, "Sim", "pccare", "snapshot.json"));
    }
}

/// <summary>
/// Sandbox source: reads a <see cref="PcCareSnapshot"/> from JSON (missing or broken file = nothing available on this PC).
/// Its DNS test returns the snapshot's own results, so no packet ever leaves a test.
/// </summary>
public sealed class FixturePcCareSource : IPcCareSource
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _file;

    public FixturePcCareSource(string file) => _file = file;

    public Task<PcCareSnapshot> ReadAsync(PcCareScanOptions options, IProgress<string>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var snapshot = Load() ?? new PcCareSnapshot();
        // Like the real source: the DNS test only runs when asked for.
        if (!options.RunDnsTest && snapshot.Network is { } n) snapshot = snapshot with { Network = n with { DnsTest = null } };
        if (!options.CheckAppUpdates) snapshot = snapshot with { Winget = null };
        return Task.FromResult(snapshot);
    }

    public Task<NetworkFacts?> TestDnsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Load()?.Network);
    }

    private PcCareSnapshot? Load()
    {
        try
        {
            return File.Exists(_file) ? JsonSerializer.Deserialize<PcCareSnapshot>(File.ReadAllText(_file), Json) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }
}
