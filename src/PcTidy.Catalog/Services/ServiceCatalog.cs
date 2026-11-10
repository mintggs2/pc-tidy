using PcTidy.Core.Model;

namespace PcTidy.Catalog.Services;

/// <summary>Evidence verdict for changing a service (same scale as the Tweaks section).</summary>
public enum ServiceVerdict
{
    /// <summary>Measured or documented benefit, no real downside for the stated situation. May be in a profile.</summary>
    Proven,
    /// <summary>Helps only in some setups (no printer, no Bluetooth...). Expert mode only, never in a profile.</summary>
    Situational,
    /// <summary>Popular in tweak lists, no measurable effect. Shown with the reason, never offered.</summary>
    Placebo,
    /// <summary>Breaks something (updates, security, audio, networking, Store...). Never offered; the "bad services" repair puts it back.</summary>
    Harmful,
    /// <summary>Critical or anti-cheat: never offered, shown greyed with the reason.</summary>
    Protected,
}

/// <summary>Named bundles in the Services section and in "Optimize &amp; Organize everything". Only Proven entries belong in one.</summary>
public static class ServiceProfiles
{
    public const string Gaming = "gaming";
    public const string LaptopBattery = "laptop-battery";
    public const string Privacy = "privacy";
}

/// <summary>
/// One Windows (or common third-party) service PC Tidy knows about.
/// </summary>
/// <param name="Name">Exact service key name (e.g. "DiagTrack"). Per-user template services end in "_*" (e.g. "CDPUserSvc_*").</param>
/// <param name="WhatItDoes">Plain English, one or two short sentences, no jargon.</param>
/// <param name="Verdict">Evidence verdict for changing it.</param>
/// <param name="WindowsDefaults">Windows 10/11 default start type(s). Where 10 and 11 differ, list both.</param>
/// <param name="Recommended">What PC Tidy may set it to (Manual or Disabled), or null when nothing is offered.</param>
/// <param name="Profiles">ServiceProfiles names it belongs to (Proven only).</param>
/// <param name="Why">Why the verdict: the benefit, or what breaks.</param>
/// <param name="BreaksIf">What stops working after the change (e.g. "printing", "Xbox app sign-in"). Null when nothing.</param>
/// <param name="Source">Where the evidence came from (doc URL or tool name). Null when from general knowledge (mark unverified).</param>
/// <param name="Windows11Only">True if it only exists on Windows 11.</param>
public sealed record ServiceEntry(
    string Name,
    string WhatItDoes,
    ServiceVerdict Verdict,
    IReadOnlyList<ServiceStart> WindowsDefaults,
    ServiceStart? Recommended,
    IReadOnlyList<string> Profiles,
    string Why,
    string? BreaksIf = null,
    string? Source = null,
    bool Windows11Only = false)
{
    public bool IsTemplate => Name.EndsWith("_*", StringComparison.Ordinal);

    /// <summary>
    /// Exact name; for a template "CDPUserSvc_*" also the template key itself ("CDPUserSvc", where changes must go)
    /// and a per-user instance ("CDPUserSvc_1a2b3c").
    /// </summary>
    public bool Matches(string serviceName)
        => IsTemplate
            ? serviceName.StartsWith(Name[..^1], StringComparison.OrdinalIgnoreCase) || string.Equals(Name[..^2], serviceName, StringComparison.OrdinalIgnoreCase)
            : string.Equals(Name, serviceName, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Every service PC Tidy has a verdict for. Data lives in <c>ServiceCatalogData.Entries</c> (research-owned file);
/// this class is the lookup the scanner, the guard and the bad-services repair share.
/// </summary>
public static class ServiceCatalog
{
    private static readonly Lazy<IReadOnlyList<ServiceEntry>> _all = new(() => ServiceCatalogData.Entries());

    public static IReadOnlyList<ServiceEntry> All => _all.Value;

    /// <summary>Exact entries win over template entries.</summary>
    public static ServiceEntry? Find(string serviceName)
        => All.FirstOrDefault(e => !e.IsTemplate && e.Matches(serviceName)) ?? All.FirstOrDefault(e => e.IsTemplate && e.Matches(serviceName));
}
