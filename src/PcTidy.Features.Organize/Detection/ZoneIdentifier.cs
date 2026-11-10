namespace PcTidy.Features.Organize.Detection;

/// <summary>The "Mark of the Web" Windows attaches to downloads (the Zone.Identifier stream).</summary>
public sealed record ZoneInfo(int? ZoneId, string? HostUrl, string? ReferrerUrl)
{
    /// <summary>Host names this file came from (download host first, then the page that linked it).</summary>
    public IEnumerable<string> Hosts()
    {
        foreach (var url in new[] { HostUrl, ReferrerUrl })
            if (ZoneIdentifier.HostOf(url) is { } host) yield return host;
    }
}

/// <summary>Reads (never writes) the Zone.Identifier alternate data stream.</summary>
public static class ZoneIdentifier
{
    private const int MaxBytes = 16 * 1024;

    /// <summary>Null when the file has no download mark, or it couldn't be read. Never call for cloud placeholders.</summary>
    public static ZoneInfo? Read(string path)
    {
        string text;
        try
        {
            using var fs = new FileStream(path + ":Zone.Identifier", FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            var buf = new char[MaxBytes];
            text = new string(buf, 0, reader.ReadBlock(buf, 0, buf.Length));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return null;
        }
        return Parse(text);
    }

    public static ZoneInfo Parse(string text)
    {
        int? zone = null; string? host = null, referrer = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (key.Equals("ZoneId", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var z)) zone = z;
            else if (key.Equals("HostUrl", StringComparison.OrdinalIgnoreCase)) host = value;
            else if (key.Equals("ReferrerUrl", StringComparison.OrdinalIgnoreCase)) referrer = value;
        }
        return new ZoneInfo(zone, host, referrer);
    }

    /// <summary>"https://dl.example.com/x.zip" → "dl.example.com"; also accepts a bare host the user typed.</summary>
    public static string? HostOf(string? urlOrHost)
    {
        if (string.IsNullOrWhiteSpace(urlOrHost)) return null;
        var s = urlOrHost.Trim();
        if (Uri.TryCreate(s, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)) return uri.Host.ToLowerInvariant();
        if (Uri.TryCreate("https://" + s, UriKind.Absolute, out uri) && !string.IsNullOrEmpty(uri.Host)) return uri.Host.ToLowerInvariant();
        return null;
    }

    /// <summary>"github.com" matches "github.com" and "codeload.github.com", not "notgithub.com".</summary>
    public static bool HostMatches(string host, string site)
    {
        var want = HostOf(site);
        if (want is null) return false;
        if (want.StartsWith("www.", StringComparison.Ordinal)) want = want[4..];
        return host.Equals(want, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + want, StringComparison.OrdinalIgnoreCase);
    }
}
