using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Engine.IO;
using static PcTidy.Engine.IO.NativeMethods;

namespace PcTidy.Engine.Handlers;

/// <summary>
/// PERMANENT: overwrites one file with random data once, forces it to the disk, renames it to a random name, then
/// deletes it. No quarantine, no undo (the plan item must be <c>Reversible = false</c>; the guard requires it too).
/// <list type="bullet">
/// <item>Single files only, never folders, links, online-only (cloud) files, or hard-linked files (overwriting one name
/// would destroy the data of every other name too).</item>
/// <item>Everything is done through ONE exclusive handle (refuses links, confirms the real location is the checked path):
/// nobody can have it open, and it can't be swapped between the check and the overwrite.</item>
/// <item>Alternate data streams (e.g. "Zone.Identifier" on downloads) are overwritten too. They're all opened before
/// anything is written; if one can't be, the file is left alone untouched.</item>
/// <item>Hard drive vs SSD is detected (seek penalty) and written in the step message: on an SSD, or a drive PC Tidy
/// can't identify, shredding is best-effort (the drive may keep old copies internally).</item>
/// </list>
/// UndoData: media (hdd|ssd|unknown) and the planned random name (so recovery can finish the delete).
/// </summary>
public sealed class ShredFileHandler : FileHandlerBase, IPreparingHandler
{
    private const int ChunkBytes = 1 << 20;

    public ShredFileHandler() : base(null) { }

    public override bool CanHandle(PlanItem item) => item.Kind == ActionKind.ShredFile;

    public StepRecord Prepare(StepRecord step, HandlerContext ctx)
    {
        var source = Require(step.Item.SourcePath, "file path");
        var folder = Path.GetDirectoryName(source) ?? throw new ArgumentException("PC Tidy never shreds a whole drive.");
        var random = PlannedTargets.FreeName(Path.Combine(folder, "~pctidy-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant() + ".tmp"));
        return step with { UndoData = Merge(step.UndoData, new() { [UndoKeys.ShredName] = random }) };
    }

    public override Task<StepRecord> ExecuteAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        if (Get(step, UndoKeys.ShredName) is null) step = Prepare(step, ctx);
        return Task.Run(() => Shred(step, ct), CancellationToken.None);
    }

    private StepRecord Shred(StepRecord step, CancellationToken ct)
    {
        if (step.Item.Reversible) return Skipped(step, "Shredding can't be undone, so the step must be marked permanent.");
        var source = Require(step.Item.SourcePath, "file path");
        if (Problem(FsOps.Stat(source)) is { } problem) return Skipped(step, problem);
        var randomName = Get(step, UndoKeys.ShredName)!;
        if (!string.Equals(Path.GetDirectoryName(LongPath.Display(LongPath.Io(randomName))), Path.GetDirectoryName(source), StringComparison.OrdinalIgnoreCase))
            return Skipped(step, "PC Tidy's plan for this step doesn't look right, so nothing was changed.");
        if (FsOps.Exists(randomName)) return Skipped(step, $"Something new called '{Name(randomName)}' appeared just now, so nothing was changed.");
        var media = StorageMedia.Of(source);
        ct.ThrowIfCancellationRequested(); // last point where stopping changes nothing

        // A read-only file can't be opened for writing: the attribute is cleared just for this (and put back if PC Tidy stops).
        var attributes = File.GetAttributes(LongPath.Io(source));
        var readOnly = (attributes & FileAttributes.ReadOnly) != 0;
        if (readOnly) File.SetAttributes(LongPath.Io(source), attributes & ~FileAttributes.ReadOnly);
        void PutBack()
        {
            try { if (readOnly && File.Exists(LongPath.Io(source))) File.SetAttributes(LongPath.Io(source), attributes); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
        }

        SafeFileHandle h;
        try { h = HandleFs.OpenVerified(source, GenericRead | GenericWrite | Delete | FileReadAttributes | FileWriteAttributes, share: 0); }
        catch (SafetyRefusalException ex) { PutBack(); return Skipped(step, ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { PutBack(); return Skipped(step, Friendly(ex)); }

        var streams = new List<FileStream>();
        try
        {
            var (size, lastWrite, now) = HandleFs.Facts(h);
            if ((now & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 || CloudFile.IsPlaceholder(now))
            { PutBack(); return Skipped(step, "It isn't a plain file any more, so it was left alone."); }
            if (step.Item.ExpectedLastWriteUtc is { } expected && lastWrite != Utc(expected).Ticks)
            { PutBack(); return Skipped(step, "It changed since the scan, so it was left alone."); }
            if (HandleFs.LinkCount(h) > 1)
            { PutBack(); return Skipped(step, "It shares its data with another file (a hard link), so shredding it would destroy that file too. Left alone."); }

            // All or nothing: every alternate stream is opened before a single byte is overwritten.
            try
            {
                foreach (var (name, _) in HandleFs.AlternateStreams(h))
                    streams.Add(new FileStream(LongPath.Io(source) + name, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete, 1)); // the main handle holds DELETE, which NTFS checks for the whole file
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                PutBack();
                return Skipped(step, "It has extra hidden data (an alternate data stream) PC Tidy couldn't overwrite, so it was left alone.");
            }

            // ---- from here on the data is being destroyed: no cancelling, no going back.
            Overwrite(h, size);
            foreach (var s in streams)
            {
                Overwrite(s.SafeFileHandle, s.Length);
                s.Dispose();
            }
            streams.Clear();

            string? renameNote = null;
            try
            {
                using var folder = HandleFs.OpenVerified(Path.GetDirectoryName(source)!, FileReadAttributes | FileTraverse);
                HandleFs.RenameInto(h, folder, Name(randomName), source);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { renameNote = " (its name couldn't be scrambled first)"; }
            try { HandleFs.DeleteByHandle(h); }
            catch (IOException ex)
            {
                return Failed(step, "It was overwritten, but Windows didn't let PC Tidy delete it: " + Friendly(ex), new() { [UndoKeys.Media] = Media(media) });
            }
            return Done(step, $"Overwritten with random data and deleted for good{renameNote}.{StorageMedia.Note(media)}",
                new() { [UndoKeys.Media] = Media(media) }, size);
        }
        finally
        {
            foreach (var s in streams) s.Dispose();
            h.Dispose();
        }
    }

    /// <summary>Random bytes over the whole length, in place, then forced to the disk.</summary>
    private static void Overwrite(SafeFileHandle h, long length)
    {
        var buf = new byte[(int)Math.Clamp(length, 1, ChunkBytes)];
        for (long offset = 0; offset < length; offset += buf.Length)
        {
            var n = (int)Math.Min(buf.Length, length - offset);
            RandomNumberGenerator.Fill(buf.AsSpan(0, n));
            RandomAccess.Write(h, buf.AsSpan(0, n), offset);
        }
        if (!FlushFileBuffers(h)) throw FsOps.Win32Io(Marshal.GetLastWin32Error(), "");
    }

    private static string? Problem(FileSystemInfo? info)
    {
        if (info is null) return "It's no longer there.";
        if (info is DirectoryInfo) return "Only single files can be shredded, never whole folders.";
        if (FsOps.IsReparsePoint(info)) return "It's a link to somewhere else, so PC Tidy left it alone.";
        if (CloudFile.IsPlaceholder(info.Attributes)) return "It's an online-only OneDrive file. PC Tidy never downloads it.";
        return null;
    }

    private static DateTime Utc(DateTime t) => t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : DateTime.SpecifyKind(t, DateTimeKind.Utc);

    private static string Media(MediaKind m) => m.ToString().ToLowerInvariant();

    public override Task<StepRecord> SimulateAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var source = Require(step.Item.SourcePath, "file path");
        var info = FsOps.Stat(source);
        if (Problem(info) is { } problem) return Task.FromResult(Skipped(step, problem));
        var size = ((FileInfo)info!).Length;
        return Task.FromResult(Simulated(step,
            $"Would overwrite {Describe(source)} ({Sizes.Format(size)}) with random data and delete it for good. This can't be undone.{StorageMedia.Note(StorageMedia.Of(source))}", size));
    }

    public override Task<StepRecord> UndoAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
        => Task.FromResult(UndoLeftAlone(step, "Shredded files can't be brought back."));

    /// <summary>Done once the original name is gone; a renamed, already-overwritten leftover is deleted now.</summary>
    public override Task<bool> WasCompletedAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var source = Require(step.Item.SourcePath, "file path");
        if (FsOps.Exists(source)) return Task.FromResult(false);
        if (Get(step, UndoKeys.ShredName) is { } random && FsOps.Stat(random) is FileInfo) FsOps.DeleteTree(random);
        return Task.FromResult(true);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle file);
}
