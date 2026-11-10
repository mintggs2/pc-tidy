using System.Windows.Threading;
using PcTidy.Core;
using PcTidy.Features.Assistant.Engine;
using PcTidy.Features.Organize.UI;
using PcTidy.UI.Flow;

namespace PcTidy.App.Shell;

/// <summary>
/// Navigation requests from features and from outside (Explorer's right-click entries, the tray, notifications):
/// <see cref="INavigationService"/> (open a section, optionally a group and pre-ticked items) and the assistant's
/// <see cref="ISectionReviewHost"/>. Opening never runs anything: at most it scans (read-only) and changes ticks.
/// </summary>
public sealed class ShellNavigation : INavigationService, ISectionReviewHost
{
    private readonly MainWindow _window;
    private readonly IServiceProvider _services;

    public ShellNavigation(MainWindow window, IServiceProvider services)
    {
        _window = window;
        _services = services;
    }

    private FlowHost Host => _window.FlowHost;

    public void Open(string moduleId, IReadOnlyCollection<string>? preselectItemIds = null, string? groupId = null)
    {
        if (!_window.Dispatcher.CheckAccess())
        {
            _window.Dispatcher.BeginInvoke(() => Open(moduleId, preselectItemIds, groupId));
            return;
        }
        OpenAsync(moduleId, preselectItemIds, groupId).Forget(Host, "Couldn't open that section");
    }

    /// <summary>"module#group" (what <see cref="IShellFlowHost.NavigateTo"/> also accepts) split into its parts.</summary>
    public static (string ModuleId, string? GroupId) SplitKey(string key)
    {
        int hash = key.IndexOf('#');
        return hash < 0 ? (key, null) : (key[..hash], key[(hash + 1)..] is { Length: > 0 } g ? g : null);
    }

    private async Task OpenAsync(string moduleId, IReadOnlyCollection<string>? ids, string? groupId)
    {
        if (string.IsNullOrWhiteSpace(moduleId) || !_window.HasPage(moduleId)) return;
        _window.ShowFromBackground();
        _window.NavigateTo(moduleId);
        await _window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        var page = _window.PageObject(moduleId);
        if (groupId is not null && page is IGroupFocus focus) await focus.FocusGroupAsync(groupId);
        if (ids is { Count: > 0 } && page is IPreselectable pre) await pre.PreselectAsync(ids);
    }

    /// <summary>
    /// "Review in Clean →" from the assistant: opens the section that owns the scanner and ticks exactly those items.
    /// If the page isn't showing that scan, the cached result (e.g. from Optimize everything) is shown without a
    /// rescan. Returns false when the items can't be shown (the section was scanned again since).
    /// </summary>
    public bool OpenForReview(SectionReviewRequest request)
    {
        var module = _window.Modules.FirstOrDefault(m => m.Scanners.Any(s => string.Equals(s.FeatureId, request.FeatureId, StringComparison.OrdinalIgnoreCase)))
                     ?? _window.Modules.FirstOrDefault(m => string.Equals(m.Id, request.FeatureId, StringComparison.OrdinalIgnoreCase));
        if (module is null) return false;
        _window.ShowFromBackground();
        _window.NavigateTo(module.Id);
        if (_window.PageObject(module.Id) is not StandardFlowPage page) return false;
        if (SectionReview.TryApply(page, request)) return true;
        var cached = (_services.GetService(typeof(IScanResultsCache)) as IScanResultsCache)?.Get(request.FeatureId);
        if (cached is null || cached.FinishedUtc != request.ScanFinishedUtc) return false;
        return page.ShowExistingResults(new[] { cached }) && SectionReview.TryApply(page, request);
    }

    /// <summary>Explorer's "Tidy this folder": Organize, for that folder only, up to the preview. Nothing moves.</summary>
    public void OpenFolder(string folder)
    {
        if (!_window.Dispatcher.CheckAccess())
        {
            _window.Dispatcher.BeginInvoke(() => OpenFolder(folder));
            return;
        }
        OpenFolderAsync(folder).Forget(Host, "Couldn't open Organize");
    }

    /// <summary>A name that never exists: classifying it asks about the folder's contents, not the folder itself.</summary>
    internal const string ProbeChildName = "pc-tidy-probe.tmp";

    /// <summary>
    /// Why the files inside <paramref name="folder"/> can't be tidied, or null when they can. The guard protects user
    /// folders, cloud roots and drive roots themselves (they never move) but allows what's in them, so this asks about
    /// a child path, as Organize's own root check does.
    /// </summary>
    public static string? ContentsRefusal(ISafetyGuard guard, string folder)
    {
        var verdict = guard.ClassifyPath(Path.Combine(folder, ProbeChildName));
        return verdict.Protection == PcTidy.Core.Model.PathProtection.Never ? verdict.Reason ?? "It's protected." : null;
    }

    private async Task OpenFolderAsync(string folder)
    {
        _window.ShowFromBackground();
        if (!Directory.Exists(folder))
        {
            Host.ShowMessage("That folder isn't there", $"\"{folder}\" doesn't exist any more, so there's nothing to tidy.");
            return;
        }
        if (_services.GetService(typeof(ISafetyGuard)) is ISafetyGuard guard && ContentsRefusal(guard, folder) is { } refusal)
        {
            Host.ShowMessage("PC Tidy doesn't tidy this folder", refusal + " Nothing was changed.");
            return;
        }
        const string organize = "organize";
        if (!_window.HasPage(organize))
        {
            Host.ShowMessage("Organize isn't available", "Organize isn't part of this version of PC Tidy.");
            return;
        }
        _window.NavigateTo(organize);
        await _window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        switch (_window.PageObject(organize))
        {
            case IFolderTarget target:
                await target.OpenFolderAsync(folder);
                break;
            case OrganizeView { DataContext: OrganizeViewModel vm }:
                OrganizeFolderAdapter.Open(vm, folder, _services);
                break;
        }
    }
}

/// <summary>
/// Points Organize's own page at one folder through its public view model (until Organize implements
/// <see cref="IFolderTarget"/> itself): adds the folder to its folder list for this session, selects it and starts
/// the preview scan. The folder isn't saved to Organize's settings and nothing moves until the user confirms there.
/// </summary>
internal static class OrganizeFolderAdapter
{
    public static void Open(OrganizeViewModel vm, string folder, IServiceProvider services)
    {
        var full = PathUtil.Normalize(folder);
        var option = vm.Folders.FirstOrDefault(f => !f.IsPicker && PathUtil.Normalize(f.Path).Equals(full, StringComparison.OrdinalIgnoreCase));
        if (option is null)
        {
            bool cloud = services.GetService(typeof(ISafetyGuard)) is ISafetyGuard g && g.IsInCloudRoot(full);
            option = new FolderOption(full, Path.GetFileName(full.TrimEnd('\\')) is { Length: > 0 } name ? name : full, cloud ? "in OneDrive, needs your OK" : null);
            int picker = vm.Folders.ToList().FindIndex(f => f.IsPicker);
            vm.Folders.Insert(picker < 0 ? vm.Folders.Count : picker, option);
        }
        vm.SelectedFolder = option;
        if (vm.ScanCommand.CanExecute(null)) vm.ScanCommand.Execute(null);
    }
}
