using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.DriveMove.Common;
using PcTidy.Features.DriveMove.UI;
using PcTidy.UI.Flow;

namespace PcTidy.Features.DriveMove;

/// <summary>
/// "Move to drive" in the left rail (Organize group, v2; Plan §8 "10 · Move to another drive"): drive balance, big folders
/// with a link left behind, Windows user folders through Windows' own Location setting, games through their launchers,
/// and app caches through each tool's own setting. Read-only until the user confirms through the shell (IFlowHost).
/// </summary>
public sealed class DriveMoveModule : IFeatureModule, IModuleRiskInfo
{
    public string Id => DriveMoveIds.FeatureId;
    public string Title => DriveMoveIds.Title;
    public string Summary => "See how full each drive is and move big folders, Windows user folders, games and app caches to a drive with room, each the supported way.";
    public NavGroup Group => NavGroup.Organize;
    public string Icon => "ArrowSwap24";
    public int Order => 40;
    public Release Release => Release.V2;
    /// <summary>Every move is the user's own pick (nothing is ever ticked for them).</summary>
    public ModuleRisk Risk => ModuleRisk.Check;
    public IReadOnlyList<IFeatureScanner> Scanners { get; } = new IFeatureScanner[] { new DriveMoveScanner() };

    public object? CreatePage(IServiceProvider services) => new DriveMovePage(services);
}
