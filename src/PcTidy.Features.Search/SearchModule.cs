using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Search.UI;
using PcTidy.UI.Flow;

namespace PcTidy.Features.Search;

/// <summary>
/// "Search" in the left rail (Organize group, v3; Plan §8 "7 · Search and navigation"): instant search through
/// Everything when it's running, else PC Tidy's own index; PC Tidy's own Favorites; and suggested folder structures
/// with a guided, undoable move. Searching only reads; the only changes are ones the user confirms through the shell.
/// </summary>
public sealed class SearchModule : IFeatureModule, IModuleRiskInfo
{
    public string Id => SearchIds.FeatureId;
    public string Title => SearchIds.Title;
    public string Summary => "Find any file in an instant, keep your favorite folders handy, and get a tidy folder structure suggested for each drive.";
    public NavGroup Group => NavGroup.Organize;
    public string Icon => "Search24";
    public int Order => 9;
    public Release Release => Release.V3;
    /// <summary>Searching changes nothing; structure moves are always your own pick.</summary>
    public ModuleRisk Risk => ModuleRisk.Check;
    public IReadOnlyList<IFeatureScanner> Scanners { get; } = new IFeatureScanner[] { new SearchScanner() };

    public object? CreatePage(IServiceProvider services) => new SearchPage(services);
}
