using System.Buffers.Binary;
using System.Text.Json;
using PcTidy.Core.Model;

namespace PcTidy.Platform;

/// <summary>
/// Execute: run / simulate / undo steps. RestorePoint: make one. Settle: for steps left "Started" (the helper stopped),
/// say from the helper's own ledger whether they happened (read-only). ListAdminQuarantine / PurgeAdminQuarantine:
/// the admin-only quarantine for the Quarantine page (purge needs RunId of a run this helper quarantined for).
/// </summary>
public enum ElevatedRequestKind { Execute, RestorePoint, Cancel, Bye, Settle, ListAdminQuarantine, PurgeAdminQuarantine }

/// <summary>Message from PC Tidy to its admin helper.</summary>
public sealed record ElevatedRequest
{
    public required ElevatedRequestKind Kind { get; init; }
    public string? RunId { get; init; }
    public ExecutionMode Mode { get; init; } = ExecutionMode.Real;
    /// <summary>True = undo these (Done) steps instead of running them.</summary>
    public bool Undo { get; init; }
    public IReadOnlyList<StepRecord>? Steps { get; init; }
    /// <summary>RestorePoint only.</summary>
    public string? Description { get; init; }
}

/// <summary>Answer from the admin helper. Steps come back in the same order they were sent.</summary>
public sealed record ElevatedResponse
{
    public IReadOnlyList<StepRecord>? Steps { get; init; }
    public RestorePointInfo? RestorePoint { get; init; }
    /// <summary>ListAdminQuarantine: what the admin-only quarantine holds.</summary>
    public IReadOnlyList<QuarantineEntry>? Quarantine { get; init; }
    /// <summary>PurgeAdminQuarantine: true when the run's admin quarantine was removed.</summary>
    public bool Purged { get; init; }
    /// <summary>Set when the whole request was refused (plain English).</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Wire format between the app and its elevated helper: a 4-byte little-endian length, then UTF-8 JSON
/// (same enum-as-string options as the rest of PC Tidy). Messages over <see cref="MaxMessageBytes"/> are rejected.
/// </summary>
public static class ElevationProtocol
{
    public const int MaxMessageBytes = 32 * 1024 * 1024;
    public const string ElevatedSwitch = "--elevated";
    public const string PipePrefix = "PcTidy.Elevated.";

    public static readonly JsonSerializerOptions Json = new(AppSettings.Json) { WriteIndented = false };

    public static string NewPipeName() => PipePrefix + Guid.NewGuid().ToString("N");

    /// <summary>Only names PC Tidy itself makes are accepted (the helper gets it on its command line).</summary>
    public static bool IsValidPipeName(string? name)
        => name is { Length: 48 } && name.StartsWith(PipePrefix, StringComparison.Ordinal)
           && name[PipePrefix.Length..].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static byte[] Serialize<T>(T message) => JsonSerializer.SerializeToUtf8Bytes(message, Json);

    public static T Deserialize<T>(ReadOnlySpan<byte> utf8) => JsonSerializer.Deserialize<T>(utf8, Json)
        ?? throw new InvalidDataException("Empty message.");

    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken ct)
    {
        var body = Serialize(message);
        if (body.Length > MaxMessageBytes) throw new InvalidDataException("Message too large.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads one message, or returns null if the other side closed the pipe.</summary>
    public static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken ct) where T : class
    {
        var header = new byte[4];
        if (!await ReadExactlyOrEndAsync(stream, header, ct).ConfigureAwait(false)) return null;
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxMessageBytes) throw new InvalidDataException("Bad message length.");
        var body = new byte[length];
        if (!await ReadExactlyOrEndAsync(stream, body, ct).ConfigureAwait(false)) throw new EndOfStreamException("The pipe closed mid-message.");
        return Deserialize<T>(body);
    }

    private static async Task<bool> ReadExactlyOrEndAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                if (read == 0) return false;
                throw new EndOfStreamException("The pipe closed mid-message.");
            }
            read += n;
        }
        return true;
    }
}
