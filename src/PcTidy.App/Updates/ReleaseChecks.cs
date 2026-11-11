using System.Text.RegularExpressions;

namespace PcTidy.App.Updates;

/// <summary>How this copy of PC Tidy was installed, which decides which release file updates it.</summary>
public enum InstallKind
{
    /// <summary>Installed with PC Tidy Setup (per machine, Program Files): updated by PC-Tidy-Setup-&lt;v&gt;.exe.</summary>
    Installed,
    /// <summary>Unpacked from the portable zip into a folder of the user's: updated by PC-Tidy-&lt;v&gt;-portable.zip.</summary>
    Portable,
}

/// <summary>The release files the updater may use. Every name is exact: nothing else in a release is ever downloaded.</summary>
public static class UpdateAssets
{
    public const string SumsName = "SHA256SUMS.txt";

    /// <summary>Release file names use letters, digits, dot, dash and underscore only (build-release.ps1 enforces it).</summary>
    private static readonly Regex SafeName = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.CultureInvariant);

    public static string ExpectedName(InstallKind kind, Version v) => kind == InstallKind.Installed
        ? $"PC-Tidy-Setup-{ReleaseVersion.Show(v)}.exe"
        : $"PC-Tidy-{ReleaseVersion.Show(v)}-portable.zip";

    /// <summary>A plain file name that is safe to use under the updates folder (no folders, no "..", no odd characters).</summary>
    public static bool IsSafeFileName(string? name) => name is not null && SafeName.IsMatch(name) && !name.Contains("..", StringComparison.Ordinal);

    /// <summary>
    /// Installed when PC Tidy's own "Installed apps" entry (<see cref="OwnProcesses.FindUninstaller()"/>) names the folder
    /// this PC Tidy runs from; anything else (no entry, or a copy running from another folder) is a portable copy.
    /// </summary>
    public static InstallKind Detect(UninstallEntry? entry, string appFolder)
    {
        if (entry is null) return InstallKind.Portable;
        try
        {
            var a = Path.GetFullPath(entry.InstallLocation).TrimEnd('\\');
            var b = Path.GetFullPath(appFolder).TrimEnd('\\');
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ? InstallKind.Installed : InstallKind.Portable;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return InstallKind.Portable; }
    }

    /// <summary>The one asset named exactly <paramref name="name"/>, or the plain reason there isn't one.</summary>
    public static (GitHubAsset? Asset, string? Problem) Pick(UpdateOffer offer, string name)
    {
        var matches = offer.Assets.Where(a => string.Equals(a.Name, name, StringComparison.Ordinal)).ToList();
        if (matches.Count == 0) return (null, $"PC Tidy {offer.VersionText} on GitHub has no file called {name}, so PC Tidy can't update itself this time. Nothing was downloaded. You can get it from the release page yourself.");
        if (matches.Count > 1) return (null, $"PC Tidy {offer.VersionText} on GitHub lists {name} more than once, so PC Tidy stopped. Nothing was downloaded.");
        return (matches[0], null);
    }
}

/// <summary>
/// SHA256SUMS.txt as build-release.ps1 writes it: one <c>&lt;64 hex digits&gt;  &lt;file name&gt;</c> per line
/// (sha256sum's format; a <c>*</c> binary marker, tabs, extra spaces and CRLF line ends are accepted).
/// </summary>
public static class Sha256Sums
{
    private static readonly Regex Line = new(@"^([0-9A-Fa-f]{64})[ \t]+\*?(.+?)[ \t]*$", RegexOptions.CultureInvariant);

    /// <summary>The lower-case SHA-256 listed for exactly <paramref name="fileName"/>, or the plain reason there is none.</summary>
    public static (string? Sha256, string? Problem) Find(string text, string fileName)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim('﻿').TrimStart(' ', '\t');
            var m = Line.Match(line);
            if (!m.Success) continue;
            if (string.Equals(m.Groups[2].Value, fileName, StringComparison.Ordinal)) found.Add(m.Groups[1].Value.ToLowerInvariant());
        }
        return found.Count switch
        {
            0 => (null, $"The release's {UpdateAssets.SumsName} doesn't list {fileName}, so the download can't be checked. Nothing was installed."),
            1 => (found.First(), null),
            _ => (null, $"The release's {UpdateAssets.SumsName} lists two different checksums for {fileName}, so PC Tidy stopped. Nothing was installed."),
        };
    }
}

/// <summary>
/// Proof that a downloaded file passed every release check (UPD-3): it came over HTTPS from the
/// mintggs2/pc-tidy Releases API (<see cref="ReleaseSource"/>), the release is neither a draft nor a pre-release, its
/// version is higher than the running one, the file name is exactly the expected one, and its SHA-256 matched the
/// line for that name in the same release's SHA256SUMS.txt. Only <see cref="UpdateChecker"/> creates one, right after
/// those checks. An unsigned PC Tidy accepts an unsigned update only with this proof (<see cref="UpdateTrust"/>).
/// </summary>
public sealed class VerifiedRelease
{
    internal VerifiedRelease(Version version, string tag, string assetName, string sha256, InstallKind kind)
    {
        Version = version;
        Tag = tag;
        AssetName = assetName;
        Sha256 = sha256;
        Kind = kind;
    }

    public Version Version { get; }
    public string Tag { get; }
    public string AssetName { get; }
    /// <summary>Lower-case hex, from the release's SHA256SUMS.txt (and equal to the download's own hash).</summary>
    public string Sha256 { get; }
    public InstallKind Kind { get; }
}
