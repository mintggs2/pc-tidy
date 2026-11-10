using System.Diagnostics;
using System.Text;

namespace PcTidy.Features.Organize.Detection;

public readonly record struct InstallerVerdict(bool IsInstaller, string Why);

/// <summary>
/// Tells a real installer .exe from a portable tool (Plan §9: only installers go to "Installers";
/// portable apps stay put). Read-only: looks at the name, the version resource and a few well-known
/// installer-builder markers in the first and last part of the file. Never runs anything.
/// </summary>
public static class InstallerDetector
{
    /// <summary>Bytes read from the start of the file (stub, PE section table, resources, manifest).</summary>
    private const int HeadBytes = 2 * 1024 * 1024;
    /// <summary>Bytes read from the end (Inno/NSIS append their payload headers there on some versions).</summary>
    private const int TailBytes = 256 * 1024;

    private static readonly (string Marker, string Builder)[] Markers =
    {
        ("Nullsoft.NSIS", "NSIS"),
        ("NullsoftInst", "NSIS"),
        ("Inno Setup", "Inno Setup"),
        ("InstallShield", "InstallShield"),
        (".wixburn", "WiX"),
        ("WixBundle", "WiX"),
    };

    private static readonly byte[][] MarkerBytes = Markers.Select(m => Encoding.ASCII.GetBytes(m.Marker)).ToArray();

    /// <summary>
    /// <paramref name="mayReadContent"/> must be false for cloud placeholders (reading would download them):
    /// then only the name is used.
    /// </summary>
    public static InstallerVerdict Inspect(string path, bool mayReadContent)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (NameSaysInstaller(name)) return new(true, "The name says it's an installer.");
        if (!mayReadContent) return new(false, "Only the name was checked (file is online-only).");

        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            foreach (var field in new[] { info.FileDescription, info.ProductName, info.InternalName, info.OriginalFilename, info.Comments })
            {
                if (field is null) continue;
                if (field.Contains("Inno Setup", StringComparison.OrdinalIgnoreCase)) return new(true, "Built with Inno Setup.");
                if (NameSaysInstaller(field)) return new(true, "Its version info says it's an installer.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Unreadable version info: fall through to the marker scan.
        }

        try
        {
            var builder = FindBuilderMarker(path);
            if (builder is not null) return new(true, $"It's a {builder} installer package.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(false, "Couldn't read the file, so it was treated as a portable app.");
        }
        return new(false, "Looks like a portable app.");
    }

    /// <summary>"setup"/"install" anywhere in the text, but not uninstallers ("uninstall", "unins000").</summary>
    internal static bool NameSaysInstaller(string text)
    {
        var t = text.ToLowerInvariant();
        if (t.Contains("uninst")) return false;
        return t.Contains("setup") || t.Contains("install");
    }

    private static string? FindBuilderMarker(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024);
        var head = ReadAt(fs, 0, (int)Math.Min(HeadBytes, fs.Length));
        var found = Search(head);
        if (found is not null || fs.Length <= HeadBytes) return found;
        var tailStart = Math.Max(HeadBytes, fs.Length - TailBytes);
        return Search(ReadAt(fs, tailStart, (int)(fs.Length - tailStart)));
    }

    private static byte[] ReadAt(FileStream fs, long offset, int count)
    {
        var buf = new byte[count];
        fs.Position = offset;
        int read = 0, n;
        while (read < count && (n = fs.Read(buf, read, count - read)) > 0) read += n;
        return read == count ? buf : buf[..read];
    }

    private static string? Search(ReadOnlySpan<byte> data)
    {
        for (int i = 0; i < MarkerBytes.Length; i++)
            if (data.IndexOf(MarkerBytes[i]) >= 0) return Markers[i].Builder;
        return null;
    }
}
