using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.SpaceMap.Actions;
using PcTidy.Features.SpaceMap.View;

namespace PcTidy.Features.SpaceMap;

/// <summary>Left-rail entry for Space map (plan §8.2): custom page + the Big-and-old scanner for Optimize-all.</summary>
public sealed class SpaceMapModule : IFeatureModule
{
    public SpaceMapModule() => Scanners = new IFeatureScanner[] { new SpaceMapScanner() };

    public string Id => SpaceActions.FeatureId;
    public string Title => "Space map";
    public string Summary => "See what fills each drive, and find big files you haven't touched in months.";
    public NavGroup Group => NavGroup.CleanUp;
    public string Icon => "DataTreemap24";
    /// <summary>Right after Clean (10) in the Clean-up group.</summary>
    public int Order => 20;
    public Release Release => Release.V1;
    public IReadOnlyList<IFeatureScanner> Scanners { get; }

    /// <summary>Needs IKnownLocations, ISafetyGuard, IFileWalker, AppSettings and IFlowHost; ISystemInfo is optional.</summary>
    public object? CreatePage(IServiceProvider services) => new SpaceMapView(services);
}
