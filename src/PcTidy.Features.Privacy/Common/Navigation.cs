using PcTidy.UI.Flow;

namespace PcTidy.Features.Privacy.Common;

/// <summary>
/// "Take me to another part of PC Tidy" (internal navigation, never an OpenExternal step). The page raises it; a shell
/// that understands groups can scroll to <see cref="GroupId"/>; today's <see cref="IShellFlowHost.NavigateTo"/> takes the
/// module id only, so <see cref="Go"/> opens the module and the hint tells the user which group to look at.
/// </summary>
/// <param name="ModuleId">Left-rail module id, e.g. "cleanup".</param>
/// <param name="GroupId">Scan group to bring into view, e.g. "cleanup.browser-privacy" (null = the page itself).</param>
/// <param name="Hint">Shown if the shell can't jump to the group: where to look on that page.</param>
public sealed record NavigationRequest(string ModuleId, string? GroupId, string Hint)
{
    /// <summary>Clean → Privacy extras: each browser's History and Cookies files (quarantined, restorable).</summary>
    public static readonly NavigationRequest CleanPrivacyExtras = new("cleanup", "cleanup.browser-privacy",
        "Scan in Clean, then open the \"Privacy extras\" group. It lists each browser's history and cookies, unticked.");

    /// <summary>Opens the module through the shell. Returns false when the host can't navigate (e.g. in a test host).</summary>
    public bool Go(IServiceProvider services)
    {
        var shell = services.GetService(typeof(IShellFlowHost)) as IShellFlowHost
                    ?? services.GetService(typeof(IFlowHost)) as IShellFlowHost;
        if (shell is null) return false;
        shell.NavigateTo(ModuleId);
        return true;
    }
}

/// <summary>Opens a Settings page as a one-step OpenExternal plan, so it goes through the safety guard and the shell (no Process.Start here).</summary>
public static class LinkOpener
{
    public static async Task OpenAsync(IFlowHost flow, string featureId, string target, string what)
    {
        var plan = new ActionPlan
        {
            FeatureId = featureId,
            Title = "Open " + what,
            Items = new[] { PrivacyPlanItems.OpenExternal(featureId, target, "Open " + what) },
        };
        try { await flow.RunApprovedAsync(plan, CancellationToken.None); }
        catch (Exception ex) { flow.ShowMessage("Couldn't open the page", ex.Message); }
    }
}
