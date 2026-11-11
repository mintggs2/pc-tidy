using System.Windows;
using System.Windows.Threading;
using PcTidy.Engine;
using PcTidy.Features.Services.Sources;
using PcTidy.Hosting;
using PcTidy.Platform;
using PcTidy.Safety;
// The platform has a ServiceRunState of its own (v3.1); these fakes mean the scanner's.
using ServiceRunState = PcTidy.Features.Services.Sources.ServiceRunState;

namespace PcTidy.Features.Services.Tests;

/// <summary>A per-test folder under testbed\services (deleted afterwards).</summary>
public sealed class TestFolder : IDisposable
{
    public static readonly string TestbedRoot = FindTestbed();

    public TestFolder(string? name = null)
    {
        Path = System.IO.Path.Combine(TestbedRoot, (name ?? "t") + "-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    private static string FindTestbed()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(System.IO.Path.Combine(d.FullName, "PcTidy.sln"))) d = d.Parent;
        if (d is null) throw new InvalidOperationException("PcTidy.sln not found above the test folder.");
        var root = System.IO.Path.Combine(d.FullName, "testbed", "services");
        Directory.CreateDirectory(root);
        return root;
    }
}

/// <summary>
/// A fake PC's services, shared by the scanner (IServiceSource) and the Platform handler (IServiceControl), so a scan,
/// a run and an undo all see the same state. Behaves like the Service Control Manager: the delayed flag only changes
/// for automatic services. Never touches anything real.
/// </summary>
public sealed class FakeServiceWorld : IServiceSource, IServiceControl, IProcessUsageSource
{
    public sealed record State(int Start, bool Delayed, int Type, ServiceRecoveryConfig Recovery);

    private readonly Dictionary<string, ServiceRecord> _records = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, State> States { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<FileFacts> Files { get; } = new();
    public Dictionary<int, ProcessUsage> Usage { get; } = new();
    public int RecoveryWrites { get; private set; }
    public List<string> StartWrites { get; } = new();

    public static readonly ServiceRecoveryConfig DefaultRecovery = new(86400, null, null,
        new[] { new ServiceRecoveryAction(1, 60000), new ServiceRecoveryAction(1, 60000), new ServiceRecoveryAction(0, 0) }, false);

    public FakeServiceWorld Add(ServiceRecord r, FileFacts? facts = null, ServiceRecoveryConfig? recovery = null)
    {
        _records[r.Name] = r;
        States[r.Name] = new State(r.Start, r.DelayedAutostart, r.ServiceType, recovery ?? DefaultRecovery);
        if (facts is not null) Files.Add(facts);
        return this;
    }

    public ServicesEnvironment Environment() => new() { Services = this, Files = new FixedFileFacts(Files), Usage = this };

    // ---- IServiceSource (what the scanner reads)
    public IReadOnlyList<ServiceRecord> ReadAll(ICollection<string> problems, CancellationToken ct)
        => _records.Values.Select(r => r with { Start = States[r.Name].Start, DelayedAutostart = States[r.Name].Delayed }).ToList();

    public IReadOnlyDictionary<int, ProcessUsage> Read(IReadOnlyCollection<int> pids)
        => pids.Where(Usage.ContainsKey).Distinct().ToDictionary(p => p, p => Usage[p]);

    // ---- IServiceControl (what the Platform handler changes)
    public bool TouchesRealSystem => false;

    public ServiceStartConfig? Query(string serviceName)
        => States.TryGetValue(serviceName, out var s) ? new ServiceStartConfig(s.Start, s.Delayed, s.Type) : null;

    public void SetStart(string serviceName, int start, bool delayedAutostart)
    {
        StartWrites.Add($"{serviceName}={start}");
        var s = States[serviceName];
        States[serviceName] = s with { Start = start, Delayed = start == 2 ? delayedAutostart : s.Delayed };
    }

    public ServiceRecoveryConfig? QueryRecovery(string serviceName) => States.TryGetValue(serviceName, out var s) ? s.Recovery : null;

    public void SetRecovery(string serviceName, ServiceRecoveryConfig recovery)
    {
        RecoveryWrites++;
        States[serviceName] = States[serviceName] with { Recovery = recovery };
    }

    /// <summary>A comparable copy of every service's start, delayed flag and recovery settings.</summary>
    public IReadOnlyDictionary<string, string> Snapshot()
        => States.ToDictionary(kv => kv.Key, kv => $"{kv.Value.Start}|{kv.Value.Delayed}|{kv.Value.Type}|{kv.Value.Recovery.Serialize()}", StringComparer.OrdinalIgnoreCase);
}

/// <summary>Builders for fake service records.</summary>
public static class Svc
{
    public const string Windows = @"C:\Windows";

    public static ServiceRecord Own(string name, int start, string? exe = null, ServiceRunState state = ServiceRunState.Stopped, int pid = 0,
        bool delayed = false, string? display = null, params string[] dependsOn) => new()
    {
        Name = name, DisplayName = display ?? name, ServiceType = 0x10, Start = start, DelayedAutostart = delayed, State = state, ProcessId = pid,
        ImagePath = exe is null ? null : $"\"{exe}\"", DependsOn = dependsOn,
    };

    /// <summary>A svchost-hosted Windows service (its DLL is the file that's checked).</summary>
    public static ServiceRecord Shared(string name, int start, string dll, ServiceRunState state = ServiceRunState.Running, int pid = 1000, params string[] dependsOn) => new()
    {
        Name = name, DisplayName = name, ServiceType = 0x20, Start = start, State = state, ProcessId = state == ServiceRunState.Running ? pid : 0,
        ImagePath = @"%SystemRoot%\system32\svchost.exe -k netsvcs -p", ServiceDll = dll, DependsOn = dependsOn,
    };
}

/// <summary>Sandbox host whose service handler talks to a <see cref="FakeServiceWorld"/> (real guard, executor, journal, undo).</summary>
public sealed class FakeServicesHost : IDisposable
{
    public FakeServicesHost(string root, FakeServiceWorld world, bool expert = false) : this(root, _ => world, expert) { }

    /// <param name="world">Builds the fake services from the sandbox's own locations (paths inside its root).</param>
    public FakeServicesHost(string root, Func<IKnownLocations, FakeServiceWorld> world, bool expert = false)
    {
        Sandbox = PcTidyHost.CreateSandbox(root, SandboxProfile.Minimal, s => s.ExpertMode = expert);
        var locations = Sandbox.Locations;
        World = world(locations);
        var platform = PlatformHandlers.CreateAll().Where(h => h is not ServiceStartTypeHandler).Prepend(new ServiceStartTypeHandler(World)).ToList();
        var handlers = EngineFactory.CreateFileHandlers().Concat(platform).ToList();
        IQuarantineStore? quarantine = null;
        var inProcess = new ElevatedBatchExecutor(handlers, () => new HandlerContext
        {
            RunId = "", Locations = locations, Safety = Sandbox.Safety, InUse = Sandbox.InUse, Quarantine = quarantine!,
            Settings = Sandbox.Settings, Mode = ExecutionMode.Real, IsElevated = true,
        });
        Elevation = new ElevationService(locations, inProcess);
        Engine = EngineFactory.Create(locations, Sandbox.Safety, Sandbox.InUse, Sandbox.Settings, platform, new RestorePointService(locations), Elevation);
        quarantine = Engine.Quarantine;
    }

    public PcTidyHost Sandbox { get; }
    public FakeServiceWorld World { get; }
    public EngineServices Engine { get; }
    private IElevationService Elevation { get; }
    public ISafetyGuard Safety => Sandbox.Safety;
    public ScanContext Context(string? preset = null) => Sandbox.CreateScanContext(ScanDepth.Quick, preset: preset);

    public HandlerContext HandlerContext(ExecutionMode mode = ExecutionMode.Real) => new()
    {
        RunId = "t", Locations = Sandbox.Locations, Safety = Sandbox.Safety, InUse = Sandbox.InUse, Quarantine = Engine.Quarantine,
        Settings = Sandbox.Settings, Mode = mode, IsElevated = true,
    };

    public void Dispose()
    {
        (Elevation as IDisposable)?.Dispose();
        Sandbox.Dispose();
    }
}

/// <summary>Records what a page hands to the shell.</summary>
public sealed class FakeFlowHost : IFlowHost
{
    public List<ActionPlan> Reviewed { get; } = new();
    public List<ActionPlan> Simulated { get; } = new();
    public List<string> Messages { get; } = new();

    public Task<RunRecord?> ReviewAndRunAsync(ActionPlan plan, CancellationToken ct) { Reviewed.Add(plan); return Task.FromResult<RunRecord?>(null); }
    public Task<RunRecord?> SimulateAsync(ActionPlan plan, CancellationToken ct) { Simulated.Add(plan); return Task.FromResult<RunRecord?>(null); }
    public Task<RunRecord?> RunApprovedAsync(ActionPlan plan, CancellationToken ct) => Task.FromResult<RunRecord?>(null);
    public void OpenInExplorer(string path) { }
    public void ShowMessage(string title, string message, bool isError = false) => Messages.Add(title + ": " + message);
}

/// <summary>One long-lived STA thread with WPF-UI and PC Tidy theme resources for page tests.</summary>
public static class Sta
{
    private static readonly Lazy<Dispatcher> Ui = new(() =>
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Dark });
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/PcTidy.UI;component/Themes/TidyDark.xaml") });
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/PcTidy.UI;component/Themes/Tidy.xaml") });
            ready.Set();
            Dispatcher.Run();
        }) { IsBackground = true, Name = "services-tests-ui" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return dispatcher!;
    });

    public static T Run<T>(Func<T> work) => Ui.Value.Invoke(work);
    public static void Run(Action work) => Ui.Value.Invoke(work);

    /// <summary>Runs an async UI action and lets its continuations finish on the UI thread.</summary>
    public static void RunAsync(Func<Task> work, TimeSpan? timeout = null)
    {
        var task = Ui.Value.Invoke(work);
        var until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (!task.IsCompleted && DateTime.UtcNow < until)
        {
            Ui.Value.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(10);
        }
        task.GetAwaiter().GetResult();
    }
}
