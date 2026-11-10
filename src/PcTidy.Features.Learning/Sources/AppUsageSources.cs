using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PcTidy.Core;
using PcTidy.Features.Learning.Model;

namespace PcTidy.Features.Learning.Sources;

/// <summary>Reports how often apps were started. Read-only.</summary>
public interface IAppUsageSource
{
    IReadOnlyList<AppUsage> Read(CancellationToken ct);
}

/// <summary>Opens registry keys read-only. Injectable so tests read a fake tree under HKCU\Software\PCTidyTest\learning.</summary>
public interface IRegistryKeyReader
{
    /// <summary>Opens a key under HKCU read-only, or null when it doesn't exist or can't be read.</summary>
    RegistryKey? OpenCurrentUser(string path);
}

public sealed class WindowsRegistryKeyReader : IRegistryKeyReader
{
    public static readonly WindowsRegistryKeyReader Instance = new();

    public RegistryKey? OpenCurrentUser(string path)
    {
        try { return Registry.CurrentUser.OpenSubKey(path, writable: false); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException) { return null; }
    }
}

/// <summary>Reads the HKCU subtree <paramref name="root"/> as if it were HKCU (tests).</summary>
public sealed class RedirectedRegistryKeyReader(string root) : IRegistryKeyReader
{
    public RegistryKey? OpenCurrentUser(string path) => WindowsRegistryKeyReader.Instance.OpenCurrentUser($@"{root.Trim('\\')}\{path.Trim('\\')}");
}

/// <summary>
/// Windows' UserAssist launch records (HKCU\...\Explorer\UserAssist\{GUID}\Count): ROT13 value names, 72-byte data with
/// the run count at offset 4 and the last run FILETIME at offset 60. Only real programs (.exe outside Windows) and Store
/// apps ("Family!App") are kept; setup and uninstall programs are not apps. Read-only, no admin.
/// </summary>
public sealed class UserAssistSource : IAppUsageSource
{
    public const string UserAssistPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist";
    private static readonly Regex NotAnApp = new(@"(setup|install|uninst|unins\d|update|updater|helper|crashpad|elevate)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly IRegistryKeyReader _registry;
    private readonly IKnownLocations _locations;
    private readonly Dictionary<string, string> _knownFolders;

    public UserAssistSource(IRegistryKeyReader registry, IKnownLocations locations)
    {
        _registry = registry;
        _locations = locations;
        _knownFolders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["{6D809377-6AF0-444B-8957-A3773F02200E}"] = locations.ProgramFiles,
            ["{905E63B6-C1BF-494E-B29C-65B732D3D21A}"] = locations.ProgramFiles,
            ["{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}"] = locations.ProgramFilesX86,
            ["{F38BF404-1D43-42F2-9305-67DE0B28FC23}"] = locations.WindowsDir,
            ["{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}"] = Path.Combine(locations.WindowsDir, "System32"),
            ["{D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27}"] = Path.Combine(locations.WindowsDir, "SysWOW64"),
            ["{F1B32785-6FBA-4FCF-9D55-7B8E7F157091}"] = locations.LocalAppData,
            ["{3EB685DB-65F9-4CF6-A03A-E3EF65729F3D}"] = locations.RoamingAppData,
            ["{5E6C858F-0E22-4760-9AFE-EA3317B67173}"] = locations.UserProfile,
        };
    }

    public IReadOnlyList<AppUsage> Read(CancellationToken ct)
    {
        var best = new Dictionary<string, AppUsage>(StringComparer.OrdinalIgnoreCase);
        using var root = _registry.OpenCurrentUser(UserAssistPath);
        if (root is null) return Array.Empty<AppUsage>();
        foreach (var guid in Safe(root.GetSubKeyNames))
        {
            ct.ThrowIfCancellationRequested();
            using var count = _registry.OpenCurrentUser($@"{UserAssistPath}\{guid}\Count");
            if (count is null) continue;
            foreach (var valueName in Safe(count.GetValueNames))
            {
                if (ReadValue(count, valueName) is not byte[] data || Parse(data) is not { } parsed) continue;
                var target = ResolveKnownFolder(Rot13(valueName));
                if (!IsApp(target)) continue;
                var usage = new AppUsage(target, DisplayName(target), AppSource.UserAssist, parsed.RunCount, parsed.LastRunUtc);
                // The same program can appear under two GUIDs (direct start and shortcut): keep the newest record.
                if (!best.TryGetValue(target, out var old) || (usage.LastRunUtc ?? DateTime.MinValue) > (old.LastRunUtc ?? DateTime.MinValue))
                    best[target] = usage with { RunCount = Math.Max(usage.RunCount, old?.RunCount ?? 0) };
            }
        }
        return best.Values.ToList();
    }

    private bool IsApp(string target)
    {
        var bang = target.IndexOf('!');
        if (bang > 0 && !target.Contains('\\')) return !target.StartsWith("Microsoft.Windows.", StringComparison.OrdinalIgnoreCase);
        if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !Path.IsPathFullyQualified(target)) return false;
        if (PathUtil.IsUnder(target, _locations.WindowsDir)) return false;
        return !NotAnApp.IsMatch(Path.GetFileNameWithoutExtension(target));
    }

    private static string DisplayName(string target)
    {
        var bang = target.IndexOf('!');
        if (bang > 0 && !target.Contains('\\'))
        {
            var family = target[..bang];
            var underscore = family.IndexOf('_');
            var name = underscore > 0 ? family[..underscore] : family;
            var dot = name.LastIndexOf('.');
            return dot >= 0 && dot < name.Length - 1 ? name[(dot + 1)..] : name;
        }
        return Path.GetFileNameWithoutExtension(target);
    }

    public static string Rot13(string s)
    {
        var chars = s.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (c is >= 'a' and <= 'z') chars[i] = (char)('a' + (c - 'a' + 13) % 26);
            else if (c is >= 'A' and <= 'Z') chars[i] = (char)('A' + (c - 'A' + 13) % 26);
        }
        return new string(chars);
    }

    /// <summary>Run count and last run of a Windows 7+ record; null for other sizes. Zero/implausible times = unknown.</summary>
    public static (int RunCount, DateTime? LastRunUtc)? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 68) return null;
        var count = Math.Max(0, BinaryPrimitives.ReadInt32LittleEndian(data[4..]));
        var fileTime = BinaryPrimitives.ReadInt64LittleEndian(data[60..]);
        DateTime? last = null;
        if (fileTime > 0)
        {
            try
            {
                var d = DateTime.FromFileTimeUtc(fileTime);
                if (d.Year >= 1995 && d <= DateTime.UtcNow.AddDays(2)) last = d;
            }
            catch (ArgumentOutOfRangeException) { /* not a valid time */ }
        }
        return (count, last);
    }

    private string ResolveKnownFolder(string target)
        => target.Length > 38 && target[0] == '{' && target[37] == '}' && _knownFolders.TryGetValue(target[..38], out var folder)
            ? folder + target[38..] : target;

    private static object? ReadValue(RegistryKey key, string name)
    {
        try { return key.GetValue(name); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    private static string[] Safe(Func<string[]> read)
    {
        try { return read(); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return Array.Empty<string>(); }
    }
}

/// <summary>
/// Steam games and when they were last played, from each library's appmanifest_*.acf ("LastPlayed", Unix seconds;
/// 0 = Steam never saw it played, which PC Tidy treats as "unknown", not "never"). Read-only; no Steam = nothing.
/// </summary>
public sealed class SteamPlayedSource : IAppUsageSource
{
    private static readonly Regex Field = new("^\\s*\"(?<k>[^\"]+)\"\\s+\"(?<v>[^\"]*)\"", RegexOptions.CultureInvariant);
    private readonly IKnownLocations _locations;

    public SteamPlayedSource(IKnownLocations locations) => _locations = locations;

    public IReadOnlyList<AppUsage> Read(CancellationToken ct)
    {
        var result = new List<AppUsage>();
        foreach (var library in _locations.SteamLibraries)
        {
            var apps = Path.Combine(library, "steamapps");
            IEnumerable<string> manifests;
            try { manifests = Directory.Exists(apps) ? Directory.EnumerateFiles(apps, "appmanifest_*.acf").ToList() : Enumerable.Empty<string>(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in manifests)
            {
                ct.ThrowIfCancellationRequested();
                if (ParseManifest(file) is { } usage) result.Add(usage);
            }
        }
        return result;
    }

    public static AppUsage? ParseManifest(string path)
    {
        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        string? id = null, name = null;
        long lastPlayed = 0;
        foreach (var line in lines)
        {
            var m = Field.Match(line);
            if (!m.Success) continue;
            var (k, v) = (m.Groups["k"].Value, m.Groups["v"].Value);
            if (k.Equals("appid", StringComparison.OrdinalIgnoreCase)) id ??= v;
            else if (k.Equals("name", StringComparison.OrdinalIgnoreCase)) name ??= v;
            else if (k.Equals("LastPlayed", StringComparison.OrdinalIgnoreCase)) long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out lastPlayed);
        }
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name)) return null;
        DateTime? last = lastPlayed > 0 ? DateTimeOffset.FromUnixTimeSeconds(lastPlayed).UtcDateTime : null;
        // Steam has no launch counter; a newer LastPlayed is what tells PC Tidy the game was played.
        return new AppUsage("steam:" + id, name, AppSource.Steam, 0, last);
    }
}
