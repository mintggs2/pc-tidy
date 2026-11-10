using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using PcTidy.Features.Duplicates.Hashing;
using PcTidy.Features.Duplicates.Keep;
using PcTidy.Features.Duplicates.Model;
using PcTidy.Features.Duplicates.Planning;

namespace PcTidy.Features.Duplicates.UI;

/// <summary>Bits every list row exposes for the shared flat-list style (screen-reader name, selection shape).</summary>
public abstract class RowBase : ObservableObject
{
    public abstract string AutomationName { get; }
    public virtual string HelpText => "";
    public Thickness SelectionMargin => new(0);
    public CornerRadius SelectionRadius => new(8);
}

/// <summary>Something the user can tick to remove. Totals and the plan come from these.</summary>
public interface IRemovableRow
{
    bool IsChecked { get; }
    bool CanRemove { get; }
    long Bytes { get; }
    PlanItem? Action { get; }
}

/// <summary>One copy in a duplicate group: keep it (radio) or remove it (tick).</summary>
public sealed class CopyRow : ObservableObject, IRemovableRow
{
    private readonly GroupRow _group;
    private bool _isChecked;
    private bool _isKept;

    public CopyRow(GroupRow group, DuplicateCopy copy)
    {
        _group = group;
        Copy = copy;
    }

    public DuplicateCopy Copy { get; private set; }
    public string Path => Copy.Path;
    public string Name => DuplicatePlanner.Name(Copy.Path);
    public string Folder => _group.Friendly(DuplicatePlanner.Folder(Copy.Path));
    public string SizeText => Copy.IsHardLink ? "0 B (hard link)" : Sizes.Format(Copy.Size);
    public string Changed => Copy.LastWriteUtc.ToLocalTime().ToString("d MMM yyyy");
    public string GroupName => _group.Group.Id;

    /// <summary>Short tag next to the name: "Kept", "Hard link", "Reference", "Protected".</summary>
    public string? Tag => IsKept ? "Kept" : Copy.IsHardLink ? "Hard link" : Copy.InReferenceFolder ? "Reference" : Copy.RemovalBlockedReason is not null ? "Protected" : null;

    public string? DisabledReason => IsKept ? null : DuplicatePlanner.DisabledReasonFor(Copy);
    /// <summary>The tick box is usable. For a probable (sampled-only) group, ticking starts the full check.</summary>
    public bool CanTick => !IsKept && Copy.CanBeRemoved && !_group.IsChecking;
    /// <summary>Goes into the plan when ticked: never while the group is only "probably the same".</summary>
    public bool CanRemove => !IsKept && Copy.CanBeRemoved && !_group.Group.IsProbable;
    public bool CanKeep => !Copy.IsHardLink || Copy.HardLinkOf == "";
    public long Bytes => Copy.ReclaimableBytes;

    public bool IsKept
    {
        get => _isKept;
        set
        {
            if (!SetProperty(ref _isKept, value)) return;
            if (value) _group.Keep(this);
            Refresh();
        }
    }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (!SetProperty(ref _isChecked, value && CanTick)) return;
            // A tick on a sampled-only match is a request: the full hash runs first, and only a full match keeps it.
            if (_isChecked && _group.Group.IsProbable) _group.Owner.RequestFullCheck(_group);
            _group.Owner.SelectionChanged();
        }
    }

    /// <summary>The verified plan item; null while the copy is kept, blocked, or only probably the same.</summary>
    public PlanItem? Action => CanRemove ? DuplicatePlanner.ForCopy(_group.Group, Copy, _group.Friendly) : null;

    internal void SetState(DuplicateCopy copy, bool kept, bool ticked)
    {
        Copy = copy;
        _isKept = kept;
        _isChecked = ticked && CanTick;
        Refresh();
    }

    internal void Refresh()
    {
        OnPropertyChanged(nameof(IsKept)); OnPropertyChanged(nameof(IsChecked)); OnPropertyChanged(nameof(Tag));
        OnPropertyChanged(nameof(CanRemove)); OnPropertyChanged(nameof(CanTick)); OnPropertyChanged(nameof(DisabledReason)); OnPropertyChanged(nameof(SizeText));
    }
}

/// <summary>A duplicate group card: header, "why this copy is kept", one row per copy.</summary>
public sealed class GroupRow : RowBase
{
    public GroupRow(DuplicatesViewModel owner, DuplicateGroup group, Func<string, string> friendly)
    {
        Owner = owner;
        Group = group;
        Friendly = friendly;
        Copies = new ObservableCollection<CopyRow>(group.Copies.Select(c => new CopyRow(this, c)));
        Apply(group);
    }

    public DuplicatesViewModel Owner { get; }
    public DuplicateGroup Group { get; private set; }
    public Func<string, string> Friendly { get; }
    public ObservableCollection<CopyRow> Copies { get; }

    public string Header => Group.IsFolderGroup
        ? $"{Group.Copies.Count} matching folders · {Group.FileCount:N0} file{(Group.FileCount == 1 ? "" : "s")} · {Sizes.Format(Group.Size)} each"
        : (Group.IsProbable ? "Probably the same (not fully checked) · " : Group.CheckedFully ? "Checked fully · " : "")
          + $"{Group.Copies.Count} copies of '{DuplicatePlanner.Name(Group.KeptPath)}' · {Sizes.Format(Group.Size)} each";
    public string KeepLine => Group.IsProbable
        ? $"Same size, and the start, middle and end match. The rest wasn't compared, so nothing is removed until you choose Check fully (ticking a copy starts it). {CheckStatus}".TrimEnd()
        : $"Keeping {Friendly(Group.KeptPath)}. {Group.KeepReason}";
    public string Frees => Group.IsProbable ? "Not checked yet" : Group.ReclaimableBytes > 0 ? "Up to " + Sizes.Format(Group.ReclaimableBytes) : "Nothing to free";
    public override string AutomationName => Header;
    public override string HelpText => KeepLine;

    // ---- probable (sampled-only) groups: "Check fully" ----

    private bool _isChecking;
    private string _checkStatus = "";

    /// <summary>The full hash is running in the background.</summary>
    public bool IsChecking
    {
        get => _isChecking;
        internal set
        {
            if (!SetProperty(ref _isChecking, value)) return;
            OnPropertyChanged(nameof(CanCheckFully));
            foreach (var c in Copies) c.Refresh();
        }
    }

    public bool CanCheckFully => Group.IsProbable && !IsChecking;

    /// <summary>"Checking fully… 40%" while it runs.</summary>
    public string CheckStatus
    {
        get => _checkStatus;
        internal set { if (SetProperty(ref _checkStatus, value)) OnPropertyChanged(nameof(KeepLine)); }
    }

    /// <summary>Paths ticked while the group was only probable (they stay ticked if the full hash matches).</summary>
    internal IEnumerable<string> TickedPaths => Copies.Where(c => c.IsChecked).Select(c => c.Path).ToList();

    /// <summary>The user picked another copy to keep: the rules re-run around that choice.</summary>
    internal void Keep(CopyRow row)
    {
        if (DuplicateGroup.PathEquals(row.Path, Group.KeptPath)) return;
        var group = KeepRules.WithKeeper(Group, row.Path);
        // Huge files are never ticked for the user, whoever keeps which copy.
        if (group.IsProbable || group.CheckedFully) group = group with { AutoTicked = new HashSet<string>(StringComparer.OrdinalIgnoreCase) };
        Apply(group);
        Owner.SelectionChanged();
    }

    private void Apply(DuplicateGroup group)
    {
        Group = group;
        foreach (var row in Copies)
        {
            var copy = group.Copies.First(c => DuplicateGroup.PathEquals(c.Path, row.Path));
            row.SetState(copy, DuplicateGroup.PathEquals(copy.Path, group.KeptPath), group.AutoTicked.Contains(copy.Path));
        }
        OnPropertyChanged(nameof(KeepLine)); OnPropertyChanged(nameof(Frees)); OnPropertyChanged(nameof(Header));
    }

    public void SetAll(bool remove)
    {
        foreach (var c in Copies) c.SetState(c.Copy, c.IsKept, remove);
        Owner.SelectionChanged();
    }

    public void SelectClearCases()
    {
        foreach (var c in Copies) c.SetState(c.Copy, c.IsKept, Group.AutoTicked.Contains(c.Path));
    }
}

/// <summary>One picture in a look-alike group, with a small thumbnail loaded in the background.</summary>
public sealed class ImageRow : ObservableObject, IRemovableRow
{
    private readonly SimilarGroupRow _group;
    private bool _isChecked;
    private BitmapSource? _thumbnail;

    public ImageRow(SimilarGroupRow group, SimilarImage image)
    {
        _group = group;
        Image = image;
    }

    public SimilarImage Image { get; }
    public string Path => Image.Path;
    public string Name => DuplicatePlanner.Name(Image.Path);
    public string Folder => _group.Friendly(DuplicatePlanner.Folder(Image.Path));
    public string Info => $"{Image.Width} × {Image.Height} · {Sizes.Format(Image.Size)}";
    public string Likeness => IsSuggestedKeep ? (_group.KeptByUser ? "Kept (your choice)" : "Suggested to keep") : $"{Image.SimilarityPercent}% alike";
    public bool IsSuggestedKeep => DuplicateGroup.PathEquals(Image.Path, _group.Group.SuggestedKeep);
    public bool CanRemove => !IsSuggestedKeep && Image.CanBeRemoved;
    public string? DisabledReason => IsSuggestedKeep ? null : Image.InReferenceFolder ? "It's in a reference folder you marked." : Image.RemovalBlockedReason;
    public long Bytes => Image.Size;
    public PlanItem? Action => CanRemove ? DuplicatePlanner.ForSimilar(_group.Group, Image, _group.Friendly) : null;

    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        private set => SetProperty(ref _thumbnail, value);
    }

    public bool IsChecked
    {
        get => _isChecked;
        set { if (SetProperty(ref _isChecked, value && CanRemove)) _group.Owner.SelectionChanged(); }
    }

    /// <summary>After the kept picture changed: the new keeper is unticked, the others become removable.</summary>
    internal void Refresh()
    {
        if (!CanRemove) _isChecked = false;
        OnPropertyChanged(nameof(IsChecked)); OnPropertyChanged(nameof(CanRemove)); OnPropertyChanged(nameof(Likeness));
        OnPropertyChanged(nameof(IsSuggestedKeep)); OnPropertyChanged(nameof(DisabledReason));
    }

    /// <summary>Decodes a small preview off the UI thread (read-only; the picture is never an online-only file).</summary>
    public async Task LoadThumbnailAsync()
    {
        if (_thumbnail is not null) return;
        var path = Image.Path;
        var bmp = await Task.Run(() =>
        {
            try
            {
                using var fs = new FileStream(Win32Path.Raw(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var b = new BitmapImage();
                b.BeginInit();
                b.CacheOption = BitmapCacheOption.OnLoad;
                b.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                b.DecodePixelWidth = 220;
                b.StreamSource = fs;
                b.EndInit();
                b.Freeze();
                return (BitmapSource?)b;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException
                                           or InvalidOperationException or System.Runtime.InteropServices.COMException or FileFormatException)
            {
                return null;
            }
        });
        Thumbnail = bmp;
    }
}

public sealed class SimilarGroupRow : RowBase
{
    public SimilarGroupRow(DuplicatesViewModel owner, SimilarImageGroup group, Func<string, string> friendly)
    {
        Owner = owner;
        Group = group;
        Friendly = friendly;
        Images = new ObservableCollection<ImageRow>(group.Images.Select(i => new ImageRow(this, i)));
    }

    public DuplicatesViewModel Owner { get; }
    public SimilarImageGroup Group { get; private set; }
    public Func<string, string> Friendly { get; }
    public ObservableCollection<ImageRow> Images { get; }
    public string Header => $"{Group.Images.Count} look-alike pictures";
    public string KeepLine => $"{(KeptByUser ? "Keeping" : "Suggested to keep")} '{DuplicatePlanner.Name(Group.SuggestedKeep)}'. {Group.KeepReason} They aren't identical, so compare them first.";
    public bool KeptByUser { get; private set; }
    public override string AutomationName => Header;
    public override string HelpText => KeepLine;

    /// <summary>The user chose which picture stays (side-by-side compare): it can't be ticked, the others can.</summary>
    public void KeepImage(string path)
    {
        if (DuplicateGroup.PathEquals(path, Group.SuggestedKeep) || !Group.Images.Any(i => DuplicateGroup.PathEquals(i.Path, path))) return;
        Group = Group with { SuggestedKeep = path, KeepReason = "You chose to keep this one." };
        KeptByUser = true;
        foreach (var img in Images) img.Refresh();
        OnPropertyChanged(nameof(KeepLine));
        Owner.SelectionChanged();
    }
}

/// <summary>Empty folder, empty file, broken shortcut, or a compare-to-master match.</summary>
public sealed class SimpleRow : RowBase, IRemovableRow
{
    private readonly DuplicatesViewModel _owner;
    private readonly PlanItem _action;
    private bool _isChecked;

    public SimpleRow(DuplicatesViewModel owner, string title, string detail, string reason, long bytes, string? disabledReason, PlanItem action)
    {
        _owner = owner;
        Title = title;
        Detail = detail;
        Reason = reason;
        Bytes = bytes;
        DisabledReason = disabledReason;
        _action = action;
    }

    public string Title { get; }
    public string Detail { get; }
    public string Reason { get; }
    public long Bytes { get; }
    public string SizeText => Bytes > 0 ? Sizes.Format(Bytes) : "";
    public string? DisabledReason { get; }
    public bool CanRemove => DisabledReason is null;
    public string Path => _action.SourcePath ?? "";
    public PlanItem? Action => CanRemove ? _action : null;
    public override string AutomationName => $"{Title}, {Detail}";
    public override string HelpText => DisabledReason ?? Reason;

    public bool IsChecked
    {
        get => _isChecked;
        set { if (SetProperty(ref _isChecked, value && CanRemove)) _owner.SelectionChanged(); }
    }

    internal void SetChecked(bool value) { _isChecked = value && CanRemove; OnPropertyChanged(nameof(IsChecked)); }
}

/// <summary>
/// A file whose content isn't what its extension says. Harmless mismatches can be ticked to rename; a program named
/// like a document is a security warning and can never be ticked.
/// </summary>
public sealed class ExtensionRow : RowBase, IRemovableRow
{
    private readonly DuplicatesViewModel _owner;
    private bool _isChecked;

    public ExtensionRow(DuplicatesViewModel owner, WrongExtensionItem item, Func<string, string> friendly)
    {
        _owner = owner;
        Item = item;
        Folder = friendly(DuplicatePlanner.Folder(item.Path));
    }

    public WrongExtensionItem Item { get; }
    public string Title => DuplicatePlanner.Name(Item.Path);
    public string Folder { get; }
    public string Path => Item.Path;
    public string Reason => Item.Reason;
    public bool IsSecurityWarning => Item.IsSecurityWarning;
    public string? Tag => Item.IsSecurityWarning ? "Security warning" : Item.SuggestedPath is null ? "Shown so you know" : null;
    public string? NewName => Item.SuggestedPath is { } p && !Item.IsSecurityWarning ? "Rename to " + DuplicatePlanner.Name(p) : null;
    public string? DisabledReason => Item.IsSecurityWarning
        ? "PC Tidy never renames a program into something that runs."
        : Item.SuggestedPath is null ? "PC Tidy can't tell which name is right." : Item.RenameBlockedReason;
    public bool CanRemove => DisabledReason is null;
    /// <summary>A rename frees no space.</summary>
    public long Bytes => 0;
    public PlanItem? Action => CanRemove ? DuplicatePlanner.ForRename(Item.Path, Item.SuggestedPath!, Item.Size, Item.LastWriteUtc) : null;
    public override string AutomationName => (Item.IsSecurityWarning ? "Security warning: " : "") + $"{Title}, {Folder}";
    public override string HelpText => Reason;

    public bool IsChecked
    {
        get => _isChecked;
        set { if (SetProperty(ref _isChecked, value && CanRemove)) _owner.SelectionChanged(); }
    }

    internal void SetChecked(bool value) { _isChecked = value && CanRemove; OnPropertyChanged(nameof(IsChecked)); }
}

/// <summary>A reference folder in the list (never removed from).</summary>
public sealed record ReferenceFolderRow(string Path, string Display);
