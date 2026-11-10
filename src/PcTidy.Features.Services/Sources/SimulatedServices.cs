using System.Text.Json;

namespace PcTidy.Features.Services.Sources;

/// <summary>
/// The services of a practice PC (sandbox, demo, screenshots): read from <c>&lt;root&gt;\Sim\services.json</c> if it
/// exists, else a small built-in sample with made-up companies. Nothing real is read. The same object answers the
/// file and process questions for those services, so the scanner sees one consistent fake PC.
/// </summary>
public sealed class SimulatedServices : IServiceSource, IProcessUsageSource
{
    /// <summary>Shape of services.json (all fields optional except the service name).</summary>
    public sealed record SimFile(List<SimService>? Services, List<SimBinary>? Files, List<SimProcess>? Processes);

    public sealed record SimService(
        string Name, string? DisplayName, string? Description, int? Type, int? Start, bool? Delayed, bool? Trigger,
        string? State, int? Pid, string? ImagePath, string? ServiceDll, string? Account, List<string>? DependsOn);

    /// <param name="Signature">Verified, Unsigned, NotTrusted (default Verified when a signer is given, else Unsigned).</param>
    public sealed record SimBinary(string Path, string? Company, string? Signer, string? Signature, string? Description);

    public sealed record SimProcess(int Pid, long PrivateBytes, double? CpuSeconds);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private readonly IReadOnlyList<ServiceRecord> _services;
    private readonly Dictionary<int, ProcessUsage> _usage;

    public SimulatedServices(IReadOnlyList<ServiceRecord> services, IEnumerable<FileFacts> files, IEnumerable<ProcessUsage> usage, string? note = null)
    {
        _services = services;
        Files = new FixedFileFacts(files);
        _usage = usage.GroupBy(u => u.Pid).ToDictionary(g => g.Key, g => g.Last());
        Note = note;
    }

    /// <summary>File facts of the simulated binaries (unlisted paths count as missing).</summary>
    public IFileFactsProvider Files { get; }

    /// <summary>Shown in the scan notes so nobody mistakes practice data for their PC.</summary>
    public string? Note { get; }

    public IReadOnlyList<ServiceRecord> ReadAll(ICollection<string> problems, CancellationToken ct)
    {
        if (Note is not null) problems.Add(Note);
        return _services;
    }

    public IReadOnlyDictionary<int, ProcessUsage> Read(IReadOnlyCollection<int> pids)
        => pids.Where(_usage.ContainsKey).Distinct().ToDictionary(p => p, p => _usage[p]);

    /// <summary>The practice PC's services: its services.json, or the built-in sample.</summary>
    public static SimulatedServices ForSandbox(IKnownLocations locations, string? simFolder)
    {
        var file = simFolder is null ? null : Path.Combine(simFolder, "services.json");
        if (file is not null && File.Exists(file))
        {
            try
            {
                var sim = JsonSerializer.Deserialize<SimFile>(File.ReadAllText(file), Json);
                if (sim is not null) return FromFile(sim, locations);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return Sample(locations, "This practice PC's services.json couldn't be read, so sample services are shown.");
            }
        }
        return Sample(locations, "Practice PC: these are sample services, not the ones on your PC.");
    }

    public static SimulatedServices FromFile(SimFile sim, IKnownLocations locations)
    {
        var services = (sim.Services ?? new()).Where(s => !string.IsNullOrWhiteSpace(s.Name)).Select(s => new ServiceRecord
        {
            Name = s.Name.Trim(),
            DisplayName = s.DisplayName ?? s.Name,
            Description = s.Description,
            ServiceType = s.Type ?? 0x10,
            Start = s.Start ?? 3,
            DelayedAutostart = s.Delayed ?? false,
            TriggerStart = s.Trigger ?? false,
            State = Enum.TryParse<ServiceRunState>(s.State, true, out var st) ? st : ServiceRunState.Stopped,
            ProcessId = s.Pid ?? 0,
            ImagePath = s.ImagePath,
            ServiceDll = s.ServiceDll,
            Account = s.Account,
            DependsOn = s.DependsOn ?? new List<string>(),
        }).ToList();
        var files = (sim.Files ?? new()).Where(f => !string.IsNullOrWhiteSpace(f.Path)).Select(f =>
        {
            var path = ServiceBinary.Normalize(f.Path, locations, realPc: false);
            var signature = Enum.TryParse<SignatureState>(f.Signature, true, out var sig) ? sig : f.Signer is null ? SignatureState.Unsigned : SignatureState.Verified;
            return new FileFacts(path, true, f.Company, f.Description, signature, f.Signer);
        });
        var usage = (sim.Processes ?? new()).Select(p => new ProcessUsage(p.Pid, p.PrivateBytes, p.CpuSeconds is { } c ? TimeSpan.FromSeconds(c) : null));
        return new SimulatedServices(services, files, usage);
    }

    /// <summary>
    /// A small, anonymous sample: Windows services sharing one host, the catalog's seeded entries, a few third-party
    /// services (one unsigned in a temp folder, one left behind by an uninstalled app) and a driver.
    /// </summary>
    public static SimulatedServices Sample(IKnownLocations l, string? note)
    {
        var sys32 = Path.Combine(l.WindowsDir, "System32");
        var svchost = Path.Combine(sys32, "svchost.exe");
        var contoso = Path.Combine(l.ProgramFiles, "Contoso", "Sync", "ContosoSyncService.exe");
        var fabrikam = Path.Combine(l.ProgramFilesX86, "Fabrikam", "Updater", "FabrikamUpdate.exe");
        var oldApp = Path.Combine(l.ProgramFiles, "Northwind", "Agent", "NorthwindAgent.exe");
        var tempTool = Path.Combine(l.UserTemp, "helper", "svcworker.exe");
        var printer = Path.Combine(sys32, "spoolsv.exe");
        ServiceRecord Shared(string name, string display, string dll, int start, ServiceRunState state, bool delayed = false, params string[] deps) => new()
        {
            Name = name, DisplayName = display, ServiceType = 0x20, Start = start, DelayedAutostart = delayed, State = state,
            ProcessId = state == ServiceRunState.Running ? 1020 : 0, ImagePath = @"%SystemRoot%\system32\svchost.exe -k netsvcs -p",
            ServiceDll = Path.Combine(sys32, dll), Account = "LocalSystem", DependsOn = deps,
        };
        var services = new List<ServiceRecord>
        {
            Shared("RpcSs", "Remote Procedure Call (RPC)", "rpcss.dll", 2, ServiceRunState.Running),
            Shared("DiagTrack", "Connected User Experiences and Telemetry", "diagtrack.dll", 2, ServiceRunState.Running, false, "RpcSs"),
            Shared("Schedule", "Task Scheduler", "schedsvc.dll", 2, ServiceRunState.Running, false, "RpcSs"),
            Shared("BITS", "Background Intelligent Transfer Service", "qmgr.dll", 4, ServiceRunState.Stopped, false, "RpcSs"),
            new() { Name = "Spooler", DisplayName = "Print Spooler", ServiceType = 0x10, Start = 2, State = ServiceRunState.Running, ProcessId = 2210,
                ImagePath = @"%SystemRoot%\System32\spoolsv.exe", Account = "LocalSystem", DependsOn = new[] { "RpcSs" },
                Description = "Queues print jobs and talks to your printers." },
            new() { Name = "ContosoSync", DisplayName = "Contoso Sync Service", ServiceType = 0x10, Start = 2, DelayedAutostart = true,
                State = ServiceRunState.Running, ProcessId = 3300, ImagePath = $"\"{contoso}\" --service", Account = "LocalSystem",
                Description = "Keeps Contoso folders in sync." },
            new() { Name = "FabrikamUpdate", DisplayName = "Fabrikam Updater", ServiceType = 0x10, Start = 2, State = ServiceRunState.Stopped,
                ImagePath = $"\"{fabrikam}\" /svc", Account = "LocalSystem", Description = "Checks for Fabrikam app updates." },
            new() { Name = "NorthwindAgent", DisplayName = "Northwind Agent", ServiceType = 0x10, Start = 3, State = ServiceRunState.Stopped,
                ImagePath = $"\"{oldApp}\"", Account = "LocalSystem" },
            new() { Name = "SvcWorker", DisplayName = "Service Worker", ServiceType = 0x10, Start = 2, State = ServiceRunState.Running,
                ProcessId = 4400, ImagePath = tempTool, Account = "LocalSystem" },
            new() { Name = "contosodrv", DisplayName = "Contoso Filter Driver", ServiceType = 0x1, Start = 1, State = ServiceRunState.Running,
                ImagePath = @"\SystemRoot\System32\drivers\contosodrv.sys" },
        };
        var files = new[]
        {
            FixedFileFacts.Microsoft(svchost, "Host Process for Windows Services"),
            FixedFileFacts.Microsoft(Path.Combine(sys32, "rpcss.dll")),
            FixedFileFacts.Microsoft(Path.Combine(sys32, "diagtrack.dll")),
            FixedFileFacts.Microsoft(Path.Combine(sys32, "schedsvc.dll")),
            FixedFileFacts.Microsoft(Path.Combine(sys32, "qmgr.dll")),
            FixedFileFacts.Microsoft(printer),
            FixedFileFacts.Signed(contoso, "Contoso Ltd."),
            FixedFileFacts.Signed(fabrikam, "Fabrikam, Inc."),
            FixedFileFacts.Unsigned(tempTool),
            FixedFileFacts.Signed(Path.Combine(sys32, "drivers", "contosodrv.sys"), "Contoso Ltd."),
        };
        var usage = new[]
        {
            new ProcessUsage(1020, 38L << 20, TimeSpan.FromSeconds(95)),
            new ProcessUsage(2210, 9L << 20, TimeSpan.FromSeconds(2)),
            new ProcessUsage(3300, 61L << 20, TimeSpan.FromSeconds(140)),
            new ProcessUsage(4400, 22L << 20, TimeSpan.FromSeconds(31)),
        };
        return new SimulatedServices(services, files, usage, note);
    }
}
