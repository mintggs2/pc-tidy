using System.Text;
using PcTidy.Core;

namespace PcTidy.Features.Insights.Scripts;

/// <summary>
/// Opens a script the user picked, read-only and as text (byte-order marks detected, so UTF-16 .reg files read correctly).
/// Refuses OneDrive online-only placeholders (reading one would download it) and reads at most <see cref="MaxBytes"/>.
/// </summary>
public static class ScriptFileReader
{
    public const int MaxBytes = 2 * 1024 * 1024;

    public sealed record Result(string? Text, bool Truncated, string? Problem);

    public static async Task<Result> ReadAsync(string path, CancellationToken ct)
    {
        if (ScriptAnalyzer.LanguageOf(path) is null)
            return new Result(null, false, "PC Tidy reads .ps1, .bat, .cmd, .reg, .vbs and .js files.");
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return new Result(null, false, "That file isn't there any more.");
            if (CloudFile.IsPlaceholder(info.Attributes))
                return new Result(null, false, "This file is online-only in OneDrive. Open it once so it downloads, then scan it.");

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true);
            var limit = (int)Math.Min(stream.Length, MaxBytes);
            var buffer = new byte[limit];
            int read = 0;
            while (read < limit)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(read, limit - read), ct).ConfigureAwait(false);
                if (n == 0) break;
                read += n;
            }
            return new Result(Decode(buffer.AsSpan(0, read)), stream.Length > MaxBytes, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Result(null, false, "PC Tidy couldn't read that file (it may be open in another program or blocked).");
        }
    }

    /// <summary>Text from bytes: BOM first (UTF-8 / UTF-16 LE/BE), then UTF-16 without BOM (regedit exports), else UTF-8.</summary>
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes[3..]);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes[2..]);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        if (bytes.Length >= 4 && bytes[1] == 0 && bytes[3] == 0 && bytes[0] != 0) return Encoding.Unicode.GetString(bytes);
        return Encoding.UTF8.GetString(bytes);
    }
}
