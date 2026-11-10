using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Learning.Model;
using PcTidy.Features.Organize;
using PcTidy.Features.Organize.Detection;

namespace PcTidy.Features.Learning.Sources;

/// <summary>What learning takes from one finished run.</summary>
public sealed record MinedRun(RunMark Mark, IReadOnlyList<MoveEvent> Moves, IReadOnlyList<FolderVisit> Visits);

/// <summary>
/// Reads PC Tidy's own run journal (read-only) for the user's own moves: finished, real (not simulated) runs, steps
/// that are Done (an undone step is not a move), single files moved with <see cref="ActionKind.MovePath"/>.
/// Moves made by an existing Organize rule are skipped (a rule already covers them). The download site comes from
/// the moved file's Zone.Identifier HostUrl, read-only, never for cloud placeholders.
/// </summary>
public sealed class JournalMiner
{
    // The engine records where a moved file really ended up (after auto-numbering) under this undo-data key.
    private const string FinalPathKey = "finalPath";

    private readonly PrivacyFilter _privacy;
    private readonly Func<string, string?> _hostOf;

    /// <param name="hostOf">Download host of a file, or null. Default: Zone.Identifier HostUrl/ReferrerUrl.</param>
    public JournalMiner(PrivacyFilter privacy, Func<string, string?>? hostOf = null)
    {
        _privacy = privacy;
        _hostOf = hostOf ?? DownloadHost;
    }

    public static bool IsFinished(RunRecord run)
        => run.Mode == ExecutionMode.Real && run.FinishedUtc is not null
           && run.Status is RunStatus.Completed or RunStatus.CompletedWithSkips or RunStatus.Failed or RunStatus.PartiallyUndone;

    public MinedRun Mine(RunRecord run)
    {
        var utc = run.FinishedUtc ?? run.CreatedUtc;
        var moves = new List<MoveEvent>();
        var visits = new List<FolderVisit>();
        var drives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in run.Steps.Where(s => s.Status == StepStatus.Done))
        {
            var item = step.Item;
            var paths = new[] { item.SourcePath, item.DestinationPath }.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            if (paths.Count == 0) drives.Add("*");
            foreach (var p in paths) if (DriveOf(p!) is { } d) drives.Add(d);

            if (item.Kind != ActionKind.MovePath || item.IsDirectory || item.SourcePath is null) continue;
            if (item.Args?.ContainsKey(OrganizeLedger.RuleIdArg) == true) continue;
            var final = step.UndoData?.GetValueOrDefault(FinalPathKey) ?? item.DestinationPath;
            if (string.IsNullOrWhiteSpace(final)) continue;
            if (final.EndsWith('\\')) final = Path.Combine(final, Path.GetFileName(item.SourcePath));
            if (!_privacy.MayRemember(item.SourcePath) || !_privacy.MayRemember(final)) continue;

            moves.Add(new MoveEvent(run.RunId, step.FinishedUtc ?? utc, item.SourcePath, final, item.ExpectedSize, _hostOf(final)));
            if (Path.GetDirectoryName(final) is { Length: > 0 } folder) visits.Add(new FolderVisit(folder, step.FinishedUtc ?? utc, VisitSource.Move));
        }
        return new MinedRun(new RunMark(run.RunId, utc, drives.ToList()), moves, visits);
    }

    private static string? DriveOf(string path)
    {
        try { return PathUtil.DriveOf(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>Host the file was downloaded from (Zone.Identifier), "www." dropped. Read-only.</summary>
    public static string? DownloadHost(string path)
    {
        try
        {
            if (!File.Exists(path) || CloudFile.IsPlaceholder(File.GetAttributes(path))) return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
        var host = ZoneIdentifier.Read(path)?.Hosts().FirstOrDefault();
        return host is not null && host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }
}
