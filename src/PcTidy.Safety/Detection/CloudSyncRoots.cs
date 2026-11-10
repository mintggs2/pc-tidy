using System.Security.Principal;
using System.Text.Json;
using System.Xml;
using Microsoft.Win32;

namespace PcTidy.Safety.Detection;

/// <summary>
/// Folders a cloud or sync service keeps in sync: every OneDrive account (registry + environment variables),
/// any provider registered with Windows' Cloud Files API (OneDrive, Dropbox, Google Drive mirror, iCloud...),
/// Dropbox's info.json, Google Drive's mount point (best effort) and Syncthing's configured folders.
/// Missing services simply contribute nothing.
/// </summary>
public static class CloudSyncRoots
{
    public static IReadOnlyList<string> Detect(string localAppData, string roamingAppData)
    {
        var raw = new List<string>();
        raw.AddRange(DetectionUtil.Safe(() => DetectionUtil.ReadFromSubKeys(RegistryHive.CurrentUser, RegistryView.Default,
            @"Software\Microsoft\OneDrive\Accounts", "UserFolder")));
        foreach (var env in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            var v = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrWhiteSpace(v)) raw.Add(v);
        }
        raw.AddRange(DetectionUtil.Safe(RegisteredSyncRoots));
        raw.AddRange(DetectionUtil.Safe(() => Dropbox(localAppData, roamingAppData)));
        raw.AddRange(DetectionUtil.Safe(GoogleDrive));
        raw.AddRange(DetectionUtil.Safe(() => Syncthing(localAppData)));
        return raw
            .Select(DetectionUtil.CleanPath)
            .OfType<string>()
            .Where(DetectionUtil.DirectoryExists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>HKLM\...\SyncRootManager\&lt;provider&gt;\UserSyncRoots: value name = user SID, data = folder.</summary>
    private static IEnumerable<string> RegisteredSyncRoots()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (sid is null) yield break;
        const string manager = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager";
        using var root = DetectionUtil.OpenKey(RegistryHive.LocalMachine, RegistryView.Registry64, manager);
        foreach (var provider in DetectionUtil.SubKeyNames(root))
        {
            using var roots = DetectionUtil.OpenKey(RegistryHive.LocalMachine, RegistryView.Registry64, $@"{manager}\{provider}\UserSyncRoots");
            var path = DetectionUtil.ReadString(roots, sid);
            if (!string.IsNullOrWhiteSpace(path)) yield return path;
        }
    }

    /// <summary>Dropbox writes {"personal": {"path": ...}, "business": {"path": ...}} to info.json.</summary>
    public static IEnumerable<string> ParseDropboxInfo(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) yield break;
        foreach (var account in doc.RootElement.EnumerateObject())
        {
            if (account.Value.ValueKind == JsonValueKind.Object
                && account.Value.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String
                && p.GetString() is { Length: > 0 } path)
                yield return path;
        }
    }

    private static IEnumerable<string> Dropbox(string localAppData, string roamingAppData)
    {
        foreach (var dir in new[] { localAppData, roamingAppData })
        {
            var info = Path.Combine(dir, "Dropbox", "info.json");
            if (!File.Exists(info)) continue;
            foreach (var p in ParseDropboxInfo(File.ReadAllText(info))) yield return p;
        }
    }

    /// <summary>
    /// Google Drive for desktop: streaming mode mounts a virtual drive ("DefaultMountPoint" policy/preference,
    /// or a volume labelled "Google Drive"); mirror mode registers with the Cloud Files API (handled above).
    /// </summary>
    private static IEnumerable<string> GoogleDrive()
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            using var key = DetectionUtil.OpenKey(hive, RegistryView.Registry64, @"Software\Google\DriveFS");
            var mount = DetectionUtil.ReadString(key, "DefaultMountPoint");
            if (string.IsNullOrWhiteSpace(mount)) continue;
            mount = mount.Trim();
            yield return mount.Length == 1 && char.IsAsciiLetter(mount[0]) ? mount + ":\\" : mount;
        }
        foreach (var drive in DriveInfo.GetDrives())
        {
            string? label = null;
            try { if (drive.IsReady) label = drive.VolumeLabel; } catch { /* not ready */ }
            if (label?.Equals("Google Drive", StringComparison.OrdinalIgnoreCase) == true) yield return drive.RootDirectory.FullName;
        }
    }

    /// <summary>Syncthing lists its folders in config.xml as &lt;folder path="..."&gt;.</summary>
    public static IEnumerable<string> ParseSyncthingConfig(string xml)
    {
        var doc = new XmlDocument { XmlResolver = null };
        using (var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
            doc.Load(reader);
        var folders = doc.SelectNodes("/configuration/folder");
        if (folders is null) yield break;
        foreach (XmlNode folder in folders)
            if (folder.Attributes?["path"]?.Value is { Length: > 0 } path) yield return path;
    }

    private static IEnumerable<string> Syncthing(string localAppData)
    {
        var config = Path.Combine(localAppData, "Syncthing", "config.xml");
        if (!File.Exists(config)) return Enumerable.Empty<string>();
        // Syncthing allows "~" for the home folder.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return ParseSyncthingConfig(File.ReadAllText(config)).Select(p => p.StartsWith('~') ? home + p[1..] : p).ToList();
    }
}
