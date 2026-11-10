using PcTidy.Core;
using PcTidy.TestbedGen.Content;
using PcTidy.TestbedGen.Io;

namespace PcTidy.TestbedGen.Layout;

/// <summary>Everything a layout area needs: where things go, fake content, the writer, and the lists of
/// canaries / placeholders / junctions / tagged files that tests use as expected results.</summary>
internal sealed class Ctx
{
    private readonly List<Canary> _canaries = new();
    private readonly List<string> _placeholders = new();
    private readonly List<JunctionInfo> _junctions = new();
    private readonly List<CatalogEntry> _catalog = new();
    private readonly List<(string Link, string Target)> _pendingJunctions = new();

    public Ctx(string root, SandboxProfile profile, SimHardware hardware, DateTime now, int seed, FsWriter writer)
    {
        Root = root;
        P = profile;
        Hw = hardware;
        Now = now;
        Seed = seed;
        W = writer;
        K = new SandboxKnownLocations(root, profile);
        F = new Fake(seed);
    }

    public string Root { get; }
    public SandboxProfile P { get; }
    public SimHardware Hw { get; }
    public SandboxKnownLocations K { get; }
    public DateTime Now { get; }
    public int Seed { get; }
    public Fake F { get; }
    public FsWriter W { get; }

    private SharedFiles? _shared;
    /// <summary>Created on first use; areas always run in the same order, so the seeded content is stable.</summary>
    public SharedFiles Shared => _shared ??= new SharedFiles(this);

    /// <summary>Bulk files per area (bulk files are not catalogued one by one).</summary>
    public SortedDictionary<string, int> BulkCounts { get; } = new(StringComparer.Ordinal);

    public IReadOnlyList<Canary> Canaries => _canaries;
    public IReadOnlyList<string> Placeholders => _placeholders;
    public IReadOnlyList<JunctionInfo> Junctions => _junctions;
    public IReadOnlyList<CatalogEntry> Catalog => _catalog;

    /// <summary>Fake second drive (<c>&lt;root&gt;\D</c>); only meaningful when the profile has one.</summary>
    public string DriveD => Path.Combine(Root, "D");
    public string DriveC => Path.Combine(Root, "C");
    public string OneDriveRoot => Path.Combine(K.UserProfile, "OneDrive");

    public DateTime Ago(double days) => Now - TimeSpan.FromDays(days);

    /// <summary>Plain substring, not Path.GetRelativePath: that normalises away trailing dots and spaces.</summary>
    public string Rel(string path) => path.StartsWith(Root + "\\", StringComparison.OrdinalIgnoreCase)
        ? path[(Root.Length + 1)..]
        : throw new ArgumentException("Path is outside the testbed: " + path);

    /// <summary>Writes a file <paramref name="ageDays"/> old and catalogues it with <paramref name="tags"/> (comma separated).</summary>
    public string Put(string path, byte[] data, double ageDays, string tags = "", FileAttributes attributes = 0)
    {
        W.File(path, data, Ago(ageDays), attributes: attributes);
        if (tags.Length > 0) _catalog.Add(new CatalogEntry(Rel(path), tags));
        return path;
    }

    /// <summary>Sparse file: real header bytes, then <paramref name="length"/> logical bytes that cost no disk space.</summary>
    public string Sparse(string path, byte[] header, long length, double ageDays, string tags)
    {
        W.File(path, header, Ago(ageDays), sparseLength: length);
        _catalog.Add(new CatalogEntry(Rel(path), tags + ",sparse,big"));
        return path;
    }

    public string EmptyDir(string path, double ageDays, string tags = "empty-folder")
    {
        W.Dir(path, Ago(ageDays));
        _catalog.Add(new CatalogEntry(Rel(path), tags));
        return path;
    }

    public void Never(string path, string why) => _canaries.Add(new Canary(Rel(path), CanaryTier.Never, false, why));
    public void Default(string path, string why) => _canaries.Add(new Canary(Rel(path), CanaryTier.Default, false, why));
    public void NeverTree(string dir, string why) => _canaries.Add(new Canary(Rel(dir), CanaryTier.Never, true, why));
    public void DefaultTree(string dir, string why) => _canaries.Add(new Canary(Rel(dir), CanaryTier.Default, true, why));

    /// <summary>Puts a file and registers it as a canary in one go.</summary>
    public string Canary(string path, byte[] data, double ageDays, string why, CanaryTier tier = CanaryTier.Never)
    {
        Put(path, data, ageDays, tier == CanaryTier.Never ? "canary" : "canary-default");
        _canaries.Add(new Canary(Rel(path), tier, false, why));
        return path;
    }

    /// <summary>A file standing in for a OneDrive online-only placeholder: listed in Sim\placeholders.txt and given the
    /// Offline attribute so <c>CloudFile.IsPlaceholder</c> is true. Its bytes are real, so opening it wouldn't fail:
    /// tests must assert it was never touched.</summary>
    public string Placeholder(string path, byte[] data, double ageDays)
    {
        Put(path, data, ageDays, "placeholder", FileAttributes.Offline);
        _placeholders.Add(Rel(path));
        _canaries.Add(new Canary(Rel(path), CanaryTier.Never, false, "OneDrive online-only file: never opened, moved or downloaded"));
        return path;
    }

    /// <summary>Junctions are made after all files, so a scan of the target is complete before the link exists.</summary>
    public void Junction(string link, string target)
    {
        _pendingJunctions.Add((link, target));
        _junctions.Add(new JunctionInfo(Rel(link), Rel(target)));
        _catalog.Add(new CatalogEntry(Rel(link), "junction"));
    }

    public void CreatePendingJunctions()
    {
        foreach (var (link, target) in _pendingJunctions) W.Junction(link, target);
    }

    public void Tag(string path, string tags) => _catalog.Add(new CatalogEntry(Rel(path), tags));

    public byte[] Photo(int seed, DateTime? taken = null, int quality = 90, double scale = 1.0, int width = 96, int height = 64)
        => Images.Encode(new PictureSpec(seed, width, height, false, quality, scale, taken));

    public byte[] Png(int seed, int width = 120, int height = 80) => Images.Encode(new PictureSpec(seed, width, height, Png: true));
}
