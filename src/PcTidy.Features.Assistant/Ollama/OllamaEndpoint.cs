namespace PcTidy.Features.Assistant.Ollama;

/// <summary>
/// Where Ollama listens. The address is hard-coded to 127.0.0.1 (this PC); only the port can be configured.
/// There is deliberately no way to give it a host name or another address.
/// </summary>
public sealed record OllamaEndpoint
{
    public const int DefaultPort = 11434;
    public const string LoopbackAddress = "127.0.0.1";

    public OllamaEndpoint(int port = DefaultPort)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port), port, "A port is a number from 1 to 65535.");
        Port = port;
    }

    public int Port { get; }

    public Uri BaseUri => new($"http://{LoopbackAddress}:{Port}/");

    /// <summary>An API path on this PC's Ollama, e.g. "api/tags".</summary>
    public Uri For(string apiPath) => new(BaseUri, apiPath);
}
