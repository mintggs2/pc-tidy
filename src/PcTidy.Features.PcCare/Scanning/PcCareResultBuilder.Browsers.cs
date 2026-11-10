using PcTidy.Core.Model;
using PcTidy.Features.PcCare.Analysis;
using PcTidy.Features.PcCare.Model;
using PcTidy.Features.PcCare.Planning;
using T = PcTidy.Features.PcCare.Scanning.PcCareTabs;

namespace PcTidy.Features.PcCare.Scanning;

/// <summary>
/// Browsers tab (hijack and unwanted-program check). Browser files are never edited: fixes open the browser's own
/// reset/extension help, or Settings > Apps for a program's own uninstaller. Defender stays the antivirus.
/// </summary>
public static partial class PcCareResultBuilder
{
    private static void AddBrowsers(PcCareSnapshot s, List<ScanGroup> groups, Dictionary<string, string> notes)
    {
        var b = s.Browsers;
        if (b is null)
        {
            notes[T.Browsers] = "Browser settings weren't checked this time.";
            return;
        }

        var settings = BrowserAudit.Settings(b).Select(f => Pick(
            $"{f.Profile.Browser} ({f.Profile.ProfileName}): {AreaText(f.Area)} is {f.Value}",
            $"Not a well-known {AreaNoun(f.Area)}. If you didn't choose it, reset the browser's settings: type {PcCareLinks.InternalPage(f.Profile.Browser, "settings/reset")} in its address bar. Your bookmarks and passwords stay.",
            Badge.Check, new[] { PcCareActions.Open(PcCareLinks.ResetHelp(f.Profile.Browser), $"Open {f.Profile.Browser}'s reset help") })).ToList();
        groups.Add(Group(T.Browsers, "settings", "Search, home and startup pages",
            "Pages that aren't a well-known search engine or the browser's own page. Search hijackers change these to show ads.", Badge.Check, settings));

        var policies = b.Policies.Select(p => Disabled(
            $"{p.Browser}: {PolicyTitle(p.Name)} is set by a policy",
            $"Value: {BrowserAudit.HostOf(p.Value)}.",
            "A policy on this PC sets this, so the browser's reset can't change it. If no company or school manages this PC, an unwanted program probably set it: check the programs below and Apps.")).ToList();
        foreach (var f in b.Forced.GroupBy(f => f.Browser))
            policies.Add(Disabled($"{f.Key}: {Plural(f.Count(), "extension")} installed by a policy",
                "Policy-installed extensions can't be removed from the browser.",
                "Set by a policy on this PC. Expected on work or school PCs; otherwise an unwanted program probably added it."));
        groups.Add(Group(T.Browsers, "policies", "Set by a policy",
            "Browser settings a policy pins in place (shown so you know; PC Tidy doesn't change policies).", Badge.Check, policies));

        var extensions = BrowserAudit.Extensions(b).Select(f =>
        {
            var reason = Capitalize(string.Join("; ", f.Flags.Select(x => x.Text))) + ".";
            var detail = $"{f.Profile.Browser} · {f.Profile.ProfileName}" + (f.Extension.Enabled ? "" : " · turned off");
            var details = new Dictionary<string, string> { ["Extension id"] = f.Extension.Id, ["Browser"] = f.Profile.Browser };
            if (f.ForcedByPolicy)
                return Disabled(f.Extension.Name, reason, "A policy installed it, so the browser won't let you remove it. If no company manages this PC, look in Apps for the program that added it.", detail: detail);
            return Pick(f.Extension.Name, reason + $" To remove it, type {PcCareLinks.InternalPage(f.Profile.Browser, "extensions")} in the browser.",
                Badge.Check, new[] { PcCareActions.Open(PcCareLinks.ExtensionsHelp(f.Profile.Browser), $"Open {f.Profile.Browser}'s extensions help") },
                detail: detail, details: details);
        }).ToList();
        groups.Add(Group(T.Browsers, "extensions", "Extensions worth a look",
            "Extensions with wide access, from outside the store, or added by another program. Many good ones need some of this: you decide.", Badge.Check, extensions));

        var programs = BrowserAudit.UnwantedPrograms(b).Select(x => Pick(
            x.Program.DisplayName,
            x.Entry.Why + " Often flagged as unwanted by security companies. If you didn't mean to install it, uninstall it in Apps.",
            Badge.Check, new[] { PcCareActions.Open(PcCareLinks.Apps, "Open Settings > Apps") },
            detail: x.Program.Publisher)).ToList();
        groups.Add(Group(T.Browsers, "programs", "Programs often flagged as unwanted",
            "Toolbars, search changers and \"PC repair\" tools that security companies class as unwanted. Removal uses the program's own uninstaller.", Badge.Check, programs));

        int found = settings.Count + extensions.Count + programs.Count + policies.Count;
        notes[T.Browsers] = b.Profiles.Count == 0
            ? "No Chrome, Edge or Brave profile was found." + (programs.Count > 0 ? " Some programs are worth a look below." : "")
            : found == 0
                ? $"Checked {Plural(b.Profiles.Count, "browser profile")}: nothing unusual."
                : $"Checked {Plural(b.Profiles.Count, "browser profile")}. Nothing is changed for you: each fix opens the browser's own settings help or Settings > Apps.";
    }

    private static string AreaText(HijackArea a) => a switch
    {
        HijackArea.SearchEngine => "search engine",
        HijackArea.HomePage => "home page",
        _ => "startup page",
    };

    private static string AreaNoun(HijackArea a) => a == HijackArea.SearchEngine ? "search engine" : "page";

    private static string PolicyTitle(string name) => name switch
    {
        "HomepageLocation" => "the home page",
        "RestoreOnStartupURLs" => "the startup pages",
        "NewTabPageLocation" => "the new tab page",
        "DefaultSearchProviderSearchURL" => "the search engine",
        _ => name,
    };

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
