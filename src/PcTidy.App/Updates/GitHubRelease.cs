using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PcTidy.App.Updates;

/// <summary>
/// Where updates come from: PC Tidy's own GitHub releases, hard-coded. Nothing in settings, an imported settings file or
/// a server answer can point the updater anywhere else. The only addresses PC Tidy ever asks are
/// <see cref="LatestApi"/> and the release's own asset downloads (<see cref="IsAssetUrl"/>), plus the hosts GitHub
/// redirects those downloads to (<see cref="IsRedirectTarget"/>). HTTPS only, default port, no user:password@.
/// </summary>
public static class ReleaseSource
{
    public const string Owner = "mintggs2";
    public const string Repo = "pc-tidy";

    /// <summary>GitHub's "latest release" (never a draft or a pre-release): the one request a check makes.</summary>
    public static readonly Uri LatestApi = new("https://api.github.com/repos/mintggs2/pc-tidy/releases/latest");

    /// <summary>Where people can see every release (shown in messages; opened only after the user asks).</summary>
    public static readonly Uri ReleasesPage = new("https://github.com/mintggs2/pc-tidy/releases");

    /// <summary>Path prefix of every asset download on github.com: /mintggs2/pc-tidy/releases/download/&lt;tag&gt;/&lt;name&gt;.</summary>
    public const string DownloadPrefix = "/mintggs2/pc-tidy/releases/download/";

    /// <summary>
    /// The hosts github.com redirects a release download to (a short-lived signed link). Since 2025 GitHub serves
    /// release assets from release-assets.githubusercontent.com; older links used objects.githubusercontent.com.
    /// verify: GitHub doesn't document this list; it was checked against GitHub community reports (2025-2026). If GitHub
    /// adds another host, downloads stop with a calm message and nothing is installed.
    /// </summary>
    public static readonly IReadOnlyList<string> RedirectHosts = new[] { "release-assets.githubusercontent.com", "objects.githubusercontent.com" };

    private static bool PlainHttps(Uri? u) => u is { IsAbsoluteUri: true } && u.Scheme == Uri.UriSchemeHttps && u.IsDefaultPort && string.IsNullOrEmpty(u.UserInfo);

    /// <summary>
    /// The release's own download link for exactly <paramref name="expectedName"/>:
    /// <c>https://github.com/mintggs2/pc-tidy/releases/download/&lt;tag&gt;/&lt;expectedName&gt;</c>, with no query or fragment.
    /// When <paramref name="tag"/> is given, the tag in the link must be that release's tag.
    /// </summary>
    public static bool IsAssetUrl(Uri? u, string expectedName, string? tag = null)
    {
        if (!PlainHttps(u) || !string.Equals(u!.Host, "github.com", StringComparison.OrdinalIgnoreCase)) return false;
        if (u.Query.Length > 0 || u.Fragment.Length > 0) return false;
        var path = u.AbsolutePath;
        if (!path.StartsWith(DownloadPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        var rest = path[DownloadPrefix.Length..].Split('/');
        if (rest.Length != 2 || rest[0].Length == 0 || rest[0] is "." or ".." || rest[0].Contains('%')) return false;
        if (tag is not null && !string.Equals(rest[0], tag, StringComparison.Ordinal)) return false;
        return string.Equals(rest[1], expectedName, StringComparison.Ordinal);
    }

    /// <summary>A hop of a download's redirect chain: GitHub's asset hosts, or github.com's release downloads again.</summary>
    public static bool IsRedirectTarget(Uri? u)
    {
        if (!PlainHttps(u)) return false;
        if (RedirectHosts.Any(h => string.Equals(u!.Host, h, StringComparison.OrdinalIgnoreCase))) return true;
        return string.Equals(u!.Host, "github.com", StringComparison.OrdinalIgnoreCase) && u.AbsolutePath.StartsWith(DownloadPrefix, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Release tags are <c>vX.Y.Z</c>. A tag with any suffix (<c>v3.2.0-beta1</c>, <c>v3.2.0-rc.1</c>, <c>v3.2.0+build.7</c>)
/// is read but counts as a pre-release and is never offered, even when GitHub's "pre-release" box wasn't ticked: else
/// a beta would be offered as 3.2.0 and the real 3.2.0 never (3.1.1 review L1). Anything else is refused.
/// </summary>
public static class ReleaseVersion
{
    private static readonly Regex Tag = new(@"^[vV]?(\d{1,5})\.(\d{1,5})\.(\d{1,5})(?<suffix>[-+][0-9A-Za-z.\-+]{0,40})?$", RegexOptions.CultureInvariant);

    /// <summary>Reads X.Y.Z from a tag; <paramref name="preRelease"/> is true when the tag has a suffix.</summary>
    public static bool TryParseTag(string? tag, out Version version, out bool preRelease)
    {
        version = new Version(0, 0, 0);
        preRelease = false;
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var m = Tag.Match(tag.Trim());
        if (!m.Success) return false;
        version = new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
        preRelease = m.Groups["suffix"].Success;
        return true;
    }

    /// <summary>Reads X.Y.Z from a tag (a suffix is allowed here; use the other overload to know about it).</summary>
    public static bool TryParseTag(string? tag, out Version version) => TryParseTag(tag, out version, out _);

    /// <summary>The running PC Tidy's version ("3.1.1", "3.1.1-beta+abc", "3.1.1.0") as X.Y.Z.</summary>
    public static Version Current(string? informational)
    {
        var text = (informational ?? "").Split('+')[0].Split('-')[0].Trim();
        if (!Version.TryParse(text, out var v)) return new Version(0, 0, 0);
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    public static string Show(Version v) => new Version(v.Major, v.Minor, Math.Max(0, v.Build)).ToString(3);
}

/// <summary>The parts of GitHub's release JSON the updater reads (everything else is ignored).</summary>
public sealed record GitHubRelease
{
    [JsonPropertyName("tag_name")] public string? TagName { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("body")] public string? Body { get; init; }
    [JsonPropertyName("draft")] public bool Draft { get; init; }
    [JsonPropertyName("prerelease")] public bool Prerelease { get; init; }
    [JsonPropertyName("assets")] public List<GitHubAsset>? Assets { get; init; }
}

public sealed record GitHubAsset
{
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("browser_download_url")] public string? DownloadUrl { get; init; }
    [JsonPropertyName("size")] public long Size { get; init; }
}

/// <summary>A finished release, newer than the running PC Tidy, as the check found it (release notes as plain text).</summary>
public sealed record UpdateOffer(Version Version, string Tag, string Notes, IReadOnlyList<GitHubAsset> Assets)
{
    public string VersionText => ReleaseVersion.Show(Version);
}
