using System.Net;
using System.Net.Http;
using PcTidy.Features.Assistant.Ollama;
using Xunit;

namespace PcTidy.Features.Assistant.Tests;

/// <summary>The helper only ever talks to this PC: fixed 127.0.0.1, every address re-checked, no redirects.</summary>
public class LoopbackTests
{
    [Fact]
    public void Endpoint_is_always_127_0_0_1_and_only_the_port_can_change()
    {
        Assert.Equal(new Uri("http://127.0.0.1:11434/"), new OllamaEndpoint().BaseUri);
        Assert.Equal(new Uri("http://127.0.0.1:11500/api/tags"), new OllamaEndpoint(11500).For("api/tags"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OllamaEndpoint(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OllamaEndpoint(70000));
        // No constructor or property takes a host: the type has exactly one int constructor parameter.
        var ctor = Assert.Single(typeof(OllamaEndpoint).GetConstructors(), c => c.GetParameters().Length == 1);
        Assert.Equal(typeof(int), ctor.GetParameters()[0].ParameterType);
        Assert.DoesNotContain(typeof(OllamaEndpoint).GetProperties(), p => p.CanWrite);
    }

    [Theory]
    [InlineData("http://127.0.0.1:11434/api/version")]
    [InlineData("http://127.8.9.10:11434/")]
    [InlineData("http://[::1]:11434/")]
    [InlineData("http://[::ffff:127.0.0.1]:11434/")]
    public async Task Loopback_addresses_are_allowed(string url)
    {
        var resolver = new FakeResolver();
        var verdict = await new LoopbackGuard(resolver).CheckAsync(new Uri(url), CancellationToken.None);
        Assert.True(verdict.Allowed, verdict.Why);
        Assert.Empty(resolver.Asked); // address literals never need a lookup
    }

    [Theory]
    [InlineData("http://192.168.1.20:11434/")]
    [InlineData("http://10.0.0.5:11434/")]
    [InlineData("http://0.0.0.0:11434/")]
    [InlineData("http://[::]:11434/")]
    [InlineData("http://93.184.216.34/")]
    [InlineData("http://[2001:db8::1]:11434/")]
    public async Task Other_addresses_are_refused(string url)
    {
        var verdict = await new LoopbackGuard(new FakeResolver()).CheckAsync(new Uri(url), CancellationToken.None);
        Assert.False(verdict.Allowed);
        Assert.False(string.IsNullOrWhiteSpace(verdict.Why));
    }

    [Fact]
    public async Task Example_com_is_refused_after_resolving()
    {
        var resolver = new FakeResolver().Map("example.com", "93.184.216.34");
        var verdict = await new LoopbackGuard(resolver).CheckAsync(new Uri("http://example.com/"), CancellationToken.None);
        Assert.False(verdict.Allowed);
        Assert.Contains("93.184.216.34", verdict.Why);
    }

    [Fact]
    public async Task Localhost_is_judged_by_what_it_resolves_to()
    {
        var good = new LoopbackGuard(new FakeResolver().Map("localhost", "127.0.0.1", "::1"));
        Assert.True((await good.CheckAsync(new Uri("http://localhost:11434/"), CancellationToken.None)).Allowed);

        var hijacked = new LoopbackGuard(new FakeResolver().Map("localhost", "203.0.113.5"));
        Assert.False((await hijacked.CheckAsync(new Uri("http://localhost:11434/"), CancellationToken.None)).Allowed);

        // A mixed answer is refused outright, not filtered down to the loopback part.
        var mixed = new LoopbackGuard(new FakeResolver().Map("localhost", "127.0.0.1", "192.168.1.9"));
        Assert.False((await mixed.CheckAsync(new Uri("http://localhost:11434/"), CancellationToken.None)).Allowed);
        await Assert.ThrowsAsync<LoopbackRefusedException>(() => mixed.ResolveLoopbackOnlyAsync("localhost", CancellationToken.None));
    }

    [Theory]
    [InlineData("https://127.0.0.1:11434/")]
    [InlineData("ftp://127.0.0.1/")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("http://user:secret@127.0.0.1:11434/")]
    public async Task Only_plain_http_without_credentials_is_allowed(string url)
    {
        var verdict = await new LoopbackGuard(new FakeResolver()).CheckAsync(new Uri(url), CancellationToken.None);
        Assert.False(verdict.Allowed);
    }

    [Fact]
    public async Task Unknown_hosts_are_refused_without_a_real_lookup()
    {
        var resolver = new FakeResolver();
        var verdict = await new LoopbackGuard(resolver).CheckAsync(new Uri("http://ollama.internal:11434/"), CancellationToken.None);
        Assert.False(verdict.Allowed);
        Assert.Equal(new[] { "ollama.internal" }, resolver.Asked);
    }

    [Theory]
    [InlineData("http://example.com/api/version")]
    [InlineData("http://192.168.1.20:11434/api/version")]
    [InlineData("http://localhost:11434/api/version")]
    public async Task The_handler_stops_off_PC_requests_before_they_reach_the_transport(string url)
    {
        var fake = new FakeOllama();
        var resolver = new FakeResolver().Map("example.com", "93.184.216.34").Map("localhost", "198.51.100.7");
        using var http = new HttpClient(new LoopbackOnlyHandler(new LoopbackGuard(resolver), fake));
        await Assert.ThrowsAsync<LoopbackRefusedException>(() => http.GetAsync(url));
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task Redirects_are_never_followed()
    {
        var fake = new FakeOllama { RedirectTo = "http://example.com/steal" };
        using var client = new OllamaClient(transport: fake, resolver: new FakeResolver());
        var ex = await Assert.ThrowsAsync<LoopbackRefusedException>(() => client.GetVersionAsync(CancellationToken.None));
        Assert.Contains("redirect", ex.Message);
        Assert.Single(fake.Requests); // the redirect target was never asked
        Assert.Equal("127.0.0.1", fake.Requests[0].Uri.Host);
    }

    [Fact]
    public async Task The_probe_reports_a_refusal_instead_of_treating_it_as_not_installed()
    {
        using var folder = new TestFolder("refused");
        var fake = new FakeOllama { RedirectTo = "http://10.0.0.1/" };
        using var client = new OllamaClient(transport: fake);
        var status = await new OllamaProbe(client, new PcTidy.Core.SandboxKnownLocations(folder.Root)).CheckAsync(CancellationToken.None);
        Assert.Equal(OllamaState.Refused, status.State);
    }

    [Fact]
    public void The_real_transport_uses_no_proxy_and_never_redirects()
    {
        using var transport = OllamaTransport.Create(new LoopbackGuard(new FakeResolver()));
        Assert.False(transport.UseProxy);
        Assert.Null(transport.Proxy);
        Assert.False(transport.AllowAutoRedirect);
        Assert.False(transport.UseCookies);
        Assert.Null(transport.Credentials);
        Assert.NotNull(transport.ConnectCallback);
        Assert.True(transport.ConnectTimeout <= TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void IPv4_mapped_addresses_are_judged_by_their_IPv4_part()
    {
        Assert.True(LoopbackGuard.IsLoopback(IPAddress.Parse("::ffff:127.0.0.1")));
        Assert.False(LoopbackGuard.IsLoopback(IPAddress.Parse("::ffff:192.168.1.1")));
        Assert.False(LoopbackGuard.IsLoopback(IPAddress.Any));
        Assert.False(LoopbackGuard.IsLoopback(IPAddress.IPv6Any));
    }
}
