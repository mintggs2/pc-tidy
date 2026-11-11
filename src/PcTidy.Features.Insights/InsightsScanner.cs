using System.Diagnostics;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Insights.Cards;
using PcTidy.Features.Insights.Model;
using PcTidy.Features.Insights.Source;

namespace PcTidy.Features.Insights;

/// <summary>
/// Builds every Insights card from read-only facts. Not part of "Optimize everything": the cards are explanations,
/// and the few actions (OneDrive "Free up space", compressing an old folder, opening a page) are always picked by hand.
/// </summary>
public sealed class InsightsScanner : IFeatureScanner
{
    private readonly Func<IKnownLocations, IInsightsSource> _sourceFor;
    private readonly TimeProvider _clock;

    /// <param name="sourceFor">Fact source override (tests, demos). Default: the real PC, or Sim\insights.json in a sandbox.</param>
    /// <param name="clock">Clock override for tests.</param>
    public InsightsScanner(Func<IKnownLocations, IInsightsSource>? sourceFor = null, TimeProvider? clock = null)
    {
        _sourceFor = sourceFor ?? DefaultSource;
        _clock = clock ?? TimeProvider.System;
    }

    public string FeatureId => InsightsIds.FeatureId;
    public string Title => "Insights";
    public Release Release => Release.V3;
    public bool IncludedInOptimizeAll => false;

    /// <summary>The real PC, or Sim\insights.json in a sandbox (also used by the System info window).</summary>
    internal static IInsightsSource DefaultSource(IKnownLocations locations)
        => locations.IsSandbox ? SandboxInsightsSource.For(locations) : new WindowsInsightsSource(locations);

    /// <summary>The card builders in page order. Each is independent: one failing never stops the others.</summary>
    private static readonly (string Id, InsightCategory Category, string Title, string Icon, Func<CardContext, InsightCard> Build)[] Builders =
    {
        (InsightsIds.ScriptScan, InsightCategory.Safety, "Scan this script", "DocumentSearch24", _ => SafetyCards.ScriptScan()),
        (InsightsIds.ModifiedWindows, InsightCategory.Safety, "Modified Windows", "WindowShield24", SafetyCards.ModifiedWindows),
        (InsightsIds.Hosts, InsightCategory.Safety, "Hosts file", "Globe24", SafetyCards.Hosts),
        (InsightsIds.SmartAppControl, InsightCategory.Safety, "Smart App Control", "ShieldTask24", ProtectionCards.SmartAppControl),
        (InsightsIds.MemoryIntegrity, InsightCategory.Safety, "Memory integrity and game speed", "ShieldLock24", MeasuredCards.MemoryIntegrity),
        (InsightsIds.BitLocker, InsightCategory.Safety, "BitLocker and its recovery key", "LockClosed24", ProtectionCards.BitLocker),
        (InsightsIds.Reliability, InsightCategory.Health, "Stability history", "Pulse24", HealthCards.Reliability),
        (InsightsIds.MemoryTest, InsightCategory.Health, "Memory test", "Ram20", HealthCards.MemoryTest),
        (InsightsIds.Backup, InsightCategory.Health, "Backups", "History24", HealthCards.Backup),
        (InsightsIds.OneDriveFreeUp, InsightCategory.Storage, "OneDrive: free up space", "CloudArrowDown24", StorageCards.OneDriveFreeUp),
        (InsightsIds.CompressFolders, InsightCategory.Storage, "Compress old folders", "FolderZip24", StorageCards.CompressFolders),
        (InsightsIds.DevDrive, InsightCategory.Storage, "Dev Drive", "Code24", StorageCards.DevDrive),
        (InsightsIds.RefreshRate, InsightCategory.Hardware, "Display refresh rate", "Desktop24", HardwareCards.RefreshRate),
        (InsightsIds.Ram, InsightCategory.Hardware, "Memory speed", "Ram20", HardwareCards.Ram),
        (InsightsIds.GamesOnHdd, InsightCategory.Hardware, "Games on a hard disk", "Games24", HardwareCards.GamesOnHdd),
        (InsightsIds.ResizableBar, InsightCategory.Hardware, "Resizable BAR", "DeveloperBoard24", HardwareCards.ResizableBar),
        (InsightsIds.Timer, InsightCategory.Hardware, "System timer", "Timer24", HardwareCards.Timer),
        (InsightsIds.Vanguard, InsightCategory.Hardware, "Anti-cheat requirements", "ShieldCheckmark24", HardwareCards.Vanguard),
        (InsightsIds.Runtimes, InsightCategory.Hardware, "Game and app runtimes", "PuzzlePiece24", RuntimeCards.Runtimes),
        (InsightsIds.WindowsBuild, InsightCategory.Hardware, "Windows build and your processor", "Window24", MeasuredCards.WindowsBuild),
        (InsightsIds.DualCcdX3D, InsightCategory.Hardware, "Ryzen X3D with two core groups", "DeveloperBoard24", MeasuredCards.DualCcdX3D),
        (InsightsIds.NvidiaApp, InsightCategory.Hardware, "NVIDIA App game filters", "Filter24", MeasuredCards.NvidiaApp),
        (InsightsIds.GpuReinstall, InsightCategory.Guides, "Clean graphics driver reinstall", "Wrench24", GuideCards.GpuReinstall),
        (InsightsIds.Thermal, InsightCategory.Guides, "Heat slowing the PC down", "Temperature24", MeasuredCards.Thermal),
    };

    /// <summary>Reads everything and builds the cards (runs on a background thread).</summary>
    public Task<InsightsReport> ScanReportAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
        => Task.Run(() =>
        {
            var source = _sourceFor(ctx.Locations);
            var c = new CardContext(ctx, source, _clock.GetUtcNow().UtcDateTime, ct);
            var cards = new List<InsightCard>();
            var notes = new List<string>();
            for (int i = 0; i < Builders.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var b = Builders[i];
                progress?.Report(new ScanProgress($"Checking {b.Title.ToLowerInvariant()}…", (double)i / Builders.Length));
                try
                {
                    cards.Add(b.Build(c));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Insights card {b.Id} failed: {ex}");
                    cards.Add(InsightCard.NotAvailable(b.Id, b.Category, b.Title, b.Icon, "PC Tidy couldn't check this on this PC."));
                    notes.Add($"{b.Title}: couldn't be checked.");
                }
            }
            return new InsightsReport(cards, c.NowUtc, notes);
        }, ct);

    public async Task<ScanResult> ScanAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        var report = await ScanReportAsync(ctx, progress, ct).ConfigureAwait(false);
        return ToScanResult(report, ctx.Depth, started.Elapsed);
    }

    /// <summary>The cards as standard scan groups: one information row per card, followed by its tickable rows.</summary>
    public static ScanResult ToScanResult(InsightsReport report, ScanDepth depth, TimeSpan duration)
    {
        var groups = report.Cards.GroupBy(c => c.Category).Select(g => new ScanGroup
        {
            Id = "insights." + g.Key.ToString().ToLowerInvariant(),
            Title = CategoryTitle(g.Key),
            Description = CategoryDescription(g.Key),
            Badge = g.Any(c => c.Items.Count > 0) ? Badge.Check : Badge.Safe,
            Items = g.SelectMany(card => new[] { InfoRow(card) }.Concat(card.Items)).ToList(),
        }).ToList();
        return new ScanResult
        {
            FeatureId = InsightsIds.FeatureId, Title = "Insights", Depth = depth, Duration = duration,
            FinishedUtc = report.FinishedUtc, Groups = groups, Notes = report.Notes,
        };
    }

    private static ScanItem InfoRow(InsightCard card) => new()
    {
        Id = card.Id,
        Title = card.Title,
        Detail = card.Headline,
        Reason = card.Explanation,
        CurrentState = card.Status switch
        {
            InsightStatus.NotAvailable => "Not available on this PC",
            InsightStatus.NeedsAdmin => "Needs admin",
            InsightStatus.Attention => "Worth a look",
            InsightStatus.Good => "All good",
            _ => "For information",
        },
        Badge = Badge.Safe,
        Details = card.Facts.GroupBy(f => f.Label).ToDictionary(g => g.Key, g => string.Join("; ", g.Select(f => f.Value)))
            .Concat(card.Measured.Select((m, i) => KeyValuePair.Create($"Measured {i + 1}", $"{m.Number} ({m.Where})")))
            .GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.First().Value),
    };

    public static string CategoryTitle(InsightCategory c) => c switch
    {
        InsightCategory.Safety => "Safety checks",
        InsightCategory.Health => "Health and backups",
        InsightCategory.Storage => "Storage",
        InsightCategory.Hardware => "Hardware and games",
        _ => "Guides",
    };

    public static string CategoryDescription(InsightCategory c) => c switch
    {
        InsightCategory.Safety => "Scripts, modified Windows and the hosts file. PC Tidy only reads; fixes live in Tweaks.",
        InsightCategory.Health => "Windows' own stability score, memory test results and whether your files are backed up.",
        InsightCategory.Storage => "Ways to get space back without deleting anything, and the Dev Drive advisor.",
        InsightCategory.Hardware => "Settings that hold a PC back without anyone noticing. PC Tidy reads them and explains what to change.",
        _ => "Checklists PC Tidy explains but never carries out itself.",
    };
}
