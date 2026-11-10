using System.Reflection;

namespace PcTidy.Setup.Payload;

/// <summary>
/// The payload's SHA-256 as compiled INTO this program (build-release.ps1 publishes the download Setup with
/// <c>-p:PcTidyPayloadSha256=&lt;hex&gt;</c>, which becomes an assembly attribute).
/// <para>
/// Why: the hash in the payload trailer sits in the same file as the payload, so whoever swaps the file writes a
/// matching trailer too. The copy compiled into the assembly is read from the program image Windows started (and the
/// user approved in the admin prompt); renaming the file away and putting another one in its place doesn't change the
/// running image. The admin part installs only a payload whose bytes match this value.
/// </para>
/// </summary>
public static class PayloadTrust
{
    public const string MetadataKey = "PcTidyPayloadSha256";

    /// <summary>The compiled-in hash (64 lower-case hex digits), or null for a developer build or uninstall.exe.</summary>
    public static byte[]? Embedded { get; } = Read(typeof(PayloadTrust).Assembly);

    internal static byte[]? Read(Assembly assembly)
    {
        var value = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == MetadataKey)?.Value;
        return Parse(value);
    }

    /// <summary>64 hex digits → 32 bytes; anything else → null.</summary>
    public static byte[]? Parse(string? hex)
    {
        if (hex is not { Length: 64 } || !hex.All(Uri.IsHexDigit)) return null;
        return Convert.FromHexString(hex);
    }

    /// <summary>
    /// True when the payload's trailer names the compiled-in hash (always false when nothing is compiled in).
    /// A mismatch means the payload isn't the one this Setup was built with.
    /// </summary>
    public static bool TrailerMatchesEmbedded(PayloadInfo payload, byte[]? embedded = null)
        => (embedded ?? Embedded) is { } e && e.AsSpan().SequenceEqual(payload.Sha256);
}
