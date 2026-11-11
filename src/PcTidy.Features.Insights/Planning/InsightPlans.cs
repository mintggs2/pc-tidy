using PcTidy.Catalog.Insights;
using PcTidy.Core;
using PcTidy.Core.Model;

namespace PcTidy.Features.Insights.Planning;

/// <summary>
/// The only changes Insights can describe. Each is a PlanItem the user previews and confirms (IFlowHost); the engine
/// and platform handlers carry them out.
///   OpenExternal: Args["target"] (https / ms-settings / windowsdefender URI).
///   OneDriveFreeUpSpace: SourcePath = a fully downloaded file inside a OneDrive folder (reversible).
///   RunSystemTool compact.lzx: Args["folder"] (NTFS LZX compression of an old folder; reversible with compact /u).
///   RunSystemTool winget.install: Args["id"] (a Microsoft runtime from RuntimeInstalls; not reversible).
/// </summary>
public static class InsightPlans
{
    public static PlanItem OpenExternal(string target, string description) => new()
    {
        Kind = ActionKind.OpenExternal,
        FeatureId = InsightsIds.FeatureId,
        Description = description,
        Args = new Dictionary<string, string> { ["target"] = target },
    };

    public static PlanItem FreeUpSpace(string file, long size, DateTime lastWriteUtc) => new()
    {
        Kind = ActionKind.OneDriveFreeUpSpace,
        FeatureId = InsightsIds.FeatureId,
        Description = $"Make '{Path.GetFileName(file)}' online-only (frees {Sizes.Format(size)}, stays in OneDrive)",
        SourcePath = file,
        ExpectedSize = size,
        ExpectedLastWriteUtc = lastWriteUtc,
        Reversible = true,
    };

    public static PlanItem CompressFolder(string folder, long estimatedSaving) => new()
    {
        Kind = ActionKind.RunSystemTool,
        ToolId = ToolIds.CompactLzx,
        FeatureId = InsightsIds.FeatureId,
        Description = $"Compress the folder '{Path.GetFileName(folder.TrimEnd('\\'))}' with Windows' own NTFS compression (about {Sizes.Format(estimatedSaving)} saved; undo uncompresses it)",
        SourcePath = folder,
        IsDirectory = true,
        ExpectedSize = estimatedSaving,
        Reversible = true,
        Args = new Dictionary<string, string> { ["folder"] = folder },
    };

    /// <summary>
    /// RunSystemTool winget.install: Args["id"] = an id from <see cref="RuntimeInstalls"/> (anything else throws). winget runs
    /// unelevated and asks for admin itself when the installer needs it, so NeedsAdmin is false. An install can't be undone
    /// from History (Reversible = false).
    /// </summary>
    public static PlanItem InstallRuntime(RuntimeInstall runtime)
    {
        if (!RuntimeInstalls.IsAllowed(runtime.WingetId)) throw new ArgumentException("Not an allow-listed runtime.", nameof(runtime));
        return new PlanItem
        {
            Kind = ActionKind.RunSystemTool,
            ToolId = ToolIds.WingetInstall,
            FeatureId = InsightsIds.FeatureId,
            Description = $"Install {runtime.Name}. {RuntimeInstalls.PreviewText(runtime.Name)}",
            NeedsAdmin = false,
            IsSystemChange = false,
            Reversible = false,
            Args = new Dictionary<string, string> { ["id"] = runtime.WingetId },
        };
    }

    /// <summary>Plan for "open this page" from a card button.</summary>
    public static ActionPlan OpenPlan(string target, string description)
        => new() { FeatureId = InsightsIds.FeatureId, Title = "Open a page", Items = new[] { OpenExternal(target, description) } };
}
