using System.Text.Json;
using System.Text.Json.Serialization;
using PcTidy.Core;

namespace PcTidy.TestbedGen;

public enum TestbedSize
{
    /// <summary>About 3k files: fast enough for every end-to-end test.</summary>
    Small,
    /// <summary>About 300k files: performance tests only.</summary>
    Large,
}

public enum GpuVendor { NVIDIA, AMD, Intel }

/// <summary>
/// Simulated hardware/OS state, written to <c>&lt;root&gt;\Sim\sim.json</c>. Later features read it in sandbox
/// mode instead of asking the real PC (GPU vendor picks shader-cache folders, laptop picks power advice, ...).
/// </summary>
public sealed record SimHardware
{
    public GpuVendor GpuVendor { get; init; } = GpuVendor.NVIDIA;
    public bool IsLaptop { get; init; }
    public bool IsAdmin { get; init; }
    public bool RebootPending { get; init; }

    /// <summary>Hardware that fits a profile: the Default desktop has an NVIDIA card, the Minimal laptop Intel graphics.</summary>
    public static SimHardware For(SandboxProfile profile) => profile.SecondDrive || profile.Steam
        ? new SimHardware()
        : new SimHardware { GpuVendor = GpuVendor.Intel, IsLaptop = true };

    public const string RelativePath = @"Sim\sim.json";

    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Reads <c>Sim\sim.json</c> of a generated testbed.</summary>
    public static SimHardware Load(string root)
        => JsonSerializer.Deserialize<SimHardware>(File.ReadAllText(Path.Combine(root, RelativePath)), Json)
           ?? throw new InvalidDataException("sim.json is empty");
}

public sealed record TestbedOptions
{
    /// <summary>Fixed seed: same seed + same anchor + same root = byte-identical testbed.</summary>
    public int Seed { get; init; } = 20261110;
    /// <summary>Number of tiny "bulk" files. Null = 2,500 (Small) or 300,000 (Large).</summary>
    public int? BulkFiles { get; init; }
    /// <summary>"Now" that every file age is relative to. Null = current time (whole seconds).</summary>
    public DateTime? AnchorUtc { get; init; }
    /// <summary>Hardware markers. Null = <see cref="SimHardware.For"/> the profile.</summary>
    public SimHardware? Hardware { get; init; }
    /// <summary>Replace an existing testbed at the root (only one this tool made: needs <c>Sim\testbed.json</c>).</summary>
    public bool Force { get; init; }
}

/// <summary>Never = must survive every run, even explicit opt-ins. Default = must survive default-ticked runs
/// (only an explicit, unticked-by-default choice such as "browser cookies/history" may remove it).</summary>
public enum CanaryTier { Never, Default }

/// <summary>A file (or whole folder tree, when <see cref="IsTree"/>) that tests assert is unchanged after every run.</summary>
public sealed record Canary(string RelativePath, CanaryTier Tier, bool IsTree, string Why);

/// <summary>A generated file/folder with tags saying what it is meant to exercise (e.g. "installer,old").</summary>
public sealed record CatalogEntry(string RelativePath, string Tags);

public sealed record JunctionInfo(string RelativeLink, string RelativeTarget);

/// <summary>Result of <see cref="TestbedBuilder.Generate"/>. Paths are relative to <see cref="Root"/>; use <see cref="Abs"/>.</summary>
public sealed record TestbedInfo
{
    public required string Root { get; init; }
    public required SandboxProfile Profile { get; init; }
    public required SimHardware Hardware { get; init; }
    public required SandboxKnownLocations Locations { get; init; }
    public required DateTime AnchorUtc { get; init; }
    public required int Seed { get; init; }
    public required TestbedSize Size { get; init; }
    public required IReadOnlyList<Canary> Canaries { get; init; }
    /// <summary>Files to treat as OneDrive online-only (also carry the Offline attribute, so FileEntry.IsCloudPlaceholder is true).</summary>
    public required IReadOnlyList<string> Placeholders { get; init; }
    public required IReadOnlyList<JunctionInfo> Junctions { get; init; }
    /// <summary>Hand-placed files with tags (bulk files are not listed one by one).</summary>
    public required IReadOnlyList<CatalogEntry> Catalog { get; init; }
    /// <summary>Bulk tiny files per area (browser-cache, node_modules, user-temp, documents-notes, build-output, app-assets).</summary>
    public required IReadOnlyDictionary<string, int> BulkCounts { get; init; }
    public required long FileCount { get; init; }
    public required long DirectoryCount { get; init; }
    public required long LogicalBytes { get; init; }
    public required TimeSpan Elapsed { get; init; }

    public string Abs(string relativePath) => Path.Combine(Root, relativePath);

    /// <summary>Catalog entries carrying every one of the given tags.</summary>
    public IEnumerable<CatalogEntry> Tagged(params string[] tags)
        => Catalog.Where(e => tags.All(t => e.Tags.Split(',').Contains(t, StringComparer.OrdinalIgnoreCase)));
}
