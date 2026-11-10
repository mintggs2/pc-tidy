namespace PcTidy.Catalog.PcCare;

public enum PupKind { Toolbar, SearchHijacker, Adware, BundledBrowser, ScareRepairTool }

/// <summary>
/// A program widely classified as "potentially unwanted" (PUP/PUA) by Microsoft Defender or Malwarebytes.
/// <paramref name="NamePrefix"/> is matched against the start of an Uninstall entry's DisplayName (case-insensitive, whole words).
/// </summary>
public sealed record PupEntry(string NamePrefix, PupKind Kind, string Why, string Source);

/// <summary>
/// Known unwanted programs. Wording stays calm ("often flagged as unwanted"): PC Tidy never calls something malware,
/// never removes it by itself, and Defender stays the antivirus. Removal is the program's own uninstaller (Apps section).
/// </summary>
public static class PupCatalog
{
    private const string Toolbar = "A browser toolbar that changes your search and home page.";
    private const string Search = "Changes your browser's search engine or new-tab page to show its own ads.";
    private const string Ads = "Shows extra ads or pop-ups and is often installed bundled with other downloads.";
    private const string Browser = "A browser that's often installed bundled with other downloads and sets itself as the default.";
    private const string Scare = "A 'PC repair' or 'cleaner' tool known for exaggerated scan results that push you to pay.";

    public static readonly IReadOnlyList<PupEntry> All = new PupEntry[]
    {
        new("Ask Toolbar", PupKind.Toolbar, Toolbar, PcCareSources.MalwarebytesPup),
        new("Babylon Toolbar", PupKind.Toolbar, Toolbar, PcCareSources.MalwarebytesPup),
        new("Conduit", PupKind.Toolbar, Toolbar, PcCareSources.MalwarebytesPup),
        new("Delta Toolbar", PupKind.Toolbar, Toolbar, PcCareSources.MalwarebytesPup),
        new("MyWebSearch", PupKind.SearchHijacker, Search, PcCareSources.MalwarebytesPup),
        new("Search Protect", PupKind.SearchHijacker, Search, PcCareSources.MalwarebytesPup),
        new("Wajam", PupKind.Adware, Ads, PcCareSources.MalwarebytesPup),
        new("Superfish", PupKind.Adware, Ads, PcCareSources.MalwarebytesPup),
        new("WebDiscover", PupKind.BundledBrowser, Browser, PcCareSources.MalwarebytesPup),
        new("OneLaunch", PupKind.BundledBrowser, Browser, PcCareSources.MalwarebytesPup),
        new("Wave Browser", PupKind.BundledBrowser, Browser, PcCareSources.MalwarebytesPup),
        new("Segurazo", PupKind.ScareRepairTool, Scare, PcCareSources.MalwarebytesPup),
        new("SAntivirus", PupKind.ScareRepairTool, Scare, PcCareSources.MalwarebytesPup),
        new("ByteFence", PupKind.ScareRepairTool, Scare, PcCareSources.MalwarebytesPup),
        new("Reimage Repair", PupKind.ScareRepairTool, Scare, PcCareSources.MalwarebytesPup),
        new("Restoro", PupKind.ScareRepairTool, Scare, PcCareSources.MalwarebytesPup),
        new("PC Accelerate", PupKind.ScareRepairTool, Scare, PcCareSources.MalwarebytesPup),
    };

    /// <summary>The entry whose name starts the display name (followed by the end or a non-letter), or null.</summary>
    public static PupEntry? Match(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return null;
        var name = displayName.Trim();
        foreach (var e in All)
        {
            if (!name.StartsWith(e.NamePrefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Length == e.NamePrefix.Length || !char.IsLetterOrDigit(name[e.NamePrefix.Length])) return e;
        }
        return null;
    }
}
