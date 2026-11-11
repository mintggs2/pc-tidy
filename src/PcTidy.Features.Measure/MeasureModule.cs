using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Measure.Storage;
using PcTidy.Features.Measure.UI;
using PcTidy.UI.Flow;

namespace PcTidy.Features.Measure;

/// <summary>
/// Measure (v3.1, build list B1, "measure it yourself" stage 1): import frame-time recordings the user made with a free tool
/// (PresentMon, CapFrameX, OCAT, FrameView), compare a Before set with an After set, and say Better / Worse /
/// No measurable change / Too noisy to say, with the numbers. It never runs a recorder (that is stage 2), never changes the
/// PC and never emits PlanItems: it has no scanners. The only thing it writes is its own saved comparisons (JSON) in its
/// data folder.
/// </summary>
public sealed class MeasureModule : IFeatureModule, IModuleRiskInfo
{
    public const string FeatureId = "measure";

    private readonly string? _dataFolder;
    private string? _pendingRunId;
    private WeakReference<MeasurePage>? _page;

    /// <param name="dataFolder">Folder for saved comparisons. Default: &lt;DataRoot&gt;\measure from the app's settings.</param>
    public MeasureModule(string? dataFolder = null) => _dataFolder = dataFolder;

    public string Id => FeatureId;
    public string Title => "Measure";
    public string Summary => "Compare frame-time recordings from before and after a change, on your own PC.";
    public NavGroup Group => NavGroup.SpeedUp;
    public string Icon => "DataLine24";
    /// <summary>Speed up group, after Insights (75).</summary>
    public int Order => 78;
    /// <summary>V3 is the current maximum release.</summary>
    public Release Release => Release.V3;
    /// <summary>Only reads files the user picks.</summary>
    public ModuleRisk Risk => ModuleRisk.Safe;
    /// <summary>None: Measure never scans the PC and never emits plan items.</summary>
    public IReadOnlyList<IFeatureScanner> Scanners { get; } = Array.Empty<IFeatureScanner>();

    /// <summary>
    /// "Check if this helped" from a Done screen or History: links the next comparison to that History run. Works before or
    /// after the page exists; then navigate to <see cref="FeatureId"/>. Null clears the link.
    /// </summary>
    public void LinkToRun(string? runId)
    {
        _pendingRunId = runId;
        if (_page is not null && _page.TryGetTarget(out var page)) page.LinkedRunId = runId;
    }

    /// <summary>The folder saved comparisons go to, for the given services.</summary>
    public string DataFolderFor(IServiceProvider services)
    {
        if (!string.IsNullOrWhiteSpace(_dataFolder)) return _dataFolder!;
        var root = (services.GetService(typeof(AppSettings)) as AppSettings)?.DataRoot
                   ?? (services.GetService(typeof(IKnownLocations)) as IKnownLocations)?.DataRoot
                   ?? AppSettings.DefaultDataRoot;
        return Path.Combine(root, ComparisonStore.FolderName);
    }

    /// <summary>Uses IFlowHost (opening the tools' web pages) and IShellFlowHost (the "Open History" link) when present.</summary>
    public object? CreatePage(IServiceProvider services)
    {
        var page = new MeasurePage(services, new ComparisonStore(DataFolderFor(services))) { LinkedRunId = _pendingRunId };
        _page = new WeakReference<MeasurePage>(page);
        return page;
    }
}
