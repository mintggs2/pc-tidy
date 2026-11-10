using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace PcTidy.Features.Assistant.Ollama;

/// <summary>
/// Outermost handler of the helper's HttpClient: refuses any request whose address isn't this PC before it is
/// sent, and refuses every redirect answer (3xx) instead of following it. Sits in front of any transport,
/// including the fakes the tests use, so the rule is tested exactly as it runs.
/// </summary>
public sealed class LoopbackOnlyHandler : DelegatingHandler
{
    private readonly LoopbackGuard _guard;

    public LoopbackOnlyHandler(LoopbackGuard guard, HttpMessageHandler inner) : base(inner) => _guard = guard;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var verdict = await _guard.CheckAsync(request.RequestUri, ct).ConfigureAwait(false);
        if (!verdict.Allowed) throw new LoopbackRefusedException(verdict.Why!);

        var response = await base.SendAsync(request, ct).ConfigureAwait(false);
        if ((int)response.StatusCode is >= 300 and <= 399)
        {
            var target = response.Headers.Location?.ToString() ?? "another address";
            response.Dispose();
            throw new LoopbackRefusedException($"Ollama tried to send PC Tidy to {target}. PC Tidy never follows redirects.");
        }
        return response;
    }
}

/// <summary>
/// The real transport: no proxy (a system proxy would see the scan data), no redirects, no cookies or
/// credentials, short connect timeout, and a connect callback that checks the socket's address again.
/// This is <see cref="SocketsHttpHandler"/>, which <c>HttpClientHandler</c> wraps on .NET 8 with the same
/// <c>UseProxy</c>/<c>AllowAutoRedirect</c> settings; only it exposes <c>ConnectCallback</c>.
/// </summary>
public static class OllamaTransport
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    public static SocketsHttpHandler Create(LoopbackGuard guard) => new()
    {
        UseProxy = false,
        Proxy = null,
        AllowAutoRedirect = false,
        UseCookies = false,
        Credentials = null,
        PreAuthenticate = false,
        ConnectTimeout = ConnectTimeout,
        AutomaticDecompression = DecompressionMethods.None,
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        ConnectCallback = async (context, ct) =>
        {
            var addresses = await guard.ResolveLoopbackOnlyAsync(context.DnsEndPoint.Host, ct).ConfigureAwait(false);
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct).ConfigureAwait(false);
                if (socket.RemoteEndPoint is IPEndPoint remote && !LoopbackGuard.IsLoopback(remote.Address))
                    throw new LoopbackRefusedException($"The connection ended up at {remote.Address}, which isn't this PC.");
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };
}
