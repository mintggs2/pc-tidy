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
        // The full setup-data header, not just "Inno Setup" (which other programs mention in their text; v3.3 review D5).
        ("Inno Setup Setup Data", "Inno Setup"),
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
        // PortableApps.com: "X_1.2.paf.exe" is the installer, "XPortable.exe" is the app itself (NSIS-built, so the
        // marker scan would call it an installer: v3.3 review D5).
        if (name.EndsWith(".paf", StringComparison.OrdinalIgnoreCase)) return new(true, "A PortableApps.com installer package.");
        if (NameSaysPortable(name)) return new(false, "The name says it's a portable app.");
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

    /// <summary>
    /// A word starting with "setup" or "install" ("ChromeSetup", "npp.8.6.Installer.x64", "install_tool"), but never an
    /// uninstaller ("uninstall", "unins000"). Words are split at anything but letters and digits, and at lower→Upper case changes.
    /// </summary>
    internal static bool NameSaysInstaller(string text)
    {
        if (text.Contains("unins", StringComparison.OrdinalIgnoreCase)) return false;
        return Words(text).Any(w => w.StartsWith("setup", StringComparison.Ordinal) || w.StartsWith("install", StringComparison.Ordinal));
    }

    /// <summary>"Portable" as a word ("FooPortable", "foo-portable").</summary>
    internal static bool NameSaysPortable(string text) => Words(text).Any(w => w == "portable");

    private static readonly System.Text.RegularExpressions.Regex WordSplit =
        new(@"(?<=[a-z])(?=[A-Z])|[^A-Za-z0-9]+", System.Text.RegularExpressions.RegexOptions.CultureInvariant); // "bo2", "x64" stay one word

    internal static IEnumerable<string> Words(string text)
        => WordSplit.Split(text).Where(w => w.Length > 0).Select(w => w.ToLowerInvariant());

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
