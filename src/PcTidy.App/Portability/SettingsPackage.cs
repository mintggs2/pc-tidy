using System.IO.Compression;
using System.Text.Json;
using PcTidy.Core;

namespace PcTidy.App.Portability;

/// <summary>
/// What a .pctidy file says about itself. No user name, machine name or file list is in the package's settings (see
/// <see cref="SettingsPackage.ExportJson"/>); the learned data, which names folders on the PC, only when the user ticks it.
/// </summary>
public sealed record PackageManifest
{
    public const string FormatName = "pctidy-settings";
    public const int CurrentVersion = 1;

    public string Format { get; init; } = FormatName;
    public int Version { get; init; } = CurrentVersion;
    public string AppVersion { get; init; } = "";
    public DateTime CreatedUtc { get; init; }
    /// <summary>The fixed drives of the PC it was made on ("C:\", "D:\"), so import can say which paths aren't here.</summary>
    public IReadOnlyList<string> Drives { get; init; } = Array.Empty<string>();
    public bool HasLearning { get; init; }
}

/// <summary>A .pctidy file read back and checked.</summary>
public sealed record LoadedPackage(PackageManifest Manifest, AppSettings Settings, byte[]? LearningDb);

/// <summary>A .pctidy file that can't be used, with the reason in plain words.</summary>
public sealed class PackageException(string message) : Exception(message);

/// <summary>
/// The .pctidy format (one zip): <c>manifest.json</c>, <c>settings.json</c> (every setting, including each section's
/// settings and rules) and optionally <c>learning/learned.db</c>. Reading accepts only those three entries, with size
/// limits, and never extracts anything to disk.
/// </summary>
public static class SettingsPackage
{
    public const string Extension = ".pctidy";
    public const string ManifestEntry = "manifest.json";
    public const string SettingsEntry = "settings.json";
    public const string LearningEntry = "learning/learned.db";

    private const long MaxFileBytes = 256L << 20;
    private const long MaxSettingsBytes = 8L << 20;
    private const long MaxLearningBytes = 200L << 20;
    private const int MaxEntries = 8;

    /// <summary>Stands for the user's own profile folder in an exported path ("%USERPROFILE%\Documents"), so no user name is in the file.</summary>
    public const string ProfileToken = "%USERPROFILE%";

    /// <summary>
    /// Writes a package. <paramref name="learningDb"/> is a consistent copy of the learning file, or null.
    /// The settings are written by <see cref="ExportJson"/> (without this PC's data folder or Organize's list of sorted files).
    /// </summary>
    public static void Write(Stream output, AppSettings settings, IReadOnlyList<string> drives, string appVersion, Stream? learningDb, DateTime nowUtc, string? userProfile = null)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        var manifest = new PackageManifest { AppVersion = appVersion, CreatedUtc = nowUtc, Drives = drives, HasLearning = learningDb is not null };
        WriteText(zip, ManifestEntry, JsonSerializer.Serialize(manifest, AppSettings.Json));
        WriteText(zip, SettingsEntry, ExportJson(settings, userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)).Json);
        if (learningDb is not null)
        {
            var e = zip.CreateEntry(LearningEntry, CompressionLevel.Optimal);
            using var s = e.Open();
            learningDb.CopyTo(s);
        }
    }

    /// <summary>
    /// The settings as they go into an export (EXP-3): preferences only. Left out: the data folder (it names this PC's
    /// account) and Organize's ledger (path, size and time of every file it ever sorted). Every path inside the user's
    /// profile is written as <see cref="ProfileToken"/>\…, so the account name isn't in the file; import puts the new
    /// PC's profile back in. <c>FolderPaths</c> counts the folder paths that remain (the export dialog says so).
    /// </summary>
    public static (string Json, int FolderPaths) ExportJson(AppSettings settings, string userProfile)
    {
        var node = JsonSerializer.SerializeToNode(settings, AppSettings.Json)!.AsObject();
        node.Remove(nameof(AppSettings.DataRoot));
        if (node[nameof(AppSettings.Features)]?["organize"] is System.Text.Json.Nodes.JsonObject organize) organize.Remove("Ledger");
        var profile = Path.TrimEndingDirectorySeparator(userProfile);
        int paths = 0;
        Rewrite(node, s =>
        {
            if (!LooksLikePath(s)) return s;
            paths++;
            if (profile.Length > 3 && (s.Equals(profile, StringComparison.OrdinalIgnoreCase) || s.StartsWith(profile + "\\", StringComparison.OrdinalIgnoreCase)))
                return ProfileToken + s[profile.Length..];
            return s;
        });
        return (node.ToJsonString(AppSettings.Json), paths);
    }

    /// <summary>On import: <see cref="ProfileToken"/>\… becomes this PC's profile folder again.</summary>
    internal static void ExpandProfile(System.Text.Json.Nodes.JsonNode node, string userProfile)
    {
        var profile = Path.TrimEndingDirectorySeparator(userProfile);
        Rewrite(node, s => s.StartsWith(ProfileToken, StringComparison.OrdinalIgnoreCase) && (s.Length == ProfileToken.Length || s[ProfileToken.Length] == '\\')
            ? profile + s[ProfileToken.Length..]
            : s);
    }

    private static bool LooksLikePath(string v)
        => v.Length >= 3 && ((char.IsAsciiLetter(v[0]) && v[1] == ':' && v[2] is '\\' or '/') || v.StartsWith(@"\\", StringComparison.Ordinal));

    /// <summary>Replaces every string value in the tree (property names are left alone).</summary>
    private static void Rewrite(System.Text.Json.Nodes.JsonNode? node, Func<string, string> map)
    {
        switch (node)
        {
            case System.Text.Json.Nodes.JsonObject o:
                foreach (var key in o.Select(p => p.Key).ToList())
                {
                    if (o[key] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s)) o[key] = map(s);
                    else Rewrite(o[key], map);
                }
                break;
            case System.Text.Json.Nodes.JsonArray a:
                for (int i = 0; i < a.Count; i++)
                {
                    if (a[i] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s)) a[i] = map(s);
                    else Rewrite(a[i], map);
                }
                break;
        }
    }

    private static void WriteText(ZipArchive zip, string name, string text)
    {
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(e.Open(), new System.Text.UTF8Encoding(false));
        w.Write(text);
    }

    /// <summary>Reads and checks a package. Throws <see cref="PackageException"/> with a friendly reason.</summary>
    public static LoadedPackage Read(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new PackageException("That file isn't there any more.");
            if (info.Length > MaxFileBytes) throw new PackageException("That file is far too big to be a PC Tidy settings file.");
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Read(fs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PackageException("PC Tidy couldn't open that file: " + ex.Message);
        }
    }

    public static LoadedPackage Read(Stream input)
    {
        ZipArchive zip;
        try { zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException) { throw new PackageException("That isn't a PC Tidy settings file (it isn't a .pctidy package)."); }
        using (zip)
        {
            if (zip.Entries.Count > MaxEntries) throw new PackageException("That file has more in it than a PC Tidy settings file should.");
            var unknown = zip.Entries.Where(e => e.FullName is not (ManifestEntry or SettingsEntry or LearningEntry)).Select(e => e.FullName).ToList();
            if (unknown.Count > 0) throw new PackageException("That file contains things a PC Tidy settings file never has, so it wasn't used.");

            var manifest = ReadJson<PackageManifest>(zip, ManifestEntry, MaxSettingsBytes)
                           ?? throw new PackageException("That file has no description, so it isn't a PC Tidy settings file.");
            if (manifest.Format != PackageManifest.FormatName) throw new PackageException("That isn't a PC Tidy settings file.");
            if (manifest.Version > PackageManifest.CurrentVersion) throw new PackageException("That file was made by a newer PC Tidy. Update PC Tidy first, then import it.");
            var settings = ReadJson<AppSettings>(zip, SettingsEntry, MaxSettingsBytes, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
                           ?? throw new PackageException("That file has no settings in it.");
            byte[]? learning = null;
            if (zip.GetEntry(LearningEntry) is { } le)
            {
                if (le.Length > MaxLearningBytes) throw new PackageException("The learned data in that file is too big to be real.");
                learning = ReadBounded(le, MaxLearningBytes);
                if (!LooksLikeSqlite(learning)) throw new PackageException("The learned data in that file is damaged, so it wasn't used.");
            }
            return new LoadedPackage(manifest, settings, learning);
        }
    }

    /// <param name="userProfile">Set for the settings: <see cref="ProfileToken"/> paths are expanded to this profile first.</param>
    private static T? ReadJson<T>(ZipArchive zip, string name, long max, string? userProfile = null) where T : class
    {
        if (zip.GetEntry(name) is not { } e) return null;
        if (e.Length > max) throw new PackageException("That file's settings are too big to be real.");
        try
        {
            var bytes = ReadBounded(e, max);
            if (userProfile is null) return JsonSerializer.Deserialize<T>(bytes, AppSettings.Json);
            var node = System.Text.Json.Nodes.JsonNode.Parse(bytes);
            if (node is null) return null;
            ExpandProfile(node, userProfile);
            return node.Deserialize<T>(AppSettings.Json);
        }
        catch (JsonException) { throw new PackageException("That file's settings are damaged, so it wasn't used."); }
    }

    /// <summary>Reads an entry, refusing more than <paramref name="max"/> bytes whatever the zip claims (no zip bombs).</summary>
    private static byte[] ReadBounded(ZipArchiveEntry e, long max)
    {
        using var s = e.Open();
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0)
        {
            if (ms.Length + n > max) throw new PackageException("That file's contents are too big to be real.");
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }

    private static bool LooksLikeSqlite(byte[] b)
        => b.Length >= 16 && System.Text.Encoding.ASCII.GetString(b, 0, 15) == "SQLite format 3";
}
