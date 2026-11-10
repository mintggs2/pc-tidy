using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PcTidy.Features.Assistant.Ollama;

/// <summary>A model installed in Ollama (from /api/tags).</summary>
public sealed record OllamaModel(string Name, long SizeBytes, string? ParameterSize = null, string? Family = null, string? Quantization = null);

/// <summary>One chat message sent to Ollama ("system", "user" or "assistant").</summary>
public sealed record OllamaChatMessage(string Role, string Content);

/// <summary>Ollama isn't reachable or answered with an error. The page shows the message as is.</summary>
public sealed class OllamaUnavailableException : Exception
{
    public OllamaUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Talks to Ollama on this PC, and only on this PC: base address fixed to 127.0.0.1 (<see cref="OllamaEndpoint"/>),
/// every request checked by <see cref="LoopbackOnlyHandler"/>, no proxy, no redirects, short timeouts, capped answers.
/// Only reads (/api/version, /api/tags) and /api/chat with <c>stream:false</c> and a JSON-schema <c>format</c>.
/// It never pulls, deletes or changes models.
/// </summary>
public sealed class OllamaClient : IDisposable
{
    public static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(3);
    /// <summary>A first answer can take a while: the model has to load into the graphics card.</summary>
    public static readonly TimeSpan DefaultChatTimeout = TimeSpan.FromSeconds(120);
    private const int MaxResponseBytes = 4 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly TimeSpan _probeTimeout;
    private readonly TimeSpan _chatTimeout;

    /// <param name="transport">Test seam: a fake HTTP handler. It still sits behind the loopback check. Default: <see cref="OllamaTransport"/>.</param>
    /// <param name="resolver">Test seam for host lookups. Default: DNS (never needed for the hard-coded 127.0.0.1).</param>
    public OllamaClient(OllamaEndpoint? endpoint = null, HttpMessageHandler? transport = null, IHostResolver? resolver = null,
        TimeSpan? probeTimeout = null, TimeSpan? chatTimeout = null)
    {
        Endpoint = endpoint ?? new OllamaEndpoint();
        var guard = new LoopbackGuard(resolver);
        _http = new HttpClient(new LoopbackOnlyHandler(guard, transport ?? OllamaTransport.Create(guard)))
        {
            Timeout = Timeout.InfiniteTimeSpan, // per-call timeouts below, so the caller's Stop button still works
            MaxResponseContentBufferSize = MaxResponseBytes,
        };
        _probeTimeout = probeTimeout ?? DefaultProbeTimeout;
        _chatTimeout = chatTimeout ?? DefaultChatTimeout;
    }

    public OllamaEndpoint Endpoint { get; }

    /// <summary>Ollama's version ("0.12.3"). Throws <see cref="OllamaUnavailableException"/> when it isn't running.</summary>
    public async Task<string> GetVersionAsync(CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, "api/version", null, _probeTimeout, ct).ConfigureAwait(false);
        return doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "unknown";
    }

    /// <summary>Installed models, in the order Ollama lists them. Read-only.</summary>
    public async Task<IReadOnlyList<OllamaModel>> ListModelsAsync(CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, "api/tags", null, _probeTimeout, ct).ConfigureAwait(false);
        var list = new List<OllamaModel>();
        if (!doc.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array) return list;
        foreach (var m in models.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object) continue;
            var name = Str(m, "name") ?? Str(m, "model");
            if (string.IsNullOrWhiteSpace(name)) continue;
            long size = m.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
            string? parameters = null, family = null, quant = null;
            if (m.TryGetProperty("details", out var d) && d.ValueKind == JsonValueKind.Object)
            {
                parameters = Str(d, "parameter_size");
                family = Str(d, "family");
                quant = Str(d, "quantization_level");
            }
            list.Add(new OllamaModel(name, size, parameters, family, quant));
        }
        return list;
    }

    /// <summary>
    /// One non-streaming chat turn constrained by a JSON schema (<paramref name="format"/>). Returns the model's
    /// raw message content: untrusted text the caller must parse and validate.
    /// </summary>
    public async Task<string> ChatAsync(string model, IReadOnlyList<OllamaChatMessage> messages, JsonNode format, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(messages.Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content }).ToArray()),
            ["stream"] = false,
            ["format"] = format.DeepClone(),
            // Low temperature: answers about the user's own files should be factual, not creative.
            ["options"] = new JsonObject { ["temperature"] = 0.2, ["num_ctx"] = 8192 },
            ["keep_alive"] = "5m",
        };
        using var doc = await SendAsync(HttpMethod.Post, "api/chat", body, _chatTimeout, ct).ConfigureAwait(false);
        if (doc.RootElement.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object && Str(msg, "content") is { } content)
            return content;
        throw new OllamaUnavailableException("Ollama sent an empty answer. Try asking again.");
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, JsonNode? body, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var request = new HttpRequestMessage(method, Endpoint.For(path));
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new OllamaUnavailableException(FriendlyError((int)response.StatusCode, text));
            return JsonDocument.Parse(text);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new OllamaUnavailableException(path == "api/chat"
                ? "The model took too long to answer. A smaller model answers faster."
                : "Ollama didn't answer in time.");
        }
        catch (HttpRequestException ex) when (ex.InnerException is LoopbackRefusedException refused)
        {
            throw refused; // raised inside the connect callback: same refusal, not "not running"
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError)
        {
            throw new OllamaUnavailableException("Ollama isn't running on this PC.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new OllamaUnavailableException("Ollama's answer couldn't be read (" + ex.Message + ").", ex);
        }
        catch (JsonException)
        {
            throw new OllamaUnavailableException("Ollama's answer couldn't be read.");
        }
    }

    /// <summary>Ollama errors look like {"error":"model 'x' not found"}.</summary>
    private static string FriendlyError(int status, string body)
    {
        string? error = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            error = doc.RootElement.ValueKind == JsonValueKind.Object ? Str(doc.RootElement, "error") : null;
        }
        catch (JsonException) { /* not JSON: fall back to the status */ }
        if (status == 404 && error?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true)
            return "That model isn't installed in Ollama any more. Pick another one.";
        if (string.IsNullOrWhiteSpace(error)) return $"Ollama answered with an error ({status}).";
        error = error.Trim();
        return "Ollama said: " + (error.Length > 200 ? error[..200] + "…" : error);
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public void Dispose() => _http.Dispose();
}
