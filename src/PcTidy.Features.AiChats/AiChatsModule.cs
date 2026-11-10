using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.AiChats.Local;
using PcTidy.Features.AiChats.UI;
using PcTidy.UI.Flow;

namespace PcTidy.Features.AiChats;

/// <summary>
/// "AI chats" in the left rail (Organize group, v3; Plan §8 "11 · AI chats" with extras E and F): a private, offline
/// library of ChatGPT / Claude exports and local Claude Code / Codex chats (masked secrets, full-text search, tags,
/// pins, folders, topics), export to Obsidian, archive copies of agent chats, and the Exposed secrets report.
/// Never logs in anywhere, never uses cookies or APIs: you download the exports yourself.
/// </summary>
public sealed class AiChatsModule : IFeatureModule, IModuleRiskInfo
{
    private readonly IProcessProbe? _processes;
    private readonly TimeProvider? _clock;

    /// <param name="processes">Running-process check override (tests). Default: the real PC.</param>
    /// <param name="clock">Clock override (tests).</param>
    public AiChatsModule(IProcessProbe? processes = null, TimeProvider? clock = null)
    {
        _processes = processes;
        _clock = clock;
        Scanners = new IFeatureScanner[] { new AiChatsScanner() };
    }

    public string Id => AiChatsIds.FeatureId;
    public string Title => "AI chats";
    public string Summary => "Your ChatGPT, Claude, Claude Code and Codex chats in one private, searchable library. Keys are masked; export to Obsidian; keep copies of agent chats.";
    public NavGroup Group => NavGroup.Organize;
    public string Icon => "Chat24";
    /// <summary>Organize group: Organize · Photos · Desktop · Move to drive · AI chats.</summary>
    public int Order => 50;
    public Release Release => Release.V3;
    public ModuleRisk Risk => ModuleRisk.Safe;
    public IReadOnlyList<IFeatureScanner> Scanners { get; }

    public object? CreatePage(IServiceProvider services) => new AiChatsPage(services, ScanOptions());

    internal IReadOnlyDictionary<string, object> ScanOptions()
    {
        var o = new Dictionary<string, object>();
        if (_processes is not null) o[AiChatsOptions.ProcessProbe] = _processes;
        if (_clock is not null) o[AiChatsOptions.Clock] = _clock;
        return o;
    }
}
