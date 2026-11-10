using System.Text;

namespace PcTidy.Features.Speed.Common;

/// <summary>What a .lnk shortcut points at.</summary>
public sealed record ShortcutInfo(string? TargetPath, string? Arguments);

/// <summary>
/// Reads the target and arguments of a Windows shortcut (.lnk, MS-SHLLINK format) straight from its bytes.
/// Read-only: the shortcut is never resolved through the shell (which could search drives or touch the network)
/// and its target is never opened.
/// </summary>
public static class ShortcutReader
{
    private const int HeaderSize = 0x4C;
    private const uint HasLinkTargetIdList = 0x1, HasLinkInfo = 0x2, HasName = 0x4, HasRelativePath = 0x8,
        HasWorkingDir = 0x10, HasArguments = 0x20, HasIconLocation = 0x40, IsUnicode = 0x80;
    private const uint EnvironmentBlockSignature = 0xA0000001;
    private const int MaxShortcutBytes = 1 << 20;

    /// <summary>Reads a shortcut file. Null when it isn't a readable shortcut or is an online-only file.</summary>
    public static ShortcutInfo? Read(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if (CloudFile.IsPlaceholder(attributes) || (attributes & FileAttributes.ReparsePoint) != 0) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < HeaderSize || stream.Length > MaxShortcutBytes) return null;
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            var info = Parse(bytes);
            if (info?.TargetPath is { } t && !Path.IsPathRooted(t))
                info = info with { TargetPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path) ?? "", t)) };
            return info;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Parses shortcut bytes. Null when they aren't a shortcut.</summary>
    public static ShortcutInfo? Parse(ReadOnlySpan<byte> b)
    {
        if (b.Length < HeaderSize || BitConverter.ToUInt32(b[..4]) != HeaderSize) return null;
        var flags = BitConverter.ToUInt32(b.Slice(20, 4));
        var pos = HeaderSize;

        if ((flags & HasLinkTargetIdList) != 0)
        {
            if (pos + 2 > b.Length) return null;
            pos += 2 + BitConverter.ToUInt16(b.Slice(pos, 2));
        }

        string? target = null;
        if ((flags & HasLinkInfo) != 0)
        {
            if (pos + 4 > b.Length) return null;
            var linkInfoSize = (int)BitConverter.ToUInt32(b.Slice(pos, 4));
            if (linkInfoSize < 0x1C || pos + linkInfoSize > b.Length) return null;
            target = LocalBasePath(b.Slice(pos, linkInfoSize));
            pos += linkInfoSize;
        }

        var unicode = (flags & IsUnicode) != 0;
        string? relative = null, arguments = null;
        foreach (var (flag, set) in new (uint, Action<string>)[]
                 {
                     (HasName, _ => { }), (HasRelativePath, s => relative = s), (HasWorkingDir, _ => { }),
                     (HasArguments, s => arguments = s), (HasIconLocation, _ => { }),
                 })
        {
            if ((flags & flag) == 0) continue;
            if (!ReadCountedString(b, ref pos, unicode, out var s)) return new ShortcutInfo(target ?? relative, arguments);
            set(s);
        }

        target ??= EnvironmentTarget(b, pos) ?? relative;
        return new ShortcutInfo(target, string.IsNullOrWhiteSpace(arguments) ? null : arguments.Trim());
    }

    private static string? LocalBasePath(ReadOnlySpan<byte> info)
    {
        var headerSize = (int)BitConverter.ToUInt32(info.Slice(4, 4));
        var linkFlags = BitConverter.ToUInt32(info.Slice(8, 4));
        if ((linkFlags & 0x1) == 0) return null; // VolumeIDAndLocalBasePath
        var localOffset = (int)BitConverter.ToUInt32(info.Slice(16, 4));
        var suffixOffset = (int)BitConverter.ToUInt32(info.Slice(24, 4));
        if (headerSize >= 0x24 && info.Length >= 0x24)
        {
            var localUnicode = (int)BitConverter.ToUInt32(info.Slice(28, 4));
            var suffixUnicode = (int)BitConverter.ToUInt32(info.Slice(32, 4));
            if (localUnicode > 0 && localUnicode < info.Length)
                return Join(UnicodeZ(info, localUnicode), suffixUnicode > 0 && suffixUnicode < info.Length ? UnicodeZ(info, suffixUnicode) : "");
        }
        if (localOffset <= 0 || localOffset >= info.Length) return null;
        return Join(AnsiZ(info, localOffset), suffixOffset > 0 && suffixOffset < info.Length ? AnsiZ(info, suffixOffset) : "");
    }

    private static string Join(string basePath, string suffix)
    {
        if (suffix.Length == 0) return basePath;
        return basePath.EndsWith('\\') ? basePath + suffix : basePath + "\\" + suffix;
    }

    /// <summary>The EnvironmentVariableDataBlock (target written with %VARS%), used by shortcuts without LinkInfo.</summary>
    private static string? EnvironmentTarget(ReadOnlySpan<byte> b, int pos)
    {
        while (pos + 8 <= b.Length)
        {
            var size = (int)BitConverter.ToUInt32(b.Slice(pos, 4));
            if (size < 8 || pos + size > b.Length) return null;
            if (BitConverter.ToUInt32(b.Slice(pos + 4, 4)) == EnvironmentBlockSignature && size >= 8 + 260 + 520)
            {
                var unicode = UnicodeZ(b.Slice(pos + 8 + 260, 520), 0);
                var text = unicode.Length > 0 ? unicode : AnsiZ(b.Slice(pos + 8, 260), 0);
                return text.Length == 0 ? null : Environment.ExpandEnvironmentVariables(text);
            }
            pos += size;
        }
        return null;
    }

    private static bool ReadCountedString(ReadOnlySpan<byte> b, ref int pos, bool unicode, out string value)
    {
        value = "";
        if (pos + 2 > b.Length) return false;
        var count = BitConverter.ToUInt16(b.Slice(pos, 2));
        pos += 2;
        var bytes = unicode ? count * 2 : count;
        if (pos + bytes > b.Length) return false;
        value = unicode ? Encoding.Unicode.GetString(b.Slice(pos, bytes)) : Encoding.Latin1.GetString(b.Slice(pos, bytes));
        pos += bytes;
        return true;
    }

    private static string AnsiZ(ReadOnlySpan<byte> b, int offset)
    {
        var s = b[offset..];
        var end = s.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? s : s[..end]);
    }

    private static string UnicodeZ(ReadOnlySpan<byte> b, int offset)
    {
        var s = b[offset..];
        var end = 0;
        while (end + 1 < s.Length && (s[end] != 0 || s[end + 1] != 0)) end += 2;
        return Encoding.Unicode.GetString(s[..end]);
    }
}
