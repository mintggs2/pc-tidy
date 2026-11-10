using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Engine.IO;

namespace PcTidy.Engine.Handlers;

/// <summary>Creates a folder (and missing parents). Undo removes only the folders it created, and only while empty.</summary>
public sealed class CreateDirectoryHandler : FileHandlerBase, IPreparingHandler
{
    public CreateDirectoryHandler() : base(null) { }

    public override bool CanHandle(PlanItem item) => item.Kind == ActionKind.CreateDirectory;

    public StepRecord Prepare(StepRecord step, HandlerContext ctx)
    {
        var dest = Require(step.Item.DestinationPath, "folder path");
        return step with { UndoData = Merge(step.UndoData, new() { [UndoKeys.CreatedDirs] = FsOps.EncodeList(FsOps.MissingDirectories(dest)) }) };
    }

    public override Task<StepRecord> ExecuteAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var dest = Require(step.Item.DestinationPath, "folder path");
        var info = FsOps.Stat(dest);
        if (info is FileInfo) return Task.FromResult(Failed(step, $"A file called '{Name(dest)}' is already there."));
        if (info is DirectoryInfo)
            return Task.FromResult(Done(step, "The folder was already there.", new() { [UndoKeys.CreatedDirs] = FsOps.EncodeList(Array.Empty<string>()) }));
        var created = FsOps.CreateDirectories(dest);
        return Task.FromResult(Done(step, $"Created '{Name(dest)}'.", new() { [UndoKeys.CreatedDirs] = FsOps.EncodeList(created) }));
    }

    public override Task<StepRecord> SimulateAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var dest = Require(step.Item.DestinationPath, "folder path");
        return Task.FromResult(FsOps.Exists(dest)
            ? Simulated(step, $"'{Name(dest)}' is already there; nothing to create.")
            : Simulated(step, $"Would create the folder '{dest}'."));
    }

    public override Task<StepRecord> UndoAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var created = FsOps.DecodeList(Get(step, UndoKeys.CreatedDirs));
        if (created.Count == 0) return Task.FromResult(Undone(step, "Nothing to undo: the folder was already there."));
        var kept = FsOps.RemoveEmptyDirectories(created);
        return Task.FromResult(kept.Count == 0
            ? Undone(step, "Removed the empty folder.")
            : Undone(step, $"Kept '{Name(kept[^1])}' because it isn't empty any more."));
    }

    public override Task<bool> WasCompletedAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
        => Task.FromResult(FsOps.IsDirectory(Require(step.Item.DestinationPath, "folder path")));
}
