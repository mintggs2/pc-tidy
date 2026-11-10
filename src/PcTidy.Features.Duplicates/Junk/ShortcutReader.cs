using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using PcTidy.Features.Duplicates.Hashing;

namespace PcTidy.Features.Duplicates.Junk;

public enum ShortcutTargetKind
{
    /// <summary>A path on this PC that can be checked.</summary>
    Local,
    /// <summary>A network share (\\server\share or a mapped network drive): can't be checked without the network.</summary>
    Network,
    /// <summary>A shell item (This PC, a Store app, an installer-managed shortcut...) or something unreadable.</summary>
    Unknown,
}

/// <summary>Where a shortcut points.</summary>
/// <param name="Path">The best path the shortcut records (shown to the user).</param>
public sealed record ShortcutTarget(ShortcutTargetKind Kind, string? Path)
{
    /// <summary>Other local paths the same shortcut records for its target (e.g. its relative path). It works if any exists.</summary>
    public IReadOnlyList<string> OtherPaths { get; init; } = Array.Empty<string>();

    /// <summary>
    /// <see cref="Path"/> was stored in a code page (not Unicode) and has non-ASCII letters, so its spelling may be wrong
    /// (the shortcut may come from a PC with another code page). Such a shortcut is never called broken.
    /// </summary>
    public bool MaybeMisread { get; init; }
}

/// <summary>
/// Reads where a .lnk points by parsing the Shell Link binary format ([MS-SHLLINK]) directly. Read-only: unlike
/// IShellLink::Resolve it never searches the disk, contacts the network or rewrites the shortcut.
/// Unicode strings are preferred; strings stored in the code page are decoded with this PC's ANSI code page.
/// </summary>
public static class ShortcutReader
{
    private const uint HasLinkTargetIdList = 0x1, HasLinkInfo = 0x2, HasName = 0x4, HasRelativePath = 0x8,
        HasWorkingDir = 0x10, HasArguments = 0x20, HasIconLocation = 0x40, IsUnicode = 0x80, HasDarwinId = 0x1000;
    private const uint EnvironmentBlockSignature = 0xA0000001;
    private const int MaxLnkBytes = 1 << 20;

    /// <summary>The code page Windows uses for non-Unicode text on this PC (what the shell wrote the ANSI strings in).</summary>
    private static readonly Lazy<Encoding> Ansi = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        int codePage;
        try { codePage = (int)GetACP(); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { codePage = CultureInfo.CurrentCulture.TextInfo.ANSICodePage; }
        try { return Encoding.GetEncoding(codePage); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
        }
    });

    public static ShortcutTarget Read(string lnkPath)
    {
        try
        {
            using var fs = new FileStream(Win32Path.Raw(lnkPath), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < 0x4C || fs.Length > MaxLnkBytes) return Unknown;
            var data = new byte[fs.Length];
            fs.ReadExactly(data);
            return Parse(data, System.IO.Path.GetDirectoryName(lnkPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Unknown;
        }
    }

    private static readonly ShortcutTarget Unknown = new(ShortcutTargetKind.Unknown, null);

    /// <summary>A decoded string, and whether its spelling is certain (Unicode, or plain ASCII in the code page).</summary>
    private readonly record struct Text(string Value, bool Certain);

    internal static ShortcutTarget Parse(byte[] d, string? lnkFolder)
    {
        if (BitConverter.ToUInt32(d, 0) != 0x4C) return Unknown;
        uint flags = BitConverter.ToUInt32(d, 20);
        // Installer-managed ("advertised") shortcuts are repaired by Windows Installer: never judged broken.
        if ((flags & HasDarwinId) != 0) return Unknown;
        int pos = 0x4C;
        if ((flags & HasLinkTargetIdList) != 0)
        {
            if (pos + 2 > d.Length) return Unknown;
            pos += 2 + BitConverter.ToUInt16(d, pos);
        }

        Text? local = null;
        bool network = false;
        if ((flags & HasLinkInfo) != 0 && pos + 28 <= d.Length)
        {
            int infoStart = pos;
            int infoSize = BitConverter.ToInt32(d, pos);
            int headerSize = BitConverter.ToInt32(d, pos + 4);
            uint infoFlags = BitConverter.ToUInt32(d, pos + 8);
            int localBaseOffset = BitConverter.ToInt32(d, pos + 16);
            int suffixOffset = BitConverter.ToInt32(d, pos + 24);
            if ((infoFlags & 1) != 0)
            {
                Text? basePath = null, suffix = null;
                // The Unicode fields only exist in the long header (0x24+); many shortcuts only carry the ANSI ones.
                if (headerSize >= 0x24 && infoStart + 36 <= d.Length)
                {
                    int baseU = BitConverter.ToInt32(d, infoStart + 28);
                    int suffixU = BitConverter.ToInt32(d, infoStart + 32);
                    if (baseU > 0) basePath = new Text(UnicodeZ(d, infoStart + baseU), true);
                    if (suffixU > 0) suffix = new Text(UnicodeZ(d, infoStart + suffixU), true);
                }
                basePath ??= localBaseOffset > 0 ? AnsiZ(d, infoStart + localBaseOffset) : null;
                suffix ??= suffixOffset > 0 ? AnsiZ(d, infoStart + suffixOffset) : null;
                if (basePath is { Value.Length: > 0 } b)
                    local = suffix is not { Value.Length: > 0 } s ? b : new Text(System.IO.Path.Combine(b.Value, s.Value), b.Certain && s.Certain);
            }
            if ((infoFlags & 2) != 0) network = true;
            pos = infoStart + Math.Max(infoSize, 0);
        }

        // String data: NAME, RELATIVE_PATH, WORKING_DIR, ARGUMENTS, ICON_LOCATION (each only if flagged).
        bool unicode = (flags & IsUnicode) != 0;
        Text? relative = null;
        foreach (var bit in new[] { HasName, HasRelativePath, HasWorkingDir, HasArguments, HasIconLocation })
        {
            if ((flags & bit) == 0) continue;
            if (pos + 2 > d.Length) return Unknown;
            int chars = BitConverter.ToUInt16(d, pos);
            pos += 2;
            int bytes = unicode ? chars * 2 : chars;
            if (pos + bytes > d.Length) return Unknown;
            if (bit == HasRelativePath)
                relative = unicode ? new Text(Encoding.Unicode.GetString(d, pos, bytes), true) : Decode(d, pos, bytes);
            pos += bytes;
        }

        // Extra data: an environment-variable target ("%ProgramFiles%\App\app.exe", Unicode) wins over the stored path.
        string? envTarget = null;
        while (pos + 8 <= d.Length)
        {
            int size = BitConverter.ToInt32(d, pos);
            if (size < 4) break;
            uint signature = BitConverter.ToUInt32(d, pos + 4);
            if (signature == EnvironmentBlockSignature && size >= 0x314 && pos + 0x314 <= d.Length)
                envTarget = UnicodeZ(d, pos + 8 + 260, 520);
            pos += size;
        }

        // Candidates, best first: the environment target, the stored absolute path, the path relative to the shortcut.
        var candidates = new List<Text>();
        if (!string.IsNullOrWhiteSpace(envTarget))
        {
            var expanded = Environment.ExpandEnvironmentVariables(envTarget);
            if (!expanded.Contains('%')) candidates.Add(new Text(expanded, true));
        }
        if (local is { } l) candidates.Add(l);
        else if (network && candidates.Count == 0) return new ShortcutTarget(ShortcutTargetKind.Network, null);
        if (relative is { Value.Length: > 0 } r && lnkFolder is not null)
        {
            try { candidates.Add(r with { Value = System.IO.Path.GetFullPath(System.IO.Path.Combine(lnkFolder, r.Value)) }); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        }

        if (candidates.Count == 0) return Unknown;
        var first = Classify(candidates[0].Value);
        if (first.Kind != ShortcutTargetKind.Local) return first;
        return first with
        {
            OtherPaths = candidates.Skip(1).Select(c => Classify(c.Value)).Where(c => c.Kind == ShortcutTargetKind.Local)
                .Select(c => c.Path!).Where(p => !string.Equals(p, first.Path, StringComparison.OrdinalIgnoreCase)).ToList(),
            MaybeMisread = !candidates[0].Certain,
        };
    }

    private static ShortcutTarget Classify(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return new ShortcutTarget(ShortcutTargetKind.Network, path);
        if (path.Length < 3 || path[1] != ':' || path[2] != '\\') return Unknown;
        return new ShortcutTarget(ShortcutTargetKind.Local, path);
    }

    private static string UnicodeZ(byte[] d, int offset, int maxBytes = int.MaxValue)
    {
        if (offset < 0 || offset >= d.Length) return "";
        int end = offset;
        int limit = (int)Math.Min(d.Length - 1L, (long)offset + maxBytes - 1);
        while (end + 1 <= limit && (d[end] != 0 || d[end + 1] != 0)) end += 2;
        return Encoding.Unicode.GetString(d, offset, end - offset);
    }

    private static Text AnsiZ(byte[] d, int offset)
    {
        if (offset < 0 || offset >= d.Length) return new Text("", true);
        int end = offset;
        while (end < d.Length && d[end] != 0) end++;
        return Decode(d, offset, end - offset);
    }

    /// <summary>Code-page text: exact when it's plain ASCII; otherwise decoded with this PC's ANSI code page, but not certain.</summary>
    private static Text Decode(byte[] d, int offset, int count)
    {
        bool ascii = true;
        for (int i = offset; i < offset + count; i++)
            if (d[i] >= 0x80) { ascii = false; break; }
        return new Text(Ansi.Value.GetString(d, offset, count), ascii);
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetACP();
}
