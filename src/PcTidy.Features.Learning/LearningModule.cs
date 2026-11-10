using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Learning.Advisor;
using PcTidy.Features.Learning.UI;
using PcTidy.UI.Flow;

namespace PcTidy.Features.Learning;

/// <summary>
/// Section 9 · Learns from me: the "What PC Tidy has learned" page. Learning only changes defaults, order and
/// suggestions; nothing learned ever acts on its own. Everything stays in &lt;DataRoot&gt;\learning\learned.db.
/// </summary>
public sealed class LearningModule : IFeatureModule, IModuleRiskInfo
{
    public const string FeatureId = "learned";

    private readonly object _gate = new();
    private LearningAdvisor? _ownAdvisor;

    public string Id => FeatureId;
    public string Title => "Learned";
    public string Summary => "What PC Tidy learned from your choices. See, change or delete any of it.";
    public NavGroup Group => NavGroup.You;
    public string Icon => "Lightbulb24";
    public int Order => 10;
    public Release Release => Release.V3;
    public ModuleRisk Risk => ModuleRisk.Safe;
    public IReadOnlyList<IFeatureScanner> Scanners { get; } = Array.Empty<IFeatureScanner>();

    public object? CreatePage(IServiceProvider services)
        => new LearnedView(new LearnedViewModel(services, AdvisorFor(services)));

    /// <summary>The advisor the shell registered, or one this module makes (and keeps) when none was registered.</summary>
    public LearningAdvisor AdvisorFor(IServiceProvider services)
    {
        if (services.GetService(typeof(ILearningAdvisor)) is LearningAdvisor registered) return registered;
        lock (_gate) return _ownAdvisor ??= LearningAdvisor.Create(services);
    }
}
