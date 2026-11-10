using System.Text.Json;

namespace PcTidy.Safety.Detection;

/// <summary>One installed Epic Games item, read from its .item manifest (JSON).</summary>
public sealed record EpicApp(
    string AppName,
    string DisplayName,
    string InstallLocation,
    long InstallSize,
    string? LaunchExecutable,
    IReadOnlyList<string> ProcessNames,
    bool IsIncomplete);

/// <summary>Read-only reader for Epic Games Launcher manifests. PC Tidy never edits them.</summary>
public static class EpicManifests
{
    public static IEnumerable<EpicApp> Read(string? manifestsDir)
    {
        if (manifestsDir is null || !DetectionUtil.DirectoryExists(manifestsDir)) yield break;
        string[] files;
        try { files = Directory.GetFiles(manifestsDir, "*.item"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }

        foreach (var file in files)
        {
            var app = TryParse(file);
            if (app is not null) yield return app;
        }
    }

    private static EpicApp? TryParse(string file)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var location = DetectionUtil.CleanPath(Str(root, "InstallLocation"));
            if (location is null) return null;
            var processes = (Str(root, "ProcessNames") ?? "")
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            return new EpicApp(
                AppName: Str(root, "AppName") ?? Path.GetFileNameWithoutExtension(file),
                DisplayName: Str(root, "DisplayName") ?? Str(root, "AppName") ?? Path.GetFileName(location),
                InstallLocation: location,
                InstallSize: root.TryGetProperty("InstallSize", out var size) && size.TryGetInt64(out var n) ? n : 0,
                LaunchExecutable: Str(root, "LaunchExecutable"),
                ProcessNames: processes,
                IsIncomplete: root.TryGetProperty("bIsIncompleteInstall", out var inc) && inc.ValueKind == JsonValueKind.True);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
