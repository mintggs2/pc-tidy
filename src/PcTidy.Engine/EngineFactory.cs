using PcTidy.Core;
using PcTidy.Engine.Handlers;

namespace PcTidy.Engine;

/// <summary>Everything the engine provides, wired together.</summary>
public sealed record EngineServices(
    RunJournal Journal,
    QuarantineStore Quarantine,
    Executor Executor,
    UndoEngine Undo,
    IReadOnlyList<IActionHandler> Handlers)
{
    public void Deconstruct(out IRunJournal journal, out IQuarantineStore quarantine, out IExecutor executor, out IUndoEngine undo)
        => (journal, quarantine, executor, undo) = (Journal, Quarantine, Executor, Undo);
}

/// <summary>Composition helper for the app shell (and the elevated helper).</summary>
public static class EngineFactory
{
    /// <summary>
    /// The engine's own file handlers: QuarantinePath, MovePath, RenamePath, CreateDirectory, WriteTextFile, ArchiveToZip,
    /// ExtractZip, MoveFolderWithJunction, and (v2/v3) EditTextFile, ShredFile, CookiesDeleteExcept, HostsCommentOut,
    /// CopyPath, OneDriveFreeUpSpace. The elevated helper uses this same list (HostsCommentOut is an admin step).
    /// </summary>
    public static IReadOnlyList<IActionHandler> CreateFileHandlers() => CreateFileHandlers(TransferOptions.Default);

    internal static IReadOnlyList<IActionHandler> CreateFileHandlers(TransferOptions options) => new IActionHandler[]
    {
        new QuarantinePathHandler(),
        new MovePathHandler(options),
        new RenamePathHandler(),
        new CreateDirectoryHandler(),
        new WriteTextFileHandler(),
        new ArchiveToZipHandler(),
        new ExtractZipHandler(),
        new MoveFolderWithJunctionHandler(options),
        new EditTextFileHandler(),
        new ShredFileHandler(),
        new CookiesDeleteExceptHandler(),
        new HostsCommentOutHandler(),
        new CopyPathHandler(),
        new OneDriveFreeUpSpaceHandler(),
    };

    /// <summary>
    /// Builds journal, quarantine, executor and undo engine. Data goes to <see cref="AppSettings.DataRoot"/>.
    /// <paramref name="platformHandlers"/> (registry, services, system tools...) are consulted after the file handlers.
    /// </summary>
    public static EngineServices Create(
        IKnownLocations locations,
        ISafetyGuard safety,
        IInUseChecker inUse,
        AppSettings settings,
        IEnumerable<IActionHandler>? platformHandlers = null,
        IRestorePointService? restorePoints = null,
        IElevationService? elevation = null)
        => Create(locations, safety, inUse, settings, platformHandlers, restorePoints, elevation, forceCopy: false);

    /// <summary>Tests: <paramref name="forceCopy"/> makes every move behave as cross-volume (copy → verify → remove).</summary>
    internal static EngineServices Create(
        IKnownLocations locations, ISafetyGuard safety, IInUseChecker inUse, AppSettings settings,
        IEnumerable<IActionHandler>? platformHandlers, IRestorePointService? restorePoints, IElevationService? elevation, bool forceCopy)
    {
        var journal = new RunJournal(settings.DataRoot);
        var quarantine = new QuarantineStore(settings.DataRoot, locations, settings) { ForceCopyIntoDataRoot = forceCopy };
        var handlers = CreateFileHandlers(new TransferOptions { ForceCopy = forceCopy })
            .Concat(platformHandlers ?? Enumerable.Empty<IActionHandler>()).ToList();
        var executor = new Executor(handlers, journal, safety, inUse, quarantine, locations, settings, restorePoints, elevation);
        return new EngineServices(journal, quarantine, executor, new UndoEngine(executor), handlers);
    }
}
