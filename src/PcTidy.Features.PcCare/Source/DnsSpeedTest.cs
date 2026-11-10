using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using PcTidy.Features.PcCare.Model;
using PcTidy.Features.PcCare.Parsing;

namespace PcTidy.Features.PcCare.Source;

/// <summary>
/// Read-only DNS speed test: sends ~10 single-question UDP lookups to each server (the current ones plus Cloudflare,
/// Google and Quad9) and reports the median answer time. Nothing is changed: the result is a suggestion only.
/// </summary>
internal static class DnsSpeedTest
{
    internal static readonly IReadOnlyList<(string Ip, string Label)> PublicServers = new[]
    {
        ("1.1.1.1", "Cloudflare"),
        ("8.8.8.8", "Google"),
        ("9.9.9.9", "Quad9"),
    };

    private static readonly string[] Names =
    {
        "microsoft.com", "wikipedia.org", "github.com", "bing.com", "amazon.com",
        "cloudflare.com", "mozilla.org", "apple.com", "netflix.com", "steampowered.com",
    };

    private static readonly TimeSpan PerQueryTimeout = TimeSpan.FromMilliseconds(1500);

    public static async Task<IReadOnlyList<DnsServerResult>> RunAsync(IReadOnlyList<string> current, CancellationToken ct)
    {
        var servers = current.Take(2).Select(ip => (Ip: ip, Label: PublicServers.FirstOrDefault(p => p.Ip == ip).Label ?? "Your DNS", Current: true)).ToList();
        foreach (var (ip, label) in PublicServers)
            if (!servers.Any(s => s.Ip == ip)) servers.Add((ip, label, false));

        var tasks = servers.Select(s => TimeServerAsync(s.Ip, s.Label, s.Current, ct)).ToList();
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static async Task<DnsServerResult> TimeServerAsync(string ip, string label, bool isCurrent, CancellationToken ct)
    {
        if (!IPAddress.TryParse(ip, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            return new DnsServerResult(ip, label, isCurrent, null, 0, 0);

        var timings = new List<double>();
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Connect(new IPEndPoint(address, 53));
        ushort id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        foreach (var name in Names)
        {
            ct.ThrowIfCancellationRequested();
            id++;
            var query = DnsPacket.BuildQuery(id, name);
            var sw = Stopwatch.StartNew();
            try
            {
                await udp.SendAsync(query, ct).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(PerQueryTimeout);
                while (true)
                {
                    var reply = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                    if (DnsPacket.IsAnswer(reply.Buffer, id)) { timings.Add(sw.Elapsed.TotalMilliseconds); break; }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* no answer in time */ }
            catch (SocketException) { /* unreachable: counts as no answer */ }
        }
        return new DnsServerResult(ip, label, isCurrent, DnsPacket.Median(timings), timings.Count, Names.Length);
    }
}
