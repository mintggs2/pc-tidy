using System.Text.Json;
using PcTidy.Core;
using PcTidy.Features.Assistant.Ollama;

namespace PcTidy.Features.Assistant.Engine;

/// <summary>What the page shows for one question.</summary>
/// <param name="Text">Plain English, already cleaned (<see cref="AnswerSanitizer"/>).</param>
/// <param name="Suggestions">Validated "Review in … →" buttons. Each only opens a preview.</param>
/// <param name="Rejected">Why parts of the model's reply were dropped (for tests and diagnostics; not shown).</param>
/// <param name="UsedScanResults">False when there was no scan this session, so the answer only knew drive space.</param>
public sealed record AssistantAnswer(string Text, IReadOnlyList<ValidatedSuggestion> Suggestions, IReadOnlyList<string> Rejected, bool UsedScanResults);

/// <summary>
/// Answers a question from PC Tidy's own latest scan results with the local model:
/// build the data block → ask Ollama (127.0.0.1 only) for JSON → clean the text → validate every suggestion
/// against the allow-list and the real cached scan. Model output is never executed, and it can only point at
/// items the data block gave a handle to. Nothing is saved anywhere.
/// </summary>
public sealed class AssistantEngine
{
    public const string EmptyAnswer = "The helper didn't say anything useful. Try asking in other words.";

    private readonly OllamaClient _client;
    private readonly IScanResultsCache _cache;
    private readonly ISystemInfo? _system;
    private readonly Func<string, string>? _sectionTitle;

    /// <param name="sectionTitle">Feature id → the section's name in the rail ("Clean"); default: the scan result's title.</param>
    public AssistantEngine(OllamaClient client, IScanResultsCache cache, ISystemInfo? system = null, Func<string, string>? sectionTitle = null)
    {
        _client = client;
        _cache = cache;
        _system = system;
        _sectionTitle = sectionTitle;
    }

    /// <summary>The data block a question would be asked with right now (the page shows "no scan yet" from it).</summary>
    public ScanDataBlock BuildData() => ScanDataBlock.Build(_cache, _system, _sectionTitle);

    public async Task<AssistantAnswer> AskAsync(string model, string question, IReadOnlyList<ChatTurn> history, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("Pick a model first.", nameof(model));
        var data = BuildData();
        var messages = AssistantPrompt.BuildMessages(data, history, question);
        var content = await _client.ChatAsync(model, messages, AssistantPrompt.ReplySchema, ct).ConfigureAwait(false);
        return Interpret(content, data);
    }

    /// <summary>Turns the model's raw reply into what the page may show. Public so tests can feed hostile replies directly.</summary>
    public AssistantAnswer Interpret(string content, ScanDataBlock data)
    {
        string? answer = null;
        var report = new ValidationReport(Array.Empty<ValidatedSuggestion>(), Array.Empty<string>());
        try
        {
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("answer", out var a) && a.ValueKind == JsonValueKind.String) answer = a.GetString();
                var suggestions = doc.RootElement.TryGetProperty("suggestions", out var s) ? s : default;
                report = ReplyValidator.Validate(suggestions, data, _cache, _sectionTitle);
            }
            else report = report with { Rejected = new[] { "reply is not a JSON object" } };
        }
        catch (JsonException)
        {
            // Not JSON despite the schema: show it as text (cleaned like any answer), offer no buttons.
            answer = content;
            report = report with { Rejected = new[] { "reply is not JSON" } };
        }
        var text = AnswerSanitizer.Clean(answer, data.KnownPaths);
        return new AssistantAnswer(text.Length == 0 ? EmptyAnswer : text, report.Suggestions, report.Rejected, data.HasScanResults);
    }
}
