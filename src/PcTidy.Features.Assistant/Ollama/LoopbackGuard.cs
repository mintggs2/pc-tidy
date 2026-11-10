using System.Net;
using System.Net.Sockets;

namespace PcTidy.Features.Assistant.Ollama;

/// <summary>Turns a host name into addresses. Real: DNS (only ever asked about the hard-coded 127.0.0.1). Tests: a fake.</summary>
public interface IHostResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct);
}

public sealed class DnsHostResolver : IHostResolver
{
    public static readonly DnsHostResolver Instance = new();

    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => Dns.GetHostAddressesAsync(host, ct);
}

public sealed record LoopbackVerdict(bool Allowed, string? Why)
{
    public static readonly LoopbackVerdict Ok = new(true, null);
    public static LoopbackVerdict Refused(string why) => new(false, why);
}

/// <summary>
/// The one rule of the local AI helper's networking: every address it talks to must be this PC.
/// A URI is allowed only if it is plain http, has no user info, and its host is (or resolves only to)
/// loopback addresses, checked with <see cref="IPAddress.IsLoopback"/> after resolving. Anything else is refused.
/// Used twice: on every request URI (<see cref="LoopbackOnlyHandler"/>) and again on the socket's actual
/// connect address (<see cref="OllamaTransport"/>), so even a host name that changes its answer can't escape.
/// </summary>
public sealed class LoopbackGuard
{
    private readonly IHostResolver _resolver;

    public LoopbackGuard(IHostResolver? resolver = null) => _resolver = resolver ?? DnsHostResolver.Instance;

    public async Task<LoopbackVerdict> CheckAsync(Uri? uri, CancellationToken ct)
    {
        if (uri is null || !uri.IsAbsoluteUri) return LoopbackVerdict.Refused("The address isn't complete.");
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            return LoopbackVerdict.Refused($"Only plain http to this PC is allowed, not {uri.Scheme}.");
        if (!string.IsNullOrEmpty(uri.UserInfo)) return LoopbackVerdict.Refused("Addresses with a user name or password aren't allowed.");
        try
        {
            await ResolveLoopbackOnlyAsync(uri.DnsSafeHost, ct).ConfigureAwait(false);
            return LoopbackVerdict.Ok;
        }
        catch (LoopbackRefusedException ex)
        {
            return LoopbackVerdict.Refused(ex.Message);
        }
    }

    /// <summary>
    /// The host's addresses, if every one of them is loopback. Throws <see cref="LoopbackRefusedException"/>
    /// if the host can't be resolved or ANY address points off this PC (a mixed answer is refused, not filtered).
    /// </summary>
    public async Task<IPAddress[]> ResolveLoopbackOnlyAsync(string host, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(host)) throw new LoopbackRefusedException("The address has no host.");
        IPAddress[] addresses;
        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal)) addresses = new[] { literal };
        else
        {
            try { addresses = await _resolver.ResolveAsync(host, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is SocketException or ArgumentException)
            {
                throw new LoopbackRefusedException($"\"{host}\" couldn't be looked up, so PC Tidy didn't connect.");
            }
        }
        if (addresses.Length == 0) throw new LoopbackRefusedException($"\"{host}\" has no address, so PC Tidy didn't connect.");
        foreach (var a in addresses)
            if (!IsLoopback(a))
                throw new LoopbackRefusedException($"\"{host}\" points to {a}, which isn't this PC. The helper only talks to 127.0.0.1.");
        return addresses.Select(Unmap).ToArray();
    }

    /// <summary>Loopback, including IPv4-mapped forms such as ::ffff:127.0.0.1.</summary>
    public static bool IsLoopback(IPAddress address) => IPAddress.IsLoopback(Unmap(address));

    private static IPAddress Unmap(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;
}

/// <summary>A request was aimed (or redirected) somewhere that isn't this PC. Never retried.</summary>
public sealed class LoopbackRefusedException : Exception
{
    public LoopbackRefusedException(string message) : base(message) { }
}
