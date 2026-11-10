using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PcTidy.Core;
using PcTidy.TestbedGen.Content;
using PcTidy.TestbedGen.Io;
using PcTidy.TestbedGen.Layout;

namespace PcTidy.TestbedGen.Generation;

/// <summary>Runs the layout areas in a fixed order, then bulk files, junctions, the Sim\ metadata and folder times.</summary>
internal static class TestbedGenerator
{
    public const string SimFolder = "Sim";
    public const string MarkerFile = "testbed.json";
    public const string CanariesFile = "canaries.tsv";
    public const string PlaceholdersFile = "placeholders.txt";
    public const string CatalogFile = "catalog.tsv";
    public const string JunctionsFile = "junctions.tsv";

    public static TestbedInfo Generate(string root, TestbedSize size, SandboxProfile profile, TestbedOptions options, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        root = RootGuard.Validate(root);
        PrepareRoot(root, options.Force);

        var now = options.AnchorUtc?.ToUniversalTime() ?? DateTime.UtcNow;
        now = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        var hardware = options.Hardware ?? SimHardware.For(profile);
        int bulk = options.BulkFiles ?? (size == TestbedSize.Large ? 300_000 : 2_500);

        // Provisional marker first: an interrupted run can still be replaced with Force.
        Directory.CreateDirectory(Path.Combine(root, SimFolder));
        File.WriteAllText(Path.Combine(root, SimFolder, MarkerFile), "{\"tool\":\"PcTidy.TestbedGen\",\"status\":\"generating\"}");

        var writer = new FsWriter(root, now.AddDays(-30));
        var c = new Ctx(root, profile, hardware, now, options.Seed, writer);
        SystemArea.Build(c);
        BrowserArea.Build(c);
        AppsArea.Build(c);
        GamesArea.Build(c);
        DownloadsArea.Build(c);
        UserFoldersArea.Build(c);
        DevAndAiArea.Build(c);
        SecondDriveArea.Build(c);
        writer.Dir(c.K.DataRoot, now.AddDays(-1));    // PC Tidy's own data folder: left empty
        ct.ThrowIfCancellationRequested();
        BulkArea.Build(c, bulk, ct);
        c.CreatePendingJunctions();

        long files = writer.FileCount, bytes = writer.LogicalBytes;
        int dirs = writer.DirectoryCount;
        File.Delete(Path.Combine(root, SimFolder, MarkerFile));
        WriteSimFiles(c, size, bulk, files, dirs, bytes);
        writer.StampDirectories();

        return new TestbedInfo
        {
            Root = root, Profile = profile, Hardware = hardware, Locations = c.K, AnchorUtc = now, Seed = options.Seed, Size = size,
            Canaries = c.Canaries, Placeholders = c.Placeholders, Junctions = c.Junctions, Catalog = c.Catalog,
            BulkCounts = new Dictionary<string, int>(c.BulkCounts),
            FileCount = files, DirectoryCount = dirs, LogicalBytes = bytes, Elapsed = sw.Elapsed,
        };
    }

    private static void PrepareRoot(string root, bool force)
    {
        if (!Directory.Exists(root) || !Directory.EnumerateFileSystemEntries(root).Any())
        {
            Directory.CreateDirectory(root);
            return;
        }
        if (!TestbedCleaner.IsTestbed(root))
            throw new IOException($"'{root}' is not empty and is not a testbed made by this tool (no {SimFolder}\\{MarkerFile}). Refusing to touch it.");
        if (!force)
            throw new IOException($"'{root}' already holds a testbed. Pass --force (TestbedOptions.Force) to replace it.");
        TestbedCleaner.Delete(root);
        Directory.CreateDirectory(root);
    }

    /// <summary>Sim\ holds the simulated-hardware markers and the expected-results lists. Content depends only on
    /// seed, anchor, size, profile and hardware, so it is deterministic too.</summary>
    private static void WriteSimFiles(Ctx c, TestbedSize size, int bulk, long files, int dirs, long bytes)
    {
        var sim = Path.Combine(c.Root, SimFolder);
        void Write(string name, string text) => c.W.File(Path.Combine(sim, name), Fake.Text(text), c.Now);

        Write("sim.json", JsonSerializer.Serialize(c.Hw, SimHardware.Json));
        var marker = new
        {
            tool = "PcTidy.TestbedGen", version = 1, seed = c.Seed, size = size.ToString(), bulkFiles = bulk,
            anchorUtc = c.Now.ToString("O"), profile = c.P, files, directories = dirs, logicalBytes = bytes, bulk = c.BulkCounts,
        };
        Write(MarkerFile, JsonSerializer.Serialize(marker, SimHardware.Json));

        var canaries = new StringBuilder("# tier\tkind\tpath (relative to root)\twhy\n");
        foreach (var k in c.Canaries) canaries.Append($"{k.Tier}\t{(k.IsTree ? "tree" : "file")}\t{k.RelativePath}\t{k.Why}\n");
        Write(CanariesFile, canaries.ToString());

        var placeholders = new StringBuilder("# Files to treat as OneDrive online-only placeholders (they also carry the Offline attribute).\n");
        foreach (var p in c.Placeholders) placeholders.Append(p).Append('\n');
        Write(PlaceholdersFile, placeholders.ToString());

        var catalog = new StringBuilder("# path (relative to root)\ttags\n");
        foreach (var e in c.Catalog) catalog.Append($"{e.RelativePath}\t{e.Tags}\n");
        Write(CatalogFile, catalog.ToString());

        var junctions = new StringBuilder("# link\ttarget (both relative to root)\n");
        foreach (var j in c.Junctions) junctions.Append($"{j.RelativeLink}\t{j.RelativeTarget}\n");
        Write(JunctionsFile, junctions.ToString());
    }
}

/// <summary>Refuses roots where a fake PC could hurt the real one.</summary>
internal static class RootGuard
{
    public static string Validate(string root)
    {
        var full = PathUtil.Normalize(root);
        if (string.Equals(Path.GetPathRoot(full)?.TrimEnd('\\'), full.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A testbed can't be a drive root: " + full);
        var real = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        }.Where(p => p.Length > 0);
        foreach (var p in real)
        {
            if (PathUtil.IsUnder(p, full)) throw new ArgumentException($"A testbed can't contain the real '{p}': {full}");
            if (!p.Equals(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase) && PathUtil.IsUnder(full, p))
                throw new ArgumentException($"A testbed can't live inside the real '{p}': {full}");
        }
        return full;
    }
}

/// <summary>Deletes a testbed this tool made: extended-length paths (odd names, long paths), clears read-only,
/// removes junctions as links without ever entering them.</summary>
internal static class TestbedCleaner
{
    public static bool IsTestbed(string root)
        => File.Exists(LongPath.Ext(Path.Combine(root, TestbedGenerator.SimFolder, TestbedGenerator.MarkerFile)));

    public static void Delete(string root)
    {
        root = RootGuard.Validate(root);
        if (!Directory.Exists(root)) return;
        if (!IsTestbed(root)) throw new IOException($"'{root}' is not a testbed made by this tool; not deleting it.");
        DeleteTree(root);
    }

    private static void DeleteTree(string dir)
    {
        var ext = LongPath.Ext(dir);
        foreach (var entry in new DirectoryInfo(ext).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, RecurseSubdirectories = false }))
        {
            bool isLink = entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
            if (entry is DirectoryInfo sub)
            {
                if (isLink) sub.Delete();                 // removes the junction itself, target untouched
                else DeleteTree(LongPath.Strip(sub.FullName));
            }
            else
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReadOnly)) entry.Attributes &= ~FileAttributes.ReadOnly;
                entry.Delete();
            }
        }
        var self = new DirectoryInfo(ext);
        if (self.Attributes.HasFlag(FileAttributes.ReadOnly)) self.Attributes &= ~FileAttributes.ReadOnly;
        self.Delete();
    }
}
