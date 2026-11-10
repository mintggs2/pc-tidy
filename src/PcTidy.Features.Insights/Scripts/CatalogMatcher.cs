using System.Globalization;
using System.Text.RegularExpressions;
using PcTidy.Catalog.Tweaks;
using PcTidy.Core.Model;

namespace PcTidy.Features.Insights.Scripts;

/// <summary>
/// Looks each change a script would make up in the Tweaks research (PcTidy.Catalog: <see cref="TweakCatalog"/> verdict table and
/// <see cref="BadTweakCatalog"/> checks), read-only. A change the research doesn't cover is "Unknown": PC Tidy never guesses.
/// </summary>
public static partial class CatalogMatcher
{
    private const string NoResearch = "PC Tidy has no research on this change, so it can't say whether it helps.";
    private static readonly IReadOnlySet<string> DefenderServices =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "WinDefend", "WdNisSvc", "WdFilter", "WdBoot", "Sense", "SecurityHealthService", "wscsvc", "mpssvc", "BFE" };

    // ------------------------------------------------------------------ registry

    public static ScriptFinding ForRegistry(RegistryWrite w, bool decoded = false)
    {
        var spec = w.Hive is { } h && w.ValueName is not null
            ? new RegistryValueSpec { Hive = h, KeyPath = w.KeyPath, ValueName = w.ValueName, Kind = w.Kind ?? RegistryKind.DWord, Data = w.Data }
            : null;
        ScriptFinding Make(ScriptArea area, string what, ScriptVerdict verdict, string why, string? id = null, bool red = false)
            => new()
            {
                Line = w.Line, Area = area, What = what, Verdict = verdict, Why = why, Evidence = Shorten(w.Evidence),
                MatchedId = id, RedFlag = red, Registry = spec, FromDecodedPayload = decoded,
            };

        var data = RegistryText.Comparable(w.Data);
        string what = w.Op switch
        {
            RegistryOp.SetValue => $"Sets {w.Label} to {Show(data)}",
            RegistryOp.DeleteValue => $"Removes {w.Label}",
            RegistryOp.DeleteKey => $"Deletes the whole key {w.FullKey}",
            _ => $"Creates the key {w.FullKey}",
        };

        // A service's start type written straight into the registry.
        if (w.Hive == RegistryHive.LocalMachine && w.Op == RegistryOp.SetValue && "Start".Equals(w.ValueName, StringComparison.OrdinalIgnoreCase)
            && ServiceKey().Match(w.KeyPath) is { Success: true } svc && FromStartNumber(data) is { } start)
        {
            var f = ForService(w.Line, w.Evidence, svc.Groups[1].Value, start, stop: false, decoded);
            return f with { Area = ScriptArea.Service, Registry = spec };
        }

        if (w.Hive is { } hive && w.ValueName is not null)
        {
            var bad = MatchBadTweak(hive, w, data);
            var tw = w.Op == RegistryOp.SetValue ? MatchTweak(hive, w, data) : null;
            if (bad is { RestoresDefault: true })
                return Make(ScriptArea.Registry, what, ScriptVerdict.Proven, $"This puts Windows' default back (it undoes \"{bad.Check.Title}\").", bad.Check.Id);
            // The verdict table is the more specific answer for placebo rows the finder also resets (MMCSS, throttling…).
            if (tw is { Verdict: ScriptVerdict.Placebo or ScriptVerdict.Harmful }) return Make(ScriptArea.Registry, what, tw.Verdict, tw.Why, tw.Id);
            if (bad is not null)
                return Make(AreaOf(bad.Check), what, ScriptVerdict.Harmful, bad.Check.WhyBad, bad.Check.Id, red: bad.Check.Area == BadTweakArea.Security && IsDefenderKey(w.KeyPath));
            if (tw is not null) return Make(ScriptArea.Registry, what, tw.Verdict, tw.Why, tw.Id);
            if (w.Op == RegistryOp.DeleteValue && MatchTweakDelete(hive, w) is { } undo) return Make(ScriptArea.Registry, what, undo.Verdict, undo.Why, undo.Id);
            if (w.Op == RegistryOp.SetValue && LeftoverValue(hive, w, data) is { } left) return Make(ScriptArea.Registry, left.What ?? what, left.Verdict, left.Why, left.Id);
        }

        if (IsDefenderKey(w.KeyPath) && w.Op is RegistryOp.SetValue or RegistryOp.DeleteKey)
            return Make(ScriptArea.Defender, what, ScriptVerdict.Harmful,
                "It changes Microsoft Defender's settings. Turning protection down is how fake optimizers keep their own files from being caught.", red: true);
        if (w.Op == RegistryOp.CreateKey)
            return Make(ScriptArea.Registry, what, ScriptVerdict.Unknown, "Creating an empty key changes nothing by itself; the values written into it are listed separately.");
        if (w.Op == RegistryOp.DeleteKey)
            return Make(ScriptArea.Registry, what, ScriptVerdict.Unknown, "Deleting a whole key removes every value inside it, and there's no undo unless you exported it first.");
        if (w.Hive is null)
            return Make(ScriptArea.Registry, what, ScriptVerdict.Unknown, $"PC Tidy has no research on {w.RawHive} settings.");
        return Make(ScriptArea.Registry, what, ScriptVerdict.Unknown, NoResearch);
    }

    private sealed record BadMatch(BadTweakCheck Check, bool RestoresDefault);

    private static BadMatch? MatchBadTweak(RegistryHive hive, RegistryWrite w, string? data)
    {
        foreach (var check in BadTweakCatalog.All)
            foreach (var signal in check.Signals)
            {
                if (signal.Probe is not RegistryProbe p || p.Hive != hive || !SameKey(p.KeyPath, w.KeyPath)
                    || !p.ValueName.Equals(w.ValueName, StringComparison.OrdinalIgnoreCase)) continue;
                if (w.Op == RegistryOp.DeleteValue)
                {
                    if (signal.Fixes.OfType<RegistryDeleteFix>().Any(f => f.ValueName is null || f.ValueName.Equals(w.ValueName, StringComparison.OrdinalIgnoreCase)))
                        return new BadMatch(check, RestoresDefault: true);
                    continue;
                }
                if (w.Op != RegistryOp.SetValue) continue;
                if (signal.Fixes.OfType<RegistrySetFix>().Any(f => Same(RegistryText.Comparable(f.Data), data)))
                    return new BadMatch(check, RestoresDefault: true);
                if (Fires(p, data)) return new BadMatch(check, RestoresDefault: false);
            }
        return null;
    }

    private static bool Fires(RegistryProbe p, string? data)
    {
        var values = p.Values.Select(RegistryText.Comparable).ToList();
        return p.Test switch
        {
            ValueTest.Present => true,
            ValueTest.AnyOf => values.Any(v => Same(v, data)),
            ValueTest.NoneOf => !values.Any(v => Same(v, data)),
            ValueTest.AnyBits => ulong.TryParse(data, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d)
                                 && ulong.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bits) && (d & bits) != 0,
            ValueTest.Empty => string.IsNullOrEmpty(data),
            _ => false,
        };
    }

    private sealed record TweakMatch(string Id, ScriptVerdict Verdict, string Why);

    private static TweakMatch? MatchTweak(RegistryHive hive, RegistryWrite w, string? data)
    {
        foreach (var tweak in TweakCatalog.All)
            foreach (var change in tweak.Changes)
            {
                switch (change)
                {
                    case RegistryValueChange r when r.Hive == hive && SameKey(r.KeyPath, w.KeyPath) && r.ValueName.Equals(w.ValueName, StringComparison.OrdinalIgnoreCase):
                        if (Same(RegistryText.Comparable(r.Data), data)) return new TweakMatch(tweak.Id, Map(tweak.Verdict), WhyOf(tweak));
                        if (r.WindowsDefault is not null && Same(RegistryText.Comparable(r.WindowsDefault), data))
                            return new TweakMatch(tweak.Id, ScriptVerdict.Proven, $"This is Windows' own default for \"{tweak.Title}\".");
                        // Same setting, other value: placebo and harmful settings stay so whatever the number.
                        if (tweak.Verdict is TweakVerdict.Placebo or TweakVerdict.Harmful) return new TweakMatch(tweak.Id, Map(tweak.Verdict), WhyOf(tweak));
                        return new TweakMatch(tweak.Id, ScriptVerdict.Unknown, $"It's the setting behind \"{tweak.Title}\", but with a value the research didn't test.");
                    case RegistryTokenChange t when t.Hive == hive && SameKey(t.KeyPath, w.KeyPath) && t.ValueName.Equals(w.ValueName, StringComparison.OrdinalIgnoreCase)
                                                    && (data ?? "").Contains(t.Token + "=" + t.Value, StringComparison.OrdinalIgnoreCase):
                        return new TweakMatch(tweak.Id, Map(tweak.Verdict), WhyOf(tweak));
                }
            }
        return null;
    }

    private sealed record Leftover(string? What, ScriptVerdict Verdict, string Why, string? Id);

    private const string WindowsUpdatePolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";

    /// <summary>
    /// Values the bad-tweak finder (or the Games leftovers check) reports but that have no plain registry probe to match:
    /// a forced CPU priority, the "run as administrator" compatibility flag, Windows Update pause/deferral day counts.
    /// </summary>
    private static Leftover? LeftoverValue(RegistryHive hive, RegistryWrite w, string? data)
    {
        var name = w.ValueName ?? "";
        if (hive == RegistryHive.LocalMachine && name.Equals("CpuPriorityClass", StringComparison.OrdinalIgnoreCase) && PerfOptionsKey().Match(w.KeyPath) is { Success: true } po)
        {
            var exe = po.Groups["exe"].Value;
            var check = BadTweakCatalog.Find("scheduler.realtime-priority");
            return data == "4"
                ? new($"Forces {exe} to Realtime CPU priority", ScriptVerdict.Harmful,
                    check?.WhyBad ?? "Realtime priority can starve Windows itself (mouse, audio, disk) and freeze the PC.", check?.Id)
                : new($"Forces {exe} to a fixed CPU priority ({PriorityName(data)})", ScriptVerdict.Unknown,
                    "PC Tidy has no research showing a gain from a fixed priority. Realtime (4) is the one that can freeze the PC.", null);
        }
        if (LayersKey().IsMatch(w.KeyPath) && (data ?? "").Contains("RUNASADMIN", StringComparison.OrdinalIgnoreCase))
            return new($"Makes {Path.GetFileName(name)} always run as administrator", ScriptVerdict.Placebo,
                "Guides tick \"Run as administrator\" for more FPS. It doesn't make the program faster, but the program (and any mod or overlay it loads) then gets full admin rights every time, "
                + "and some launchers and anti-cheats stop working properly. PC Tidy's Games section shows this tick for installed games.", null);
        if (hive == RegistryHive.LocalMachine && SameKey(w.KeyPath, WindowsUpdatePolicyKey) && UpdateHoldName().IsMatch(name) && data is { Length: > 0 } && data != "0")
        {
            var check = BadTweakCatalog.Find("update.paused-by-policy");
            return new(null, ScriptVerdict.Harmful, check?.WhyBad ?? "A pause or deferral policy keeps security fixes away for weeks or months.", check?.Id);
        }
        return null;
    }

    private static string PriorityName(string? n) => n switch
    {
        "1" => "Idle", "2" => "Normal", "3" => "High", "5" => "Below normal", "6" => "Above normal", _ => n ?? "unreadable",
    };

    private static TweakMatch? MatchTweakDelete(RegistryHive hive, RegistryWrite w)
    {
        foreach (var tweak in TweakCatalog.All)
            foreach (var r in tweak.Changes.OfType<RegistryValueChange>())
                if (r.Hive == hive && SameKey(r.KeyPath, w.KeyPath) && r.ValueName.Equals(w.ValueName, StringComparison.OrdinalIgnoreCase) && r.WindowsDefault is null)
                    return new TweakMatch(tweak.Id, ScriptVerdict.Proven, $"This puts Windows' default back for \"{tweak.Title}\" (the value isn't there on a fresh Windows).");
        return null;
    }

    // ------------------------------------------------------------------ services

    public static ScriptFinding ForService(int line, string evidence, string service, ServiceStart? target, bool stop, bool decoded = false)
    {
        ScriptFinding Make(ScriptVerdict v, string what, string why, string? id = null, bool red = false, ScriptArea area = ScriptArea.Service)
            => new() { Line = line, Area = area, What = what, Verdict = v, Why = why, Evidence = Shorten(evidence), MatchedId = id, RedFlag = red, FromDecodedPayload = decoded };

        bool security = DefenderServices.Contains(service);
        if (stop)
            return security
                ? Make(ScriptVerdict.Harmful, $"Stops the '{service}' service", "It switches off part of Windows Security. Fake optimizers do this so their downloads aren't caught.", red: true, area: ScriptArea.Defender)
                : Make(ScriptVerdict.Unknown, $"Stops the '{service}' service", "It stops until the next restart. Stopping services rarely speeds anything up.");

        var what = $"Sets the '{service}' service to {(target is { } t ? RegistryNames.Friendly(t) : "a new start type")}";
        if (target == ServiceStart.Disabled)
        {
            foreach (var check in BadTweakCatalog.All)
                if (check.Signals.Any(s => s.Probe is ServiceDisabledProbe p && p.ServiceName.Equals(service, StringComparison.OrdinalIgnoreCase)))
                    return Make(ScriptVerdict.Harmful, what, check.WhyBad, check.Id, red: security, area: security ? ScriptArea.Defender : ScriptArea.Service);
            if (security)
                return Make(ScriptVerdict.Harmful, what, "It switches off part of Windows Security.", red: true, area: ScriptArea.Defender);
        }
        foreach (var tweak in TweakCatalog.All)
            foreach (var s in tweak.Changes.OfType<ServiceStartChange>())
            {
                if (!s.ServiceName.Equals(service, StringComparison.OrdinalIgnoreCase)) continue;
                if (target == s.WindowsDefault) return Make(ScriptVerdict.Proven, what, $"That's Windows' default for this service (it undoes \"{tweak.Title}\").", tweak.Id);
                return Make(Map(tweak.Verdict), what, WhyOf(tweak), tweak.Id);
            }
        return target == ServiceStart.Disabled
            ? Make(ScriptVerdict.Unknown, what, "Turning services off rarely speeds anything up and can break features that need them later.")
            : Make(ScriptVerdict.Unknown, what, NoResearch);
    }

    // ------------------------------------------------------------------ boot settings (bcdedit)

    public static ScriptFinding ForBoot(int line, string evidence, string name, string? value, bool delete, bool decoded = false)
    {
        ScriptFinding Make(ScriptVerdict v, string what, string why, string? id = null, bool red = false)
            => new() { Line = line, Area = ScriptArea.BootSettings, What = what, Verdict = v, Why = why, Evidence = Shorten(evidence), MatchedId = id, RedFlag = red, FromDecodedPayload = decoded };

        var lower = name.ToLowerInvariant();
        var what = delete ? $"Removes the boot setting '{name}'" : $"Sets the boot setting '{name}' to {value ?? "on"}";
        var bitLocker = BadTweakCatalog.BitLockerWatchedBootValues.Contains(lower)
            ? " BitLocker watches this setting, so a BitLocker PC may ask for its recovery key at the next start." : "";

        if (lower is "recoveryenabled" or "bootstatuspolicy" && !delete && value is not null
            && (value.Equals("no", StringComparison.OrdinalIgnoreCase) || value.Contains("ignoreallfailures", StringComparison.OrdinalIgnoreCase)))
            return Make(ScriptVerdict.Harmful, what, "It turns off Windows' automatic repair at startup. Ransomware does exactly this.", red: true);
        if (!delete && IsBootDefault(lower, value))
            return Make(ScriptVerdict.Proven, what, "That's Windows' default value." + bitLocker);

        foreach (var check in BadTweakCatalog.All)
            foreach (var p in check.Signals.Select(s => s.Probe).OfType<BootValueProbe>())
                if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return delete
                        ? Make(ScriptVerdict.Proven, what, $"Removing it puts Windows' default back (it undoes \"{check.Title}\").{bitLocker}", check.Id)
                        : Make(ScriptVerdict.Harmful, what, check.WhyBad + bitLocker, check.Id);
        return Make(ScriptVerdict.Unknown, what, (delete ? "PC Tidy has no research on this boot setting." : "Boot settings change how Windows starts; PC Tidy has no research on this one.") + bitLocker);
    }

    private static bool IsBootDefault(string name, string? value) => (name, value?.ToLowerInvariant()) switch
    {
        ("nx", "optin") => true,
        ("hypervisorlaunchtype", "auto") => true,
        _ => false,
    };

    // ------------------------------------------------------------------ commands from the verdict table (netsh, powercfg, bcdedit text)

    /// <summary>A command line the Tweaks table lists word for word (e.g. "netsh int tcp set global autotuninglevel=disabled").</summary>
    public static ScriptFinding? ForCommand(int line, string evidence, string command, ScriptArea area, bool decoded = false)
    {
        var norm = NormalizeCommand(command);
        foreach (var tweak in TweakCatalog.All)
            foreach (var c in tweak.Changes.OfType<CommandChange>())
                if (norm.Contains(NormalizeCommand(c.CommandLine), StringComparison.Ordinal))
                    return new ScriptFinding
                    {
                        Line = line, Area = area, What = tweak.Title, Verdict = Map(tweak.Verdict), Why = WhyOf(tweak),
                        Evidence = Shorten(evidence), MatchedId = tweak.Id, FromDecodedPayload = decoded,
                    };
        return null;
    }

    public static ScriptFinding ForPowerPlan(int line, string evidence, Guid scheme, bool decoded = false)
    {
        foreach (var tweak in TweakCatalog.All)
            foreach (var p in tweak.Changes.OfType<PowerPlanChange>())
                if (p.SchemeGuid == scheme)
                    return new ScriptFinding
                    {
                        Line = line, Area = ScriptArea.PowerPlan, What = $"Switches to the '{p.Name}' power plan", Verdict = Map(tweak.Verdict),
                        Why = WhyOf(tweak), Evidence = Shorten(evidence), MatchedId = tweak.Id, FromDecodedPayload = decoded,
                    };
        var known = KnownPlans.TryGetValue(scheme, out var name);
        return new ScriptFinding
        {
            Line = line, Area = ScriptArea.PowerPlan, What = known ? $"Switches to the '{name}' power plan" : $"Switches to the power plan {scheme:B}",
            Verdict = known ? ScriptVerdict.Situational : ScriptVerdict.Unknown,
            Why = known ? "A built-in Windows plan: harmless, and you can switch back in Settings → System → Power." : NoResearch,
            Evidence = Shorten(evidence), FromDecodedPayload = decoded,
        };
    }

    private static readonly IReadOnlyDictionary<Guid, string> KnownPlans = new Dictionary<Guid, string>
    {
        [new Guid("381b4222-f694-41f0-9685-ff5bb260df2e")] = "Balanced",
        [new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c")] = "High performance",
        [new Guid("a1841308-3541-4fab-bc81-f71556f20b4a")] = "Power saver",
    };

    // ------------------------------------------------------------------ Store apps

    public static ScriptFinding ForAppxRemoval(int line, string evidence, IReadOnlyList<string> names, bool decoded = false)
    {
        ScriptFinding Make(ScriptVerdict v, string what, string why, bool red = false)
            => new() { Line = line, Area = ScriptArea.StoreApp, What = what, Verdict = v, Why = why, Evidence = Shorten(evidence), RedFlag = red, FromDecodedPayload = decoded };

        if (names.Count == 0)
            return Make(ScriptVerdict.Harmful, "Removes every Store app it can find",
                "With no name filter this removes the Microsoft Store, frameworks and Windows' own apps too. Getting them back can need a Windows reinstall.");
        var core = names.FirstOrDefault(IsCorePackage);
        var list = string.Join(", ", names.Take(4)) + (names.Count > 4 ? $" and {names.Count - 4} more" : "");
        if (core is not null)
            return Make(ScriptVerdict.Harmful, $"Removes the Store app(s) {list}",
                $"'{core.Trim('*')}' is part of Windows (Store, Xbox, frameworks or security). Removing it breaks updates, games or sign-in.");
        return Make(ScriptVerdict.Situational, $"Removes the Store app(s) {list}",
            "Removing an app you don't use is fine; reinstall it from the Microsoft Store to get it back. The Apps section does this with a list you can check first.");
    }

    private static bool IsCorePackage(string pattern)
    {
        var p = pattern.Trim('*', '"', '\'', ' ');
        if (p.Length == 0) return true;
        if (TweakSafetyRules.MatchesAny(p, TweakSafetyRules.NeverRemovedPackages)) return true;
        // Wildcard filters like *xbox* or *store*: core when the fragment appears in a protected name.
        return TweakSafetyRules.NeverRemovedPackages.Any(n => n.TrimEnd('*').Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ scheduled tasks

    public static ScriptFinding ForTask(int line, string evidence, string taskPath, bool disable, bool decoded = false)
    {
        ScriptFinding Make(ScriptVerdict v, string what, string why, string? id = null)
            => new() { Line = line, Area = ScriptArea.ScheduledTask, What = what, Verdict = v, Why = why, Evidence = Shorten(evidence), MatchedId = id, FromDecodedPayload = decoded };

        var name = taskPath.Trim('"', '\'', ' ');
        if (!disable) return Make(ScriptVerdict.Unknown, $"Turns on the scheduled task '{name}'", NoResearch);
        foreach (var check in BadTweakCatalog.All)
            foreach (var p in check.Signals.Select(s => s.Probe).OfType<TaskDisabledProbe>())
                if (p.TaskPath.TrimEnd('\\').EndsWith(name.TrimStart('\\'), StringComparison.OrdinalIgnoreCase))
                    return Make(ScriptVerdict.Harmful, $"Turns off the scheduled task '{name}'", check.WhyBad, check.Id);
        return Make(ScriptVerdict.Unknown, $"Turns off the scheduled task '{name}'", "Windows' tasks keep things like updates, search and the clock working. PC Tidy has no research on this one.");
    }

    // ------------------------------------------------------------------ helpers

    public static ScriptVerdict Map(TweakVerdict v) => v switch
    {
        TweakVerdict.Proven => ScriptVerdict.Proven,
        TweakVerdict.Situational => ScriptVerdict.Situational,
        TweakVerdict.Placebo => ScriptVerdict.Placebo,
        _ => ScriptVerdict.Harmful,
    };

    private static string WhyOf(TweakDefinition t) => t.Verdict switch
    {
        TweakVerdict.Placebo or TweakVerdict.Harmful => t.Caveat ?? t.Effect,
        _ => t.Effect + " The Tweaks section can do this with one-click undo.",
    };

    private static ScriptArea AreaOf(BadTweakCheck c) => c.Area switch
    {
        BadTweakArea.Security => ScriptArea.Registry,
        BadTweakArea.Network => ScriptArea.Network,
        BadTweakArea.Services => ScriptArea.Service,
        BadTweakArea.BootSettings => ScriptArea.BootSettings,
        _ => ScriptArea.Registry,
    };

    public static ServiceStart? FromStartNumber(string? n) => n switch
    {
        "0" => ServiceStart.Boot,
        "1" => ServiceStart.System,
        "2" => ServiceStart.Automatic,
        "3" => ServiceStart.Manual,
        "4" => ServiceStart.Disabled,
        _ => null,
    };

    public static ServiceStart? FromStartWord(string word) => word.Trim('"', '\'', ' ').ToLowerInvariant() switch
    {
        "disabled" => ServiceStart.Disabled,
        "manual" or "demand" => ServiceStart.Manual,
        "automatic" or "auto" => ServiceStart.Automatic,
        "delayed-auto" or "automaticdelayedstart" => ServiceStart.AutomaticDelayed,
        "boot" => ServiceStart.Boot,
        "system" => ServiceStart.System,
        _ => null,
    };

    private static bool IsDefenderKey(string key) => key.Contains("Windows Defender", StringComparison.OrdinalIgnoreCase);

    private static bool SameKey(string a, string b)
        => RegistryText.NormalizeKey(a).Equals(RegistryText.NormalizeKey(b), StringComparison.OrdinalIgnoreCase);

    private static bool Same(string? a, string? b) => string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);

    private static string Show(string? d) => d is null ? "(unreadable)" : d.Length == 0 ? "\"\"" : d.Length > 60 ? d[..57] + "…" : d;

    public static string NormalizeCommand(string c) => Spaces().Replace(c.Trim().Trim('"').ToLowerInvariant(), " ").Replace(" = ", "=", StringComparison.Ordinal);

    public static string Shorten(string s)
    {
        var t = s.Trim();
        return t.Length <= 160 ? t : t[..157] + "…";
    }

    [GeneratedRegex(@"^SYSTEM\\CurrentControlSet\\Services\\([^\\]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ServiceKey();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^SOFTWARE\\(?:WOW6432Node\\)?Microsoft\\Windows NT\\CurrentVersion\\Image File Execution Options\\(?<exe>[^\\]{1,260})\\PerfOptions$", RegexOptions.IgnoreCase, CommandRules.RegexTimeoutMs)]
    private static partial Regex PerfOptionsKey();

    [GeneratedRegex(@"^Software\\(?:WOW6432Node\\)?Microsoft\\Windows NT\\CurrentVersion\\AppCompatFlags\\Layers$", RegexOptions.IgnoreCase, CommandRules.RegexTimeoutMs)]
    private static partial Regex LayersKey();

    [GeneratedRegex(@"^(?:Pause|Defer)\w{0,60}$", RegexOptions.IgnoreCase, CommandRules.RegexTimeoutMs)]
    private static partial Regex UpdateHoldName();
}
