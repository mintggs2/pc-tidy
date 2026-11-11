using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;

namespace PcTidy.Features.PcCare.Source;

/// <summary>
/// The lag check's network layer, injectable so tests use a fake server (no real traffic ever leaves a test).
/// Read-only: it only measures; nothing on this PC changes.
/// </summary>
public interface ILagCheckTransport
{
    /// <summary>One tiny timing request. Returns the round-trip time, or null when there was no answer in time.</summary>
    Task<TimeSpan?> PingAsync(TimeSpan timeout, CancellationToken ct);

    /// <summary>Downloads up to <paramref name="bytes"/> bytes and throws them away, reporting each block received.</summary>
    Task DownloadAsync(long bytes, Action<int> onBytes, CancellationToken ct);

    /// <summary>Uploads <paramref name="bytes"/> blank bytes (the server throws them away), reporting each block sent.</summary>
    Task UploadAsync(long bytes, Action<int> onBytes, CancellationToken ct);
}

/// <summary>
/// HTTPS transport for a <see cref="LagCheckEndpoint"/>: GET download?bytes=N, POST upload. The timing request is
/// GET download?bytes=0, timed to the response headers. No cookies, no redirects, no compression, a generic user agent.
/// </summary>
public sealed class HttpLagCheckTransport : ILagCheckTransport, IDisposable
{
    private const int BlockSize = 64 * 1024;
    private readonly LagCheckEndpoint _endpoint;
    private readonly HttpClient _http;

    /// <param name="handler">Tests pass a fake handler; the default is a fresh SocketsHttpHandler.</param>
    public HttpLagCheckTransport(LagCheckEndpoint endpoint, HttpMessageHandler? handler = null)
    {
        if (!endpoint.IsValid) throw new ArgumentException("The lag check endpoint must be https, with both URLs on one host.", nameof(endpoint));
        _endpoint = endpoint;
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            MaxConnectionsPerServer = 8,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(10),
        }, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan, // every call has its own timeout and cancel
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("PCTidy-LagCheck/1.0");
    }

    internal Uri DownloadUri(long bytes) => new UriBuilder(_endpoint.DownloadUrl) { Query = "bytes=" + bytes.ToString(CultureInfo.InvariantCulture) }.Uri;

    public async Task<TimeSpan?> PingAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, DownloadUri(0));
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true, NoStore = true };
        var sw = Stopwatch.StartNew();
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            sw.Stop();
            return response.IsSuccessStatusCode ? sw.Elapsed : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; } // no answer in time
        catch (HttpRequestException) { return null; }
    }

    public async Task DownloadAsync(long bytes, Action<int> onBytes, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, DownloadUri(bytes));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[BlockSize];
        long left = bytes;
        while (left > 0)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), ct).ConfigureAwait(false);
            if (n == 0) break;
            left -= n;
            onBytes(n);
        }
    }

    public async Task UploadAsync(long bytes, Action<int> onBytes, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint.UploadUrl) { Content = new BlankContent(bytes, onBytes) };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public void Dispose() => _http.Dispose();

    /// <summary>A request body of N zero bytes, written in blocks so progress and cancel work. Nothing from this PC is in it.</summary>
    internal sealed class BlankContent : HttpContent
    {
        private readonly long _length;
        private readonly Action<int> _onBytes;

        public BlankContent(long length, Action<int> onBytes)
        {
            _length = length;
            _onBytes = onBytes;
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            var block = new byte[BlockSize];
            long left = _length;
            while (left > 0)
            {
                int n = (int)Math.Min(block.Length, left);
                await stream.WriteAsync(block.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                left -= n;
                _onBytes(n);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _length;
            return true;
        }
    }
}
