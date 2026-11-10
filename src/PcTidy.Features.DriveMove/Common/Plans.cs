using PcTidy.Catalog.DriveMove;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.DriveMove.Drives;

namespace PcTidy.Features.DriveMove.Common;

/// <summary>
/// Args this section puts on its plan items, beyond the documented ones (KnownFolderRedirect: "folderId";
/// EditTextFile: "content"; RunSystemTool: none for wsl.shutdown; OpenExternal: "target").
/// </summary>
public static class DriveMoveArgs
{
    /// <summary>KnownFolderRedirect: the folder's GUID, "{374DE290-…}" (documented in Core).</summary>
    public const string FolderId = "folderId";
    /// <summary>KnownFolderRedirect: "Downloads", "Videos"… (for messages and History).</summary>
    public const string FolderName = "folderName";
    /// <summary>KnownFolderRedirect: where the folder was at scan time (the handler moves its contents from there).</summary>
    public const string CurrentPath = "currentPath";
    /// <summary>KnownFolderRedirect: "move" or "restore" (back to %USERPROFILE%\&lt;Name&gt;).</summary>
    public const string Purpose = "purpose";
    /// <summary>EditTextFile: the full new file text (documented in Core).</summary>
    public const string Content = "content";
    /// <summary>EditTextFile: "true" when the config file doesn't exist yet (undo removes the file it created).</summary>
    public const string CreateIfMissing = "createIfMissing";
    /// <summary>EditTextFile: which tool's setting changes ("pip") and its new value, for History.</summary>
    public const string Tool = "drivemove.tool";
    public const string NewLocation = "drivemove.newLocation";
    /// <summary>EditTextFile: the one setting this edit changes (format, INI section, key), so two edits of one file can be merged.</summary>
    public const string Format = "drivemove.format";
    public const string Section = "drivemove.section";
    public const string Key = "drivemove.key";
    /// <summary>OpenExternal: the link to open (documented in Core).</summary>
    public const string Target = "target";
}

/// <summary>Builds this section's plan items. Every item is re-checked by the safety guard before it's offered and before it runs.</summary>
internal static class Plans
{
    public static PlanItem MoveWithJunction(string source, string destination, FolderTotals totals, DriveFacts to, IReadOnlyList<string>? closeFirst)
        => new()
        {
            Kind = ActionKind.MoveFolderWithJunction,
            FeatureId = DriveMoveIds.FeatureId,
            Description = $"Move \"{Path.GetFileName(source)}\" ({Sizes.Format(totals.Bytes)}) to {to.Name} and leave a link at the old place",
            SourcePath = source,
            DestinationPath = destination,
            IsDirectory = true,
            ExpectedSize = totals.Bytes,
            ExpectedLastWriteUtc = totals.LastWriteUtc,
            RequiresClosedApps = closeFirst is { Count: > 0 } ? closeFirst : null,
        };

    public static PlanItem Redirect(Guid folderId, string name, string current, string destination, long bytes, bool restore)
        => new()
        {
            Kind = ActionKind.KnownFolderRedirect,
            FeatureId = DriveMoveIds.FeatureId,
            Description = restore
                ? $"Put your {name} folder back in its usual place ({destination})"
                : $"Move your {name} folder ({Sizes.Format(bytes)}) to {destination}",
            SourcePath = current,
            DestinationPath = destination,
            IsDirectory = true,
            // Not an ExpectedSize: the folder keeps changing (new downloads) and Windows moves whatever is there.
            Args = new Dictionary<string, string>
            {
                [DriveMoveArgs.FolderId] = folderId.ToString("B").ToUpperInvariant(),
                [DriveMoveArgs.FolderName] = name,
                [DriveMoveArgs.CurrentPath] = current,
                [DriveMoveArgs.Purpose] = restore ? "restore" : "move",
            },
        };

    public static PlanItem EditConfig(string file, string content, bool exists, DateTime? lastWriteUtc, string toolId, string toolName, string newLocation,
        ConfigFileTarget setting, IReadOnlyList<string> closeFirst)
    {
        var args = new Dictionary<string, string>
        {
            [DriveMoveArgs.Content] = content,
            [DriveMoveArgs.Tool] = toolId,
            [DriveMoveArgs.NewLocation] = newLocation,
            [DriveMoveArgs.Format] = setting.Format.ToString(),
            [DriveMoveArgs.Key] = setting.Key,
        };
        if (setting.Section is { } section) args[DriveMoveArgs.Section] = section;
        if (!exists) args[DriveMoveArgs.CreateIfMissing] = "true";
        return new PlanItem
        {
            Kind = ActionKind.EditTextFile,
            FeatureId = DriveMoveIds.FeatureId,
            Description = $"Tell {toolName} to use {newLocation} ({(exists ? "edit" : "create")} {Path.GetFileName(file)})",
            SourcePath = file,
            ExpectedSize = 0,
            // Skipped if the tool rewrote its config since the scan.
            ExpectedLastWriteUtc = exists ? lastWriteUtc : null,
            Args = args,
            RequiresClosedApps = closeFirst.Count > 0 ? closeFirst : null,
        };
    }

    public static PlanItem Quarantine(string folder, FolderTotals totals, string what)
        => new()
        {
            Kind = ActionKind.QuarantinePath,
            FeatureId = DriveMoveIds.FeatureId,
            Description = $"Move the old {what} ({Sizes.Format(totals.Bytes)}) to quarantine",
            SourcePath = folder,
            IsDirectory = true,
            ExpectedSize = totals.Bytes,
            ExpectedLastWriteUtc = totals.LastWriteUtc,
        };

    /// <summary>
    /// One entry of a user folder's OLD copy, after Windows moved the folder (Location move = copy, old copy kept).
    /// Runs only if <paramref name="redirect"/> finished: the handler has then checked every file is in the new place.
    /// Skipped if the entry changed since the scan.
    /// </summary>
    public static PlanItem QuarantineOldCopy(FileSystemInfo entry, long bytes, string folderName, PlanItem redirect)
        => new()
        {
            Kind = ActionKind.QuarantinePath,
            FeatureId = DriveMoveIds.FeatureId,
            Description = $"Move the old copy of \"{entry.Name}\" ({Sizes.Format(bytes)}) from your {folderName} folder's old place to quarantine",
            SourcePath = entry.FullName,
            IsDirectory = entry is DirectoryInfo,
            ExpectedSize = bytes,
            ExpectedLastWriteUtc = entry.LastWriteTimeUtc,
            DependsOn = new[] { redirect.Id },
        };

    public static PlanItem OpenExternal(string target, string description)
        => new()
        {
            Kind = ActionKind.OpenExternal,
            FeatureId = DriveMoveIds.FeatureId,
            Description = description,
            Reversible = true,
            Args = new Dictionary<string, string> { [DriveMoveArgs.Target] = target },
        };

    public static PlanItem StopWsl()
        => new()
        {
            Kind = ActionKind.RunSystemTool,
            FeatureId = DriveMoveIds.FeatureId,
            ToolId = ToolIds.WslShutdown,
            Description = "Stop WSL so its disks can be moved (it starts again the next time you open Linux)",
        };
}

/// <summary>Restart Manager checks for many files, in batches (it never follows links). Problems count as "not in use".</summary>
internal static class InUse
{
    public static async Task<InUseInfo?> FirstBusyAsync(IInUseChecker checker, IReadOnlyList<string> files, CancellationToken ct)
    {
        foreach (var batch in files.Chunk(200))
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<InUseInfo> results;
            try { results = await checker.CheckAsync(batch, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { continue; }
            if (results.FirstOrDefault(r => r.InUse) is { } busy) return busy;
        }
        return null;
    }

    public static string Describe(InUseInfo busy)
        => busy.Apps.Count > 0
            ? $"{string.Join(", ", busy.Apps.Take(3))} {(busy.Apps.Count == 1 ? "is" : "are")} using files in it. Close {(busy.Apps.Count == 1 ? "it" : "them")}, then scan again."
            : "Some files in it are in use right now. Close the app using them, then scan again.";
}
