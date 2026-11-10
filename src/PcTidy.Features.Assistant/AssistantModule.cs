using System.Net.Http;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Assistant.UI;
using PcTidy.UI.Flow;

namespace PcTidy.Features.Assistant;

/// <summary>
/// "Ask PC Tidy" in the left rail (You group, v3; Plan "Across the whole app" → Local AI helper): questions such as
/// "Why is my C: drive full?" answered from PC Tidy's own latest scan by a local model in Ollama, only if Ollama is
/// installed, only over 127.0.0.1, off by default. Answers can only offer "Review in Clean →" buttons that open a
/// section's preview with items pre-selected; the user still confirms there. The module scans nothing itself.
/// </summary>
public sealed class AssistantModule : IFeatureModule, IModuleRiskInfo
{
    public const string ModuleId = AssistantSettings.FeatureKey;

    private readonly HttpMessageHandler? _transport;
    private readonly Func<string, string?>? _environment;

    /// <param name="transport">Test seam: fake Ollama HTTP handler (still behind the loopback check). Default: the real loopback transport.</param>
    /// <param name="environment">Test seam for reading OLLAMA_HOST. Default: the real environment, read-only.</param>
    public AssistantModule(HttpMessageHandler? transport = null, Func<string, string?>? environment = null)
    {
        _transport = transport;
        _environment = environment;
    }

    public string Id => ModuleId;
    public string Title => "Ask PC Tidy";
    public string Summary => "Ask why a drive is full or what's safe to clean. A local AI model answers from PC Tidy's own scan. Nothing leaves this PC.";
    public NavGroup Group => NavGroup.You;
    /// <summary>WPF-UI 4.3 SymbolRegular name (checked by a test against the icon font).</summary>
    public string Icon => "ChatSparkle24";
    /// <summary>First in the You group (Plan §3): it's the way into the others. Then Learned (10) · Schedule &amp; reports (20) · Privacy sweep (30) · Notifications (40).</summary>
    public int Order => 5;
    public Release Release => Release.V3;
    /// <summary>It never changes anything itself; the sections it opens show their own badges.</summary>
    public ModuleRisk Risk => ModuleRisk.Safe;
    public IReadOnlyList<IFeatureScanner> Scanners { get; } = Array.Empty<IFeatureScanner>();

    public object? CreatePage(IServiceProvider services) => new AssistantPage(services, _transport, _environment);
}
