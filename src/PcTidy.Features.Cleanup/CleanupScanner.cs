using System.Diagnostics;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Cleanup.Scanning;
using PcTidy.Features.Cleanup.SystemState;
using PcTidy.Features.Cleanup.Targets;

namespace PcTidy.Features.Cleanup;

/// <summary>
/// Disk cleanup scanner (Plan §7). Read-only: lists what could be cleaned, one group per §7 row in order,
/// and describes every change as a <see cref="PlanItem"/> for the engine. Groups for apps or folders this PC
/// doesn't have are left out; a group with nothing to clean right now is kept with no rows.
/// </summary>
public sealed class CleanupScanner : IFeatureScanner
{
    private readonly IReadOnlyList<ICleanupTarget> _targets = new ICleanupTarget[]
    {
        TempTarget.User(),
        TempTarget.Windows(),
        new ChromiumCacheTarget(),
        new FirefoxCacheTarget(),
        new InetCacheTarget(),
        new BrowserPrivacyTarget(),
        new CrashDumpsTarget(),
        new ErrorReportsTarget(),
        new WindowsLogsTarget(),
        new UpdateDownloadsTarget(),
        new DeliveryOptimizationTarget(),
        new ShaderCacheTarget(),
        new ThumbnailCacheTarget(),
        new RecycleBinTarget(),
        new DiscordTarget(),
        new SteamTarget(),
        new SpotifyTarget(),
        new TelegramTarget(),
        new InstallersTarget(),
        new WindowsOldTarget(),
        new ComponentStoreTarget(),
    };

    public string FeatureId => "cleanup";
    public string Title => "Clean";
    public Release Release => Release.V1;
    public bool IncludedInOptimizeAll => true;

    public Task<ScanResult> ScanAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
        => Task.Run(() => Scan(ctx, progress, ct), ct);

    private ScanResult Scan(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var session = new ScanSession(ctx, SystemStateFactory.For(ctx.Locations, ctx.Walker), DateTime.UtcNow);
        var groups = new List<ScanGroup>();
        long itemsSeen = 0, bytesSeen = 0;

        for (var i = 0; i < _targets.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var target = _targets[i];
            progress?.Report(new ScanProgress($"Checking {target.Info.Title}…", (double)i / _targets.Count, itemsSeen, bytesSeen));

            IReadOnlyList<ScanItem>? items;
            try
            {
                items = target.Collect(session, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One broken area never spoils the whole scan.
                session.AddNote($"{target.Info.Title} couldn't be checked ({ex.Message}). Everything else was scanned.");
                continue;
            }
            if (items is null) continue; // not on this PC: hide the group

            groups.Add(new ScanGroup
            {
                Id = target.Info.Id,
                Title = target.Info.Title,
                Description = items.Count == 0 ? "Nothing to clean here right now." : target.Info.GroupText,
                Badge = target.Info.Badge,
                Items = items,
            });
            itemsSeen += items.Count;
            bytesSeen += items.Sum(x => x.SizeBytes);
        }

        if (session.UnreadableFolders > 0)
            session.AddNote($"{session.UnreadableFolders:N0} folders couldn't be read (usually they need admin) and were skipped.");
        if (session.IsDeep) session.AddNote(session.Deep.Summary());
        progress?.Report(new ScanProgress("Done", 1, itemsSeen, bytesSeen));

        return new ScanResult
        {
            FeatureId = FeatureId,
            Title = Title,
            Depth = ctx.Depth,
            Duration = clock.Elapsed,
            Groups = groups,
            Notes = session.Notes.ToList(),
        };
    }
}
