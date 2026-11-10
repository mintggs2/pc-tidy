using System.IO.Hashing;
using System.Text;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Engine.IO;

namespace PcTidy.Engine.Handlers;

/// <summary>
/// Duplicates: right before a copy is quarantined, prove that it AND the copy that stays are both still exactly the
/// content the scan matched. Plan item Args (from the Duplicates section):
/// <list type="bullet">
/// <item><c>verifyHash</c>: XXH3-64 hex of the content (file mode: the file, as <c>FileHash.Full</c> writes it; folder
/// mode: the folder signature below).</item>
/// <item><c>keepPath</c>: the copy that stays.</item>
/// <item><c>verifyMode</c>: <c>file</c> (default) | <c>folder</c>.</item>
/// </list>
/// Folder signature (the Duplicates section's): for each folder, lines <c>F|NAME|size|hash</c> per file (hash "EMPTY" for
/// 0 bytes) and <c>D|NAME|signature</c> per subfolder, names upper-cased, lines sorted ordinally, joined with "\n",
/// XXH3-64 of the UTF-8 bytes. A folder with no files anywhere, a link or an online-only file has no signature (never
/// a match). Everything is read with literal paths and never follows links.
/// </summary>
internal static class DuplicateVerifier
{
    public const string HashArg = "verifyHash";
    public const string KeepArg = "keepPath";
    public const string ModeArg = "verifyMode";
    private const string EmptyFileHash = "EMPTY";

    public static bool Applies(PlanItem item) => item.Args?.ContainsKey(HashArg) == true;

    /// <summary>Why the duplicate must stay where it is, or null when both copies are still the same content.</summary>
    public static string? Problem(PlanItem item, string source, CancellationToken ct)
    {
        var args = item.Args!;
        var expected = args[HashArg];
        if (!args.TryGetValue(KeepArg, out var keepRaw) || string.IsNullOrWhiteSpace(keepRaw))
            return "PC Tidy doesn't know which copy stays, so this one was left alone.";
        var folderMode = args.TryGetValue(ModeArg, out var m) && m.Equals("folder", StringComparison.OrdinalIgnoreCase);

        string keep;
        try { keep = LongPath.Display(LongPath.Io(keepRaw)); }
        catch (ArgumentException) { return "The copy that stays has an unusual path, so this one was left alone."; }
        if (LongPath.Same(keep, source) || LongPath.IsUnder(keep, source) || LongPath.IsUnder(source, keep))
            return "The copy that stays is this same item, so it was left alone.";

        var src = FsOps.Stat(source);
        var kept = FsOps.Stat(keep);
        if (kept is null) return "The copy that was going to stay is gone, so this one was kept.";
        if (src is null) return "It's no longer there.";
        if (FsOps.IsReparsePoint(kept) || CloudFile.IsPlaceholder(kept.Attributes))
            return "The copy that stays is a link or an online-only file, so PC Tidy can't check it. This one was kept.";

        try
        {
            if (folderMode)
            {
                if (src is not DirectoryInfo || kept is not DirectoryInfo) return "One of the two is no longer a folder, so this one was kept.";
                // Two spellings of one folder (a junction or mount point above one of them, an 8.3 name) have the same
                // signature, so prove they are two different folders, and that neither is inside the other.
                if (SameOrNestedFolder(source, keep)) return "The folder that stays is this same folder (or holds it) under another name, so it was left alone.";
                if (!Same(Signature(source, ct), expected)) return "This folder changed since the scan, so it was kept.";
                if (!Same(Signature(keep, ct), expected)) return "The folder that stays changed since the scan, so this one was kept.";
                return null;
            }
            if (src is not FileInfo || kept is not FileInfo) return "One of the two is no longer a file, so this one was kept.";
            if (SameFile(source, keep)) return "The copy that stays is this same file under another name, so it was left alone.";
            if (!Same(VerifiedCopier.HashFile(source, ct), expected)) return "This copy changed since the scan, so it was kept.";
            if (!Same(VerifiedCopier.HashFile(keep, ct), expected)) return "The copy that stays changed since the scan, so this one was kept.";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "PC Tidy couldn't re-check both copies, so this one was kept: " + FileHandlerBase.Friendly(ex);
        }
    }

    /// <summary>True when the copy that stays isn't there (yet): in a simulation, an earlier step would put it there.</summary>
    public static bool KeptCopyMissing(PlanItem item)
    {
        if (item.Args is null || !item.Args.TryGetValue(KeepArg, out var keepRaw) || string.IsNullOrWhiteSpace(keepRaw)) return false;
        try { return FsOps.Stat(LongPath.Display(LongPath.Io(keepRaw))) is null; }
        catch (ArgumentException) { return false; }
    }

    /// <summary>
    /// Simulation only, while the copy that stays is still to be put in place by an earlier step: this copy alone must
    /// still be the content the scan matched (file mode). Null when it is.
    /// </summary>
    public static string? SourceProblem(PlanItem item, string source, CancellationToken ct)
    {
        var expected = item.Args![HashArg];
        if (item.Args.TryGetValue(ModeArg, out var m) && m.Equals("folder", StringComparison.OrdinalIgnoreCase))
            return "The folder that stays isn't there, so this one would be kept.";
        try
        {
            if (FsOps.Stat(source) is not FileInfo) return "It's no longer a file, so it would be kept.";
            return Same(VerifiedCopier.HashFile(source, ct), expected) ? null : "This copy changed since the scan, so it would be kept.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "PC Tidy couldn't re-check this copy, so it would be kept: " + FileHandlerBase.Friendly(ex);
        }
    }

    private static bool Same(string? a, string b) => a is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Two names of one file (hard link, or another spelling of the same path): volume + file id are equal.</summary>
    private static bool SameFile(string a, string b)
    {
        using var ha = HandleFs.OpenNoFollow(a, NativeMethods.FileReadAttributes);
        using var hb = HandleFs.OpenNoFollow(b, NativeMethods.FileReadAttributes);
        return FileId(ha).SequenceEqual(FileId(hb));
    }

    /// <summary>
    /// True when two folder paths are one folder (same volume + file id), or when one's real location (every junction,
    /// mount point and short name resolved, device form) is inside the other's.
    /// </summary>
    internal static bool SameOrNestedFolder(string a, string b)
    {
        using var ha = HandleFs.OpenNoFollow(a, NativeMethods.FileReadAttributes);
        using var hb = HandleFs.OpenNoFollow(b, NativeMethods.FileReadAttributes);
        if (FileId(ha).SequenceEqual(FileId(hb))) return true;
        var ra = DevicePath(ha);
        var rb = DevicePath(hb);
        return ra.Equals(rb, StringComparison.OrdinalIgnoreCase)
               || ra.StartsWith(rb + "\\", StringComparison.OrdinalIgnoreCase)
               || rb.StartsWith(ra + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The handle's real location as a device path (\Device\HarddiskVolume3\…): one name per volume, whatever drive letters or mount points it has.</summary>
    private static string DevicePath(Microsoft.Win32.SafeHandles.SafeFileHandle h)
    {
        const uint VolumeNameNt = 0x2;
        var sb = new StringBuilder(512);
        var n = NativeMethods.GetFinalPathNameByHandle(h, sb, sb.Capacity, VolumeNameNt);
        if (n > sb.Capacity)
        {
            sb = new StringBuilder(n + 1);
            n = NativeMethods.GetFinalPathNameByHandle(h, sb, sb.Capacity, VolumeNameNt);
        }
        if (n <= 0) throw FsOps.Win32Io(System.Runtime.InteropServices.Marshal.GetLastWin32Error(), "");
        return sb.ToString(0, n).TrimEnd('\\');
    }

    private static byte[] FileId(Microsoft.Win32.SafeHandles.SafeFileHandle h)
    {
        var info = new byte[24]; // FILE_ID_INFO: VolumeSerialNumber (8), FileId (16)
        if (!NativeMethods.GetFileInformationByHandleEx(h, FileIdInfo, info, info.Length)) throw FsOps.Win32Io(System.Runtime.InteropServices.Marshal.GetLastWin32Error(), "");
        return info;
    }

    private const int FileIdInfo = 18;

    /// <summary>The folder signature (see the class summary), or null when the folder can't be judged.</summary>
    internal static string? Signature(string folder, CancellationToken ct)
    {
        var dir = new DirectoryInfo(LongPath.Io(folder));
        var lines = new List<string>();
        int files = 0;
        foreach (var e in dir.EnumerateFileSystemInfos())
        {
            ct.ThrowIfCancellationRequested();
            if (FsOps.IsReparsePoint(e) || CloudFile.IsPlaceholder(e.Attributes)) return null;
            if (e is DirectoryInfo sub)
            {
                var s = Signature(sub.FullName, ct);
                if (s is null) return null;
                lines.Add($"D|{sub.Name.ToUpperInvariant()}|{s}");
                files++; // a subfolder with a signature holds at least one file
            }
            else if (e is FileInfo f)
            {
                var hash = f.Length == 0 ? EmptyFileHash : VerifiedCopier.HashFile(f.FullName, ct);
                lines.Add($"F|{f.Name.ToUpperInvariant()}|{f.Length}|{hash}");
                files++;
            }
        }
        if (files == 0) return null;
        lines.Sort(StringComparer.Ordinal);
        return Convert.ToHexString(XxHash3.Hash(Encoding.UTF8.GetBytes(string.Join("\n", lines))));
    }
}
