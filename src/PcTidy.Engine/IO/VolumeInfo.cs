using System.Collections.Concurrent;
using System.Text;

namespace PcTidy.Engine.IO;

/// <summary>Read-only facts about the volume a path lives on (file system name → how precisely it keeps file times).</summary>
internal static class VolumeInfo
{
    private static readonly ConcurrentDictionary<string, string> FileSystems = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"NTFS", "ReFS", "FAT32", "exFAT"... or "" when unknown.</summary>
    public static string FileSystemOf(string path)
    {
        var root = VolumeRoot(path);
        if (root is null) return "";
        return FileSystems.GetOrAdd(root, r =>
        {
            var fs = new StringBuilder(64);
            return NativeMethods.GetVolumeInformation(r, null, 0, out _, out _, out _, fs, fs.Capacity) ? fs.ToString() : "";
        });
    }

    /// <summary>
    /// How far a copied file's modified time may differ from the original on this volume: FAT keeps 2-second steps,
    /// exFAT 10 ms, NTFS/ReFS keep the exact 100 ns value.
    /// </summary>
    public static TimeSpan TimeTolerance(string path) => FileSystemOf(path).ToUpperInvariant() switch
    {
        "FAT" or "FAT32" or "FAT16" or "FAT12" => TimeSpan.FromSeconds(2),
        "EXFAT" => TimeSpan.FromMilliseconds(10),
        _ => TimeSpan.Zero,
    };

    /// <summary>Volume mount point ("D:\", or a mounted folder) for an existing path or its nearest existing parent.</summary>
    private static string? VolumeRoot(string path)
    {
        var display = LongPath.Display(LongPath.Io(path));
        var sb = new StringBuilder(display.Length + 2);
        if (NativeMethods.GetVolumePathName(display, sb, sb.Capacity)) return sb.ToString();
        return Path.GetPathRoot(display);
    }
}
