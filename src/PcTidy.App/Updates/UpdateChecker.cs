using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcTidy.App.Updates;

/// <summary>
/// The release feed's small JSON file: <c>{ "version": "1.2.0", "url": "https://…/PC Tidy Setup.exe",
/// "sha256": "64 hex digits", "changelog": "…", "page": "https://… (optional release page)" }</c>.
/// The checksum comes from the same server as the link, so it only proves the download arrived intact; who made the
/// installer is decided by its signature (<see cref="UpdateTrust"/>).
/// </summary>
public sealed record UpdateManifest
{
    [JsonPropertyName("version")] public string? Version { get; init; }
    [JsonPropertyName("url")] public string? Url { get; init; }
    [JsonPropertyName("sha256")] public string? Sha256 { get; init; }
    [JsonPropertyName("changelog")] public string? Changelog { get; init; }
    [JsonPropertyName("page")] public string? Page { get; init; }

    /// <summary>Where the user can get the update themselves: the release page if the feed names a plain https one, else the download link.</summary>
    public Uri? ReleasePage()
    {
        foreach (var candidate in new[] { Page, Url })
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(u.UserInfo)) return u;
        return null;
    }
}

public enum UpdateState { UpToDate, Available, Problem }

/// <summary>What "Check now" found. <see cref="Manifest"/> and <see cref="NewVersion"/> are set when an update is available.</summary>
public sealed record UpdateCheck(UpdateState State, string Message, UpdateManifest? Manifest = null, Version? NewVersion = null)
{
    public static UpdateCheck Fail(string message) => new(UpdateState.Problem, message);
}

/// <summary>
/// Self-update, opt-in: nothing happens unless the user typed a release feed URL in Settings. "Check now" fetches
/// only that small JSON file over HTTPS (no cookies, no PC Tidy-specific headers, nothing about this PC), checks it
/// strictly (HTTPS links only, a real SHA-256, never an older or the same version), and after the user's OK
/// downloads the installer and verifies its SHA-256 before anything may run it.
/// </summary>
public sealed class UpdateChecker
{
    public const int MaxManifestBytes = 64 * 1024;
    public const long MaxInstallerBytes = 400L << 20;
    private const int MaxChangelog = 20_000;

    /// <summary>A download that receives nothing for this long is given up on (captive Wi-Fi, a proxy holding it, sleep).</summary>
    public static readonly TimeSpan DefaultInactivityTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpMessageInvoker _http;
    private readonly Version _current;
    private readonly TimeSpan _inactivity;

    /// <param name="handler">The HTTP stack (tests: a local fake handler; real: <see cref="CreateHandler"/>).</param>
    /// <param name="inactivityTimeout">How long a download may go without receiving anything (tests: short).</param>
    public UpdateChecker(HttpMessageHandler handler, Version currentVersion, TimeSpan? inactivityTimeout = null)
    {
        _http = new HttpMessageInvoker(handler, disposeHandler: false);
        _current = currentVersion;
        _inactivity = inactivityTimeout ?? DefaultInactivityTimeout;
    }

    public const string StalledMessage = "The download stopped answering. Nothing was installed.";
    public const string StoppedMessage = "The download was stopped. Nothing was installed.";

    /// <summary>The real handler: no cookies, no credentials, no automatic decompression surprises, HTTPS redirects only (at most 3).</summary>
    public static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        UseCookies = false,
        Credentials = null,
        PreAuthenticate = false,
        AllowAutoRedirect = true, // .NET never follows an https → http redirect
        MaxAutomaticRedirections = 3,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(15),
    };

    /// <summary>Only an absolute https:// URL to a host name is accepted (no http, file, ftp, or user:password@).</summary>
    public static string? FeedProblem(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "Type the release feed address first.";
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)) return "That isn't a web address.";
        if (u.Scheme != Uri.UriSchemeHttps) return "Only https:// addresses are allowed, so nobody can change the update on the way.";
        if (!string.IsNullOrEmpty(u.UserInfo)) return "The address can't contain a user name or password.";
        return null;
    }

    public async Task<UpdateCheck> CheckAsync(string feedUrl, CancellationToken ct)
    {
        if (FeedProblem(feedUrl) is { } bad) return UpdateCheck.Fail(bad);
        byte[] body;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var response = await SendAsync(new Uri(feedUrl.Trim()), timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return UpdateCheck.Fail($"The release feed answered with an error ({(int)response.StatusCode}). Nothing was downloaded.");
            if (response.RequestMessage?.RequestUri is { Scheme: not "https" }) return UpdateCheck.Fail("The release feed redirected to a non-https address, so PC Tidy stopped.");
            body = await ReadLimitedAsync(response, MaxManifestBytes, timeout.Token).ConfigureAwait(false)
                   ?? throw new InvalidDataException("The release feed's file is too big to be an update description.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return UpdateCheck.Fail("The release feed didn't answer in time."); }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException)
        {
            return UpdateCheck.Fail("PC Tidy couldn't read the release feed: " + ex.Message);
        }
        return Evaluate(body);
    }

    /// <summary>Checks a downloaded manifest (pure: tests call it directly).</summary>
    public UpdateCheck Evaluate(ReadOnlySpan<byte> json)
    {
        UpdateManifest? m;
        try { m = JsonSerializer.Deserialize<UpdateManifest>(json); }
        catch (JsonException) { return UpdateCheck.Fail("The release feed's file isn't a valid update description."); }
        if (m is null) return UpdateCheck.Fail("The release feed's file is empty.");
        if (!TryParseVersion(m.Version, out var version)) return UpdateCheck.Fail("The release feed doesn't say which version it offers.");
        if (version == _current) return new UpdateCheck(UpdateState.UpToDate, $"You have the newest version ({Show(_current)}).");
        if (version < _current) return UpdateCheck.Fail($"The release feed offers an older version ({Show(version)}). PC Tidy never goes back to an older version this way.");
        if (!Uri.TryCreate(m.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(url.UserInfo))
            return UpdateCheck.Fail("The update's download link isn't a plain https:// address, so PC Tidy won't use it.");
        if (!url.AbsolutePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return UpdateCheck.Fail("The update's download link doesn't point to an installer.");
        if (m.Sha256 is not { Length: 64 } hash || !hash.All(Uri.IsHexDigit)) return UpdateCheck.Fail("The update has no valid checksum (SHA-256), so it can't be verified.");
        var changelog = (m.Changelog ?? "").Trim();
        if (changelog.Length > MaxChangelog) changelog = changelog[..MaxChangelog] + "…";
        return new UpdateCheck(UpdateState.Available, $"Version {Show(version)} is available (you have {Show(_current)}).",
            m with { Sha256 = hash.ToLowerInvariant(), Changelog = changelog.Length == 0 ? "No changes listed." : changelog }, version);
    }

    /// <summary>
    /// Downloads the installer to <paramref name="destination"/> (via a ".partial" file), verifying its SHA-256 on the
    /// fly. A wrong checksum deletes the partial download and fails. Writes go through <see cref="UpdateStore"/>.
    /// A connection that goes quiet for <see cref="DefaultInactivityTimeout"/> (the timer restarts after every piece
    /// received) or a Stop (<paramref name="ct"/>) ends it the same way: partial file thrown away, a plain message back.
    /// </summary>
    public async Task<string?> DownloadAsync(UpdateCheck check, UpdateStore store, IProgress<double>? progress, CancellationToken ct)
    {
        if (check is not { State: UpdateState.Available, Manifest: { } m, NewVersion: { } v }) return "There's no update to download.";
        var partial = store.PartialPath(v);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            idle.CancelAfter(_inactivity);
            using var response = await SendAsync(new Uri(m.Url!), idle.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return $"The download failed ({(int)response.StatusCode}). Nothing was installed.";
            if (response.RequestMessage?.RequestUri is { Scheme: not "https" }) return "The download was redirected to a non-https address, so PC Tidy stopped.";
            long? total = response.Content.Headers.ContentLength;
            if (total > MaxInstallerBytes) return "The installer is far too big to be PC Tidy.";
            await using var source = await response.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var target = store.CreatePartial(v))
            {
                var buf = new byte[81920];
                long done = 0;
                int n;
                while (true)
                {
                    idle.CancelAfter(_inactivity); // restarted for every piece: only silence counts, not a slow line
                    n = await source.ReadAsync(buf, idle.Token).ConfigureAwait(false);
                    if (n <= 0) break;
                    done += n;
                    if (done > MaxInstallerBytes) throw new InvalidDataException("The installer is far too big to be PC Tidy.");
                    sha.AppendData(buf, 0, n);
                    await target.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    if (total > 0) progress?.Report((double)done / total.Value);
                }
            }
            var actual = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            if (actual != m.Sha256)
            {
                store.Discard(partial);
                return "The download doesn't match the release's checksum, so it was thrown away. Nothing was installed.";
            }
            store.Complete(v);
            return null;
        }
        catch (OperationCanceledException)
        {
            store.Discard(partial);
            return ct.IsCancellationRequested ? StoppedMessage : StalledMessage;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            store.Discard(partial);
            return "The download didn't finish: " + ex.Message + " Nothing was installed.";
        }
    }

    private Task<HttpResponseMessage> SendAsync(Uri uri, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        // A generic agent string and nothing else: no version, no machine or user details, no cookies.
        request.Headers.UserAgent.ParseAdd("PCTidy-Updater");
        return _http.SendAsync(request, ct);
    }

    private static async Task<byte[]?> ReadLimitedAsync(HttpResponseMessage response, int max, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > max) return null;
        await using var s = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buf = new byte[8192];
        int n;
        while ((n = await s.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + n > max) return null;
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }

    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(text) || !Version.TryParse(text.Trim(), out var v) || v.Major < 0) return false;
        version = Normalize(v);
        return true;
    }

    /// <summary>1.2 and 1.2.0 are the same version.</summary>
    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

    private static string Show(Version v) => v.Revision > 0 ? v.ToString(4) : v.ToString(3);
}
