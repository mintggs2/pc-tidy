namespace PcTidy.Catalog.PcCare;

/// <summary>
/// Search engines and start pages that are normal to see as a browser's search or home page. Anything else isn't wrong,
/// just worth a look ("Is this what you chose?"); search hijackers swap these for their own ad-filled pages.
/// </summary>
public static class KnownSearchEngines
{
    /// <summary>Registrable domains (a host matches when it is the domain or ends with "." + domain).</summary>
    public static readonly IReadOnlyList<string> Domains = new[]
    {
        "google.com", "bing.com", "duckduckgo.com", "yahoo.com", "yahoo.co.jp", "ecosia.org", "search.brave.com", "brave.com",
        "startpage.com", "qwant.com", "yandex.ru", "yandex.com", "baidu.com", "naver.com", "seznam.cz", "ya.ru", "msn.com",
        "microsoft.com", "mozilla.org", "kagi.com", "you.com", "perplexity.ai", "chatgpt.com",
    };

    /// <summary>Google's country sites (google.de, google.co.uk...).</summary>
    private static bool IsGoogleCountrySite(string host)
    {
        var labels = host.Split('.');
        int g = Array.LastIndexOf(labels, "google");
        return g >= 0 && labels.Length - g is 2 or 3 && labels[(g + 1)..].All(l => l.Length is >= 2 and <= 3);
    }

    public static bool IsKnownHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        var h = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (h is "localhost") return true;
        return IsGoogleCountrySite(h) || Domains.Any(d => h == d || h.EndsWith("." + d, StringComparison.Ordinal));
    }

    /// <summary>Browser-internal pages (new tab, settings) are never a hijack.</summary>
    public static bool IsBrowserInternal(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return true;
        var u = url.Trim();
        return u.StartsWith("chrome://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("edge://", StringComparison.OrdinalIgnoreCase)
            || u.StartsWith("brave://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
            || u.StartsWith("chrome-search://", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True for a URL whose host is a well-known engine, or a browser page. Built-in engine templates that start with a
    /// placeholder ("{google:baseURL}", "{bing:baseURL}") are the browser's own and count as known.
    /// </summary>
    public static bool IsKnownUrl(string? url)
    {
        if (IsBrowserInternal(url)) return true;
        var u = url!.Trim();
        if (u.StartsWith('{')) return true;
        u = u.Replace("{searchTerms}", "q", StringComparison.OrdinalIgnoreCase);
        if (!u.Contains("://", StringComparison.Ordinal)) u = "https://" + u;
        return Uri.TryCreate(u, UriKind.Absolute, out var uri) && IsKnownHost(uri.Host);
    }
}
