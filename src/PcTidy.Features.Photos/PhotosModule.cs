using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Photos.Photos;
using PcTidy.Features.Photos.UI;

namespace PcTidy.Features.Photos;

/// <summary>
/// Photo organizer (v2): photos and videos into <c>{year}\{year}-{month}</c> folders by date taken, with blurry shots and
/// burst near-duplicates flagged for the user to look at. OneDrive folders are ask-first; online-only files are never opened.
/// </summary>
public sealed class PhotosModule : IFeatureModule
{
    public const string FeatureId = "photos";

    public string Id => FeatureId;
    public string Title => "Photos";
    public string Summary => "Sort photos and videos into year and month folders by the date they were taken, and spot blurry shots and bursts.";
    public NavGroup Group => NavGroup.Organize;
    public string Icon => "Image24";
    public int Order => 7;
    public Release Release => Release.V2;
    public IReadOnlyList<IFeatureScanner> Scanners { get; } = new IFeatureScanner[] { new PhotosScanner() };

    public object? CreatePage(IServiceProvider services) => new PhotosView { DataContext = new PhotosViewModel(services) };
}
