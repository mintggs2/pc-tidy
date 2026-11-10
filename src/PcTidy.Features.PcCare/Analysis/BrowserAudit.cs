using PcTidy.Catalog.PcCare;
using PcTidy.Features.PcCare.Model;

namespace PcTidy.Features.PcCare.Analysis;

/// <summary>Something about an extension worth a second look.</summary>
public sealed record ExtensionFlag(string Text, bool Serious);

public sealed record ExtensionFinding(BrowserProfile Profile, BrowserExtension Extension, IReadOnlyList<ExtensionFlag> Flags, bool ForcedByPolicy);

public enum HijackArea { SearchEngine, HomePage, StartupPages }

public sealed record HijackFinding(BrowserProfile Profile, HijackArea Area, string Value);

/// <summary>
/// Read-only checks of browser settings and extensions. Nothing here is called malware: rows say what was found and
/// let you decide in the browser itself.
/// </summary>
public static class BrowserAudit
{
    /// <summary>Extensions with at least one flag (risky permissions, not from the store, forced by policy, disabled).</summary>
    public static IReadOnlyList<ExtensionFinding> Extensions(BrowserFacts facts)
    {
        var forced = facts.Forced.Select(f => (f.Browser, f.ExtensionId)).ToHashSet();
        var list = new List<ExtensionFinding>();
        foreach (var profile in facts.Profiles)
            foreach (var ext in profile.Extensions)
            {
                bool isForced = forced.Contains((profile.Browser, ext.Id)) || ext.Location is 7 or 9;
                var flags = FlagsFor(ext, isForced);
                if (flags.Count > 0) list.Add(new ExtensionFinding(profile, ext, flags, isForced));
            }
        return list;
    }

    internal static List<ExtensionFlag> FlagsFor(BrowserExtension ext, bool forced)
    {
        var flags = new List<ExtensionFlag>();
        if (forced) flags.Add(new ExtensionFlag("installed by a policy, so it can't be removed from the browser", true));
        else if (ext.Location is 2 or 3 or 6) flags.Add(new ExtensionFlag("added by another program on this PC", true));
        else if (ext.Location == 4) flags.Add(new ExtensionFlag("loaded from a folder (developer mode)", false));
        else if (!ext.FromStore) flags.Add(new ExtensionFlag("not from the browser's store", true));

        var risky = ext.Permissions.Select(ExtensionPermissionRisk.Find).Where(r => r is not null && r.Risk != PermissionRisk.Normal)
            .Select(r => r!).DistinctBy(r => r.Means).OrderByDescending(r => r.Risk).ToList();
        // Store extensions commonly need "every website" or cookies (ad blockers, password managers): those alone are
        // not worth a warning. A store extension is listed only for a high-risk permission; anything else for where it came from.
        if (flags.Count == 0 && !risky.Any(r => r.Risk == PermissionRisk.High)) return flags;
        foreach (var r in risky.Take(3)) flags.Add(new ExtensionFlag(r.Means, r.Risk == PermissionRisk.High));

        // A disabled extension is only noise unless something else is odd about it.
        if (!ext.Enabled && flags.Count > 0) flags.Add(new ExtensionFlag("turned off but still installed", false));
        return flags;
    }

    /// <summary>Search engine, home page and startup pages that aren't a well-known engine or the browser's own page.</summary>
    public static IReadOnlyList<HijackFinding> Settings(BrowserFacts facts)
    {
        var list = new List<HijackFinding>();
        foreach (var p in facts.Profiles)
        {
            if (!string.IsNullOrWhiteSpace(p.SearchUrl) && !KnownSearchEngines.IsKnownUrl(p.SearchUrl))
                list.Add(new HijackFinding(p, HijackArea.SearchEngine, p.SearchName is { Length: > 0 } n ? $"{n} ({HostOf(p.SearchUrl)})" : HostOf(p.SearchUrl)));
            if (!p.HomepageIsNewTab && !string.IsNullOrWhiteSpace(p.Homepage) && !KnownSearchEngines.IsKnownUrl(p.Homepage))
                list.Add(new HijackFinding(p, HijackArea.HomePage, HostOf(p.Homepage)));
            if (p.RestoreOnStartup == 4)
            {
                var odd = p.StartupUrls.Where(u => !KnownSearchEngines.IsKnownUrl(u)).Select(HostOf).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (odd.Count > 0) list.Add(new HijackFinding(p, HijackArea.StartupPages, string.Join(", ", odd.Take(3))));
            }
        }
        return list;
    }

    /// <summary>Installed programs matching the unwanted-program list.</summary>
    public static IReadOnlyList<(InstalledProgram Program, PupEntry Entry)> UnwantedPrograms(BrowserFacts facts)
        => facts.Programs.Select(p => (Program: p, Entry: PupCatalog.Match(p.DisplayName)))
            .Where(x => x.Entry is not null).Select(x => (x.Program, x.Entry!))
            .DistinctBy(x => x.Program.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The host of a URL (or the text itself, shortened, when it isn't one).</summary>
    public static string HostOf(string? url)
    {
        var u = (url ?? "").Trim().Replace("{searchTerms}", "q", StringComparison.OrdinalIgnoreCase);
        if (!u.Contains("://", StringComparison.Ordinal)) u = "https://" + u;
        if (Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Host.Length > 0) return uri.Host;
        var t = (url ?? "").Trim();
        return t.Length > 60 ? t[..57] + "..." : t;
    }
}
