using PcTidy.Core;

namespace PcTidy.Features.Assistant.Ollama;

public enum OllamaState
{
    /// <summary>Ollama answered on 127.0.0.1.</summary>
    Running,
    /// <summary>ollama.exe is on this PC but nothing answered.</summary>
    InstalledNotRunning,
    /// <summary>No Ollama on this PC yet: "Ollama isn't installed yet" (it's a free app, not something this PC lacks).</summary>
    NotInstalled,
    /// <summary>A request was refused by the loopback rule (should never happen with the fixed address).</summary>
    Refused,
}

public sealed record OllamaStatus(OllamaState State, string? Version, IReadOnlyList<OllamaModel> Models, string? Problem)
{
    public bool IsRunning => State == OllamaState.Running;
}

/// <summary>
/// Finds out whether the local AI helper can work on this PC, read-only: asks Ollama on 127.0.0.1 for its
/// version and models, and when nothing answers looks for ollama.exe in the usual install folders.
/// Never installs, starts or downloads anything.
/// </summary>
public sealed class OllamaProbe
{
    private readonly OllamaClient _client;
    private readonly IKnownLocations _locations;

    public OllamaProbe(OllamaClient client, IKnownLocations locations)
    {
        _client = client;
        _locations = locations;
    }

    public const string NotAvailableTitle = "Ollama isn't installed yet";

    /// <summary>The one command the steps ask you to type (offered as a Copy button).</summary>
    public const string PullCommand = "ollama pull llama3.2:3b";

    /// <summary>Shown when Ollama isn't installed. Plain text only: PC Tidy never downloads it for you.</summary>
    public const string InstallInstructions =
        "Ask PC Tidy uses Ollama, a free app that runs AI models on your own PC, so nothing leaves it.\n" +
        "1. Get Ollama from ollama.com and install it.\n" +
        "2. Open Terminal and type: ollama pull llama3.2:3b (a small model, about 2 GB).\n" +
        "3. Come back here and press Check again.\n" +
        "PC Tidy never downloads or installs anything for you.";

    public const string NotRunningInstructions =
        "Ollama is installed but isn't running. Start Ollama from the Start menu, then press Check again.";

    public async Task<OllamaStatus> CheckAsync(CancellationToken ct)
    {
        try
        {
            var version = await _client.GetVersionAsync(ct).ConfigureAwait(false);
            var models = await _client.ListModelsAsync(ct).ConfigureAwait(false);
            return new OllamaStatus(OllamaState.Running, version, models, null);
        }
        catch (LoopbackRefusedException ex)
        {
            return new OllamaStatus(OllamaState.Refused, null, Array.Empty<OllamaModel>(), ex.Message);
        }
        catch (OllamaUnavailableException ex)
        {
            var state = IsInstalled() ? OllamaState.InstalledNotRunning : OllamaState.NotInstalled;
            return new OllamaStatus(state, null, Array.Empty<OllamaModel>(), ex.Message);
        }
    }

    /// <summary>The installer's per-user folder, or a machine-wide install. File.Exists only: nothing is opened.</summary>
    public bool IsInstalled() => InstallCandidates().Any(p =>
    {
        try { return File.Exists(p); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    });

    private IEnumerable<string> InstallCandidates()
    {
        if (!string.IsNullOrEmpty(_locations.LocalAppData))
            yield return Path.Combine(_locations.LocalAppData, "Programs", "Ollama", "ollama.exe");
        if (!string.IsNullOrEmpty(_locations.ProgramFiles))
            yield return Path.Combine(_locations.ProgramFiles, "Ollama", "ollama.exe");
    }
}
