using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcTidy.Features.Duplicates.Hashing;
using PcTidy.Features.Duplicates.Images;
using PcTidy.Features.Duplicates.Model;
using PcTidy.Features.Duplicates.Planning;

namespace PcTidy.Features.Duplicates.UI;

/// <summary>One side of the side-by-side compare: preview, size, pixels, dates, camera, and "keep this one".</summary>
public sealed partial class CompareSide : ObservableObject
{
    private readonly Action<CompareSide> _keep;

    public CompareSide(string path, long size, DateTime lastWriteUtc, string folder, string? note, Action<CompareSide> keep)
    {
        Path = path;
        Size = size;
        LastWriteUtc = lastWriteUtc;
        Folder = folder;
        Note = note;
        _keep = keep;
    }

    public string Path { get; }
    public long Size { get; }
    public DateTime LastWriteUtc { get; }
    public string Name => DuplicatePlanner.Name(Path);
    public string Folder { get; }
    /// <summary>"Hard link: …", "Protected: … (why)", "92% alike"…</summary>
    public string? Note { get; }
    public string SizeText => Sizes.Format(Size);
    public string Changed => "Changed " + LastWriteUtc.ToLocalTime().ToString("d MMM yyyy, HH:mm");

    [ObservableProperty] private BitmapSource? _preview;
    [ObservableProperty] private string _dimensions = "";
    [ObservableProperty] private string _camera = "";
    [ObservableProperty] private string _taken = "";
    [ObservableProperty] private bool _isKept;

    public string KeepName => $"Keep {Name}";

    /// <summary>The "keep this one" toggle. Turning it off is ignored: one copy is always kept.</summary>
    public bool KeepToggle
    {
        get => IsKept;
        set { if (value && !IsKept) _keep(this); else OnPropertyChanged(); }
    }

    partial void OnIsKeptChanged(bool value) => OnPropertyChanged(nameof(KeepToggle));

    internal void Show(PhotoFacts facts, BitmapSource? preview)
    {
        Preview = preview;
        Dimensions = facts.Width is { } w && facts.Height is { } h ? $"{w:N0} × {h:N0} pixels" : "";
        Camera = facts.Camera is { } c ? "Camera: " + c : "";
        Taken = facts.Taken is { } t ? "Taken " + t.ToString("d MMM yyyy, HH:mm") : "";
    }
}

/// <summary>
/// Side-by-side compare for a duplicate group or a look-alike picture group: the kept copy on the left, one other
/// copy on the right (Next/Previous to step through the others). "Keep this one" on either side updates which copy
/// the group keeps, through the same rules as the Keep button on the page. Read-only: previews and facts are read
/// in the background, never for online-only files (the scan never lists those).
/// </summary>
public sealed partial class CompareViewModel : ObservableObject
{
    private readonly IReadOnlyList<string> _paths;
    private readonly Func<string, CompareSide> _sideFor;
    private readonly Action<string> _keep;
    private readonly Func<string> _keptPath;
    private int _otherIndex;

    private CompareViewModel(string title, IReadOnlyList<string> paths, Func<string> keptPath, Action<string> keep, Func<string, CompareSide> sideFor)
    {
        Title = title;
        _paths = paths;
        _keptPath = keptPath;
        _keep = keep;
        _sideFor = sideFor;
        Rebuild();
    }

    /// <summary>Compare the copies of a duplicate group (files or folders; folders show no preview).</summary>
    public static CompareViewModel For(GroupRow group)
    {
        CompareViewModel? vm = null;
        vm = new CompareViewModel(
            group.Group.IsFolderGroup ? "Compare duplicate folders" : $"Compare copies of '{DuplicatePlanner.Name(group.Group.KeptPath)}'",
            group.Copies.Select(c => c.Path).ToList(),
            () => group.Group.KeptPath,
            path => group.Copies.First(c => DuplicateGroup.PathEquals(c.Path, path)).IsKept = true,
            path =>
            {
                var row = group.Copies.First(c => DuplicateGroup.PathEquals(c.Path, path));
                return new CompareSide(path, row.Copy.Size, row.Copy.LastWriteUtc, row.Folder, NoteFor(row), s => vm!.Keep(s));
            });
        return vm;
    }

    /// <summary>The copy's tag with its reason ("Protected" alone doesn't say why or whether it can still go).</summary>
    public static string? NoteFor(CopyRow row) => row.Tag switch
    {
        null or "Kept" => null,
        "Protected" => ("Protected: PC Tidy never removes this copy. " + row.DisabledReason).Trim(),
        "Reference" => "In a reference folder you marked: never removed.",
        "Hard link" => "Hard link: it shares its data with another file, so removing it would free no space.",
        var tag => tag,
    };

    /// <summary>Compare look-alike pictures.</summary>
    public static CompareViewModel For(SimilarGroupRow group)
    {
        CompareViewModel? vm = null;
        vm = new CompareViewModel(
            $"Compare {group.Images.Count} look-alike pictures",
            group.Images.Select(i => i.Path).ToList(),
            () => group.Group.SuggestedKeep,
            group.KeepImage,
            path =>
            {
                var row = group.Images.First(i => DuplicateGroup.PathEquals(i.Path, path));
                return new CompareSide(path, row.Image.Size, row.Image.LastWriteUtc, row.Folder, row.IsSuggestedKeep ? null : $"{row.Image.SimilarityPercent}% alike", s => vm!.Keep(s));
            });
        return vm;
    }

    public string Title { get; }
    [ObservableProperty] private CompareSide? _left;
    [ObservableProperty] private CompareSide? _right;
    [ObservableProperty] private string _position = "";

    public bool HasMoreThanTwo => _paths.Count > 2;

    private IReadOnlyList<string> Others => _paths.Where(p => !DuplicateGroup.PathEquals(p, _keptPath())).ToList();

    private void Rebuild()
    {
        var others = Others;
        _otherIndex = Math.Clamp(_otherIndex, 0, Math.Max(0, others.Count - 1));
        Left = _sideFor(_keptPath());
        Right = others.Count > 0 ? _sideFor(others[_otherIndex]) : null;
        Left.IsKept = true;
        Position = others.Count > 1 ? $"Other copy {_otherIndex + 1} of {others.Count}" : "";
        _ = LoadAsync(Left);
        if (Right is not null) _ = LoadAsync(Right);
    }

    [RelayCommand]
    private void Next() { if (Others.Count > 1) { _otherIndex = (_otherIndex + 1) % Others.Count; Rebuild(); } }

    [RelayCommand]
    private void Previous() { if (Others.Count > 1) { _otherIndex = (_otherIndex + Others.Count - 1) % Others.Count; Rebuild(); } }

    /// <summary>"Keep this one": the group keeps this copy; the panel then shows it on the left.</summary>
    public void Keep(CompareSide side)
    {
        if (DuplicateGroup.PathEquals(side.Path, _keptPath())) return;
        var previous = _keptPath();
        _keep(side.Path);
        // The copy that was kept now sits on the right, so the two just swapped places.
        var others = Others;
        _otherIndex = Math.Max(0, others.ToList().FindIndex(p => DuplicateGroup.PathEquals(p, previous)));
        Rebuild();
    }

    /// <summary>Loads the preview and photo facts off the UI thread (pictures only).</summary>
    public Task LoadTask { get; private set; } = Task.CompletedTask;

    private async Task LoadAsync(CompareSide side)
    {
        var path = side.Path;
        var load = Task.Run(() =>
        {
            if (Directory.Exists(path) || !ImageFingerprint.Extensions.Contains(System.IO.Path.GetExtension(path))) return (PhotoFacts.None, (BitmapSource?)null);
            return (PhotoFacts.Read(path), Thumbnail(path));
        });
        LoadTask = Task.WhenAll(LoadTask, load);
        var (facts, preview) = await load;
        side.Show(facts, preview);
    }

    private static BitmapSource? Thumbnail(string path)
    {
        try
        {
            using var fs = new FileStream(Win32Path.Raw(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var b = new BitmapImage();
            b.BeginInit();
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            b.DecodePixelWidth = 640;
            b.StreamSource = fs;
            b.EndInit();
            b.Freeze();
            return b;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException
                                       or InvalidOperationException or System.Runtime.InteropServices.COMException or FileFormatException)
        {
            return null;
        }
    }
}
