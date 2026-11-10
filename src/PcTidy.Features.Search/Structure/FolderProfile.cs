using PcTidy.Core;

namespace PcTidy.Features.Search.Structure;

/// <summary>What a folder mostly holds (by bytes, or by its name when the contents don't say).</summary>
public enum FolderKind { Empty, Mixed, Photos, Videos, Music, Documents, Installers, Archives, Backups, Games, Projects }

/// <summary>A top-level folder, summarised from a bounded read-only walk (names and sizes only).</summary>
public sealed record FolderProfile(string Path, string Name, FolderKind Kind, long Bytes, int Files, DateTime? NewestUtc, bool Sampled, string KindReason);

/// <summary>Reads a folder's make-up without opening any file (names, sizes and dates only; links never followed).</summary>
public static class FolderProfiler
{
    /// <summary>Walking stops after this many entries; the kind is decided from the sample.</summary>
    public const int MaxEntries = 20_000;

    private static readonly Dictionary<string, FolderKind> ExtensionKinds = BuildExtensions();

    private static readonly (string Word, FolderKind Kind)[] NameHints =
    {
        ("photo", FolderKind.Photos), ("picture", FolderKind.Photos), ("camera", FolderKind.Photos), ("dcim", FolderKind.Photos), ("wallpaper", FolderKind.Photos),
        ("video", FolderKind.Videos), ("movie", FolderKind.Videos), ("film", FolderKind.Videos), ("clips", FolderKind.Videos),
        ("music", FolderKind.Music), ("audio", FolderKind.Music), ("songs", FolderKind.Music), ("podcast", FolderKind.Music),
        ("backup", FolderKind.Backups),
        ("game", FolderKind.Games),
        ("install", FolderKind.Installers), ("setup", FolderKind.Installers), ("software", FolderKind.Installers), ("drivers", FolderKind.Installers),
        ("archive", FolderKind.Archives), ("old ", FolderKind.Archives),
        ("project", FolderKind.Projects), ("source", FolderKind.Projects), ("repos", FolderKind.Projects), ("code", FolderKind.Projects),
        ("doc", FolderKind.Documents), ("paper", FolderKind.Documents), ("scan", FolderKind.Documents), ("work", FolderKind.Documents),
    };

    public static FolderProfile Profile(string folder, IFileWalker walker, CancellationToken ct)
    {
        var name = System.IO.Path.GetFileName(folder.TrimEnd('\\'));
        var bytesByKind = new Dictionary<FolderKind, long>();
        long bytes = 0;
        int files = 0, entries = 0;
        DateTime? newest = null;
        bool sampled = false;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            walker.Walk(folder, new WalkOptions { Recurse = true, IncludeHidden = false, IncludeSystem = false, MaxDepth = 12 }, e =>
            {
                if (++entries > MaxEntries) { sampled = true; stop.Cancel(); return false; }
                if (e.IsDirectory) return !e.IsReparsePoint;
                files++;
                bytes += e.Size;
                if (newest is null || e.LastTouchedUtc > newest) newest = e.LastTouchedUtc;
                var kind = KindOfExtension(e.Name);
                bytesByKind[kind] = bytesByKind.GetValueOrDefault(kind) + Math.Max(1, e.Size);
                return false;
            }, stop.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* sample limit reached */ }

        var (k, why) = Decide(name, files, bytes, bytesByKind);
        return new FolderProfile(folder, name, k, bytes, files, newest, sampled, why);
    }

    /// <summary>Contents first (a kind with more than half the bytes), then the folder's name, else "mixed".</summary>
    internal static (FolderKind Kind, string Why) Decide(string name, int files, long bytes, IReadOnlyDictionary<FolderKind, long> bytesByKind)
    {
        if (files == 0) return (FolderKind.Empty, "It's empty");
        var total = bytesByKind.Values.Sum();
        var top = bytesByKind.Where(kv => kv.Key != FolderKind.Mixed).OrderByDescending(kv => kv.Value).FirstOrDefault();
        if (top.Value > 0 && top.Value * 2 > total)
            return (top.Key, $"Mostly {Describe(top.Key)} ({Sizes.Format(bytes)})");
        var lower = name.ToLowerInvariant() + " ";
        foreach (var (word, kind) in NameHints)
            if (lower.Contains(word, StringComparison.Ordinal)) return (kind, $"Its name says {Describe(kind)}");
        return (FolderKind.Mixed, $"A mix of files ({Sizes.Format(bytes)})");
    }

    public static string Describe(FolderKind kind) => kind switch
    {
        FolderKind.Photos => "photos",
        FolderKind.Videos => "videos",
        FolderKind.Music => "music",
        FolderKind.Documents => "documents",
        FolderKind.Installers => "installers",
        FolderKind.Archives => "archives",
        FolderKind.Backups => "backups",
        FolderKind.Games => "games",
        FolderKind.Projects => "projects",
        FolderKind.Empty => "nothing",
        _ => "a mix of files",
    };

    internal static FolderKind KindOfExtension(string name)
    {
        var dot = name.LastIndexOf('.');
        if (dot <= 0 || dot == name.Length - 1) return FolderKind.Mixed;
        return ExtensionKinds.GetValueOrDefault(name[(dot + 1)..].ToLowerInvariant(), FolderKind.Mixed);
    }

    private static Dictionary<string, FolderKind> BuildExtensions()
    {
        var d = new Dictionary<string, FolderKind>(StringComparer.Ordinal);
        void Add(FolderKind k, string list) { foreach (var e in list.Split(' ')) d[e] = k; }
        Add(FolderKind.Photos, "jpg jpeg png heic heif gif webp bmp tif tiff raw cr2 cr3 nef arw dng orf rw2 psd xmp aae");
        Add(FolderKind.Videos, "mp4 mkv mov avi wmv m4v webm mts m2ts mpg mpeg 3gp");
        Add(FolderKind.Music, "mp3 flac wav m4a aac ogg wma opus aiff alac mid");
        Add(FolderKind.Documents, "pdf doc docx xls xlsx ppt pptx odt ods odp txt md rtf csv epub pages numbers key one");
        Add(FolderKind.Installers, "exe msi msix msixbundle appx appxbundle iso img");
        Add(FolderKind.Archives, "zip 7z rar tar gz tgz bz2 xz cab");
        Add(FolderKind.Backups, "bak wbcat vhd vhdx tib");
        return d;
    }
}
