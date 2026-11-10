using System.Diagnostics;
using PcTidy.Catalog.Apps;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Apps.Inventory;
using PcTidy.Features.Apps.Uninstall;

namespace PcTidy.Features.Apps.Scanning;

/// <summary>
/// Read-only Apps scan for the standard flow: preinstalled apps you can remove, apps you haven't opened in months,
/// and orphaned uninstall entries. Uninstalls are never ticked and never part of "Optimize everything".
/// </summary>
public sealed class AppsScanner : IFeatureScanner
{
    /// <summary>Apps not opened for this long are listed as "not used lately" (only when the last use is known).</summary>
    public static readonly TimeSpan UnusedAfter = TimeSpan.FromDays(180);

    private readonly Func<ScanContext, AppInventorySources> _sources;
    private readonly Func<DateTime> _now;

    /// <param name="sources">Where to read apps from (tests pass a fake registry/Store); default: the PC in the context.</param>
    public AppsScanner(Func<ScanContext, AppInventorySources>? sources = null, Func<DateTime>? utcNow = null)
    {
        _sources = sources ?? (ctx => AppInventorySources.For(ctx.Locations, ctx.Walker));
        _now = utcNow ?? (() => DateTime.UtcNow);
    }

    public string FeatureId => AppsFeature.Id;
    public string Title => "Apps";
    public Release Release => Release.V2;
    public bool IncludedInOptimizeAll => false;

    public async Task<ScanResult> ScanAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        progress?.Report(new ScanProgress("Reading your installed apps…", 0.05));
        var inventory = new AppInventory(_sources(ctx));
        var apps = await inventory.LoadAsync(ct).ConfigureAwait(false);

        var groups = new List<ScanGroup>();
        if (Preinstalled(apps, ctx.Safety) is { Items.Count: > 0 } pre) groups.Add(pre);
        progress?.Report(new ScanProgress("Checking which apps you use…", 0.4, apps.Count));
        if (await UnusedAsync(apps, inventory, ctx, progress, ct).ConfigureAwait(false) is { Items.Count: > 0 } unused) groups.Add(unused);
        if (Orphans(apps) is { Items.Count: > 0 } orphans) groups.Add(orphans);

        var notes = new List<string>();
        if (apps.All(a => a.LastUsedSource == LastUsedSource.Unknown))
            notes.Add("Windows hasn't recorded when your apps were last opened, so none are listed as unused.");
        return new ScanResult
        {
            FeatureId = FeatureId,
            Title = Title,
            Depth = ctx.Depth,
            Duration = sw.Elapsed,
            Groups = groups,
            Notes = notes,
        };
    }

    private static ScanGroup Preinstalled(IReadOnlyList<InstalledApp> apps, ISafetyGuard guard)
    {
        var items = apps
            .Where(a => a.Source == AppSource.Store && a.Preinstalled is { Tier: not PreinstalledTier.Never })
            .OrderBy(a => a.Preinstalled!.Tier).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(a => ToItem(a, guard, TierNote(a.Preinstalled!)))
            .ToList();
        return new ScanGroup
        {
            Id = "apps.preinstalled",
            Title = "Apps that came with Windows",
            Description = "Removed for you only, and you can get any of them back from the Microsoft Store. Nothing is ticked for you.",
            Badge = Badge.Check,
            Items = items,
        };
    }

    private static string TierNote(PreinstalledApp p) => p.Tier switch
    {
        PreinstalledTier.AskFirst => "Many people use this one. ",
        PreinstalledTier.Warn => "Read this first: ",
        _ => "",
    } + p.Reason;

    private async Task<ScanGroup> UnusedAsync(IReadOnlyList<InstalledApp> apps, AppInventory inventory, ScanContext ctx,
        IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var cutoff = _now() - UnusedAfter;
        var unused = apps
            .Where(a => a.Source is AppSource.Desktop or AppSource.Steam && !a.IsOrphan && a.Preinstalled is null)
            .Where(a => a.LastUsedUtc is { } last && last < cutoff)
            .OrderBy(a => a.LastUsedUtc)
            .ToList();
        var items = new List<ScanItem>();
        for (int i = 0; i < unused.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var app = unused[i];
            // Deep scans measure each folder; quick scans use what the app reported.
            long? size = ctx.Depth == ScanDepth.Deep ? await inventory.MeasureAsync(app, ct).ConfigureAwait(false) : null;
            size ??= app.ReportedSizeBytes;
            progress?.Report(new ScanProgress($"Looking at {app.Name}…", 0.4 + 0.55 * (i + 1) / Math.Max(1, unused.Count), i + 1));
            var lastUsed = AppFacts.LastUsedText(app, _now());
            items.Add(ToItem(app, ctx.Safety, $"You last opened it {lastUsed}.", size));
        }
        return new ScanGroup
        {
            Id = "apps.unused",
            Title = "Apps you haven't opened in 6 months",
            Description = "Only apps whose last use Windows or Steam recorded. Uninstalling runs the app's own uninstaller after a restore point; leftovers can be cleaned afterwards in Apps.",
            Badge = Badge.Check,
            Items = items,
        };
    }

    private static ScanGroup Orphans(IReadOnlyList<InstalledApp> apps)
    {
        var items = apps.Where(a => a.IsOrphan).OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).Select(a => new ScanItem
        {
            Id = "orphan:" + a.Id,
            Title = a.Name,
            Detail = a.RegistryKey,
            Reason = $"{a.OrphanReason} The app is probably already gone, but Windows still lists it.",
            Badge = Badge.Check,
            OwnerApp = a.Name,
            Details = AppFacts.Details(a, null, DateTime.UtcNow),
            DisabledReason = "Shown so you know. Open Apps to look for its leftovers.",
        }).ToList();
        return new ScanGroup
        {
            Id = "apps.orphans",
            Title = "Ghost entries",
            Description = "Apps Windows still lists although their files are gone.",
            Badge = Badge.Check,
            Items = items,
        };
    }

    private static ScanItem ToItem(InstalledApp app, ISafetyGuard guard, string why, long? size = null)
    {
        var option = UninstallPlanner.For(app);
        string? disabled = option.BlockedReason;
        if (option.Item is { } item && disabled is null)
        {
            var verdict = guard.Check(item);
            if (!verdict.Allowed) disabled = verdict.Reason;
        }
        var permanent = option.Item is { Reversible: false };
        var reason = why + (option.Warning is { } w && !why.Contains(w, StringComparison.Ordinal) ? " " + w : "");
        return new ScanItem
        {
            Id = "app:" + app.Id,
            Title = app.Name,
            Detail = string.Join(" · ", new[] { app.Publisher, app.ManagedBy, size is { } s ? Sizes.Format(s) : null }.Where(x => !string.IsNullOrEmpty(x))),
            Reason = reason,
            // An uninstall can't be undone by PC Tidy: Permanent, never ticked by default.
            Badge = permanent ? Badge.Permanent : Badge.Check,
            DefaultSelected = false,
            NeedsAdmin = option.Item?.NeedsAdmin ?? false,
            OwnerApp = app.Name,
            LastModifiedUtc = app.LastUsedUtc,
            CurrentState = size is { } sz ? Sizes.Format(sz) : null,
            Details = AppFacts.Details(app, size, DateTime.UtcNow),
            Actions = option.Item is null ? Array.Empty<PlanItem>() : new[] { option.Item },
            DisabledReason = disabled,
        };
    }
}
