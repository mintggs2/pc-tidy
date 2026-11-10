using System.Diagnostics;
using System.IO.Hashing;
using PcTidy.Engine.IO;

namespace PcTidy.Engine.Handlers;

/// <summary>Raised when a file is too big for an in-place text edit.</summary>
internal sealed class FileTooBigException(string message) : IOException(message);

/// <summary>
/// Shared pieces of the "edit with a backup" handlers (EditTextFile, HostsCommentOut, CookiesDeleteExcept):
/// planned sibling names, literal reads through a verified handle, atomic replace, and file times/attributes.
/// <para>
/// The pattern: the new content is written to a planned temp sibling and flushed; <c>ReplaceFile</c> then swaps it in
/// and renames the ORIGINAL file object to a planned backup sibling (same bytes, times and attributes; nothing is
/// copied); the backup is then moved into the run's quarantine with the entry id planned in the step's intent. Undo puts
/// that exact file back.
/// </para>
/// </summary>
internal static class FileBackups
{
    public const string NewSuffix = ".pctidy-new";
    public const string BackupSuffix = ".pctidy-backup";

    /// <summary>A free sibling name ("x.ini.pctidy-new", "x.ini (2).pctidy-new"...) promised to this step only.</summary>
    public static string PlanSibling(string target, string suffix) => PlannedTargets.FreeName(target + suffix);

    /// <summary>
    /// Is <paramref name="planned"/> a sibling of <paramref name="target"/> with the given suffix, as <see cref="PlanSibling"/>
    /// makes them? The elevated helper checks this on names the app prepared, so a step can't point it anywhere else.
    /// </summary>
    public static bool IsPlannedSibling(string? planned, string target, string suffix)
    {
        if (planned is null) return false;
        try
        {
            var p = LongPath.Display(LongPath.Io(planned));
            var t = LongPath.Display(LongPath.Io(target));
            return string.Equals(Path.GetDirectoryName(p), Path.GetDirectoryName(t), StringComparison.OrdinalIgnoreCase)
                   && Path.GetFileName(p).StartsWith(Path.GetFileName(t), StringComparison.OrdinalIgnoreCase)
                   && p.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                   && p.Length > t.Length;
        }
        catch (ArgumentException) { return false; }
    }

    public static string Hash(byte[] bytes) => Convert.ToHexString(XxHash3.Hash(bytes));

    /// <summary>
    /// The whole file, read through a handle that refuses links and confirms the real location is exactly
    /// <paramref name="path"/>. Others may keep reading it; a writer holding it open makes this fail ("in use").
    /// </summary>
    public static byte[] ReadVerified(string path, long maxBytes)
    {
        using var h = HandleFs.OpenVerified(path, NativeMethods.GenericRead, NativeMethods.FileShareRead);
        using var fs = new FileStream(h, FileAccess.Read, 1);
        if (fs.Length > maxBytes) throw new FileTooBigException($"It's too big ({PcTidy.Core.Sizes.Format(fs.Length)}) for PC Tidy to edit.");
        var buf = new byte[fs.Length];
        fs.ReadExactly(buf);
        return buf;
    }

    /// <summary>Writes a NEW file (never overwrites) and forces it to the disk before anything relies on it.</summary>
    public static void WriteNew(string path, byte[] bytes)
    {
        using var fs = new FileStream(LongPath.Io(path), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1);
        fs.Write(bytes);
        fs.Flush(flushToDisk: true);
    }

    /// <summary>
    /// Swaps <paramref name="replacement"/> in for <paramref name="target"/> (ReplaceFile: the target keeps its name,
    /// security and creation time). With <paramref name="backup"/>, the original file object is renamed there. Read-only
    /// targets are handled (the attribute is cleared just for the swap) and both files end with
    /// <paramref name="attributes"/>. If the swap stops half-way (the original renamed, the new file not yet in its
    /// place) the original is put back, so a failure always means "nothing changed".
    /// </summary>
    public static void Replace(string replacement, string target, string? backup, FileAttributes attributes)
    {
        var targetIo = LongPath.Io(target);
        var clearedReadOnly = (attributes & FileAttributes.ReadOnly) != 0;
        if (clearedReadOnly) File.SetAttributes(targetIo, attributes & ~FileAttributes.ReadOnly);
        try
        {
            File.Replace(LongPath.Io(replacement), targetIo, backup is null ? null : LongPath.Io(backup), ignoreMetadataErrors: true);
        }
        catch
        {
            if (backup is not null && !FsOps.Exists(target) && FsOps.Stat(backup) is FileInfo) FsOps.Rename(backup, target);
            if (clearedReadOnly && File.Exists(targetIo)) TrySetAttributes(target, attributes);
            throw;
        }
        SetAttributes(target, attributes);
        if (backup is not null && FsOps.Stat(backup) is FileInfo) SetAttributes(backup, attributes);
    }

    /// <summary>Times and attributes, recorded before a change so undo can give the original back exactly.</summary>
    public static Dictionary<string, string> Meta(FileSystemInfo info) => new()
    {
        [UndoKeys.CreationTicks] = info.CreationTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
        [UndoKeys.LastWriteTicks] = info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
        [UndoKeys.Attributes] = ((int)info.Attributes).ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>Sets recorded times/attributes on <paramref name="path"/>; values not recorded are taken from <paramref name="fallback"/>.</summary>
    public static void ApplyMeta(string path, IReadOnlyDictionary<string, string>? meta, FileSystemInfo fallback)
    {
        static long? Ticks(IReadOnlyDictionary<string, string>? m, string key)
            => m is not null && m.TryGetValue(key, out var v) && long.TryParse(v, out var t) ? t : null;
        var creation = Ticks(meta, UndoKeys.CreationTicks) ?? fallback.CreationTimeUtc.Ticks;
        var lastWrite = Ticks(meta, UndoKeys.LastWriteTicks) ?? fallback.LastWriteTimeUtc.Ticks;
        var attributes = Ticks(meta, UndoKeys.Attributes) is { } a ? (FileAttributes)(int)a : fallback.Attributes;
        var io = LongPath.Io(path);
        File.SetAttributes(io, FileAttributes.Normal); // read-only last, so the times can be set
        File.SetCreationTimeUtc(io, new DateTime(creation, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(io, new DateTime(lastWrite, DateTimeKind.Utc));
        SetAttributes(path, attributes);
    }

    private static void SetAttributes(string path, FileAttributes attributes)
    {
        var keep = attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive | FileAttributes.NotContentIndexed);
        File.SetAttributes(LongPath.Io(path), keep == 0 ? FileAttributes.Normal : keep);
    }

    private static void TrySetAttributes(string path, FileAttributes attributes)
    {
        try { SetAttributes(path, attributes); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort while reporting the real failure */ }
    }

    /// <summary>Which of these apps are running (process names without .exe). Read-only process listing.</summary>
    public static IReadOnlyList<string> RunningApps(IEnumerable<string>? apps)
    {
        var open = new List<string>();
        foreach (var app in apps ?? Enumerable.Empty<string>())
        {
            var name = app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app[..^4] : app;
            if (name.Length == 0) continue;
            var procs = Process.GetProcessesByName(name);
            if (procs.Length > 0) open.Add(char.ToUpperInvariant(name[0]) + name[1..]);
            foreach (var p in procs) p.Dispose();
        }
        return open;
    }
}
