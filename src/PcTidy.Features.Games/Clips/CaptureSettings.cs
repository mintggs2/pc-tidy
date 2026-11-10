using System.Text;
using PcTidy.Features.Games.Common;

namespace PcTidy.Features.Games.Clips;

/// <summary>NVIDIA ShadowPlay / NVIDIA App save folder: NVSPCAPS\DefaultPathW is a REG_BINARY holding a UTF-16LE path.</summary>
public static class NvidiaCapturePath
{
    /// <summary>Decodes the value: UTF-16LE, stops at the first NUL, tolerates odd lengths and stray padding. Null if unusable.</summary>
    public static string? Decode(byte[]? value)
    {
        if (value is not { Length: >= 2 }) return null;
        var length = value.Length - (value.Length % 2);
        var text = Encoding.Unicode.GetString(value, 0, length);
        var nul = text.IndexOf('\0');
        if (nul >= 0) text = text[..nul];
        text = text.Trim().Trim('"').Trim();
        if (text.Length < 3 || text.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;
        try { return Path.IsPathFullyQualified(text) ? Path.GetFullPath(text) : null; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}

/// <summary>OBS Studio recording folders from each profile's basic.ini ([SimpleOutput] FilePath, [AdvOut] RecFilePath).</summary>
public static class ObsConfig
{
    public static IReadOnlyList<string> RecordingFolders(string basicIni)
    {
        var list = new List<string>();
        foreach (var (section, key) in new[] { ("SimpleOutput", "FilePath"), ("AdvOut", "RecFilePath"), ("AdvOut", "FFFilePath") })
        {
            var value = IniText.Get(basicIni, section, key);
            if (string.IsNullOrWhiteSpace(value)) continue;
            // OBS writes forward slashes ("C:/Users/x/Videos").
            var path = value.Replace('/', '\\').Trim().Trim('"');
            try
            {
                if (!Path.IsPathFullyQualified(path)) continue;
                var full = Path.GetFullPath(path);
                list.Add(full.Length > 3 ? full.TrimEnd('\\') : full);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
