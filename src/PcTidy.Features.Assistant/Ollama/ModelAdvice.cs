using PcTidy.Core;

namespace PcTidy.Features.Assistant.Ollama;

/// <summary>A row in the model picker.</summary>
public sealed record ModelChoice(OllamaModel Model, bool FitsTypicalGpu, string Label)
{
    public string Name => Model.Name;
}

/// <summary>
/// Picks and describes models for the picker. A 3–8B model at 4-bit is about 2–5 GB and fits an 8 GB graphics
/// card with room for the conversation; bigger ones still work but spill into slower system memory.
/// Only suggests: PC Tidy never downloads a model (the user runs "ollama pull" themselves).
/// </summary>
public static class ModelAdvice
{
    /// <summary>Models up to this size leave room on an 8 GB card for the context.</summary>
    public const long TypicalGpuFitBytes = 6L * 1024 * 1024 * 1024;

    /// <summary>Small chat models that suit an 8 GB graphics card (names as "ollama pull" takes them).</summary>
    public static readonly IReadOnlyList<(string Name, string Size)> Suggested = new[]
    {
        ("llama3.2:3b", "about 2 GB"),
        ("qwen2.5:7b", "about 4.7 GB"),
        ("llama3.1:8b", "about 4.9 GB"),
        ("gemma2:2b", "about 1.6 GB"),
    };

    public static string NoModelsText =>
        "Ollama is running but has no models yet. Small models that suit most PCs (fine on an 8 GB graphics card): " +
        string.Join(", ", Suggested.Select(s => $"{s.Name} ({s.Size})")) +
        ". To add one, open Terminal and type, for example: ollama pull llama3.2:3b. Then press Check again. PC Tidy never downloads models for you.";

    /// <summary>Chat-capable models for the picker, best fits first. Embedding-only models can't answer questions, so they're left out.</summary>
    public static IReadOnlyList<ModelChoice> Choices(IEnumerable<OllamaModel> installed)
        => installed.Where(m => !IsEmbeddingOnly(m))
            .Select(m =>
            {
                bool fits = m.SizeBytes > 0 && m.SizeBytes <= TypicalGpuFitBytes;
                var bits = new List<string> { m.Name };
                if (m.SizeBytes > 0) bits.Add(Sizes.Format(m.SizeBytes));
                bits.Add(fits ? "good fit" : m.SizeBytes > 0 ? "large: may be slow" : "size unknown");
                return new ModelChoice(m, fits, string.Join(" · ", bits));
            })
            .OrderByDescending(c => c.FitsTypicalGpu)
            .ThenByDescending(c => c.FitsTypicalGpu ? c.Model.SizeBytes : -c.Model.SizeBytes)
            .ToList();

    /// <summary>The user's saved model if it's still installed, else the biggest model that fits, else the smallest.</summary>
    public static ModelChoice? DefaultChoice(IReadOnlyList<ModelChoice> choices, string? saved)
        => choices.FirstOrDefault(c => string.Equals(c.Name, saved, StringComparison.OrdinalIgnoreCase)) ?? choices.FirstOrDefault();

    private static bool IsEmbeddingOnly(OllamaModel m)
        => m.Name.Contains("embed", StringComparison.OrdinalIgnoreCase)
           || string.Equals(m.Family, "bert", StringComparison.OrdinalIgnoreCase)
           || string.Equals(m.Family, "nomic-bert", StringComparison.OrdinalIgnoreCase);
}
