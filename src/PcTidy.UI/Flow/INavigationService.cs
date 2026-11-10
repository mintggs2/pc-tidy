namespace PcTidy.UI.Flow;

/// <summary>
/// Opens a section of the shell, optionally with some of its items picked for the user. Registered by the shell
/// (PcTidy.App); feature pages get it with <c>services.GetService(typeof(INavigationService))</c> (null in tests).
///
/// Usage (e.g. Privacy or the Assistant handing over to another section):
/// <code>
///   (services.GetService(typeof(INavigationService)) as INavigationService)?.Open("cleanup", new[] { item.Id });
/// </code>
/// Rules the shell keeps:
/// <list type="bullet">
/// <item><paramref name="moduleId"/> is a module id ("cleanup", "apps"…) or a shell page ("home", "history",
///   "quarantine", "settings", "optimize"). Unknown or hidden sections are ignored quietly.</item>
/// <item>Preselection only changes ticks in the preview; nothing ever runs without the normal confirm.
///   A standard section page scans first when it has no results yet, then ticks the matching items.</item>
/// <item>An id matches a row when it equals the row's <c>ScanItem.Id</c> (use stable ids for this) or the
///   full <c>SourcePath</c> of one of its actions (case-insensitive). Rows that can't be picked (disabled, info only)
///   and permanent rows are never ticked this way; the page says how many were picked and how many weren't found.</item>
/// <item>Custom pages opt in by implementing <see cref="IPreselectable"/>; otherwise they are just opened.</item>
/// </list>
/// </summary>
public interface INavigationService
{
    /// <param name="groupId">A scan group of that section to bring into view and open (e.g. "cleanup.browser-privacy");
    /// a standard page scans first when it has no results yet. Ignored when the section has no such group.</param>
    void Open(string moduleId, IReadOnlyCollection<string>? preselectItemIds = null, string? groupId = null);
}

/// <summary>A page that can bring one of its scan groups into view (see <see cref="INavigationService"/>).</summary>
public interface IGroupFocus
{
    /// <summary>Scans first if needed, then opens and scrolls to the group. Returns false when there is no such group.</summary>
    Task<bool> FocusGroupAsync(string groupId);
}

/// <summary>A page that can tick items another section asked for (see <see cref="INavigationService"/>).</summary>
public interface IPreselectable
{
    /// <summary>Ticks the matching items (scanning first if needed). Never runs anything.</summary>
    Task PreselectAsync(IReadOnlyCollection<string> itemIds);
}

/// <summary>
/// A page that can be pointed at one folder (Explorer's "Tidy this folder" right-click entry opens Organize this way).
/// The page shows a preview for that folder only; nothing moves until the user confirms.
/// </summary>
public interface IFolderTarget
{
    Task OpenFolderAsync(string folder);
}
