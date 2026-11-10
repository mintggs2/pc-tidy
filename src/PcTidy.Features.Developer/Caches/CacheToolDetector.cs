using PcTidy.Catalog.Developer;
using PcTidy.Core;
using PcTidy.Features.Developer.Common;

namespace PcTidy.Features.Developer.Caches;

/// <summary>A package manager's cache as found on this PC.</summary>
/// <param name="Executable">Where the tool itself was found (PATH or its usual install folder), or null.</param>
/// <param name="Folders">Existing cache folders with their exact sizes.</param>
internal sealed record CacheFinding(DevCacheTool Tool, string? Executable, IReadOnlyList<(string Path, FolderMeasure.Totals Size)> Folders)
{
    public long Bytes => Folders.Sum(f => f.Size.Bytes);
}

/// <summary>
/// Finds which package managers exist (their program on PATH or in its usual install folder) and where their caches
/// are (environment variable overrides first, then the tool's default folder). Read-only: nothing is run.
/// </summary>
internal static class CacheToolDetector
{
    public static string? FindExecutable(DevContext dev, IReadOnlyList<string> executables, IReadOnlyList<CacheLocation> knownFolders)
    {
        var folders = dev.Environment.SearchPath().Concat(knownFolders.SelectMany(l => Resolve(dev, l)));
        foreach (var folder in folders)
            foreach (var exe in executables)
            {
                string candidate;
                try { candidate = Path.Combine(folder, exe); }
                catch (ArgumentException) { continue; }
                if (FolderMeasure.FileExists(candidate)) return candidate;
            }
        return null;
    }

    /// <summary>Where a location points on this PC. A set environment variable replaces the default.</summary>
    public static IReadOnlyList<string> Resolve(DevContext dev, CacheLocation location)
    {
        var k = dev.Locations;
        if (location.Variable is { } name && dev.Environment.Variable(name) is { } value && Path.IsPathFullyQualified(value))
            return new[] { location.VariableSubPath.Length > 0 ? Path.Combine(value, location.VariableSubPath) : value };
        return location.Base switch
        {
            CacheBase.LocalAppData => new[] { Path.Combine(k.LocalAppData, location.RelativePath) },
            CacheBase.RoamingAppData => new[] { Path.Combine(k.RoamingAppData, location.RelativePath) },
            CacheBase.UserProfile => new[] { Path.Combine(k.UserProfile, location.RelativePath) },
            CacheBase.UserTemp => new[] { Path.Combine(k.UserTemp, location.RelativePath) },
            CacheBase.ProgramFiles => new[] { Path.Combine(k.ProgramFiles, location.RelativePath) },
            CacheBase.EachFixedDriveRoot => k.FixedDrives.Select(d => Path.Combine(d, location.RelativePath)).ToArray(),
            _ => Array.Empty<string>(),
        };
    }

    public static CacheFinding Find(DevContext dev, DevCacheTool tool, CancellationToken ct)
    {
        var exe = FindExecutable(dev, tool.Executables, tool.KnownInstallFolders);
        var folders = tool.Caches.SelectMany(l => Resolve(dev, l))
            .Select(PathUtil.Normalize).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(FolderMeasure.DirectoryExists)
            .Select(p => (p, FolderMeasure.Measure(dev.Walker, p, ct)))
            .ToList();
        return new CacheFinding(tool, exe, folders);
    }
}
