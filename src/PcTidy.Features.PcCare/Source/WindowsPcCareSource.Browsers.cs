using System.Text.Json;
using Microsoft.Win32;
using PcTidy.Features.PcCare.Model;
using PcTidy.Features.PcCare.Parsing;

namespace PcTidy.Features.PcCare.Source;

/// <summary>Browser profiles (Chrome, Edge, Brave), browser policies and installed programs. Files are only read.</summary>
public sealed partial class WindowsPcCareSource
{
    /// <summary>Browser name, user-data folder under LocalAppData, policy key under SOFTWARE\Policies.</summary>
    internal static readonly IReadOnlyList<(string Browser, string UserData, string PolicyKey)> Chromium = new[]
    {
        ("Chrome", @"Google\Chrome\User Data", @"Google\Chrome"),
        ("Edge", @"Microsoft\Edge\User Data", @"Microsoft\Edge"),
        ("Brave", @"BraveSoftware\Brave-Browser\User Data", @"BraveSoftware\Brave"),
    };

    private static readonly string[] PinningPolicies = { "HomepageLocation", "NewTabPageLocation", "DefaultSearchProviderSearchURL" };

    private BrowserFacts ReadBrowsers()
    {
        var profiles = new List<BrowserProfile>();
        var forced = new List<ForcedExtension>();
        var policies = new List<BrowserPolicy>();
        foreach (var (browser, userData, policyKey) in Chromium)
        {
            var root = Path.Combine(_locations.LocalAppData, userData);
            if (Directory.Exists(root)) profiles.AddRange(ReadBrowserProfiles(browser, root));
            ReadPolicies(browser, policyKey, forced, policies);
        }
        return new BrowserFacts(profiles, forced, policies, ReadInstalledPrograms());
    }

    private static IEnumerable<BrowserProfile> ReadBrowserProfiles(string browser, string root)
    {
        var names = ProfileNames(ReadSharedText(Path.Combine(root, "Local State")));
        IEnumerable<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(root)
                .Where(d => Path.GetFileName(d) is var n && (n == "Default" || n.StartsWith("Profile ", StringComparison.Ordinal)))
                .Where(d => (File.GetAttributes(d) & FileAttributes.ReparsePoint) == 0)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }

        foreach (var dir in dirs)
        {
            var prefs = ReadSharedText(Path.Combine(dir, "Preferences"));
            var secure = ReadSharedText(Path.Combine(dir, "Secure Preferences"));
            if (prefs is null && secure is null) continue;
            var leaf = Path.GetFileName(dir);
            var extensionsRoot = Path.Combine(dir, "Extensions");
            yield return ChromiumProfileParser.Parse(browser, names.TryGetValue(leaf, out var n) ? n : leaf, dir, prefs, secure,
                (id, rel) => ReadManifest(extensionsRoot, id, rel));
        }
    }

    /// <summary>"Local State" → profile.info_cache.&lt;folder&gt;.name.</summary>
    private static Dictionary<string, string> ProfileNames(string? localState)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(localState)) return map;
        try
        {
            using var doc = JsonDocument.Parse(localState);
            if (doc.RootElement.TryGetProperty("profile", out var p) && p.TryGetProperty("info_cache", out var cache) && cache.ValueKind == JsonValueKind.Object)
                foreach (var e in cache.EnumerateObject())
                    if (e.Value.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                        map[e.Name] = name.GetString() ?? e.Name;
        }
        catch (JsonException) { }
        return map;
    }

    /// <summary>Extensions\&lt;id&gt;\&lt;version&gt;\manifest.json (the "path" from Preferences, else the newest version folder).</summary>
    private static ExtensionManifest? ReadManifest(string extensionsRoot, string id, string? relativePath)
    {
        string? folder = null;
        if (!string.IsNullOrWhiteSpace(relativePath) && !Path.IsPathRooted(relativePath) && !relativePath.Contains("..", StringComparison.Ordinal))
            folder = Path.Combine(extensionsRoot, relativePath);
        else
        {
            var idDir = Path.Combine(extensionsRoot, id);
            try
            {
                if (Directory.Exists(idDir)) folder = Directory.EnumerateDirectories(idDir).OrderByDescending(d => d, StringComparer.Ordinal).FirstOrDefault();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        if (folder is null) return null;
        var manifest = ReadSharedText(Path.Combine(folder, "manifest.json"), 4L << 20);
        return ChromiumProfileParser.ParseManifest(manifest, locale => ReadSharedText(Path.Combine(folder, "_locales", locale, "messages.json"), 4L << 20)
                                                                       ?? ReadSharedText(Path.Combine(folder, "_locales", locale.Replace('-', '_'), "messages.json"), 4L << 20));
    }

    private static void ReadPolicies(string browser, string policyKey, List<ForcedExtension> forced, List<BrowserPolicy> policies)
    {
        foreach (var (hive, scope) in new[] { (RegistryHive.LocalMachine, "machine"), (RegistryHive.CurrentUser, "user") })
        {
            var basePath = @"SOFTWARE\Policies\" + policyKey;
            using (var list = OpenRead(hive, basePath + @"\ExtensionInstallForcelist"))
                foreach (var name in list?.GetValueNames() ?? Array.Empty<string>())
                    if (list!.GetValue(name) is string v && v.Length >= 32)
                    {
                        var parts = v.Split(';', 2);
                        forced.Add(new ForcedExtension(browser, parts[0].Trim(), parts.Length > 1 ? parts[1].Trim() : null, scope));
                    }

            using (var key = OpenRead(hive, basePath))
                foreach (var name in PinningPolicies)
                    if (key?.GetValue(name) is string v && v.Length > 0) policies.Add(new BrowserPolicy(browser, name, v));

            using (var startup = OpenRead(hive, basePath + @"\RestoreOnStartupURLs"))
            {
                var urls = (startup?.GetValueNames() ?? Array.Empty<string>()).Select(n => startup!.GetValue(n) as string).Where(u => !string.IsNullOrEmpty(u)).ToList();
                if (urls.Count > 0) policies.Add(new BrowserPolicy(browser, "RestoreOnStartupURLs", string.Join(", ", urls)));
            }
        }
    }

    /// <summary>Installed programs from the Uninstall keys (machine 64/32-bit and the current user), without system components.</summary>
    internal static IReadOnlyList<InstalledProgram> ReadInstalledPrograms()
    {
        const string uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        var list = new List<InstalledProgram>();
        foreach (var (hive, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32), (RegistryHive.CurrentUser, RegistryView.Registry64) })
        {
            using var root = OpenRead(hive, uninstall, view);
            foreach (var sub in root?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                try
                {
                    using var k = root!.OpenSubKey(sub, writable: false);
                    if (k?.GetValue("DisplayName") is not string name || name.Length == 0) continue;
                    if (k.GetValue("SystemComponent") is int sc && sc == 1) continue;
                    list.Add(new InstalledProgram(name, k.GetValue("Publisher") as string));
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
            }
        }
        return list.DistinctBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
