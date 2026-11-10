using System.Net;

namespace PcTidy.Features.Assistant.Ollama;

/// <summary>
/// Reads (never changes) the OLLAMA_HOST variable that tells Ollama where to listen. Two uses:
/// its port tells PC Tidy where to find Ollama, and a non-loopback bind ("0.0.0.0") means other devices on the
/// network can use this PC's Ollama, which the page warns about. PC Tidy itself still only talks to 127.0.0.1.
/// </summary>
public sealed record OllamaHostSetting(string? RawValue, int? Port, string? NetworkWarning)
{
    public const string VariableName = "OLLAMA_HOST";

    /// <summary>Process, then user, then machine environment (read-only). Tests pass their own reader.</summary>
    public static OllamaHostSetting Read(Func<string, string?>? environment = null)
    {
        var read = environment ?? ReadAllScopes;
        return Parse(read(VariableName));
    }

    public static OllamaHostSetting Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return new OllamaHostSetting(null, null, null);
        var raw = value.Trim();
        var (host, port) = Split(raw);
        string? warning = IsLoopbackHost(host) ? null
            : $"Ollama is set to listen on {(IsAnyAddress(host) ? "your whole network" : host)} ({VariableName}={raw}). " +
              "Other devices on your network may be able to use it. PC Tidy still only talks to it on this PC. " +
              $"To keep Ollama private, remove {VariableName} or set it to 127.0.0.1 in Windows' environment variables.";
        return new OllamaHostSetting(raw, port, warning);
    }

    /// <summary>"0.0.0.0", "0.0.0.0:11434", "http://0.0.0.0:11434", "[::]:11434", ":11500", "localhost".</summary>
    private static (string Host, int? Port) Split(string raw)
    {
        var text = raw;
        var scheme = text.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) text = text[(scheme + 3)..];
        var slash = text.IndexOf('/');
        if (slash >= 0) text = text[..slash];

        string host = text;
        int? port = null;
        if (text.StartsWith('['))
        {
            var close = text.IndexOf(']');
            host = close > 0 ? text[1..close] : text.Trim('[');
            if (close > 0 && close + 1 < text.Length && text[close + 1] == ':') port = ParsePort(text[(close + 2)..]);
        }
        else if (text.Count(c => c == ':') == 1)
        {
            var colon = text.IndexOf(':');
            host = text[..colon];
            port = ParsePort(text[(colon + 1)..]);
        }
        return (host.Trim(), port);
    }

    private static int? ParsePort(string s) => int.TryParse(s, out var p) && p is > 0 and <= 65535 ? p : null;

    /// <summary>Empty means Ollama's default, 127.0.0.1.</summary>
    private static bool IsLoopbackHost(string host)
        => host.Length == 0
           || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
           || (IPAddress.TryParse(host, out var ip) && LoopbackGuard.IsLoopback(ip));

    private static bool IsAnyAddress(string host)
        => IPAddress.TryParse(host, out var ip) && (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any));

    private static string? ReadAllScopes(string name)
    {
        foreach (var target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            try
            {
                var v = Environment.GetEnvironmentVariable(name, target);
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
            catch (System.Security.SecurityException) { /* not readable: try the next scope */ }
        }
        return null;
    }
}
