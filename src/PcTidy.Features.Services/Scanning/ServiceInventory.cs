using PcTidy.Features.Services.Model;
using PcTidy.Features.Services.Rules;
using PcTidy.Features.Services.Sources;

namespace PcTidy.Features.Services.Scanning;

/// <summary>Everything one scan found: services and drivers (each with its verdict), plus plain-English notes.</summary>
public sealed record ServiceInventoryResult(IReadOnlyList<ServiceInfo> Services, IReadOnlyList<ServiceInfo> Drivers, IReadOnlyList<string> Notes)
{
    public static readonly ServiceInventoryResult Empty = new(Array.Empty<ServiceInfo>(), Array.Empty<ServiceInfo>(), Array.Empty<string>());

    public IEnumerable<ServiceInfo> All => Services.Concat(Drivers);
}

/// <summary>
/// Reads every service and driver (read-only, off the UI thread, cancellable), then works out for each one: the
/// file that is the service and who signed it, the catalog entry, memory/CPU of its process (and how many services
/// share it), dependencies and dependents, flags, and the "safe to change?" verdict.
/// </summary>
public static class ServiceInventory
{
    /// <param name="expertAllowed">Expert mode is on and this isn't an Optimize-all run (unlocks Situational entries).</param>
    public static Task<ServiceInventoryResult> ReadAsync(ServicesEnvironment env, IKnownLocations locations, bool expertAllowed,
        IProgress<ScanProgress>? progress, CancellationToken ct)
        => Task.Run(() => Read(env, locations, expertAllowed, progress, ct), ct);

    public static ServiceInventoryResult Read(ServicesEnvironment env, IKnownLocations locations, bool expertAllowed,
        IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new ScanProgress("Listing services…", 0));
        var problems = new List<string>();
        IReadOnlyList<ServiceRecord> records;
        try { records = env.Services.ReadAll(problems, ct); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new ServiceInventoryResult(Array.Empty<ServiceInfo>(), Array.Empty<ServiceInfo>(), new[] { "Windows didn't allow listing services, so nothing is shown." });
        }

        // Dependents: who lists this service in its dependencies (load-order groups "+Name" are skipped).
        var dependents = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in records)
            foreach (var d in r.DependsOn.Where(d => !d.StartsWith('+')))
                (dependents.TryGetValue(d, out var list) ? list : dependents[d] = new List<string>()).Add(r.Name);

        // Usage: one read per hosting process; shared svchost groups say how many services live there.
        var running = records.Where(r => r.ProcessId > 4 && r.State == ServiceRunState.Running).ToList();
        var perProcess = running.GroupBy(r => r.ProcessId).ToDictionary(g => g.Key, g => g.Count());
        IReadOnlyDictionary<int, ProcessUsage> usage;
        try { usage = env.Usage.Read(perProcess.Keys.ToList()); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            usage = new Dictionary<int, ProcessUsage>();
        }

        var services = new List<ServiceInfo>();
        var drivers = new List<ServiceInfo>();
        for (var i = 0; i < records.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var r = records[i];
            if (i % 25 == 0) progress?.Report(new ScanProgress($"Checking {r.DisplayName}", 0.1 + 0.9 * i / Math.Max(1, records.Count), i));
            try
            {
                var info = Build(r, env, locations, expertAllowed,
                    dependents.TryGetValue(r.Name, out var deps) ? deps.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList() : new List<string>(),
                    r.ProcessId > 4 && usage.TryGetValue(r.ProcessId, out var u) ? new ServiceUsage(u.PrivateBytes, u.CpuTime, perProcess.GetValueOrDefault(r.ProcessId, 1) - 1) : null);
                (info.IsDriver ? drivers : services).Add(info);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or NotSupportedException)
            {
                problems.Add($"Couldn't check '{r.Name}': {ex.Message}");
            }
        }
        progress?.Report(new ScanProgress("Done", 1, records.Count));
        return new ServiceInventoryResult(
            services.OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList(),
            drivers.OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList(),
            problems.Distinct().Take(12).ToList());
    }

    /// <summary>
    /// The catalog entry for a service. A per-user template key ("CDPUserSvc") uses the template entry
    /// ("CDPUserSvc_*"); its per-user copies ("CDPUserSvc_1a2b3c") match that entry directly.
    /// </summary>
    public static ServiceEntry? CatalogEntry(ServiceRecord r)
        => ServiceCatalog.Find(r.Name) ?? (ServicePolicy.IsTemplate(r) ? ServiceCatalog.Find(r.Name + "_") : null);

    public static ServiceInfo Build(ServiceRecord r, ServicesEnvironment env, IKnownLocations locations, bool expertAllowed,
        IReadOnlyList<string> dependents, ServiceUsage? usage)
    {
        var binary = ServiceBinary.Of(r, locations, env.IsRealPc);
        var facts = env.Files.Get(binary);
        var entry = CatalogEntry(r);
        var underWindows = binary is not null && PathUtil.IsUnder(binary, locations.WindowsDir);
        var flags = Flags(r, binary, facts, underWindows, locations);
        var assessment = ServicePolicy.Assess(new PolicyInput(r, facts, entry, flags, dependents, underWindows && !facts.IsUnsigned), expertAllowed);
        return new ServiceInfo
        {
            Record = r, BinaryPath = binary, Facts = facts, Entry = entry, Usage = usage, Flags = flags,
            Dependents = dependents, Assessment = assessment,
        };
    }

    /// <summary>Flags (see <see cref="ServiceFlags"/>). "Leftover" needs a known path whose drive is there but whose file isn't.</summary>
    public static ServiceFlags Flags(ServiceRecord r, string? binary, FileFacts facts, bool underWindows, IKnownLocations locations)
    {
        var flags = ServiceFlags.None;
        var microsoftish = facts.IsMicrosoft || (underWindows && facts.Signature == SignatureState.NotChecked);
        if (!microsoftish) flags |= ServiceFlags.NonMicrosoft;
        if (facts.Exists && facts.IsUnsigned) flags |= ServiceFlags.Unsigned;
        if (binary is not null && !facts.Exists && facts.Signature == SignatureState.FileMissing && DriveIsThere(binary)) flags |= ServiceFlags.Leftover;
        if (binary is not null && IsSuspiciousFolder(binary, locations)) flags |= ServiceFlags.SuspiciousPath;
        if (!microsoftish && !r.IsDriver && r.StartType is ServiceStart.Automatic or ServiceStart.AutomaticDelayed && !r.TriggerStart
            && r.State == ServiceRunState.Stopped)
            flags |= ServiceFlags.AutoStartNotRunning;
        return flags;
    }

    /// <summary>Temp folders, AppData (any user's) and Downloads: real services are installed under Program Files or Windows.</summary>
    public static bool IsSuspiciousFolder(string path, IKnownLocations l)
    {
        foreach (var folder in new[] { l.UserTemp, l.WindowsTemp, l.LocalAppData, l.RoamingAppData, l.Downloads })
            if (!string.IsNullOrWhiteSpace(folder) && PathUtil.IsUnder(path, folder)) return true;
        var p = path.Replace('/', '\\');
        return p.Contains(@"\AppData\", StringComparison.OrdinalIgnoreCase)
               || p.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase)
               || p.Contains(@"\Downloads\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An unplugged drive isn't a leftover.</summary>
    private static bool DriveIsThere(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) && Directory.Exists(root);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }
}
