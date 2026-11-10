namespace PcTidy.Catalog.PcCare;

public enum PermissionRisk { Normal, Notable, High }

/// <summary>One browser extension permission worth pointing out, with what it lets the extension do.</summary>
public sealed record RiskyPermission(string Permission, PermissionRisk Risk, string Means);

/// <summary>
/// Chrome/Edge/Brave extension permissions that deserve a second look (Chrome's permission list). Many good extensions
/// need some of these (ad blockers read every site); the audit shows them so you can decide, it never removes anything.
/// </summary>
public static class ExtensionPermissionRisk
{
    public static readonly IReadOnlyList<RiskyPermission> All = new RiskyPermission[]
    {
        new("debugger", PermissionRisk.High, "can control open tabs like a developer tool"),
        new("nativeMessaging", PermissionRisk.High, "can talk to a program installed on your PC"),
        new("proxy", PermissionRisk.High, "can send your browsing through another server"),
        new("management", PermissionRisk.High, "can turn your other extensions on or off"),
        new("webRequestBlocking", PermissionRisk.High, "can change pages and requests as they load"),
        new("<all_urls>", PermissionRisk.Notable, "can read and change every website you visit"),
        new("*://*/*", PermissionRisk.Notable, "can read and change every website you visit"),
        new("http://*/*", PermissionRisk.Notable, "can read and change every website you visit"),
        new("https://*/*", PermissionRisk.Notable, "can read and change every website you visit"),
        new("webRequest", PermissionRisk.Notable, "can see the requests pages make"),
        new("cookies", PermissionRisk.Notable, "can read your site logins (cookies)"),
        new("history", PermissionRisk.Notable, "can read your browsing history"),
        new("clipboardRead", PermissionRisk.Notable, "can read what you copy"),
        new("downloads", PermissionRisk.Notable, "can start and open downloads"),
    };

    private static readonly Dictionary<string, RiskyPermission> ByName =
        All.GroupBy(p => p.Permission, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    public static RiskyPermission? Find(string? permission)
        => permission is not null && ByName.TryGetValue(permission.Trim(), out var p) ? p : null;
}
