using PcTidy.Features.Duplicates.Hashing;
using PcTidy.Features.Duplicates.Model;
using PcTidy.Features.Duplicates.Planning;

namespace PcTidy.Features.Duplicates;

/// <summary>
/// Duplicates and junk for the standard flow and "Optimize &amp; Organize everything". Only the clear cases are
/// ticked (a Downloads, temp or backup copy of a file kept in a library folder); duplicate folders, other copies,
/// look-alike pictures and junk are listed unticked for the user to decide.
/// </summary>
public sealed class DuplicatesScanner : IFeatureScanner
{
    private readonly Func<ScanContext, HashCache> _cache;
    private readonly Func<bool>? _saveCache;

    /// <param name="cache">The shared hash cache for a scan (the module's, loaded from the data folder), so rescans are quick.</param>
    /// <param name="saveCache">Saves that cache after each scan (the module's <see cref="DuplicatesModule.SaveCache"/>).</param>
    public DuplicatesScanner(Func<ScanContext, HashCache>? cache = null, Func<bool>? saveCache = null)
    {
        var own = new Lazy<HashCache>(() => new HashCache());
        _cache = cache ?? (_ => own.Value);
        _saveCache = saveCache;
    }

    public string FeatureId => DuplicatesModule.FeatureId;
    public string Title => "Duplicates and junk";
    public Release Release => Release.V2;
    public bool IncludedInOptimizeAll => true;

    public async Task<ScanResult> ScanAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        // Look-alike pictures are never ticked and take the longest, so the one-click run only looks for them in Deep.
        // The file-type check opens every judged file, so the one-click run leaves it to Deep as well.
        var options = new DuplicateScanOptions { FindSimilarImages = ctx.Depth == ScanDepth.Deep, FindWrongExtensions = ctx.Depth == ScanDepth.Deep };
        var report = await new DuplicatesEngine(_cache(ctx)).ScanAsync(ctx, options, progress, ct).ConfigureAwait(false);
        if (_saveCache is not null) await Task.Run(_saveCache, CancellationToken.None).ConfigureAwait(false);
        return ToScanResult(report, DateTime.UtcNow - started);
    }

    /// <summary>The report as standard scan groups (removable copies only; the kept copy is named in each reason).</summary>
    public static ScanResult ToScanResult(DuplicatesReport r, TimeSpan duration)
    {
        var f = r.FriendlyPath;
        var groups = new List<ScanGroup>();
        void Add(string id, string title, string description, IEnumerable<ScanItem> items)
        {
            var list = items.ToList();
            if (list.Count > 0) groups.Add(new ScanGroup { Id = id, Title = title, Description = description, Badge = Badge.Check, Items = list });
        }

        Add("duplicates.exact", "Exact duplicates",
            "Files with exactly the same content. One copy of each is always kept. Only clear cases are ticked: a copy in Downloads, temp or backup folders of a file you keep in a library folder.",
            r.ExactGroups.SelectMany(g => g.Others.Select(c => DuplicatePlanner.CopyItem(g, c, f))));
        Add("duplicates.folders", "Duplicate folders",
            "Folders with exactly the same files inside. One folder of each set is always kept. Nothing here is ticked: check them first.",
            r.FolderGroups.SelectMany(g => g.Others.Select(c => DuplicatePlanner.CopyItem(g, c, f))));
        Add("duplicates.similar", "Look-alike pictures",
            "Pictures that look almost the same (resized, re-saved or edited). They aren't identical, so nothing is ticked: compare them first.",
            r.SimilarImages.SelectMany(g => g.Images.Where(i => !DuplicateGroup.PathEquals(i.Path, g.SuggestedKeep)).Select(i => DuplicatePlanner.SimilarScanItem(g, i, f))));
        Add("duplicates.probable", "Probably the same (not fully checked)",
            "Very large files (over 2 GB) with the same size whose start, middle and end match. Only samples were compared, so nothing here can be removed yet: open Duplicates and choose Check fully.",
            r.ProbableGroups.SelectMany(g => g.Others.Select(c => DuplicatePlanner.ProbableItem(g, c, f))));
        Add("duplicates.disguised", "Programs named like documents",
            "Security warning: these are programs (they can run) with the name of a document, picture or video. PC Tidy never renames them. Don't open them unless you know where they came from.",
            r.WrongExtensions.Where(w => w.IsSecurityWarning).Select(w => DuplicatePlanner.ExtensionItem(w, f)));
        Add("duplicates.wrong-extension", "Wrong file type in the name",
            "Files whose content is a different type than their name says (a .jpg that is really a PNG). Renaming is offered only when it's harmless; nothing is ticked.",
            r.WrongExtensions.Where(w => !w.IsSecurityWarning).Select(w => DuplicatePlanner.ExtensionItem(w, f)));
        Add("duplicates.empty-folders", "Empty folders",
            "Folders with nothing inside (their empty subfolders go with them). Windows' special folders and anything changed in the last day are left alone.",
            r.EmptyFolders.Select(j => DuplicatePlanner.JunkScanItem(j, f)));
        Add("duplicates.zero-byte", "Empty files",
            "Files with 0 bytes in them. Marker files apps rely on (like .gitkeep or .nomedia) are left alone.",
            r.ZeroByteFiles.Select(j => DuplicatePlanner.JunkScanItem(j, f)));
        Add("duplicates.broken-shortcuts", "Broken shortcuts",
            "Shortcuts whose app, file or folder is gone. Shortcuts to network places or unplugged drives can't be checked, so they're skipped.",
            r.BrokenShortcuts.Select(j => DuplicatePlanner.JunkScanItem(j, f)));

        return new ScanResult
        {
            FeatureId = DuplicatesModule.FeatureId,
            Title = "Duplicates and junk",
            Depth = r.Depth,
            Duration = duration,
            Groups = groups,
            Notes = r.Notes,
        };
    }
}
