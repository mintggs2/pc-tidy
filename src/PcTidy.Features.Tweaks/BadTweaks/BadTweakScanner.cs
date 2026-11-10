using System.Diagnostics;
using PcTidy.Features.Tweaks.Planning;
using PcTidy.Features.Tweaks.Reading;

namespace PcTidy.Features.Tweaks.BadTweaks;

/// <summary>
/// "Find bad tweaks" (Plan §8, Community tweaks §3): scans the registry, services, scheduled tasks, boot settings
/// (WMI BCD provider, never bcdedit text), policies and the hosts file for harmful leftovers of old optimizers,
/// debloat scripts, "FPS packs" and malware. Each finding says what it is, why it's bad and the Windows default,
/// and offers a reset to that default (previewed, confirmed, backed up and undoable). It never applies anything
/// itself and never pre-ticks a finding.
/// </summary>
public sealed class BadTweakScanner : IFeatureScanner
{
    private readonly Func<ScanContext, TweakEnvironment> _environment;

    public BadTweakScanner(Func<ScanContext, TweakEnvironment>? environment = null)
        => _environment = environment ?? TweakEnvironment.ForContext;

    public string FeatureId => TweakPlanItems.FinderFeature;
    public string Title => "Find bad tweaks";
    public Release Release => Release.V2;
    /// <summary>Repairs are reviewed one by one, so they're not part of the one-click run.</summary>
    public bool IncludedInOptimizeAll => false;

    public Task<ScanResult> ScanAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
        => Task.Run(() => Scan(ctx, progress, ct), ct);

    private ScanResult Scan(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var env = _environment(ctx);
        var boot = env.Boot.ReadCurrentEntry();
        var covered = CoveredValues();
        var special = new SpecialChecks(env, covered);
        var notes = new List<string>();
        var findings = new List<(BadTweakCheck Check, ScanItem Item)>();
        var checks = BadTweakCatalog.All;

        for (var i = 0; i < checks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var check = checks[i];
            progress?.Report(new ScanProgress($"Checking {check.Title}", (double)i / checks.Count, i));
            var fired = new List<FiredSignal>();
            foreach (var signal in check.Signals)
            {
                try
                {
                    if (Evaluate(signal, env, boot, special) is { } f) fired.Add(f);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    // One unreadable value never stops the scan.
                }
            }
            if (fired.Count == 0) continue;
            findings.Add((check, BuildItem(check, fired, env)));
        }

        if (boot is null)
            notes.Add(env.IsRealPc
                ? "Boot settings weren't checked: Windows only lets administrators read them. Start PC Tidy as administrator to include them."
                : "Boot settings weren't checked on this PC.");
        if (env.Probe.IsDomainJoined)
            notes.Add("This PC is joined to a work network, so some policies may be set on purpose by your IT team. Ask them before removing any.");
        if (findings.Count == 0) notes.Add("No bad tweaks found. Nothing needs fixing.");
        progress?.Report(new ScanProgress("Done", 1, findings.Count));

        var groups = findings
            .GroupBy(f => f.Check.Area)
            .OrderBy(g => g.Key)
            .Select(g => new ScanGroup
            {
                Id = "tweaks.badtweaks." + g.Key.ToString().ToLowerInvariant(),
                Title = AreaTitle(g.Key),
                Description = AreaDescription(g.Key),
                Badge = Badge.Check,
                Items = g.OrderBy(f => f.Check.Severity).Select(f => f.Item).ToList(),
            })
            .ToList();
        return new ScanResult { FeatureId = FeatureId, Title = Title, Depth = ctx.Depth, Duration = sw.Elapsed, Groups = groups, Notes = notes };
    }

    // ------------------------------------------------------------------ probes

    private sealed record FiredSignal(BadSignal Signal, IReadOnlyList<string> Evidence, CustomFinding? Custom);

    private static FiredSignal? Evaluate(BadSignal signal, TweakEnvironment env, BootConfigSnapshot? boot, SpecialChecks special)
    {
        switch (signal.Probe)
        {
            case RegistryProbe r:
                var v = env.Registry.Read(r.Hive, r.KeyPath, r.ValueName);
                return v is not null && Matches(r, v) ? new FiredSignal(signal, new[] { $"{r.Label} = {RegData.Show(v.Kind, v.Data)}" }, null) : null;
            case ServiceDisabledProbe s:
                return ServiceState.Read(env.Registry, s.ServiceName) is { IsDisabled: true }
                    ? new FiredSignal(signal, new[] { $"Service '{s.ServiceName}' is Disabled" }, null) : null;
            case BootValueProbe b:
                if (boot?.Get(b.ElementType) is not { } e) return null;
                var hit = b.Test switch
                {
                    BootTest.IsTrue => e.Boolean == true,
                    BootTest.IntegerEquals => e.Integer == b.Value,
                    _ => true,
                };
                return hit ? new FiredSignal(signal, new[] { $"Boot setting '{b.Name}' = {(e.Boolean is { } bo ? (bo ? "on" : "off") : e.Integer?.ToString() ?? "set")}" }, null) : null;
            case TaskDisabledProbe t:
                return env.Tasks.IsEnabled(t.TaskPath) == false ? new FiredSignal(signal, new[] { $"Task {t.TaskPath} is disabled" }, null) : null;
            case CustomProbe c:
                return special.Run(c.Kind) is { } found ? new FiredSignal(signal, found.Evidence, found) : null;
            default:
                return null;
        }
    }

    internal static bool Matches(RegistryProbe r, RegValue v)
    {
        if (v.Kind is null) return false;
        var numeric = v.Kind is RegistryKind.DWord or RegistryKind.QWord;
        bool Same(string expected) => numeric ? RegData.SameValue(RegistryKind.DWord, v.Data, expected) : string.Equals(v.Data.Trim(), expected, StringComparison.OrdinalIgnoreCase);
        return r.Test switch
        {
            ValueTest.Present => true,
            ValueTest.AnyOf => r.Values.Any(Same),
            ValueTest.NoneOf => !r.Values.Any(Same),
            ValueTest.AnyBits => numeric && RegData.TryNumber(v.Data, out var n) && RegData.TryNumber(r.Values[0], out var mask) && (n & mask) != 0,
            ValueTest.Empty => v.Data.Split('\n').All(string.IsNullOrWhiteSpace),
            _ => false,
        };
    }

    /// <summary>Every value some catalog check (or known policy) already looks at, so "other policies" doesn't repeat them.</summary>
    internal static IReadOnlySet<string> CoveredValues()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var signal in BadTweakCatalog.All.SelectMany(c => c.Signals))
        {
            if (signal.Probe is RegistryProbe r) set.Add(TweakRegistryAllowances.KeyOf(r.Hive, r.KeyPath, r.ValueName));
            foreach (var fix in signal.Fixes.OfType<RegistryDeleteFix>())
                if (fix.Hive is { } h && fix.KeyPath is { } k && fix.ValueName is { } n) set.Add(TweakRegistryAllowances.KeyOf(h, k, n));
        }
        return set;
    }

    // ------------------------------------------------------------------ the row

    private static ScanItem BuildItem(BadTweakCheck check, IReadOnlyList<FiredSignal> fired, TweakEnvironment env)
    {
        var actions = new List<PlanItem>();
        var guidance = new List<string>();
        foreach (var f in fired)
            foreach (var fix in f.Signal.Fixes)
                AddFix(f, fix, env, actions, guidance);

        var admin = check.Flags.HasFlag(BadTweakFlags.Admin) || actions.Any(a => a.NeedsAdmin);
        var details = new Dictionary<string, string>
        {
            ["Found"] = string.Join("\n", fired.SelectMany(f => f.Evidence).Distinct()),
            ["Windows default"] = check.WindowsDefault,
            ["Severity"] = check.Severity.ToString(),
        };
        if (actions.Count > 0) details["Fix"] = string.Join("\n", actions.Select(a => a.Description));
        if (guidance.Count > 0) details["How to fix"] = string.Join("\n", guidance.Distinct());
        if (check.Flags.HasFlag(BadTweakFlags.BitLocker))
            details["BitLocker"] = "BitLocker checks this boot setting. Have your recovery key ready (account.microsoft.com/devices/recoverykey) before restarting; PC Tidy suspends nothing for you.";
        if (check.Note is { } note) details["Good to know"] = note;
        if (check.Id == "security.defender-off" && env.Probe.ThirdPartyAntivirus.Count > 0)
            details["Other antivirus"] = $"{string.Join(", ", env.Probe.ThirdPartyAntivirus)} is installed. Windows switches Defender to passive by itself when another antivirus is active, so this policy isn't needed for it.";
        var permanent = actions.Any(a => !a.Reversible);
        details["Undo"] = actions.Count == 0 ? "Nothing is changed by PC Tidy."
            : permanent ? "This reset can't be undone from History: PC Tidy can't read the old value back first. It only removes the leftover, which puts Windows' default back."
            : "Everything PC Tidy resets can be undone from History: the exact old value is saved first.";
        details["Source"] = string.Join("\n", check.Sources);

        return new ScanItem
        {
            Id = "badtweaks." + check.Id,
            Title = check.Title,
            Detail = "Windows default: " + check.WindowsDefault,
            Reason = check.WhyBad,
            // A reset that can't be undone is shown as permanent (and, like every finding, never pre-ticked).
            Badge = permanent ? Badge.Permanent : Badge.Check,
            DefaultSelected = false,
            NeedsAdmin = admin,
            NeedsRestart = check.Flags.HasFlag(BadTweakFlags.Restart),
            CurrentState = fired.Sum(f => f.Evidence.Count) == 1 ? "Found" : $"Found ({fired.Sum(f => f.Evidence.Count)})",
            Details = details,
            Actions = actions,
        };
    }

    private static void AddFix(FiredSignal fired, BadFix fix, TweakEnvironment env, List<PlanItem> actions, List<string> guidance)
    {
        const string feature = TweakPlanItems.FinderFeature;
        var redirect = env.Registry.Redirect;
        var probe = fired.Signal.Probe;
        switch (fix)
        {
            case RegistryDeleteFix del:
                var hive = del.Hive ?? (probe as RegistryProbe)?.Hive;
                var key = del.KeyPath ?? (probe as RegistryProbe)?.KeyPath;
                var name = del.ValueName ?? (probe as RegistryProbe)?.ValueName;
                if (hive is null || key is null || name is null) return;
                // Only delete what is really there (a fix may name a companion value, e.g. WUServer or the Mask).
                if (env.Registry.Read(hive.Value, key, name) is null) return;
                var kind = del.ValueName is null && probe is RegistryProbe rp ? rp.Kind : del.Kind;
                actions.Add(TweakPlanItems.RegistryRemove(feature, redirect, hive.Value, key, name, kind,
                    $@"Remove {RegistryNames.Short(hive.Value)}\{key} → {name} (back to Windows' default)"));
                break;
            case RegistrySetFix set when probe is RegistryProbe r:
                actions.Add(TweakPlanItems.RegistrySet(feature, redirect, r.Hive, r.KeyPath, r.ValueName, set.Kind, set.Data,
                    $"Set {r.Label} back to {RegData.Show(set.Kind, set.Data)}"));
                break;
            case RegistryWriteFix write:
                actions.Add(TweakPlanItems.RegistrySet(feature, redirect, write.Hive, write.KeyPath, write.ValueName, write.Kind, write.Data,
                    $@"Set {RegistryNames.Short(write.Hive)}\{write.KeyPath} → {write.ValueName} back to {RegData.Show(write.Kind, write.Data)}"));
                break;
            case RegistryDeleteFoundFix when fired.Custom is { Values.Count: > 0 } found:
                foreach (var v in found.Values)
                    actions.Add(TweakPlanItems.RegistryRemove(feature, redirect, v.Hive, v.KeyPath, v.ValueName, v.Kind,
                        v.What is { } what
                            ? $@"Remove {RegistryNames.Short(v.Hive)}\{v.KeyPath} → {v.ValueName} (it {what}; removing it puts Windows' default back)"
                            : $@"Remove {RegistryNames.Short(v.Hive)}\{v.KeyPath} → {v.ValueName} (back to Windows' default)"));
                break;
            case ServiceStartFix svc when probe is ServiceDisabledProbe s:
                actions.Add(TweakPlanItems.ServiceStart(feature, s.ServiceName, svc.Start,
                    $"Set the '{s.ServiceName}' service back to {RegistryNames.Friendly(svc.Start)}"));
                break;
            case BootDeleteFix when probe is BootValueProbe b:
                var bitlocker = BadTweakCatalog.BitLockerWatchedBootValues.Contains(b.Name) ? " (BitLocker: have your recovery key ready)" : "";
                actions.Add(TweakPlanItems.DeleteBootValue(feature, b.Name, $"Remove the boot setting '{b.Name}'{bitlocker}"));
                break;
            case ToolFix tool:
                actions.Add(TweakPlanItems.Tool(feature, tool.ToolId, tool.What));
                break;
            case TaskEnableFix when probe is TaskDisabledProbe t:
                actions.Add(TweakPlanItems.EnableTask(feature, t.TaskPath, $"Turn the task {t.TaskPath} back on"));
                break;
            case HostsCommentOutFix when fired.Custom is { HostsLines.Count: > 0 } hosts:
                actions.Add(TweakPlanItems.CommentOutHosts(feature, env.HostsPath, hosts.HostsLines,
                    $"Comment out {hosts.HostsLines.Count} line{(hosts.HostsLines.Count == 1 ? "" : "s")} in the hosts file that block Microsoft's servers (backed up first)"));
                break;
            case QuarantineFix when fired.Custom is { Files.Count: > 0 } files:
                foreach (var file in files.Files)
                    actions.Add(TweakPlanItems.Quarantine(feature, file.FullName, file.Length, file.LastWriteTimeUtc, $"Move '{file.FullName}' to quarantine"));
                break;
            case PowerPlanSetFix plan when fired.Custom is { PowerPlanAvailable: true }:
                actions.Add(TweakPlanItems.PowerPlan(feature, plan.SchemeGuid, $"Switch the power plan back to {plan.PlanName}"));
                break;
            case GuidanceFix g:
                guidance.Add(g.Steps);
                break;
        }
    }

    public static string AreaTitle(BadTweakArea a) => a switch
    {
        BadTweakArea.Security => "Security turned off",
        BadTweakArea.WindowsUpdate => "Windows Update and Store",
        BadTweakArea.BootSettings => "Boot settings",
        BadTweakArea.Scheduler => "Scheduler and timer values",
        BadTweakArea.Network => "Network",
        BadTweakArea.Services => "Services",
        BadTweakArea.Storage => "Storage and memory",
        BadTweakArea.CpuPower => "CPU and power",
        BadTweakArea.Graphics => "Graphics driver",
        BadTweakArea.Games => "Games",
        BadTweakArea.Policies => "\"Managed by your organization\" policies",
        _ => "Windows",
    };

    public static string AreaDescription(BadTweakArea a) => a switch
    {
        BadTweakArea.Security => "Protection someone switched off. These matter most.",
        BadTweakArea.WindowsUpdate => "Blocks that stop security fixes and app updates.",
        BadTweakArea.BootSettings => "Leftover boot values from timer and \"latency\" guides. A restart is needed after removing them.",
        BadTweakArea.Scheduler => "Placebo values from \"FPS\" registry packs, put back to Windows' defaults.",
        BadTweakArea.Network => "Network changes that slow downloads or break sign-in, Store and updates.",
        BadTweakArea.Services => "Windows services switched off by \"disable these\" lists.",
        BadTweakArea.Storage => "Disk and memory settings that cost speed or stability.",
        BadTweakArea.CpuPower => "Power-plan changes that can hurt on some CPUs.",
        BadTweakArea.Graphics => "Driver values from tweak packs.",
        BadTweakArea.Games => "Old per-game \"boost\" files that do nothing now.",
        BadTweakArea.Policies => "Policies make Settings say \"Managed by your organization\" and grey out the switch. Each is listed so you can decide.",
        _ => "Other leftovers.",
    };
}
