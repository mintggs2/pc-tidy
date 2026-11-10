using System.Buffers.Binary;
using System.Text;
using PcTidy.Core;
using PcTidy.Features.Learning.Model;

namespace PcTidy.Features.Learning.Sources;

/// <summary>Free space on every fixed drive right now. Read-only.</summary>
public interface IDriveSpaceSource
{
    IReadOnlyList<FreeSpaceSample> Read(DateTime nowUtc);
}

/// <summary>From <see cref="ISystemInfo.Drives"/> (WMI on the real PC, simulated in a sandbox).</summary>
public sealed class SystemInfoDriveSpace(ISystemInfo system) : IDriveSpaceSource
{
    public IReadOnlyList<FreeSpaceSample> Read(DateTime nowUtc)
    {
        try
        {
            return system.Drives.Where(d => d.TotalBytes > 0)
                .Select(d => new FreeSpaceSample(d.Root, nowUtc, d.TotalBytes, d.FreeBytes)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Array.Empty<FreeSpaceSample>();
        }
    }
}

/// <summary>Folders the user recently opened anywhere in Windows. Optional, off by default.</summary>
public interface IRecentFoldersSource
{
    /// <summary>Folders with the time they were used, newer than <paramref name="afterUtc"/>.</summary>
    IReadOnlyList<FolderVisit> Read(DateTime? afterUtc, CancellationToken ct);
}

/// <summary>
/// Windows' Recent items folder (%APPDATA%\Microsoft\Windows\Recent): one shortcut per recently opened file or folder.
/// Each shortcut's target is read from its LinkInfo block (MS-SHLLINK), read-only; the shortcut's time is when it was
/// used. Files count as a visit to their folder. Never opens the targets themselves.
/// </summary>
public sealed class WindowsRecentFolders : IRecentFoldersSource
{
    private const int MaxLinkBytes = 64 * 1024;
    private readonly string _recentDir;

    public WindowsRecentFolders(IKnownLocations locations)
        => _recentDir = Path.Combine(locations.RoamingAppData, "Microsoft", "Windows", "Recent");

    public IReadOnlyList<FolderVisit> Read(DateTime? afterUtc, CancellationToken ct)
    {
        var result = new List<FolderVisit>();
        List<FileInfo> links;
        try { links = Directory.Exists(_recentDir) ? new DirectoryInfo(_recentDir).EnumerateFiles("*.lnk").ToList() : new List<FileInfo>(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return result; }
        foreach (var link in links)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (afterUtc is { } after && link.LastWriteTimeUtc <= after) continue;
                if (link.Length > MaxLinkBytes) continue;
                var target = TargetOf(File.ReadAllBytes(link.FullName));
                if (target is null) continue;
                var folder = Directory.Exists(target) ? target : Path.GetDirectoryName(target);
                if (folder is { Length: > 3 } && Directory.Exists(folder)) result.Add(new FolderVisit(PathUtil.Normalize(folder), link.LastWriteTimeUtc, VisitSource.WindowsRecent));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // vanished or locked shortcut: skip it
            }
        }
        return result;
    }

    /// <summary>The local target path of a .lnk file (LinkInfo LocalBasePath + CommonPathSuffix), or null.</summary>
    public static string? TargetOf(ReadOnlySpan<byte> lnk)
    {
        const int HeaderSize = 0x4C;
        if (lnk.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(lnk) != HeaderSize) return null;
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(lnk[0x14..]);
        int pos = HeaderSize;
        if ((flags & 0x1) != 0) // HasLinkTargetIDList
        {
            if (pos + 2 > lnk.Length) return null;
            pos += 2 + BinaryPrimitives.ReadUInt16LittleEndian(lnk[pos..]);
        }
        if ((flags & 0x2) == 0 || pos + 28 > lnk.Length) return null; // no LinkInfo
        var info = lnk[pos..];
        var infoSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(info);
        var infoHeader = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[4..]);
        var infoFlags = BinaryPrimitives.ReadUInt32LittleEndian(info[8..]);
        if (infoSize > info.Length || (infoFlags & 0x1) == 0) return null; // not a local path
        info = info[..infoSize];
        var baseOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[16..]);
        var suffixOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[24..]);
        string? basePath = null, suffix = null;
        if (infoHeader >= 0x24 && info.Length >= 0x24)
        {
            basePath = Utf16At(info, (int)BinaryPrimitives.ReadUInt32LittleEndian(info[28..]));
            suffix = Utf16At(info, (int)BinaryPrimitives.ReadUInt32LittleEndian(info[32..]));
        }
        basePath ??= AnsiAt(info, baseOffset);
        suffix ??= AnsiAt(info, suffixOffset);
        if (string.IsNullOrEmpty(basePath)) return null;
        var full = basePath + (suffix ?? "");
        return Path.IsPathFullyQualified(full) ? full : null;
    }

    private static string? AnsiAt(ReadOnlySpan<byte> s, int offset)
    {
        if (offset <= 0 || offset >= s.Length) return null;
        var end = s[offset..].IndexOf((byte)0);
        return end < 0 ? null : Encoding.Latin1.GetString(s.Slice(offset, end));
    }

    private static string? Utf16At(ReadOnlySpan<byte> s, int offset)
    {
        if (offset <= 0 || offset >= s.Length) return null;
        var sb = new StringBuilder();
        for (int i = offset; i + 1 < s.Length; i += 2)
        {
            var ch = (char)BinaryPrimitives.ReadUInt16LittleEndian(s[i..]);
            if (ch == '\0') return sb.ToString();
            sb.Append(ch);
        }
        return null;
    }
}
