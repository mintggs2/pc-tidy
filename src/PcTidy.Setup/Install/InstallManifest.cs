using System.Text.Json;

namespace PcTidy.Setup.Install;

/// <summary>
/// install.json in the install folder: exactly which files and shortcuts Setup created, so the uninstaller removes
/// those and nothing else (a user's own files in the folder are left alone).
/// </summary>
public sealed record InstallManifest
{
    public required string Version { get; init; }
    public DateTime InstalledUtc { get; init; } = DateTime.UtcNow;
    /// <summary>Installed files, relative to the install folder.</summary>
    public required IReadOnlyList<string> Files { get; init; }
    /// <summary>Shortcuts created (full paths).</summary>
    public IReadOnlyList<string> Shortcuts { get; init; } = Array.Empty<string>();
    /// <summary>Copy of Rescue in the data folder (full path), if made.</summary>
    public string? DataRescueCopy { get; init; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static InstallManifest? Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public void Save(string path)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>payload.json inside the payload zip (written by build-release.ps1).</summary>
public sealed record PayloadDescription(string Version)
{
    public const string FileName = "payload.json";

    public static PayloadDescription? Parse(string json)
    {
        try
        {
            var d = JsonSerializer.Deserialize<PayloadDescription>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return d is { Version.Length: > 0 } ? d : null;
        }
        catch (JsonException) { return null; }
    }
}
