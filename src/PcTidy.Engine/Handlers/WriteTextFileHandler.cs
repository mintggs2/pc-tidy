using System.IO.Hashing;
using System.Text;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Engine.IO;

namespace PcTidy.Engine.Handlers;

/// <summary>
/// Writes a small new text file (reports, notes). Never overwrites: AutoNumber or Skip on clash.
/// Written to a temp name and renamed, so it's never half-written. Undo moves it to quarantine
/// (only if it's unchanged). UndoData: final path + content hash.
/// </summary>
public sealed class WriteTextFileHandler : FileHandlerBase, IPreparingHandler
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public WriteTextFileHandler() : base(null) { }

    public override bool CanHandle(PlanItem item) => item.Kind == ActionKind.WriteTextFile;

    private static string Content(PlanItem item)
        => item.Args is { } a && a.TryGetValue("content", out var c) ? c : throw new ArgumentException("The plan item has no content.");

    private static string HashOf(string text) => Convert.ToHexString(XxHash3.Hash(Utf8.GetBytes(text)));

    public StepRecord Prepare(StepRecord step, HandlerContext ctx)
    {
        var dest = Require(step.Item.DestinationPath, "file path");
        var final = PlanFinal("", dest, step.Item.ClashPolicy == NameClashPolicy.Skip ? NameClashPolicy.Skip : NameClashPolicy.AutoNumber);
        return step with { UndoData = Merge(step.UndoData, new() { [UndoKeys.FinalPath] = final, [UndoKeys.Hash] = HashOf(Content(step.Item)) }) };
    }

    public override Task<StepRecord> ExecuteAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        if (Get(step, UndoKeys.FinalPath) is null) step = Prepare(step, ctx);
        var text = Content(step.Item);
        // The journaled name (a free one picked when the step was prepared). Never overwrite, whatever the policy says.
        var final = Get(step, UndoKeys.FinalPath)!;
        if (FsOps.Exists(final))
            return Task.FromResult(Skipped(step, step.Item.ClashPolicy == NameClashPolicy.Skip
                ? $"Something called '{Name(final)}' is already there."
                : $"Something new called '{Name(final)}' appeared just now, so nothing was saved."));

        var created = FsOps.CreateParents(final);
        var temp = final + ".pctidy-partial";
        try
        {
            using (var fs = new FileStream(LongPath.Io(temp), FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var bytes = Utf8.GetBytes(text);
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            FsOps.Rename(temp, final);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FsOps.DeleteTree(temp); // our own half-written temp file
            FsOps.RemoveEmptyDirectories(created);
            return Task.FromResult(Failed(step, Friendly(ex)));
        }
        return Task.FromResult(Done(step, $"Saved '{Name(final)}'.", new()
        {
            [UndoKeys.FinalPath] = final,
            [UndoKeys.Hash] = HashOf(text),
            [UndoKeys.CreatedDirs] = FsOps.EncodeList(created),
        }));
    }

    public override Task<StepRecord> SimulateAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var dest = Require(step.Item.DestinationPath, "file path");
        return Task.FromResult(Simulated(step, $"Would save '{dest}' ({Sizes.Format(Utf8.GetByteCount(Content(step.Item)))})."));
    }

    public override async Task<StepRecord> UndoAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var final = Get(step, UndoKeys.FinalPath) ?? Require(step.Item.DestinationPath, "file path");
        if (!FsOps.Exists(final)) return Undone(step, "It was already gone.");
        if (VerifiedCopier.HashFile(final, ct) != Get(step, UndoKeys.Hash))
            return UndoLeftAlone(step, $"'{Name(final)}' was edited after PC Tidy saved it, so it was left alone.");
        await ctx.Quarantine.QuarantineAsync(final, ctx.RunId, ct).ConfigureAwait(false);
        FsOps.RemoveEmptyDirectories(FsOps.DecodeList(Get(step, UndoKeys.CreatedDirs)));
        return Undone(step, "Moved the file PC Tidy saved to quarantine.");
    }

    public override Task<bool> WasCompletedAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var final = Get(step, UndoKeys.FinalPath);
        if (final is null) return Task.FromResult(false);
        FsOps.DeleteTree(final + ".pctidy-partial");
        return Task.FromResult(FsOps.Stat(final) is FileInfo && VerifiedCopier.HashFile(final, ct) == Get(step, UndoKeys.Hash));
    }
}
