using CommunityToolkit.Mvvm.ComponentModel;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Organize.Planning;
using PcTidy.Features.Organize.UI;
using PcTidy.Features.Photos.Desktop;
using PcTidy.Features.Photos.Shared;

namespace PcTidy.Features.Photos.UI;

/// <summary>The Desktop tidy page: mode and options → scan → full before/after → confirm → one-click undo.</summary>
public sealed partial class DesktopTidyViewModel : SortPageViewModel
{
    private readonly DesktopTidyPlanner _planner;
    private readonly Dictionary<string, PlannedAction> _actions = new();
    private DesktopTidyPreview? _desktopPreview;

    public DesktopTidyViewModel(IServiceProvider services) : base(services)
    {
        _planner = new DesktopTidyPlanner(Safety, services.Get<IInUseChecker>(), services.Get<IFileWalker>(), AppSettings);
        Settings = DesktopTidySettings.Load(AppSettings);
        _modeIndex = Settings.Mode == DesktopTidyMode.GroupByType ? 1 : 0;
        _oldAfterDays = Settings.OldAfterDays;
        _includeShortcuts = Settings.IncludeShortcuts;
        DesktopInCloud = !string.IsNullOrEmpty(Locations.Desktop) && CloudFolders.AccessFor(Locations.Desktop, Safety, AppSettings) != FolderAccess.Local;
        // The same line for a OneDrive Desktop: the OneDrive bar (shown once, after a scan) explains the rest.
        Subtitle = "Files only move into folders on your Desktop. Shortcuts stay unless you say so. Every move can be put back.";
    }

    public DesktopTidySettings Settings { get; }
    public override string Title => "Desktop tidy";
    protected override string FeatureId => DesktopTidyModule.FeatureId;
    protected override string RunTitle => ModeIndex == 1 ? "Group Desktop files by type" : "Move old Desktop clutter";
    public bool DesktopInCloud { get; }

    /// <summary>0 = "Move old clutter", 1 = "Group by type".</summary>
    [ObservableProperty] private int _modeIndex;
    [ObservableProperty] private double _oldAfterDays;
    [ObservableProperty] private bool _includeShortcuts;

    public bool IsOldClutter => ModeIndex == 0;

    public override string OptionsSummary => (ModeIndex == 1 ? "Group by type" : $"Move old clutter, untouched for {Settings.OldAfterDays} days")
                                              + (IncludeShortcuts ? " · shortcuts included" : " · shortcuts stay");

    partial void OnModeIndexChanged(int value) { OnPropertyChanged(nameof(IsOldClutter)); SettingChanged(); }
    partial void OnOldAfterDaysChanged(double value) => SettingChanged();
    partial void OnIncludeShortcutsChanged(bool value) => SettingChanged();

    private void SettingChanged()
    {
        Settings.Mode = ModeIndex == 1 ? DesktopTidyMode.GroupByType : DesktopTidyMode.MoveOldClutter;
        Settings.OldAfterDays = Math.Clamp((int)Math.Round(double.IsNaN(OldAfterDays) ? DesktopTidySettings.DefaultOldAfterDays : OldAfterDays), 1, 3650);
        Settings.IncludeShortcuts = IncludeShortcuts;
        Settings.Store(AppSettings);
        ClearPreview();
    }

    protected override async Task<PagePreview> PlanAsync(ScanDepth depth, IProgress<ScanProgress> progress, CancellationToken ct)
    {
        _actions.Clear();
        var preview = await _planner.PlanAsync(DesktopTidyRequest.From(Settings, Locations.Desktop, depth), progress, ct);
        _desktopPreview = preview;
        foreach (var a in preview.Preview.Actions) _actions["desktoptidy:" + a.SourcePath] = a;

        var result = DesktopTidyResults.Build(preview);
        var groups = result.Groups.Where(g => g.Id != DesktopTidyResults.SkippedGroupId)
            .Select(g => new ChangeGroup(g.Title, g.Description, g.Badge, g.Items.Select(i => new ChangeRow(i, OnRowChanged)).ToList()))
            .ToList();
        var skipped = preview.Preview.Skipped.Select(s => new SkipRow(s.Name, s.Reason, s.IsDirectory ? "Folder24" : PreviewTree.IconFor(s.Name))).ToList();
        var root = preview.Preview.Roots.FirstOrDefault();
        var loose = root is null ? 0 : root.LooseFiles + root.LooseFolders;
        var found = loose == 1 ? "1 item on the Desktop" : $"{loose:N0} items on the Desktop";
        var needs = preview.NeedsPermission ? new[] { preview.Desktop } : Array.Empty<string>();
        return new PagePreview(groups, PreviewTree.Before(preview.Preview), skipped, preview.Preview.Notes, needs, found);
    }

    protected override IReadOnlyList<TreeRow> AfterTree(IReadOnlyList<ChangeRow> ticked)
    {
        if (_desktopPreview is not { } p) return Array.Empty<TreeRow>();
        var chosen = ticked.Select(r => _actions.GetValueOrDefault(r.Item.Id)).OfType<PlannedAction>().ToList();
        // Before permission nothing can be ticked, so the "after" side shows everything that would move once allowed.
        if (p.NeedsPermission) chosen = p.Preview.Actions.ToList();
        return PreviewTree.After(new OrganizePreview
        {
            Roots = p.Preview.Roots, Actions = chosen, Skipped = p.Preview.Skipped, Notes = p.Preview.Notes,
            Problems = p.Preview.Problems, Depth = p.Preview.Depth, Duration = p.Preview.Duration,
        });
    }

    protected override string PermissionText(IReadOnlyList<string> folders)
        => "Your Desktop is in OneDrive. If you allow it, files only move into folders on the Desktop, which are in the same OneDrive folder, "
           + "so OneDrive just renames them: nothing is uploaded again or removed from your other devices, and online-only files are never downloaded.";
}
