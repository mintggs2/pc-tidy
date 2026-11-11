using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PcTidy.App.Updates;

/// <summary>
/// Demo and practice mode's "GitHub": answers the updater's requests from memory with an invented release one version
/// above the running one (made-up notes and file contents). It never opens a connection, so practice mode uses no
/// network at all, and the Settings screens and the update notice can still be shown.
/// </summary>
public sealed class PracticeReleaseHandler : HttpMessageHandler
{
    private readonly Dictionary<string, byte[]> _bodies = new(StringComparer.Ordinal);
    private int _requests;

    public PracticeReleaseHandler(Version current)
    {
        Offered = new Version(current.Major, current.Minor, Math.Max(0, current.Build) + 1);
        var v = ReleaseVersion.Show(Offered);
        var tag = "v" + v;
        string Url(string name) => $"https://github.com{ReleaseSource.DownloadPrefix}{tag}/{name}";
        var setup = Encoding.ASCII.GetBytes("practice setup file, not a program");
        var zip = Encoding.ASCII.GetBytes("practice portable file, not a zip");
        string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
        var setupName = UpdateAssets.ExpectedName(InstallKind.Installed, Offered);
        var zipName = UpdateAssets.ExpectedName(InstallKind.Portable, Offered);
        var sums = $"{Hash(setup)}  {setupName}\n{Hash(zip)}  {zipName}\n";
        var release = new
        {
            tag_name = tag,
            name = $"PC Tidy {v}",
            draft = false,
            prerelease = false,
            body = "Sample release notes for practice mode:\n\n- Scans of big Downloads folders finish sooner.\n- Clearer wording on the confirm screen.\n- A fix for the History filter.",
            assets = new[]
            {
                new { name = setupName, browser_download_url = Url(setupName), size = (long)setup.Length },
                new { name = zipName, browser_download_url = Url(zipName), size = (long)zip.Length },
                new { name = UpdateAssets.SumsName, browser_download_url = Url(UpdateAssets.SumsName), size = (long)sums.Length },
            },
        };
        _bodies[ReleaseSource.LatestApi.AbsoluteUri] = JsonSerializer.SerializeToUtf8Bytes(release);
        _bodies[Url(setupName)] = setup;
        _bodies[Url(zipName)] = zip;
        _bodies[Url(UpdateAssets.SumsName)] = Encoding.UTF8.GetBytes(sums);
    }

    /// <summary>The invented version this fake offers.</summary>
    public Version Offered { get; }

    /// <summary>How many requests were answered (all from memory).</summary>
    public int Requests => _requests;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Interlocked.Increment(ref _requests);
        var response = request.RequestUri is { } u && _bodies.TryGetValue(u.AbsoluteUri, out var body)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body), RequestMessage = request }
            : new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };
        return Task.FromResult(response);
    }
}
