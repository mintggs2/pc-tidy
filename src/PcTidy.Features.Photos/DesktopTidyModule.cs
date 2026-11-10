using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Photos.Desktop;
using PcTidy.Features.Photos.UI;

namespace PcTidy.Features.Photos;

/// <summary>
/// Desktop tidy (v2): old clutter into <c>Desktop\Old desktop\{year}-{month}</c>, or loose files grouped into category
/// folders. Moves stay on the Desktop, so a OneDrive Desktop only sees renames. Undo is the engine's run undo.
/// </summary>
public sealed class DesktopTidyModule : IFeatureModule
{
    public const string FeatureId = "desktoptidy";

    public string Id => FeatureId;
    public string Title => "Desktop tidy";
    public string Summary => "Clear old clutter off your Desktop into an \"Old desktop\" folder, or group files into folders by type. Shortcuts stay put.";
    public NavGroup Group => NavGroup.Organize;
    public string Icon => "Desktop24";
    public int Order => 8;
    public Release Release => Release.V2;
    public IReadOnlyList<IFeatureScanner> Scanners { get; } = new IFeatureScanner[] { new DesktopTidyScanner() };

    public object? CreatePage(IServiceProvider services) => new DesktopTidyView { DataContext = new DesktopTidyViewModel(services) };
}
