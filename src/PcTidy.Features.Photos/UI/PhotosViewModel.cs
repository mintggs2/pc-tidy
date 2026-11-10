using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Organize.Planning;
using PcTidy.Features.Organize.UI;
using PcTidy.Features.Photos.Photos;
using PcTidy.Features.Photos.Shared;

namespace PcTidy.Features.Photos.UI;

/// <summary>A folder the photo organizer may sort, with its tick.</summary>
public sealed partial class FolderChoice : ObservableObject
{
    private readonly Action _changed;

    public FolderChoice(string path, string label, string? note, bool isChecked, Action changed)
    {
        Path = path;
        Label = label;
        Note = note;
        _isChecked = isChecked;
        _changed = changed;
    }

    public string Path { get; }
    public string Label { get; }
    /// <summary>"in OneDrive, asks first" or null.</summary>
    public string? Note { get; }
    public string Display => Note is null ? Label : $"{Label} ({Note})";
    [ObservableProperty] private bool _isChecked;
    partial void OnIsCheckedChanged(bool value) => _changed();
}

/// <summary>The Photos page: folders and checks → scan → date folders, blurry shots and bursts → confirm → undo.</summary>
public sealed partial class PhotosViewModel : SortPageViewModel
{
    private readonly PhotoPlanner _planner;
    private readonly Dictionary<string, PhotoMove> _moves = new();
    private readonly Dictionary<string, PhotoFlag> _flags = new();
    private readonly Dictionary<string, List<ChangeRow>> _pairs = new(StringComparer.OrdinalIgnoreCase);
    private PhotoPreview? _photoPreview;

    public PhotosViewModel(IServiceProvider services) : base(services)
    {
        _planner = new PhotoPlanner(Safety, services.Get<IInUseChecker>(), services.Get<IFileWalker>(), AppSettings);
        Settings = PhotoSettings.Load(AppSettings);
        _includeSubfolders = Settings.IncludeSubfolders;
        _findBlurry = Settings.FindBlurry;
        _findBursts = Settings.FindBursts;
        BuildFolders();
        Subtitle = @"Photos and videos go into year and month folders (like 2026\2026-11) by the date they were taken. Every move can be put back.";
    }

    public PhotoSettings Settings { get; }
    public override string Title => "Photos";
    protected override string FeatureId => PhotosModule.FeatureId;
    protected override string RunTitle => "Sort photos";
    public ObservableCollection<FolderChoice> Folders { get; } = new();

    [ObservableProperty] private bool _includeSubfolders;
    [ObservableProperty] private bool _findBlurry;
    [ObservableProperty] private bool _findBursts;

    public override string OptionsSummary
    {
        get
        {
            var folders = Folders.Where(f => f.IsChecked).Select(f => f.Label).ToList();
            var parts = new List<string> { folders.Count == 0 ? "No folders picked" : string.Join(", ", folders) };
            if (IncludeSubfolders) parts.Add("subfolders too");
            if (FindBlurry) parts.Add("blurry shots");
            if (FindBursts) parts.Add("bursts");
            return string.Join(" · ", parts);
        }
    }

    partial void OnIncludeSubfoldersChanged(bool value) => SettingChanged();
    partial void OnFindBlurryChanged(bool value) => SettingChanged();
    partial void OnFindBurstsChanged(bool value) => SettingChanged();

    private void BuildFolders()
    {
        Folders.Clear();
        var chosen = Settings.FoldersOrDefault(Locations).Select(PathUtil.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        void Add(string? path, string label)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
            var normal = PathUtil.Normalize(path);
            if (Folders.Any(f => PathUtil.Normalize(f.Path).Equals(normal, StringComparison.OrdinalIgnoreCase))) return;
            var note = CloudFolders.AccessFor(normal, Safety, AppSettings) == FolderAccess.NeedsPermission ? "in OneDrive, asks first" : null;
            Folders.Add(new FolderChoice(normal, label, note, chosen.Contains(normal), SettingChanged));
        }
        Add(Locations.Pictures, "Pictures");
        Add(Locations.Videos, "Videos");
        Add(Locations.Downloads, "Downloads");
        foreach (var extra in Settings.ExtraFolders) Add(extra, Path.GetFileName(PathUtil.Normalize(extra)));
    }

    private void SettingChanged()
    {
        Settings.Folders = Folders.Where(f => f.IsChecked).Select(f => f.Path).ToList();
        Settings.IncludeSubfolders = IncludeSubfolders;
        Settings.FindBlurry = FindBlurry;
        Settings.FindBursts = FindBursts;
        Settings.Store(AppSettings);
        ClearPreview();
    }

    [RelayCommand]
    private void AddFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder with photos" };
        if (dialog.ShowDialog() != true || !Directory.Exists(dialog.FolderName)) return;
        AddFolderPath(dialog.FolderName);
    }

    /// <summary>Adds (and ticks) a folder of the user's choice.</summary>
    public void AddFolderPath(string folder)
    {
        var normal = PathUtil.Normalize(folder);
        if (!Settings.ExtraFolders.Any(f => PathUtil.Normalize(f).Equals(normal, StringComparison.OrdinalIgnoreCase))) Settings.ExtraFolders.Add(normal);
        var ticked = (Settings.Folders ?? Settings.FoldersOrDefault(Locations).ToList()).Append(normal).ToList();
        Settings.Folders = ticked;
        BuildFolders();
        SettingChanged();
    }

    protected override async Task<PagePreview> PlanAsync(ScanDepth depth, IProgress<ScanProgress> progress, CancellationToken ct)
    {
        var roots = Folders.Where(f => f.IsChecked).Select(f => f.Path).ToList();
        _moves.Clear(); _flags.Clear(); _pairs.Clear();
        if (roots.Count == 0)
        {
            _photoPreview = null;
            return new PagePreview(Array.Empty<ChangeGroup>(), Array.Empty<TreeRow>(), Array.Empty<SkipRow>(),
                new[] { "Tick at least one folder to sort." }, Array.Empty<string>(), "No folder picked");
        }

        var preview = await _planner.PlanAsync(PhotoRequest.From(Settings, roots, depth), progress, ct);
        _photoPreview = preview;
        foreach (var m in preview.Moves) _moves["photos:move:" + m.SourcePath] = m;
        foreach (var f in preview.Flags) _flags["photos:flag:" + f.SourcePath] = f;

        var result = PhotoResultBuilder.Build(preview);
        var groups = result.Groups.Where(g => g.Id != PhotoResultBuilder.SkippedGroupId)
            .Select(g => new ChangeGroup(g.Title, g.Description, g.Badge, g.Items.Select(NewRow).ToList()))
            .ToList();
        var skipped = preview.Skipped.Select(s => new SkipRow(s.Name, s.Reason, s.IsDirectory ? "Folder24" : PreviewTree.IconFor(s.Name))).ToList();
        int media = preview.Roots.Sum(r => r.LooseFiles);
        var found = media == 1 ? "1 photo or video" : $"{media:N0} photos and videos";
        var notes = preview.Notes.ToList();
        if (preview.Checked > 0 && (Settings.FindBlurry || Settings.FindBursts))
            notes.Add($"Checked {preview.Checked:N0} photos for blur and bursts. Blur is a best guess: look at each preview before you tick it.");
        return new PagePreview(groups, PreviewTree.Before(preview.ToTreePreview(preview.Moves, Array.Empty<PhotoFlag>())), skipped, notes, preview.NeedsPermission, found);
    }

    private ChangeRow NewRow(ScanItem item)
    {
        string? pairKey = _moves.TryGetValue(item.Id, out var m) ? m.SourcePath : _flags.TryGetValue(item.Id, out var f) ? f.SourcePath : null;
        var row = new ChangeRow(item, OnRowChanged, pairKey);
        if (pairKey is not null)
        {
            if (!_pairs.TryGetValue(pairKey, out var list)) _pairs[pairKey] = list = new List<ChangeRow>();
            list.Add(row);
        }
        return row;
    }

    /// <summary>A file's move and its quarantine are alternatives: ticking one unticks the other.</summary>
    protected override void OnRowChanged(ChangeRow row)
    {
        if (row.IsChecked && row.PairKey is not null && _pairs.TryGetValue(row.PairKey, out var partners))
            foreach (var other in partners.Where(p => !ReferenceEquals(p, row) && p.IsChecked)) other.IsChecked = false;
        base.OnRowChanged(row);
    }

    protected override IReadOnlyList<TreeRow> AfterTree(IReadOnlyList<ChangeRow> ticked)
    {
        if (_photoPreview is null) return Array.Empty<TreeRow>();
        var moves = ticked.Select(r => _moves.GetValueOrDefault(r.Item.Id)).OfType<PhotoMove>();
        var flags = ticked.Select(r => _flags.GetValueOrDefault(r.Item.Id)).OfType<PhotoFlag>();
        return PreviewTree.After(_photoPreview.ToTreePreview(moves, flags));
    }

    protected override void OnPermissionGranted()
    {
        BuildFolders();
    }

    protected override string PermissionText(IReadOnlyList<string> folders)
    {
        var names = string.Join(", ", folders.Select(f => Path.GetFileName(f)));
        return $"{names} {(folders.Count == 1 ? "is" : "are")} in OneDrive. If you allow it, photos only move into year and month folders inside the same "
               + "OneDrive folder, so OneDrive just renames them: nothing is uploaded again, and online-only photos are never downloaded.";
    }
}
