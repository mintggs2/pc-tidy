using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Apps.Scanning;
using PcTidy.Features.Apps.UI;

namespace PcTidy.Features.Apps;

/// <summary>
/// Section 4 · Apps (v2): installed desktop, Store and Steam/Epic apps with sizes and last use; uninstall through the
/// app's own uninstaller (restore point first) or its launcher; strict leftover clean-up into quarantine;
/// preinstalled Windows apps removable per user. Never part of "Optimize everything".
/// </summary>
public sealed class AppsModule : IFeatureModule
{
    public AppsModule() => Scanners = new IFeatureScanner[] { new AppsScanner() };

    public string Id => AppsFeature.Id;
    public string Title => "Apps";
    public string Summary => "See what's installed, how big it is and when you last used it. Uninstall cleanly and sweep up leftovers.";
    public NavGroup Group => NavGroup.SpeedUp;
    public string Icon => "Apps24";
    /// <summary>Plan section number 4.</summary>
    public int Order => 4;
    public Release Release => Release.V2;
    public IReadOnlyList<IFeatureScanner> Scanners { get; }

    /// <summary>Needs IKnownLocations, ISafetyGuard and IFlowHost; IFileWalker is optional.</summary>
    public object? CreatePage(IServiceProvider services) => new AppsView(new AppsViewModel(services));
}
