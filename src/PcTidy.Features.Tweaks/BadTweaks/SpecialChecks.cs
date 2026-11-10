using PcTidy.Features.Tweaks.Reading;

namespace PcTidy.Features.Tweaks.BadTweaks;

/// <summary>One registry value a custom check found (logical hive and key: the plan maps it through the redirect).</summary>
/// <param name="What">What the value does, in plain words (shown in the fix line), when the check knows.</param>
internal sealed record FoundValue(RegistryHive Hive, string KeyPath, string ValueName, RegistryKind Kind, string? What = null);

/// <summary>What a custom check found: plain-English evidence lines plus anything its fix needs (hosts lines, file paths, registry values).</summary>
internal sealed record CustomFinding(IReadOnlyList<string> Evidence, IReadOnlyList<string> HostsLines, IReadOnlyList<FileInfo> Files)
{
    public static CustomFinding Lines(IReadOnlyList<string> evidence) => new(evidence, Array.Empty<string>(), Array.Empty<FileInfo>());

    /// <summary>Registry values a <see cref="RegistryDeleteFoundFix"/> removes.</summary>
    public IReadOnlyList<FoundValue> Values { get; init; } = Array.Empty<FoundValue>();

    /// <summary>The power plan a <see cref="PowerPlanSetFix"/> switches to is on this PC (otherwise only the guidance is shown).</summary>
    public bool PowerPlanAvailable { get; init; }
}

/// <summary>
/// The finder checks that need code rather than one registry value. All read-only: registry reads through the
/// (possibly redirected) reader, file reads, and the system probe's WMI/power-API answers.
/// </summary>
internal sealed class SpecialChecks
{
    private const int MaxListed = 25;
    private const string MemoryManagementKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
    private readonly TweakEnvironment _env;
    private readonly IReadOnlySet<string> _coveredValues;

    /// <param name="coveredValues">"HIVE\key\value" of every value a catalog check already looks at (not listed again as "other").</param>
    public SpecialChecks(TweakEnvironment env, IReadOnlySet<string> coveredValues)
    {
        _env = env;
        _coveredValues = coveredValues;
    }

    public CustomFinding? Run(string kind) => kind switch
    {
        CustomProbeKind.HostsFile => Hosts(),
        CustomProbeKind.TcpAutotuning => _env.Probe.TcpAutotuningLevel is int level and >= 0 and <= 2
            ? CustomFinding.Lines(new[] { "TCP receive autotuning: " + level switch { 0 => "Disabled", 1 => "Highly restricted", _ => "Restricted" } })
            : null,
        CustomProbeKind.BuiltInAdministrator => BuiltInAdministrator(),
        // High performance plans keep every core unparked by design; that only hurts on X3D and hybrid CPUs.
        CustomProbeKind.CoreParking => _env.Probe.CoreParkingMinCoresAc == 100 && _env.Probe.CpuKind != CpuKind.None
            ? CustomFinding.Lines(new[] { "Active power plan: minimum unparked cores = 100% (core parking off). On your CPU this can put games on the wrong cores." })
            : null,
        CustomProbeKind.BrokenServices => BrokenServices(),
        CustomProbeKind.FirewallBlocksMicrosoft => FirewallBlocks(),
        CustomProbeKind.FakeUpdateServer => FakeUpdateServer(),
        CustomProbeKind.NagleLeftovers => Nagle(),
        CustomProbeKind.RealtimePriority => RealtimePriority(),
        CustomProbeKind.RobloxFastFlags => Roblox(),
        CustomProbeKind.EdgePolicies => EdgePolicies(),
        CustomProbeKind.OtherPolicies => OtherPolicies(),
        CustomProbeKind.DefenderPolicyLeftovers => DefenderPolicyLeftovers(),
        CustomProbeKind.DefenderProtectionReduced => DefenderProtectionReduced(),
        CustomProbeKind.SystemProtectionOff => SystemProtectionOff(),
        CustomProbeKind.FixedPagefile => FixedPagefile(),
        CustomProbeKind.MemoryCompressionOff => _env.Probe.MemoryCompressionRunning == false
            ? CustomFinding.Lines(new[] { "Windows' Memory Compression process isn't running, so memory compression is probably off." })
            : null,
        CustomProbeKind.UnboundAdapterProtocols => UnboundProtocols(),
        CustomProbeKind.ManyServicesDisabled => ManyServicesDisabled(),
        CustomProbeKind.HardwareAccessDrivers => HardwareAccessDrivers(),
        CustomProbeKind.DefenderOnlyRealtime => DefenderOnlyRealtime(),
        CustomProbeKind.DisabledSystemDevices => DisabledSystemDevices(),
        CustomProbeKind.StrayPowerPlan => StrayPowerPlan(),
        _ => null,
    };

    // ------------------------------------------------------------------ accounts and protection

    private CustomFinding? BuiltInAdministrator()
    {
        if (_env.Probe.BuiltInAdministratorEnabled != true) return null;
        var name = _env.Probe.BuiltInAdministratorName is { Length: > 0 } n && !n.Contains('"') ? n : null;
        var lines = new List<string> { name is null
            ? "The built-in Administrator account (the one with ID 500) is enabled."
            : $"The built-in Administrator account (ID 500, called \"{name}\" on this PC) is enabled." };
        // The account's name differs by Windows language, so the command uses the name this PC reported.
        if (name is not null) lines.Add($"To turn it off: net user \"{name}\" /active:no");
        return CustomFinding.Lines(lines);
    }

    /// <summary>Only while Defender is the antivirus in charge: with another antivirus on, Defender's own switches don't apply.</summary>
    private CustomFinding? DefenderProtectionReduced()
    {
        if (_env.Probe.ThirdPartyAntivirus.Count > 0 || _env.Probe.Defender is not { AntivirusEnabled: true } d) return null;
        // The bigger "only real-time is left" finding already lists Tamper and cloud protection: don't say it twice.
        if (DefenderOnlyRealtime() is not null) return null;
        var lines = new List<string>();
        if (d.TamperProtected == false) lines.Add("Tamper Protection is off.");
        if (d.CloudReporting == 0) lines.Add("Cloud-delivered protection is off.");
        return lines.Count > 0 ? CustomFinding.Lines(lines) : null;
    }

    /// <summary>At least this many of Defender's other protections off, with real-time still on, counts as "only real-time left".</summary>
    internal const int OnlyRealtimeMinimumOff = 3;

    /// <summary>
    /// "Leave only real-time protection on" scripts: Defender is the antivirus in charge and real-time scanning is on, but most
    /// of behaviour monitoring, download scanning, script scanning, cloud protection, sample submission and Tamper Protection are off.
    /// </summary>
    private CustomFinding? DefenderOnlyRealtime()
    {
        if (_env.Probe.ThirdPartyAntivirus.Count > 0 || _env.Probe.Defender is not { AntivirusEnabled: true, RealTimeProtection: true } d) return null;
        var off = new List<string>();
        if (d.BehaviorMonitoring == false) off.Add("Behaviour monitoring is off.");
        if (d.DownloadScanning == false) off.Add("Scanning of downloaded files and attachments is off.");
        if (d.ScriptScanningDisabled == true) off.Add("Script scanning is off.");
        if (d.CloudReporting == 0) off.Add("Cloud-delivered protection is off.");
        if (d.SampleSubmission == 2) off.Add("Automatic sample submission is set to never send.");
        if (d.TamperProtected == false) off.Add("Tamper Protection is off.");
        if (off.Count < OnlyRealtimeMinimumOff) return null;
        return CustomFinding.Lines(new[] { "Real-time protection is on." }.Concat(off).ToList());
    }

    private CustomFinding? DisabledSystemDevices()
    {
        if (_env.Probe.DisabledSystemDevices is not { Count: > 0 } devices) return null;
        var evidence = devices.Take(MaxListed).Select(d => $"{d.Name} is disabled ({d.InstanceId})").ToList();
        if (devices.Count > MaxListed) evidence.Add($"and {devices.Count - MaxListed} more");
        return CustomFinding.Lines(evidence);
    }

    /// <summary>The active plan isn't Balanced, High performance or Power saver (compared by GUID, never by its translated name).</summary>
    private CustomFinding? StrayPowerPlan()
    {
        if (_env.Probe.PowerPlans is not { } plans || BadTweakCatalog.WindowsPowerPlans.Contains(plans.Active)) return null;
        var name = plans.ActiveName is { Length: > 0 } n ? $"\"{n}\"" : "A custom plan";
        return CustomFinding.Lines(new[] { $"{name} is the active power plan ({plans.Active:B})." })
            with { PowerPlanAvailable = plans.Installed.Contains(BadTweakCatalog.BalancedPlan) };
    }

    private CustomFinding? SystemProtectionOff()
    {
        if (_env.Probe.SystemProtectionOn != false) return null;
        var lines = new List<string> { "System Protection is off, so Windows can't make restore points." };
        if (_env.Probe.RestorePointCount is int count)
            lines.Add(count == 0 ? "There are no restore points on this PC." : $"{count} older restore point{(count == 1 ? "" : "s")} still exist.");
        return CustomFinding.Lines(lines);
    }

    /// <summary>
    /// Defender policy values the catalog doesn't name, sorted by what they do (DEF-1): only those that switch protection
    /// off or exclude things from scans are offered for removal. Policies that add protection (ASR rules, Controlled Folder
    /// Access, network protection, PUA blocking, update settings) and other settings are listed as kept. Nothing to
    /// remove = no finding: protective policies are not a problem.
    /// </summary>
    private CustomFinding? DefenderPolicyLeftovers()
    {
        var weakening = new List<FoundValue>();
        var kept = new List<(string Line, DefenderPolicyEffect Effect)>();
        int seen = 0;
        void Walk(string key, string relative, int depth)
        {
            foreach (var name in _env.Registry.ValueNames(RegistryHive.LocalMachine, key))
            {
                if (++seen > DefenderPolicyAllowances.MaxValues) return;
                if (name.Length == 0 || _coveredValues.Contains(TweakRegistryAllowances.KeyOf(RegistryHive.LocalMachine, key, name))) continue;
                if (_env.Registry.Read(RegistryHive.LocalMachine, key, name) is not { Kind: { } kind } value) continue;
                var (effect, what) = DefenderPolicyEffects.Classify(relative, name, value.Data);
                if (effect == DefenderPolicyEffect.Weakens) weakening.Add(new FoundValue(RegistryHive.LocalMachine, key, name, kind, what));
                else kept.Add(($@"HKLM\{key} → {name} ({what})", effect));
            }
            if (depth >= DefenderPolicyAllowances.MaxDepth) return;
            foreach (var sub in _env.Registry.SubKeyNames(RegistryHive.LocalMachine, key))
                Walk(key + "\\" + sub, relative.Length == 0 ? sub : relative + "\\" + sub, depth + 1);
        }
        Walk(DefenderPolicyAllowances.PolicyKey, "", 0);
        if (weakening.Count == 0) return null;
        var evidence = weakening.Take(MaxListed).Select(v => $@"HKLM\{v.KeyPath} → {v.ValueName}: {v.What}").ToList();
        if (weakening.Count > MaxListed) evidence.Add($"and {weakening.Count - MaxListed} more values");
        foreach (var (line, effect) in kept.Take(MaxListed))
            evidence.Add(effect == DefenderPolicyEffect.Protects
                ? $"Kept, these add protection: {line}"
                : $"Kept, set by you or your organisation: {line}");
        if (kept.Count > MaxListed) evidence.Add($"and {kept.Count - MaxListed} more kept values");
        return CustomFinding.Lines(evidence) with { Values = weakening };
    }

    // ------------------------------------------------------------------ memory and paging file

    private CustomFinding? FixedPagefile()
    {
        if (_env.Registry.Read(RegistryHive.LocalMachine, MemoryManagementKey, "PagingFiles") is not { Kind: RegistryKind.MultiString } v) return null;
        var evidence = new List<string>();
        foreach (var line in v.Data.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // "C:\pagefile.sys 16384 16384" = fixed size in MB; "?:\pagefile.sys" or "C:\pagefile.sys 0 0" = Windows manages it.
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || !long.TryParse(parts[^2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var initial)
                || !long.TryParse(parts[^1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var max)) continue;
            if (initial <= 0 || initial != max) continue;
            var file = string.Join(' ', parts[..^2]);
            var ram = _env.Probe.TotalMemoryBytes is long bytes && bytes > 0 ? $" ({initial * 1024.0 * 1024.0 / bytes:0.#}× the {Sizes.Format(bytes)} of RAM)" : "";
            evidence.Add($"{file} is fixed at {initial.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)} MB{ram}");
        }
        return evidence.Count > 0 ? CustomFinding.Lines(evidence) : null;
    }

    // ------------------------------------------------------------------ network adapters

    private CustomFinding? UnboundProtocols()
    {
        if (_env.Probe.UnboundProtocols is not { Count: > 0 } unbound) return null;
        return CustomFinding.Lines(unbound
            .GroupBy(u => u.Adapter, StringComparer.OrdinalIgnoreCase)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(u => u.Friendly))} unticked")
            .Take(MaxListed).ToList());
    }

    // ------------------------------------------------------------------ services switched off in bulk, hardware drivers

    /// <summary>
    /// "Disable all" in System Configuration leaves nearly every non-Windows service disabled. A couple of disabled
    /// updaters is normal; most of them at once isn't. Windows' own services (under the Windows folder) aren't counted.
    /// </summary>
    internal const int ManyDisabledMinimum = 5;
    internal const double ManyDisabledShare = 0.6;

    private CustomFinding? ManyServicesDisabled()
    {
        const string root = @"SYSTEM\CurrentControlSet\Services";
        var total = 0;
        var disabled = new List<string>();
        foreach (var name in _env.Registry.SubKeyNames(RegistryHive.LocalMachine, root))
        {
            var state = ServiceState.Read(_env.Registry, name);
            if (state is null || (state.Type & 0x30) == 0 || state.ImagePath is null) continue;
            if (ExecutablePath(state.ImagePath) is not { } exe || IsUnderWindows(exe)) continue;
            total++;
            if (state.IsDisabled) disabled.Add(name);
        }
        if (disabled.Count < ManyDisabledMinimum || disabled.Count < total * ManyDisabledShare) return null;
        var evidence = new List<string> { $"{disabled.Count} of {total} services from other companies are disabled." };
        evidence.AddRange(disabled.Take(MaxListed).Select(s => $"'{s}' is Disabled"));
        if (disabled.Count > MaxListed) evidence.Add($"and {disabled.Count - MaxListed} more");
        return CustomFinding.Lines(evidence);
    }

    private bool IsUnderWindows(string path)
        => path.StartsWith(_env.WindowsDir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private CustomFinding? HardwareAccessDrivers()
    {
        const string root = @"SYSTEM\CurrentControlSet\Services";
        var evidence = new List<string>();
        foreach (var name in _env.Registry.SubKeyNames(RegistryHive.LocalMachine, root))
        {
            var state = ServiceState.Read(_env.Registry, name);
            // Kernel and file-system drivers only (Type 1 or 2), wherever their .sys file lives.
            if (state is null || (state.Type & 0x3) == 0 || state.ImagePath is null) continue;
            var file = Path.GetFileName(state.ImagePath.Trim().Trim('"'));
            if (HardwareAccessDriverNames.TryGetValue(file, out var tool))
                evidence.Add($"Driver '{name}' ({file}, from {tool}){(state.IsDisabled ? ", disabled" : "")}");
            if (evidence.Count >= MaxListed) break;
        }
        return evidence.Count > 0 ? CustomFinding.Lines(evidence) : null;
    }

    /// <summary>Driver files of tools that give programs raw hardware and memory access (Community tweaks §9.6).</summary>
    internal static readonly IReadOnlyDictionary<string, string> HardwareAccessDriverNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["RwDrv.sys"] = "RWEverything, used by USB \"IMOD\" latency tweaks",
        ["WinRing0x64.sys"] = "older fan-control, RGB and monitoring apps (WinRing0)",
        ["WinRing0.sys"] = "older fan-control, RGB and monitoring apps (WinRing0)",
    };

    // ------------------------------------------------------------------ hosts

    private CustomFinding? Hosts()
    {
        var found = HostsFile.Scan(_env.HostsPath);
        if (found is null || found.CriticalLines.Count == 0) return null;
        var evidence = found.CriticalLines.Take(MaxListed).Select(l => "hosts: " + l.Trim()).ToList();
        if (found.CriticalLines.Count > MaxListed) evidence.Add($"and {found.CriticalLines.Count - MaxListed} more lines");
        if (found.TotalBlockLines > found.CriticalLines.Count)
            evidence.Add($"{found.TotalBlockLines - found.CriticalLines.Count} other blocking lines are left as they are.");
        return new CustomFinding(evidence, found.CriticalLines, Array.Empty<FileInfo>());
    }

    // ------------------------------------------------------------------ services

    private CustomFinding? BrokenServices()
    {
        const string root = @"SYSTEM\CurrentControlSet\Services";
        var evidence = new List<string>();
        foreach (var name in _env.Registry.SubKeyNames(RegistryHive.LocalMachine, root))
        {
            var state = ServiceState.Read(_env.Registry, name);
            // Win32 own/shared-process services that would be started; drivers and disabled services are skipped.
            if (state is null || (state.Type & 0x30) == 0 || state.Start is not (2 or 3) || state.ImagePath is null) continue;
            if (ExecutablePath(state.ImagePath) is not { } exe || ExistsOrUncheckable(exe)) continue;
            evidence.Add($"'{name}' → {exe} (missing)");
            if (evidence.Count >= MaxListed) break;
        }
        return evidence.Count > 0 ? CustomFinding.Lines(evidence) : null;
    }

    /// <summary>The .exe a service's ImagePath points at, with %vars% and \SystemRoot\ resolved; null when it isn't a plain exe path.</summary>
    internal string? ExecutablePath(string imagePath)
    {
        var p = imagePath.Trim();
        if (p.StartsWith('"'))
        {
            var end = p.IndexOf('"', 1);
            if (end <= 1) return null;
            p = p[1..end];
        }
        else
        {
            var exe = p.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exe < 0) return null;
            p = p[..(exe + 4)];
        }
        if (p.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) p = Path.Combine(_env.WindowsDir, p[12..]);
        else if (p.StartsWith(@"\??\", StringComparison.Ordinal)) p = p[4..];
        p = Environment.ExpandEnvironmentVariables(p);
        if (!Path.IsPathFullyQualified(p))
        {
            if (!p.StartsWith("system32", StringComparison.OrdinalIgnoreCase)) return null;
            p = Path.Combine(_env.WindowsDir, p);
        }
        if (!p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || p.EndsWith("svchost.exe", StringComparison.OrdinalIgnoreCase)) return null;
        return p;
    }

    /// <summary>True if the file exists, or if its drive isn't there right now (an unplugged drive isn't a broken service).</summary>
    private static bool ExistsOrUncheckable(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal) || !Directory.Exists(root)) return true;
            return File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return true;
        }
    }

    // ------------------------------------------------------------------ firewall

    private static readonly HashSet<string> UpdateServices = new(StringComparer.OrdinalIgnoreCase)
        { "wuauserv", "UsoSvc", "DoSvc", "BITS", "WaaSMedicSvc", "InstallService", "ClipSVC", "wlidsvc", "LicenseManager" };
    private static readonly string[] UpdateApps = { @"\wuauclt.exe", @"\MoUsoCoreWorker.exe", @"\usoclient.exe", "WinStore.App.exe", @"\WindowsUpdateBox.exe" };

    private CustomFinding? FirewallBlocks()
    {
        const string key = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules";
        var evidence = new List<string>();
        foreach (var name in _env.Registry.ValueNames(RegistryHive.LocalMachine, key))
        {
            if (_env.Registry.Read(RegistryHive.LocalMachine, key, name) is not { Kind: RegistryKind.String } rule) continue;
            var fields = rule.Data.Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Select(f => f.Split('=', 2)).Where(f => f.Length == 2)
                .GroupBy(f => f[0], StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First()[1], StringComparer.OrdinalIgnoreCase);
            if (!Is(fields, "Action", "Block") || !Is(fields, "Active", "TRUE")) continue;
            var svc = fields.GetValueOrDefault("Svc");
            var app = fields.GetValueOrDefault("App") ?? "";
            var hitsService = svc is not null && UpdateServices.Contains(svc);
            var hitsApp = UpdateApps.Any(a => app.EndsWith(a, StringComparison.OrdinalIgnoreCase) || app.Contains(a.TrimStart('\\'), StringComparison.OrdinalIgnoreCase));
            if (!hitsService && !hitsApp) continue;
            var label = fields.GetValueOrDefault("Name") is { } n && !n.StartsWith('@') ? n : name;
            evidence.Add($"Rule '{label}' blocks {(hitsService ? "the " + svc + " service" : Path.GetFileName(app))}");
            if (evidence.Count >= MaxListed) break;
        }
        return evidence.Count > 0 ? CustomFinding.Lines(evidence) : null;
    }

    private static bool Is(Dictionary<string, string> fields, string name, string value)
        => fields.TryGetValue(name, out var v) && v.Equals(value, StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ Windows Update server

    private CustomFinding? FakeUpdateServer()
    {
        const string wu = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
        if (_env.Registry.Read(RegistryHive.LocalMachine, wu + @"\AU", "UseWUServer") is not { Kind: RegistryKind.DWord } use
            || !RegData.TryNumber(use.Data, out var on) || on != 1) return null;
        var server = _env.Registry.Read(RegistryHive.LocalMachine, wu, "WUServer")?.Data?.Trim() ?? "";
        var blank = server.Length == 0;
        var local = !blank && Uri.TryCreate(server.Contains("://") ? server : "http://" + server, UriKind.Absolute, out var uri)
                    && (uri.IsLoopback || uri.Host is "0.0.0.0" || uri.Host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase));
        if (!blank && !local) return null;
        return CustomFinding.Lines(new[] { blank ? "UseWUServer = 1 with no update server set" : $"UseWUServer = 1 pointing at this PC ({server})" });
    }

    // ------------------------------------------------------------------ network adapters

    private CustomFinding? Nagle()
    {
        const string root = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
        var evidence = new List<string>();
        foreach (var adapter in _env.Registry.SubKeyNames(RegistryHive.LocalMachine, root))
        {
            var key = root + "\\" + adapter;
            var names = _env.Registry.ValueNames(RegistryHive.LocalMachine, key);
            var hits = new[] { "TcpAckFrequency", "TCPNoDelay", "TcpDelAckTicks" }.Where(v => names.Contains(v, StringComparer.OrdinalIgnoreCase)).ToList();
            if (hits.Count > 0) evidence.Add($@"HKLM\{key}: {string.Join(", ", hits)}");
            if (evidence.Count >= MaxListed) break;
        }
        return evidence.Count > 0 ? CustomFinding.Lines(evidence) : null;
    }

    // ------------------------------------------------------------------ priorities

    private CustomFinding? RealtimePriority()
    {
        const string ifeo = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
        var evidence = new List<string>();
        foreach (var exe in _env.Registry.SubKeyNames(RegistryHive.LocalMachine, ifeo))
        {
            // CpuPriorityClass 4 = Realtime (1 Idle, 2 Normal, 3 High, 5 Below normal, 6 Above normal).
            if (_env.Registry.Read(RegistryHive.LocalMachine, $@"{ifeo}\{exe}\PerfOptions", "CpuPriorityClass") is { Kind: RegistryKind.DWord } v
                && RegData.TryNumber(v.Data, out var cls) && cls == 4)
                evidence.Add($"{exe}: PerfOptions → CpuPriorityClass = 4 (Realtime)");
            if (evidence.Count >= MaxListed) break;
        }
        return evidence.Count > 0 ? CustomFinding.Lines(evidence) : null;
    }

    // ------------------------------------------------------------------ games

    private CustomFinding? Roblox()
    {
        var versions = Path.Combine(_env.LocalAppData, "Roblox", "Versions");
        var files = new List<FileInfo>();
        try
        {
            if (!Directory.Exists(versions)) return null;
            foreach (var dir in new DirectoryInfo(versions).EnumerateDirectories())
            {
                if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) continue; // never follow junctions
                var f = new FileInfo(Path.Combine(dir.FullName, "ClientSettings", "ClientAppSettings.json"));
                if (f.Exists && (f.Attributes & FileAttributes.ReparsePoint) == 0) files.Add(f);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return files.Count == 0 ? null : new CustomFinding(files.Select(f => f.FullName).ToList(), Array.Empty<string>(), files);
    }

    // ------------------------------------------------------------------ policies

    private CustomFinding? EdgePolicies()
    {
        var evidence = new List<string>();
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            var key = hive == RegistryHive.LocalMachine ? BadTweakCatalog.EdgePolicyKey : @"Software\Policies\Microsoft\Edge";
            var names = _env.Registry.ValueNames(hive, key).Where(n => !_coveredValues.Contains(TweakRegistryAllowances.KeyOf(hive, key, n))).ToList();
            if (names.Count > 0)
                evidence.Add($@"{RegistryNames.Short(hive)}\{key}: {string.Join(", ", names.Take(10))}{(names.Count > 10 ? $" and {names.Count - 10} more" : "")}");
        }
        return evidence.Count > 0 ? CustomFinding.Lines(evidence) : null;
    }

    private CustomFinding? OtherPolicies()
    {
        var evidence = new List<string>();
        foreach (var (hive, key) in BadTweakCatalog.WatchedPolicyKeys)
        {
            foreach (var name in _env.Registry.ValueNames(hive, key))
            {
                if (_coveredValues.Contains(TweakRegistryAllowances.KeyOf(hive, key, name))) continue;
                var v = _env.Registry.Read(hive, key, name);
                evidence.Add($@"{RegistryNames.Short(hive)}\{key} → {name} = {(v is null ? "?" : RegData.Show(v.Kind, v.Data))}");
                if (evidence.Count >= MaxListed) return CustomFinding.Lines(evidence);
            }
        }
        return evidence.Count > 0 ? CustomFinding.Lines(evidence) : null;
    }
}
