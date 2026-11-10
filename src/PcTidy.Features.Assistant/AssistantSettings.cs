using PcTidy.Core;

namespace PcTidy.Features.Assistant;

/// <summary>
/// What "Ask PC Tidy" remembers, stored with <c>AppSettings.SetFeature("assistant", …)</c>; the shell saves settings.
/// Off by default: nothing talks to Ollama until the user turns the helper on.
/// </summary>
public sealed class AssistantSettings
{
    public const string FeatureKey = "assistant";

    /// <summary>The user switched the helper on (Settings or the page's own switch). Default off.</summary>
    public bool Enabled { get; set; }

    /// <summary>The Ollama model the user picked (a name from /api/tags). Null = pick a good fit from what's installed.</summary>
    public string? Model { get; set; }

    /// <summary>
    /// Ollama's port on this PC. Null = the port from the OLLAMA_HOST variable, else 11434. Only the port can be
    /// changed: the address is always 127.0.0.1.
    /// </summary>
    public int? Port { get; set; }

    public static AssistantSettings From(AppSettings settings)
    {
        try { return settings.GetFeature<AssistantSettings>(FeatureKey) ?? new AssistantSettings(); }
        catch (System.Text.Json.JsonException) { return new AssistantSettings(); } // a hand-edited blob never stops the page
    }

    public void Store(AppSettings settings) => settings.SetFeature(FeatureKey, this);
}
