using PcTidy.Core;
using PcTidy.Core.Model;

namespace PcTidy.App.Composition;

/// <summary>Every feature section the app ships. Sections above <see cref="CurrentRelease"/> stay hidden.</summary>
public static class FeatureCatalog
{
    /// <summary>Raised to V2 / V3 when that release has passed its tests and reviews.</summary>
    public static Release CurrentRelease => Release.V3;

    public static IReadOnlyList<IFeatureModule> All()
    {
        // PC care's health report gets a Services heading from the Services section (one feature can't reference another).
        var services = new PcTidy.Features.Services.ServicesModule();
        return Modules(services);
    }

    private static IReadOnlyList<IFeatureModule> Modules(PcTidy.Features.Services.ServicesModule services) => new IFeatureModule[]
    {
        // v1
        new PcTidy.Features.Cleanup.CleanupModule(),
        new PcTidy.Features.SpaceMap.SpaceMapModule(),
        new PcTidy.Features.Organize.OrganizeModule(),
        // v2
        new PcTidy.Features.Duplicates.DuplicatesModule(),
        new PcTidy.Features.Apps.AppsModule(),
        new PcTidy.Features.Drivers.DriversModule(),
        new PcTidy.Features.Games.GamesModule(),
        new PcTidy.Features.Tweaks.TweaksModule(),
        new PcTidy.Features.DriveMove.DriveMoveModule(),
        new PcTidy.Features.Speed.SpeedModule(),
        new PcTidy.Features.PcCare.PcCareModule(reportSections: new[] { new ServicesReportSection(services) }),
        new PcTidy.Features.Developer.DeveloperModule(),
        new PcTidy.Features.Photos.PhotosModule(),
        new PcTidy.Features.Photos.DesktopTidyModule(),
        // v3
        new PcTidy.Features.Boost.BoostModule(),
        new PcTidy.Features.Schedule.ScheduleModule(),
        new PcTidy.Features.Assistant.AssistantModule(),
        new PcTidy.Features.Learning.LearningModule(),
        new PcTidy.Features.Privacy.PrivacyModule(),
        new PcTidy.Features.Privacy.NotificationsModule(),
        new PcTidy.Features.AiChats.AiChatsModule(),
        new PcTidy.Features.Insights.InsightsModule(),
        new PcTidy.Features.Search.SearchModule(),
        services,
        // v3.1
        new PcTidy.Features.Measure.MeasureModule(),
    };
}
