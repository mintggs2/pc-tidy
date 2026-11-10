using System.Diagnostics;
using System.Globalization;
using PcTidy.Features.Speed.Common;
using PcTidy.Features.Speed.Sources;

namespace PcTidy.Features.Speed.Running;

/// <summary>
/// "Running now": CPU (measured over a couple of seconds) and private memory per app, with processes of the same
/// program grouped together. "Close" asks an app with a window to close, like clicking its X; nothing is ever
/// force-ended, and Windows, security and anti-cheat processes are never offered. Read-only.
/// </summary>
public sealed class RunningAppsScanner : IFeatureScanner
{
    public const string ScannerId = "speed.running";
    public const string ClosableGroup = "running-apps";
    public const string BackgroundGroup = "running-background";
    public const string ProtectedGroup = "running-protected";
    /// <summary>Background and Windows lists are capped (the busiest first) so the list stays readable.</summary>
    public const int MaxInfoRows = 25;

    private readonly Func<ScanContext, SpeedEnvironment> _environment;

    public RunningAppsScanner(Func<ScanContext, SpeedEnvironment>? environment = null) => _environment = environment ?? SpeedEnvironment.For;

    public string FeatureId => ScannerId;
    public string Title => "Running now";
    public Release Release => Release.V2;
    public bool IncludedInOptimizeAll => false;

    /// <summary>One app's processes, with the CPU they used between the two samples.</summary>
    public sealed record AppUsage(string Key, string Name, IReadOnlyList<ProcessSnapshot> Processes, double CpuPercent, long PrivateBytes, AppClass Class, FileFacts Facts);

    public async Task<ScanResult> ScanAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var env = _environment(ctx);
        progress?.Report(new ScanProgress("Looking at what's running…", 0.1));
        var first = await Task.Run(env.Processes.Snapshot, ct).ConfigureAwait(false);
        var t0 = Stopwatch.GetTimestamp();
        progress?.Report(new ScanProgress("Measuring CPU use for a moment…", 0.3));
        await Task.Delay(env.CpuSampleInterval, ct).ConfigureAwait(false);
        var second = await Task.Run(env.Processes.Snapshot, ct).ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(t0);
        if (elapsed <= TimeSpan.Zero) elapsed = env.CpuSampleInterval;

        progress?.Report(new ScanProgress("Grouping by app…", 0.8));
        var apps = Measure(first, second, elapsed, Environment.ProcessorCount, env, ctx.Locations.WindowsDir);
        var groups = BuildGroups(apps);
        progress?.Report(new ScanProgress("Done", 1));
        return new ScanResult
        {
            FeatureId = ScannerId, Title = Title, Depth = ctx.Depth, Duration = sw.Elapsed, Groups = groups,
            Notes = new[] { "Closing an app is like clicking its X: it can ask you to save first. PC Tidy never force-ends anything." },
        };
    }

    /// <summary>Groups processes by program and works out CPU % between two snapshots (0–100 % of the whole PC).</summary>
    internal static IReadOnlyList<AppUsage> Measure(IReadOnlyList<ProcessSnapshot> first, IReadOnlyList<ProcessSnapshot> second,
        TimeSpan elapsed, int processors, SpeedEnvironment env, string windowsDir)
    {
        var before = first.GroupBy(p => (p.Pid, p.StartUtc)).ToDictionary(g => g.Key, g => g.First());
        var session = env.Processes.CurrentSessionId;
        var wall = elapsed.TotalMilliseconds * Math.Max(1, processors);
        var result = new List<AppUsage>();
        foreach (var group in second.GroupBy(p => p.ImagePath ?? p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var procs = group.ToList();
            double cpuMs = 0;
            foreach (var p in procs)
                if (p.Cpu is { } now && before.TryGetValue((p.Pid, p.StartUtc), out var was) && was.Cpu is { } then && now >= then)
                    cpuMs += (now - then).TotalMilliseconds;
            var lead = procs.FirstOrDefault(p => p.HasMainWindow) ?? procs[0];
            var facts = lead.ImagePath is null ? FileFacts.None(null) : env.Files.Get(lead.ImagePath);
            // The strictest class of any process wins: one protected process makes the whole app protected.
            var classes = procs.Select(p => BackgroundAppRules.Classify(p, facts, session, windowsDir)).ToList();
            var cls = classes.Contains(AppClass.Protected) ? AppClass.Protected : classes.Contains(AppClass.Closable) ? AppClass.Closable : AppClass.Background;
            result.Add(new AppUsage(group.Key, facts.Description ?? lead.WindowTitle ?? lead.Name, procs,
                Math.Clamp(cpuMs / wall * 100, 0, 100), procs.Sum(p => p.PrivateBytes), cls, facts));
        }
        return result;
    }

    internal static IReadOnlyList<ScanGroup> BuildGroups(IReadOnlyList<AppUsage> apps)
    {
        static IEnumerable<AppUsage> Busy(IEnumerable<AppUsage> a) => a.OrderByDescending(x => x.CpuPercent).ThenByDescending(x => x.PrivateBytes);

        var closable = Busy(apps.Where(a => a.Class == AppClass.Closable)).Select(ClosableItem).ToList();
        var background = Busy(apps.Where(a => a.Class == AppClass.Background)).Take(MaxInfoRows)
            .Select(a => InfoItem(a, "Runs in the background without a window, so there's nothing to close politely. If you don't need it, switch it off in Startup or uninstall it in Apps.")).ToList();
        var protectedApps = Busy(apps.Where(a => a.Class == AppClass.Protected)).Take(MaxInfoRows)
            .Select(a => InfoItem(a, "Part of Windows or your security or anti-cheat software. PC Tidy never closes these.")).ToList();

        return new[]
        {
            new ScanGroup { Id = ClosableGroup, Title = "Apps you can close", Description = "Open apps, busiest first. Close asks the app to close, like clicking its X.", Badge = Badge.Check, Items = closable },
            new ScanGroup { Id = BackgroundGroup, Title = "Background apps", Description = "Helpers and tray apps without a window. Shown so you can see what uses your PC.", Badge = Badge.Check, Items = background },
            new ScanGroup { Id = ProtectedGroup, Title = "Windows and security", Description = "Windows itself and the software that protects it. Never closed by PC Tidy.", Badge = Badge.Check, Items = protectedApps },
        }.Where(g => g.Items.Count > 0).ToList();
    }

    private static ScanItem ClosableItem(AppUsage a)
    {
        var windows = a.Processes.Where(p => p.HasMainWindow).ToList();
        return new ScanItem
        {
            Id = "speed.running:" + a.Key,
            Title = a.Name,
            Detail = a.Processes[0].ImagePath ?? a.Processes[0].Name,
            Reason = $"Asks {a.Name} to close, like clicking its X. Save your work in it first; if something isn't saved it will ask you.",
            Badge = Badge.Check,
            OwnerApp = a.Facts.Publisher,
            CurrentState = Usage(a),
            Details = Details(a),
            Actions = windows.Select(p => SpeedPlanItems.CloseApp(a.Name, p.Pid, p.Name, p.StartUtc)).ToList(),
        };
    }

    private static ScanItem InfoItem(AppUsage a, string why) => new()
    {
        Id = "speed.running:" + a.Key,
        Title = a.Name,
        Detail = a.Processes[0].ImagePath ?? a.Processes[0].Name,
        Reason = why,
        Badge = Badge.Check,
        OwnerApp = a.Facts.Publisher,
        CurrentState = Usage(a),
        Details = Details(a),
        DisabledReason = why,
    };

    internal static string Usage(AppUsage a) => $"CPU {a.CpuPercent.ToString("0.0", CultureInfo.CurrentCulture)}% · Memory {Sizes.Format(a.PrivateBytes)}";

    private static Dictionary<string, string> Details(AppUsage a) => new()
    {
        ["CPU"] = a.CpuPercent.ToString("0.0", CultureInfo.CurrentCulture) + "% of the whole PC (over a few seconds)",
        ["Memory"] = Sizes.Format(a.PrivateBytes) + " (private)",
        ["Processes"] = a.Processes.Count.ToString(CultureInfo.CurrentCulture),
        ["Publisher"] = a.Facts.Publisher ?? "Unknown",
        ["Signature"] = a.Facts.SignatureText,
    };
}
