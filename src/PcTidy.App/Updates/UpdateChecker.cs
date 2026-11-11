using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PcTidy.App.Updates;

public enum UpdateState { UpToDate, Available, Problem }

/// <summary>What a check found. <see cref="Offer"/> is set when a newer finished release is available.</summary>
public sealed record UpdateCheck(UpdateState State, string Message, UpdateOffer? Offer = null)
{
    public Version? NewVersion => Offer?.Version;
    public static UpdateCheck Fail(string message) => new(UpdateState.Problem, message);
}

/// <summary>A finished download: <see cref="Path"/> and its <see cref="Release"/> proof, or the plain reason it stopped.</summary>
public sealed record UpdateDownload(string? Problem, string? Path = null, VerifiedRelease? Release = null)
{
    public bool Ok => Problem is null && Path is not null && Release is not null;
}

/// <summary>
/// The updater's network part (UPD-3). It talks to PC Tidy's GitHub releases only (<see cref="ReleaseSource"/>):
/// <list type="number">
/// <item>A check is one GET of <see cref="ReleaseSource.LatestApi"/> with only two headers, <c>User-Agent: PC-Tidy/&lt;version&gt;</c>
/// and <c>Accept</c>: no cookies, no tokens, nothing about this PC or its user. Drafts, pre-releases, unreadable tags and
/// versions that aren't higher than the running one are never offered. The answer is capped (<see cref="MaxReleaseJsonBytes"/>).</item>
/// <item>A download (only after the user pressed Update now) picks the exact file for this kind of install, checks its
/// link is the release's own github.com download, fetches the same release's SHA256SUMS.txt, and hashes the file while
/// it downloads. Redirects are followed by hand, at most <see cref="MaxRedirects"/>, and only to GitHub's download hosts
/// over HTTPS. A mismatch deletes the download.</item>
/// </list>
/// Offline, a timeout or any odd answer gives a calm message, never an exception. Tests and practice/demo mode pass a
/// fake handler (<see cref="PracticeReleaseHandler"/>): no network at all.
/// </summary>
public sealed class UpdateChecker : IDisposable
{
    public const int MaxReleaseJsonBytes = 1 << 20;
    public const int MaxSumsBytes = 64 * 1024;
    public const long MaxDownloadBytes = 600L << 20;
    public const int MaxRedirects = 4;
    private const int MaxNotes = 20_000;

    public const string GitHubJson = "application/vnd.github+json";
    public const string Binary = "application/octet-stream";

    /// <summary>A check that takes longer than this is given up on.</summary>
    public static readonly TimeSpan DefaultCheckTimeout = TimeSpan.FromSeconds(20);
    /// <summary>A download that receives nothing for this long is given up on (captive Wi-Fi, a proxy holding it, sleep).</summary>
    public static readonly TimeSpan DefaultInactivityTimeout = TimeSpan.FromSeconds(30);

    public const string OfflineMessage = "PC Tidy couldn't reach GitHub to check for updates. This PC may be offline. Nothing was downloaded.";
    public const string TimeoutMessage = "GitHub didn't answer in time. Nothing was downloaded. PC Tidy tries again tomorrow, or press Check for updates.";
    public const string StalledMessage = "The download stopped answering. Nothing was installed.";
    public const string StoppedMessage = "The download was stopped. Nothing was installed.";
    public const string MismatchMessage = "The download doesn't match the checksum in the release's SHA256SUMS.txt, so it was deleted. Nothing was installed.";

    private readonly HttpMessageInvoker _http;
    private readonly HttpMessageHandler _handler;
    private readonly bool _ownsHandler;
    private readonly Version _current;
    private readonly TimeSpan _inactivity;
    private readonly TimeSpan _checkTimeout;

    /// <param name="handler">The HTTP stack (tests: a fake handler; real: <see cref="CreateHandler"/>).</param>
    /// <param name="currentVersion">The running PC Tidy's version (X.Y.Z).</param>
    public UpdateChecker(HttpMessageHandler handler, Version currentVersion, TimeSpan? inactivityTimeout = null, TimeSpan? checkTimeout = null, bool ownsHandler = false)
    {
        _handler = handler;
        _ownsHandler = ownsHandler;
        _http = new HttpMessageInvoker(handler, disposeHandler: false);
        _current = new Version(currentVersion.Major, currentVersion.Minor, Math.Max(0, currentVersion.Build));
        _inactivity = inactivityTimeout ?? DefaultInactivityTimeout;
        _checkTimeout = checkTimeout ?? DefaultCheckTimeout;
    }

    /// <summary>
    /// The checker the app uses: in demo or practice mode (<paramref name="practice"/>) a fake that never touches the
    /// network (<see cref="PracticeReleaseHandler"/>); otherwise the real HTTPS stack.
    /// </summary>
    public static UpdateChecker Create(bool practice, Version current)
        => new(practice ? new PracticeReleaseHandler(current) : CreateHandler(), current, ownsHandler: true);

    /// <summary>True when this checker can't go online (demo or practice mode, tests).</summary>
    public bool IsPractice => _handler is PracticeReleaseHandler;

    /// <summary>The whole User-Agent: the product and its version, nothing else.</summary>
    public string UserAgent => $"PC-Tidy/{ReleaseVersion.Show(_current)}";

    /// <summary>
    /// The real handler: no cookies, no credentials, no automatic decompression, and NO automatic redirects (each hop
    /// is checked against <see cref="ReleaseSource.IsRedirectTarget"/> before it is followed).
    /// </summary>
    public static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        UseCookies = false,
        Credentials = null,
        PreAuthenticate = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        MaxResponseHeadersLength = 64, // KB
    };

    // ---------------------------------------------------------------- check

    public async Task<UpdateCheck> CheckAsync(CancellationToken ct)
    {
        byte[] body;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_checkTimeout);
            using var response = await SendAsync(ReleaseSource.LatestApi, GitHubJson, timeout.Token).ConfigureAwait(false);
            if (IsRedirect(response.StatusCode)) return UpdateCheck.Fail("GitHub answered with a redirect instead of PC Tidy's release, so PC Tidy stopped. Nothing was downloaded.");
            if (response.StatusCode == HttpStatusCode.NotFound) return UpdateCheck.Fail("GitHub has no finished PC Tidy release yet. Nothing was downloaded.");
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                return UpdateCheck.Fail("GitHub asked PC Tidy to wait (too many checks from this network). Try again later. Nothing was downloaded.");
            if (!response.IsSuccessStatusCode) return UpdateCheck.Fail($"GitHub answered with an error ({(int)response.StatusCode}). Nothing was downloaded.");
            var read = await ReadLimitedAsync(response, MaxReleaseJsonBytes, timeout.Token).ConfigureAwait(false);
            if (read is null) return UpdateCheck.Fail("GitHub's answer was far too big to be a release description, so PC Tidy ignored it.");
            body = read;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return UpdateCheck.Fail(TimeoutMessage); }
        catch (OperationCanceledException) { return UpdateCheck.Fail("The update check was stopped. Nothing was downloaded."); }
        catch (Exception) { return UpdateCheck.Fail(OfflineMessage); } // offline, DNS, TLS, proxy: never a crash
        return Evaluate(body);
    }

    /// <summary>Checks GitHub's release JSON (pure: tests call it directly).</summary>
    public UpdateCheck Evaluate(ReadOnlySpan<byte> json)
    {
        GitHubRelease? r;
        try { r = JsonSerializer.Deserialize<GitHubRelease>(json); }
        catch (JsonException) { return UpdateCheck.Fail("GitHub's answer isn't a release description PC Tidy can read, so it was ignored."); }
        if (r is null) return UpdateCheck.Fail("GitHub's answer was empty, so it was ignored.");
        var mine = ReleaseVersion.Show(_current);
        if (r.Draft || r.Prerelease) return new UpdateCheck(UpdateState.UpToDate, $"You have the newest finished version ({mine}). PC Tidy never offers test releases.");
        if (!ReleaseVersion.TryParseTag(r.TagName, out var version, out bool suffixed)) return UpdateCheck.Fail("GitHub's newest release has a version PC Tidy can't read, so it was ignored.");
        // "v3.2.0-beta1" without the pre-release box ticked is still a test release (review L1).
        if (suffixed) return new UpdateCheck(UpdateState.UpToDate, $"You have the newest finished version ({mine}). PC Tidy never offers test releases.");
        if (version <= _current) return new UpdateCheck(UpdateState.UpToDate, $"You have the newest version ({mine}).");
        var assets = (r.Assets ?? new List<GitHubAsset>()).Where(a => a is not null).ToList();
        var offer = new UpdateOffer(version, r.TagName!.Trim(), PlainNotes(r.Body), assets);
        return new UpdateCheck(UpdateState.Available, $"PC Tidy {offer.VersionText} is available (you have {mine}).", offer);
    }

    /// <summary>Release notes as plain text for a TextBlock: no markup is ever rendered; control characters go, length is capped.</summary>
    public static string PlainNotes(string? body)
    {
        var sb = new StringBuilder();
        foreach (var c in (body ?? "").Replace("\r\n", "\n").Replace('\r', '\n'))
        {
            if (c == '\n' || c == '\t' || !char.IsControl(c)) sb.Append(c);
            if (sb.Length > MaxNotes) { sb.Append('…'); break; }
        }
        var text = sb.ToString().Trim();
        return text.Length == 0 ? "No changes listed." : text;
    }

    // ---------------------------------------------------------------- download

    /// <summary>
    /// Downloads this install kind's file of the offered release into <paramref name="store"/> and checks it against the
    /// release's SHA256SUMS.txt while it arrives. Any problem returns a plain message and leaves no file behind.
    /// </summary>
    public async Task<UpdateDownload> DownloadAsync(UpdateCheck check, InstallKind kind, UpdateStore store, IProgress<double>? progress, CancellationToken ct)
    {
        if (check is not { State: UpdateState.Available, Offer: { } offer } || offer.Version <= _current) return new UpdateDownload("There's no update to download.");
        var name = UpdateAssets.ExpectedName(kind, offer.Version);
        var (asset, problem) = UpdateAssets.Pick(offer, name);
        if (asset is null) return new UpdateDownload(problem);
        var (sums, _) = UpdateAssets.Pick(offer, UpdateAssets.SumsName);
        if (sums is null) return new UpdateDownload($"PC Tidy {offer.VersionText} on GitHub has no {UpdateAssets.SumsName}, so its download can't be checked. Nothing was downloaded.");
        if (!Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out var fileUrl) || !ReleaseSource.IsAssetUrl(fileUrl, name, offer.Tag)
            || !Uri.TryCreate(sums.DownloadUrl, UriKind.Absolute, out var sumsUrl) || !ReleaseSource.IsAssetUrl(sumsUrl, UpdateAssets.SumsName, offer.Tag))
            return new UpdateDownload("The release's download link isn't PC Tidy's own GitHub release, so PC Tidy didn't use it. Nothing was downloaded.");
        if (asset.Size > MaxDownloadBytes) return new UpdateDownload("The update file is far too big to be PC Tidy, so it wasn't downloaded.");

        // 1. The checksum list of the same release.
        string? expected;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_checkTimeout);
            var (response, refused) = await GetFollowingAsync(sumsUrl, Binary, timeout.Token).ConfigureAwait(false);
            if (response is null) return new UpdateDownload(refused);
            using (response)
            {
                if (!response.IsSuccessStatusCode) return new UpdateDownload($"GitHub couldn't send {UpdateAssets.SumsName} ({(int)response.StatusCode}). Nothing was installed.");
                var bytes = await ReadLimitedAsync(response, MaxSumsBytes, timeout.Token).ConfigureAwait(false);
                if (bytes is null) return new UpdateDownload($"The release's {UpdateAssets.SumsName} is far too big, so PC Tidy stopped. Nothing was installed.");
                (expected, problem) = Sha256Sums.Find(Encoding.UTF8.GetString(bytes), name);
                if (expected is null) return new UpdateDownload(problem);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new UpdateDownload("GitHub didn't send the checksum list in time. Nothing was installed."); }
        catch (OperationCanceledException) { return new UpdateDownload(StoppedMessage); }
        catch (Exception) { return new UpdateDownload("PC Tidy couldn't reach GitHub for the checksum list. Nothing was installed."); }

        // 2. The file itself, hashed while it arrives.
        var partial = store.PartialPath(name);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            idle.CancelAfter(_inactivity);
            var (response, refused) = await GetFollowingAsync(fileUrl, Binary, idle.Token).ConfigureAwait(false);
            if (response is null) return new UpdateDownload(refused);
            using (response)
            {
                if (!response.IsSuccessStatusCode) return new UpdateDownload($"The download failed ({(int)response.StatusCode}). Nothing was installed.");
                long? total = response.Content.Headers.ContentLength;
                if (total > MaxDownloadBytes) return new UpdateDownload("The update file is far too big to be PC Tidy, so it wasn't downloaded.");
                await using var source = await response.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var target = store.CreatePartial(name))
                {
                    var buf = new byte[81920];
                    long done = 0;
                    while (true)
                    {
                        idle.CancelAfter(_inactivity); // restarted for every piece: only silence counts, not a slow line
                        int n = await source.ReadAsync(buf, idle.Token).ConfigureAwait(false);
                        if (n <= 0) break;
                        done += n;
                        if (done > MaxDownloadBytes) throw new InvalidDataException("The update file is far too big to be PC Tidy.");
                        sha.AppendData(buf, 0, n);
                        await target.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                        var expectedSize = total ?? (asset.Size > 0 ? asset.Size : 0);
                        if (expectedSize > 0) progress?.Report(Math.Min(1, (double)done / expectedSize));
                    }
                }
                var actual = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
                if (actual != expected)
                {
                    store.Discard(partial);
                    return new UpdateDownload(MismatchMessage);
                }
            }
            var final = store.Complete(name);
            return new UpdateDownload(null, final, new VerifiedRelease(offer.Version, offer.Tag, name, expected!, kind));
        }
        catch (OperationCanceledException)
        {
            store.Discard(partial);
            return new UpdateDownload(ct.IsCancellationRequested ? StoppedMessage : StalledMessage);
        }
        catch (Exception ex)
        {
            store.Discard(partial);
            return new UpdateDownload("The download didn't finish" + (ex is InvalidDataException or IOException ? ": " + ex.Message : ".") + " Nothing was installed.");
        }
    }

    // ---------------------------------------------------------------- HTTP

    private static bool IsRedirect(HttpStatusCode code) => (int)code is 301 or 302 or 303 or 307 or 308;

    /// <summary>GETs <paramref name="start"/> (already checked by the caller), following only redirects to GitHub's download hosts.</summary>
    private async Task<(HttpResponseMessage? Response, string? Problem)> GetFollowingAsync(Uri start, string accept, CancellationToken ct)
    {
        var uri = start;
        for (int hop = 0; ; hop++)
        {
            var response = await SendAsync(uri, accept, ct).ConfigureAwait(false);
            if (!IsRedirect(response.StatusCode)) return (response, null);
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null) return (null, "GitHub redirected the download without saying where to, so PC Tidy stopped. Nothing was installed.");
            var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
            if (hop >= MaxRedirects) return (null, "The download was redirected too many times, so PC Tidy stopped. Nothing was installed.");
            if (!ReleaseSource.IsRedirectTarget(next))
                return (null, $"The download was sent on to {(next.Host.Length > 0 ? next.Host : "another address")}, which isn't one of GitHub's download servers (or isn't https), so PC Tidy stopped. Nothing was installed.");
            uri = next;
        }
    }

    private Task<HttpResponseMessage> SendAsync(Uri uri, string accept, CancellationToken ct)
    {
        // Exactly two headers: the product and version, and what kind of answer is wanted. Nothing about this PC or its user.
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", accept);
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

    public void Dispose()
    {
        _http.Dispose();
        if (_ownsHandler) _handler.Dispose();
    }
}
