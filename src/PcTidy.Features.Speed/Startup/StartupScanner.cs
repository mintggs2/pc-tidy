using System.Diagnostics;
using System.Globalization;
using PcTidy.Features.Speed.Common;

namespace PcTidy.Features.Speed.Startup;

/// <summary>
/// The Startup tab: everything that starts with Windows, with publisher, signature and measured impact.
/// Toggles are Task-Manager-style (StartupApproved), tasks are turned off (never deleted), services go to Manual
/// (never Disabled). Microsoft's own entries are hidden unless the page asks for them. Read-only.
/// </summary>
public sealed class StartupScanner : IFeatureScanner
{
    public const string ScannerId = "speed.startup";
    public const string AppsGroup = "startup-apps";
    public const string TasksGroup = "startup-tasks";
    public const string ServicesGroup = "startup-services";
    public const string OffGroup = "startup-off";
    public const string AdvancedGroup = "startup-advanced";

    private readonly Func<ScanContext, SpeedEnvironment> _environment;

    public StartupScanner(Func<ScanContext, SpeedEnvironment>? environment = null) => _environment = environment ?? SpeedEnvironment.For;

    public string FeatureId => ScannerId;
    public string Title => "Startup";
    public Release Release => Release.V2;
    public bool IncludedInOptimizeAll => false;

    /// <summary>
    /// Community tweaks §9.6: Windows can tell you each time an app adds itself to startup. Shown as guidance, not a
    /// one-click: the registry value behind the switch isn't documented by Microsoft, so PC Tidy doesn't write it.
    /// </summary>
    public const string StartupNotificationTip =
        "Tip: Windows can tell you whenever an app adds itself to startup. Turn on \"Startup app notification\" in Settings → System → Notifications.";

    public Task<ScanResult> ScanAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
        => Task.Run(() => Scan(ctx, progress, ct), ct);

    private ScanResult Scan(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var groups = BuildGroups(ctx, progress, ct, out var notes);
        return new ScanResult
        {
            FeatureId = ScannerId, Title = Title, Depth = ctx.Depth, Duration = sw.Elapsed,
            Groups = groups, Notes = notes,
        };
    }

    /// <summary>The startup groups (also used by the Optimize-everything scanner).</summary>
    internal IReadOnlyList<ScanGroup> BuildGroups(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct, out IReadOnlyList<string> notes)
    {
        var env = _environment(ctx);
        var hideMicrosoft = SpeedOptions.HidesMicrosoft(ctx);
        var presetTicks = SpeedOptions.PresetTicksStartup(ctx);
        progress?.Report(new ScanProgress("Reading startup places…", 0.1));

        var readers = new StartupReaders(env, ctx.Locations);
        var entries = readers.All(ct).ToList();
        progress?.Report(new ScanProgress("Checking publishers and signatures…", 0.4));
        var meter = new StartupImpactMeter(env.Clock, env.Processes);

        var apps = new List<ScanItem>();
        var tasks = new List<ScanItem>();
        var services = new List<ScanItem>();
        var off = new List<ScanItem>();
        int hidden = 0, done = 0;
        foreach (var e in entries)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ScanProgress("Checking " + (e.DisplayNameHint ?? e.Name), 0.4 + 0.5 * done++ / Math.Max(1, entries.Count)));
            var facts = env.Files.Get(e.TargetPath);
            var isMicrosoft = facts.IsMicrosoft || (e.Source == StartupSource.StoreApp && e.PublisherHint?.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) == true);

            if (e.Source == StartupSource.Service)
            {
                var (eligibility, why) = ServiceRules.Evaluate(e.Name, facts, ctx.Locations.WindowsDir);
                if (eligibility == ServiceEligibility.Hidden) continue;
                services.Add(ServiceItem(e, facts, meter.Measure(e.TargetPath), why));
                continue;
            }
            if (hideMicrosoft && isMicrosoft) { hidden++; continue; }

            var impact = meter.Measure(e.TargetPath);
            var item = EntryItem(e, facts, impact, isMicrosoft, presetTicks);
            if (!e.Enabled && e.Source is StartupSource.RunKey or StartupSource.StartupFolder or StartupSource.ScheduledTask or StartupSource.StoreApp) off.Add(item);
            else if (e.Source == StartupSource.ScheduledTask) tasks.Add(item);
            else apps.Add(item);
        }

        var advanced = new AdvancedReader(env, ctx.Locations).All()
            .Select(e => AdvancedItem(e, env.Files.Get(e.TargetPath))).ToList();

        var list = new List<string>();
        if (!meter.CanMeasure)
            list.Add($"Startup impact is measured only in the first {StartupImpactMeter.MeasureWindow.TotalMinutes:0} minutes after you sign in, so it says \"Not measured\" for now. Scan right after your next sign-in to see it.");
        if (hidden > 0)
            list.Add($"{SpeedOptions.Plural(hidden, "Microsoft entry is", "Microsoft entries are")} hidden. Switch off \"Hide Microsoft\" to see {(hidden == 1 ? "it" : "them")}.");
        list.Add(StartupNotificationTip);
        notes = list;

        progress?.Report(new ScanProgress("Done", 1));
        return new[]
        {
            Group(AppsGroup, "Apps that start when you sign in", "Run keys, Startup folders and Store apps. Switching one off works like Task Manager: it stays installed.", apps),
            Group(TasksGroup, "Scheduled tasks at sign-in or boot", "Tasks apps added to start themselves. Turning one off doesn't delete it. Windows' own tasks aren't listed.", tasks),
            Group(ServicesGroup, "Services that start automatically", "Background services from other companies. Manual means Windows still starts one when something needs it. Microsoft services and drivers aren't listed.", services),
            Group(OffGroup, "Already switched off", "These don't start with Windows. Tick one to let it start again (Store apps are switched in Windows Settings).", off),
            Group(AdvancedGroup, "Advanced (read-only)", "Places that are rarely changed on purpose. Shown so you can spot something odd; PC Tidy doesn't change them.", advanced),
        }.Where(g => g.Items.Count > 0).ToList();
    }

    private static ScanGroup Group(string id, string title, string description, List<ScanItem> items) => new()
    {
        Id = id, Title = title, Description = description,
        Badge = items.Any(i => i.Badge == Badge.Safe && i.Actions.Count > 0) ? Badge.Safe : Badge.Check,
        Items = items.OrderByDescending(i => ImpactRank(i)).ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase).ToList(),
    };

    private static int ImpactRank(ScanItem i)
        => i.Details?.TryGetValue("Startup impact", out var t) == true
            ? t.StartsWith("High", StringComparison.Ordinal) ? 3 : t.StartsWith("Medium", StringComparison.Ordinal) ? 2 : t.StartsWith("Low", StringComparison.Ordinal) ? 1 : 0
            : 0;

    // ------------------------------------------------------------------ rows

    private static ScanItem EntryItem(StartupEntry e, FileFacts facts, ImpactReading impact, bool isMicrosoft, bool presetTicks)
    {
        var name = RowTitle(e, facts);
        var actions = new List<PlanItem>();
        string? disabled = null;
        switch (e.Source)
        {
            case StartupSource.RunKey or StartupSource.StartupFolder when e.ApprovedLocation is not null:
                actions.Add(SpeedPlanItems.StartupToggle(name, e.ApprovedLocation, e.Name, e.Hive, enable: !e.Enabled));
                break;
            case StartupSource.ScheduledTask when e.TaskPath is not null:
                actions.Add(SpeedPlanItems.TaskToggle(name, e.TaskPath, enable: !e.Enabled));
                break;
            case StartupSource.RunOnceKey:
                disabled = "Runs once at your next sign-in, then Windows removes it by itself. There's nothing to switch off.";
                break;
            case StartupSource.StoreApp:
                disabled = e.StoreState is 3 or 4
                    ? "Your organization manages this app's startup setting."
                    : "Store apps are switched in Windows Settings > Apps > Startup (Task Manager works too).";
                break;
        }

        // Security tray apps, anti-cheat and Windows' own helpers are shown but never offered to switch off.
        if (e.Enabled && actions.Count > 0 && IsProtected(e, facts))
        {
            actions.Clear();
            disabled = "Part of Windows or of your security or anti-cheat software, so PC Tidy doesn't switch it off.";
        }

        // Only measured, high-impact, non-Microsoft apps that are on can be pre-ticked, and only by the Recommended/Gamer presets.
        var preTickable = actions.Count > 0 && e.Enabled && !isMicrosoft && impact.Level == ImpactLevel.High
                          && e.Source is StartupSource.RunKey or StartupSource.StartupFolder;
        return new ScanItem
        {
            Id = $"speed.startup:{e.Source}:{e.Hive}:{e.Name}",
            Title = name,
            Detail = Detail(e, facts),
            Reason = Reason(e, facts),
            Badge = preTickable ? Badge.Safe : Badge.Check,
            DefaultSelected = preTickable && presetTicks,
            NeedsAdmin = actions.Any(a => a.NeedsAdmin),
            OwnerApp = Publisher(e, facts),
            CurrentState = CurrentState(e),
            Details = Details(e, facts, impact),
            Actions = actions,
            DisabledReason = disabled,
        };
    }

    private static ScanItem ServiceItem(StartupEntry e, FileFacts facts, ImpactReading impact, string? why)
    {
        var name = RowTitle(e, facts);
        var details = Details(e, facts, impact);
        details["Running now"] = e.ServiceRunning ? "Yes" : "No";
        return new ScanItem
        {
            Id = $"speed.startup:Service:{e.Name}",
            Title = name,
            Detail = Detail(e, facts),
            Reason = (facts.Exists ? "" : "The program it points at is missing. ")
                     + "Starts with Windows and runs in the background. Manual means Windows still starts it when an app needs it, so things keep working.",
            Badge = Badge.Check,
            NeedsAdmin = true,
            OwnerApp = Publisher(e, facts),
            CurrentState = e.Flag ?? "Automatic",
            Details = details,
            Actions = why is null ? new[] { SpeedPlanItems.ServiceToManual(name, e.Name) } : Array.Empty<PlanItem>(),
            DisabledReason = why,
        };
    }

    private static ScanItem AdvancedItem(StartupEntry e, FileFacts facts) => new()
    {
        Id = $"speed.startup:Advanced:{e.Hive}:{e.Name}",
        Title = e.Name,
        Detail = e.Command,
        Reason = e.Flag ?? "This is the Windows default. Nothing to do.",
        Badge = Badge.Check,
        OwnerApp = Publisher(e, facts),
        CurrentState = e.Flag is null ? "Default" : "Changed",
        Details = Details(e, facts, ImpactReading.NotMeasured, includeImpact: false),
        DisabledReason = "Read-only: shown so you know. PC Tidy doesn't change these.",
    };

    internal static bool IsProtected(StartupEntry e, FileFacts facts)
        => SpeedProtectedNames.Matches(Path.GetFileNameWithoutExtension(e.TargetPath ?? ""), SpeedProtectedNames.NeverCloseProcesses)
           || SpeedProtectedNames.IsProtectedPublisher(facts.Company, facts.Signer, e.PublisherHint);

    private static string RowTitle(StartupEntry e, FileFacts facts)
    {
        if (e.Source is StartupSource.RunKey or StartupSource.RunOnceKey) return e.Name;
        return e.DisplayNameHint ?? facts.Description ?? e.Name;
    }

    private static string? Publisher(StartupEntry e, FileFacts facts) => facts.Publisher ?? e.PublisherHint;

    private static string? Detail(StartupEntry e, FileFacts facts)
        => facts.Exists && e.TargetPath is not null ? e.TargetPath : e.Command ?? e.TargetPath;

    private static string CurrentState(StartupEntry e) => e.Source switch
    {
        StartupSource.StoreApp => e.StoreState switch { 2 => "On", 4 => "On (set by your organization)", 3 => "Off (set by your organization)", _ => "Off" },
        StartupSource.RunOnceKey => "Next sign-in only",
        _ => e.Enabled ? "On" : "Off",
    };

    private static string Reason(StartupEntry e, FileFacts facts)
    {
        var missing = e.TargetPath is not null && !facts.Exists && facts.Signature == SignatureState.FileMissing
            ? "The program it points at is gone (probably uninstalled), so switching it off is safe. " : "";
        return missing + e.Source switch
        {
            _ when !e.Enabled && e.Source != StartupSource.StoreApp => "Switched off: it doesn't start with Windows. Tick it to let it start again.",
            StartupSource.ScheduledTask => "A task that runs when you sign in or when the PC starts. Turning it off doesn't delete it; the app may just check for updates less often.",
            StartupSource.StoreApp => "A Store app allowed to start when you sign in.",
            StartupSource.RunOnceKey => "Set to run one time at your next sign-in (often to finish an install).",
            _ => "Starts every time you sign in. Switching it off stops only that: the app stays installed and you can still open it yourself.",
        };
    }

    private static Dictionary<string, string> Details(StartupEntry e, FileFacts facts, ImpactReading impact, bool includeImpact = true)
    {
        var d = new Dictionary<string, string>();
        d["Starts from"] = e.Source switch
        {
            StartupSource.RunKey => $"Run key ({e.Scope})",
            StartupSource.RunOnceKey => $"RunOnce key ({e.Scope})",
            StartupSource.StartupFolder => $"Startup folder ({e.Scope})",
            StartupSource.StoreApp => "Store app startup task",
            StartupSource.ScheduledTask => "Scheduled task",
            StartupSource.Service => "Service",
            _ => "Advanced",
        };
        if (e.Where is { } where)
        {
            var label = e.Source switch
            {
                StartupSource.StartupFolder => "Folder",
                StartupSource.ScheduledTask => "Task",
                _ => "Registry key",
            };
            d[label] = e.Source == StartupSource.ScheduledTask ? e.TaskPath ?? where : where;
        }
        if (e.Source == StartupSource.Service) d["Service name"] = e.Name;
        if (!string.IsNullOrWhiteSpace(e.Command)) d["Command"] = e.Command!;
        d["Publisher"] = Publisher(e, facts) ?? "Unknown";
        if (e.Source != StartupSource.StoreApp) d["Signature"] = facts.SignatureText;
        if (includeImpact && e.Source != StartupSource.StoreApp) d["Startup impact"] = impact.Text;
        if (e.DisabledAtUtc is { } when) d["Switched off"] = when.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture);
        return d;
    }
}
