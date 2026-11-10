using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Engine.IO;
using static PcTidy.Engine.IO.NativeMethods;

namespace PcTidy.Engine.Handlers;

/// <summary>
/// OneDrive "Free up space" for one fully downloaded file inside a OneDrive folder: sets FILE_ATTRIBUTE_UNPINNED and
/// clears FILE_ATTRIBUTE_PINNED, and OneDrive then removes the local copy (the file stays in the cloud). Only attributes
/// change: the file's content is never opened. Refused outside the PC's cloud folders, for online-only placeholders
/// (nothing to free), and for links (only OneDrive's own cloud reparse point is accepted). Everything is done through
/// one handle opened without following links, whose real location must be exactly the planned path.
/// Undo restores the previous pin state (the file downloads again when it's next opened).
/// UndoData: the attributes before the change.
/// </summary>
public sealed class OneDriveFreeUpSpaceHandler : FileHandlerBase
{
    private const FileAttributes Pinned = (FileAttributes)0x00080000;
    private const FileAttributes Unpinned = (FileAttributes)0x00100000;
    private const FileAttributes PinBits = Pinned | Unpinned;

    public OneDriveFreeUpSpaceHandler() : base(null) { }

    public override bool CanHandle(PlanItem item) => item.Kind == ActionKind.OneDriveFreeUpSpace;

    private static string? Refusal(string path, HandlerContext ctx)
    {
        var display = LongPath.Display(LongPath.Io(path));
        if (!ctx.Locations.CloudRoots.Any(r => LongPath.IsUnder(display, r) && !LongPath.Same(display, r)))
            return "It isn't in a OneDrive folder, so there's no cloud copy to keep. Left alone.";
        return null;
    }

    public override Task<StepRecord> ExecuteAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var path = Require(step.Item.SourcePath, "file path");
        if (Refusal(path, ctx) is { } no) return Task.FromResult(Skipped(step, no));
        try
        {
            using var h = Open(path);
            var (_, lastWrite, attributes) = HandleFs.Facts(h);
            if (Problem(h, attributes) is { } problem) return Task.FromResult(Skipped(step, problem));
            if (step.Item.ExpectedLastWriteUtc is { } t && lastWrite != Utc(t).Ticks)
                return Task.FromResult(Skipped(step, "It changed since the scan, so it was left alone."));
            if ((attributes & PinBits) == Unpinned) return Task.FromResult(Done(step, "OneDrive was already set to free up its space.",
                new() { [UndoKeys.Attributes] = Invariant((long)attributes) }));
            SetAttributes(h, (attributes & ~Pinned) | Unpinned);
            return Task.FromResult(Done(step, "Set to online-only: OneDrive frees the space and keeps the file in the cloud.",
                new() { [UndoKeys.Attributes] = Invariant((long)attributes) }, ((FileInfo)FsOps.Stat(path)!).Length));
        }
        catch (SafetyRefusalException ex) { return Task.FromResult(Skipped(step, ex.Message)); }
        catch (FileNotFoundException) { return Task.FromResult(Skipped(step, "It's no longer there.")); }
        catch (IOException ex) { return Task.FromResult(Failed(step, Friendly(ex))); }
    }

    public override Task<StepRecord> SimulateAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var path = Require(step.Item.SourcePath, "file path");
        if (Refusal(path, ctx) is { } no) return Task.FromResult(Skipped(step, no));
        var info = FsOps.Stat(path);
        if (info is not FileInfo f) return Task.FromResult(Skipped(step, info is null ? "It's no longer there." : "Only files can be freed up."));
        if (CloudFile.IsPlaceholder(f.Attributes)) return Task.FromResult(Skipped(step, "It's already online-only."));
        return Task.FromResult(Simulated(step, $"Would set {Describe(path)} to online-only ({Sizes.Format(f.Length)} freed, kept in OneDrive).", f.Length));
    }

    public override Task<StepRecord> UndoAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var path = Require(step.Item.SourcePath, "file path");
        if (Refusal(path, ctx) is { } no) return Task.FromResult(UndoLeftAlone(step, no));
        if (!long.TryParse(Get(step, UndoKeys.Attributes), NumberStyles.Integer, CultureInfo.InvariantCulture, out var before))
            return Task.FromResult(UndoLeftAlone(step, "PC Tidy didn't record how it was set before, so it was left alone."));
        try
        {
            using var h = Open(path);
            var (_, _, now) = HandleFs.Facts(h);
            if ((now & FileAttributes.Directory) != 0 || ReparseProblem(h, now) is not null)
                return Task.FromResult(UndoLeftAlone(step, "Something else is there now, so it was left alone."));
            SetAttributes(h, (now & ~PinBits) | ((FileAttributes)before & PinBits));
            return Task.FromResult(Undone(step, "Kept on this PC again as before. If OneDrive already freed it, it downloads again the next time it's opened."));
        }
        catch (FileNotFoundException) { return Task.FromResult(UndoLeftAlone(step, "It's no longer there.")); }
        catch (SafetyRefusalException ex) { return Task.FromResult(UndoLeftAlone(step, ex.Message)); }
    }

    public override Task<bool> WasCompletedAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var path = Require(step.Item.SourcePath, "file path");
        try { return Task.FromResult((File.GetAttributes(LongPath.Io(path)) & PinBits) == Unpinned); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Task.FromResult(false); }
    }

    // ------------------------------------------------------------------ handle work (attributes only, never content)

    /// <summary>Opens the file itself (never what a link points to) for attributes only, and confirms its real location.</summary>
    private static SafeFileHandle Open(string path)
    {
        var h = HandleFs.OpenNoFollow(path, FileReadAttributes | FileWriteAttributes);
        try
        {
            if (!string.Equals(HandleFs.FinalPath(h), LongPath.Io(path), StringComparison.OrdinalIgnoreCase))
                throw new SafetyRefusalException("Its folder now leads somewhere else (a link was put in the way), so PC Tidy left it alone.");
            return h;
        }
        catch
        {
            h.Dispose();
            throw;
        }
    }

    private static string? Problem(SafeFileHandle h, FileAttributes a)
    {
        if ((a & FileAttributes.Directory) != 0) return "Only files can be freed up, not folders.";
        if (CloudFile.IsPlaceholder(a)) return "It's already online-only, so there's nothing to free.";
        return ReparseProblem(h, a);
    }

    /// <summary>OneDrive's own cloud reparse point (IO_REPARSE_TAG_CLOUD_*) is fine; any other link is refused.</summary>
    private static string? ReparseProblem(SafeFileHandle h, FileAttributes a)
    {
        if ((a & FileAttributes.ReparsePoint) == 0) return null;
        var buf = new byte[8]; // FILE_ATTRIBUTE_TAG_INFO: FileAttributes, ReparseTag
        if (!GetFileInformationByHandleEx(h, FileAttributeTagInfo, buf, buf.Length)) return "PC Tidy couldn't check it, so it was left alone.";
        var tag = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4));
        return (tag & 0xFFFF0FFF) == 0x9000001A ? null : "It's a link to somewhere else, so PC Tidy left it alone.";
    }

    private static void SetAttributes(SafeFileHandle h, FileAttributes attributes)
    {
        var basic = new byte[40]; // FILE_BASIC_INFO: zero times = leave unchanged; attributes at offset 32
        BinaryPrimitives.WriteUInt32LittleEndian(basic.AsSpan(32), (uint)(attributes & ~(FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.Compressed | FileAttributes.Encrypted | FileAttributes.SparseFile)));
        if (!SetFileInformationByHandle(h, FileBasicInfo, basic, basic.Length)) throw FsOps.Win32Io(Marshal.GetLastWin32Error(), "");
    }

    private static DateTime Utc(DateTime t) => t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : DateTime.SpecifyKind(t, DateTimeKind.Utc);
}
