using PcTidy.App.Composition.Demo;
using PcTidy.App.Shell;
using PcTidy.Core;
using PcTidy.Core.Model;

namespace PcTidy.App.Composition;

/// <summary>
/// The app's service container and THE composition seam.
///
/// LEAD: wire real services in <see cref="Register"/> (the only method you need to edit). Two styles, mix freely:
///   (a) hand over any IServiceProvider, e.g. PcTidy.Hosting:
///         var host = PcTidyHost.CreateReal();
///         services.UseProvider(host);                     // every service the shell doesn't own comes from here
///         services.AddModules(new IFeatureModule[] { new CleanupModule(), new OrganizeModule(), ... });
///       (modules the provider returns for IEnumerable&lt;IFeatureModule&gt; are picked up automatically)
///   (b) or register one by one:
///         services.Add&lt;IExecutor&gt;(new Executor(...));   services.Add&lt;ISafetyGuard&gt;(sp =&gt; new SafetyGuard(...));
///   then: services.CurrentRelease = Release.V1;  AppEntry.ElevatedHandler = ...;  AppEntry.ScheduledScanHandler = ...;
///         PcTidy.App.Pages.HelpContent.WhatWeDontClean = CleanupModule.WhatWeDontCleanText;
///
/// The shell itself provides IFlowHost / IShellFlowHost, ISettingsStore and IServiceProvider (this object), and
/// AppSettings if the provider doesn't (loaded from AppSettings.ResolveDataRoot()).
/// If any of <see cref="RequiredForRealMode"/> is missing after Register, the app starts in demo mode and says so.
/// </summary>
public sealed class AppServices : IServiceProvider
{
    private readonly Dictionary<Type, object> _instances = new();
    private readonly Dictionary<Type, Func<IServiceProvider, object>> _factories = new();
    private readonly List<IFeatureModule> _modules = new();
    private readonly object _gate = new();
    private IServiceProvider? _provider;

    private AppServices(AppArgs args, bool isDemo, string? demoReason)
    {
        Args = args;
        IsDemo = isDemo;
        DemoReason = demoReason;
    }

    public AppArgs Args { get; }
    /// <summary>True when running on in-memory fakes (the title bar says so).</summary>
    public bool IsDemo { get; }
    /// <summary>Why demo mode is on ("--demo", or that the real composition is incomplete).</summary>
    public string? DemoReason { get; }
    /// <summary>Sections with <see cref="IFeatureModule.Release"/> above this are hidden.</summary>
    public Release CurrentRelease { get; set; } = Release.V1;

    /// <summary>Services the real app can't run without.</summary>
    public static readonly IReadOnlyList<Type> RequiredForRealMode = new[]
    {
        typeof(IKnownLocations), typeof(ISafetyGuard), typeof(IInUseChecker),
        typeof(IRunJournal), typeof(IQuarantineStore), typeof(IExecutor), typeof(IUndoEngine),
    };

    // ===================================================================================
    //  THE SEAM. Real services and feature modules (filled in by the lead).
    // ===================================================================================
    public static void Register(AppServices services)
    {
        // "--sandbox <folder>": the full app, but every path points into a practice folder (e.g. a generated testbed).
        // Nothing outside that folder can be touched; Windows tools only simulate. Used for UI review and practice.
        var sandboxRoot = services.Args.SandboxRoot;
        var host = sandboxRoot is not null
            ? PcTidy.Hosting.PcTidyHost.CreateSandbox(sandboxRoot)
            : PcTidy.Hosting.PcTidyHost.CreateReal();
        services.UseProvider(host);
        services.Add(sandboxRoot is not null ? SandboxSettings.Restore(host.Settings) : host.Settings);
        var modules = FeatureCatalog.All();
        services.AddModules(modules);
        services.CurrentRelease = FeatureCatalog.CurrentRelease;
        PcTidy.App.Pages.HelpContent.WhatWeDontClean = PcTidy.Features.Cleanup.CleanupModule.WhatWeDontCleanText;
        AppEntry.ElevatedHandler = (args, _, _) => Task.FromResult(host.RunElevatedHelper(args.Raw.ToArray()));
        foreach (var organize in modules.OfType<PcTidy.Features.Organize.OrganizeModule>())
        {
            Started.Add(sp => organize.StartWatchIfEnabled(sp));
            Stopping.Add(_ => organize.StopWatchAsync());
        }
        foreach (var boost in modules.OfType<PcTidy.Features.Boost.BoostModule>())
        {
            // Always restores a boost a crash left in effect, then watches only if the user switched Game boost on.
            Started.Add(sp => boost.Start(sp));
            Stopping.Add(_ => boost.StopAsync());
        }
        // Shell additions (v3): Schedule's report-only scan, Learning's advisor and its background chores.
        AppEntry.ScheduledScanHandler = ScheduledScanBridge.RunAsync;
        services.Add<PcTidy.Features.Learning.Advisor.ILearningAdvisor>(sp => PcTidy.Features.Learning.Advisor.LearningAdvisor.Create(sp));
        var learning = new LearningChores(services);
        Started.Add(_ => learning.Start());
        Stopping.Add(_ => learning.DisposeAsync().AsTask());
        Stopping.Add(_ => { host.Dispose(); return Task.CompletedTask; });
    }

    /// <summary>Run once the main window is shown (real mode only), e.g. Organize's watch mode.</summary>
    public static readonly List<Action<IServiceProvider>> Started = new();
    /// <summary>Run when the app exits (real mode only).</summary>
    public static readonly List<Func<IServiceProvider, Task>> Stopping = new();

    /// <summary>Design-time / demo composition: in-memory fakes and sample data. Changes nothing on the PC.</summary>
    public static void RegisterDemo(AppServices services, AppSettings settings) => DemoComposition.Register(services, settings);

    // ===================================================================================

    /// <summary>Builds the container: real composition unless --demo, falling back to demo if it is incomplete.</summary>
    public static AppServices Build(AppArgs args)
    {
        if (!args.Demo)
        {
            var real = new AppServices(args, isDemo: false, demoReason: null);
            Register(real);
            if (!real.Has(typeof(AppSettings))) real.Add(AppSettings.Load(AppSettings.ResolveDataRoot()));
            if (!real.Has(typeof(ISettingsStore))) real.Add<ISettingsStore>(new FileSettingsStore());
            if (RequiredForRealMode.All(real.Has)) return real.Finish(args);
            return BuildDemo(args, "This build isn't connected to the cleaning engine yet, so it shows sample data and can't change anything.");
        }
        return BuildDemo(args, "Demo mode: sample data only. Nothing on this PC can be changed.");
    }

    private static AppServices BuildDemo(AppArgs args, string reason)
    {
        var demo = new AppServices(args, isDemo: true, demoReason: reason);
        var settings = DemoComposition.CreateSettings();
        demo.Add(settings);
        demo.Add<ISettingsStore>(new MemorySettingsStore());
        RegisterDemo(demo, settings);
        return demo.Finish(args);
    }

    private AppServices Finish(AppArgs args)
    {
        if (!Has(typeof(IFileWalker))) Add<IFileWalker>(new ManagedFileWalker());
        // Every mode: the session's scan results for "Ask PC Tidy" (memory only) and the flow pages' learning view
        // (does nothing when no learning advisor is registered, e.g. in demo mode).
        if (!Has(typeof(PcTidy.Features.Assistant.Engine.IScanResultsCache)))
            Add<PcTidy.Features.Assistant.Engine.IScanResultsCache>(new PcTidy.Features.Assistant.Engine.ScanResultsCache());
        if (!Has(typeof(PcTidy.UI.Flow.IFlowLearning))) Add<PcTidy.UI.Flow.IFlowLearning>(new LearningBridge(this));
        if (args.Release is >= 1 and <= 3) CurrentRelease = (Release)args.Release.Value;
        CrashHandler.DataRoot = IsDemo ? DemoComposition.CrashLogRoot : Get<AppSettings>().DataRoot;
        return this;
    }

    // ---------------- registration API ----------------

    /// <summary>
    /// Uses another container (e.g. PcTidyHost) for every service not registered here. Its feature modules
    /// (IEnumerable&lt;IFeatureModule&gt;) are added to the rail.
    /// </summary>
    public AppServices UseProvider(IServiceProvider provider)
    {
        _provider = provider;
        if (provider.GetService(typeof(IEnumerable<IFeatureModule>)) is IEnumerable<IFeatureModule> modules) AddModules(modules);
        return this;
    }

    public AppServices Add<T>(T instance) where T : class
    {
        lock (_gate) { _instances[typeof(T)] = instance; _factories.Remove(typeof(T)); }
        return this;
    }

    /// <summary>Lazy singleton: created on first request.</summary>
    public AppServices Add<T>(Func<IServiceProvider, T> factory) where T : class
    {
        lock (_gate) { _factories[typeof(T)] = sp => factory(sp); _instances.Remove(typeof(T)); }
        return this;
    }

    public AppServices AddModule(IFeatureModule module)
    {
        lock (_gate)
        {
            _modules.RemoveAll(m => m.Id == module.Id);
            _modules.Add(module);
        }
        return this;
    }

    public AppServices AddModules(IEnumerable<IFeatureModule> modules)
    {
        foreach (var m in modules) AddModule(m);
        return this;
    }

    public IReadOnlyList<IFeatureModule> Modules { get { lock (_gate) return _modules.ToList(); } }

    /// <summary>Modules of the current release (or earlier), in rail order.</summary>
    public IReadOnlyList<IFeatureModule> VisibleModules =>
        Modules.Where(m => m.Release <= CurrentRelease).OrderBy(m => m.Group).ThenBy(m => m.Order).ThenBy(m => m.Title, StringComparer.CurrentCultureIgnoreCase).ToList();

    public bool Has(Type t)
    {
        lock (_gate) if (_instances.ContainsKey(t) || _factories.ContainsKey(t)) return true;
        return ProviderService(t) != null;
    }

    public T Get<T>() where T : class => (T)(GetService(typeof(T)) ?? throw new InvalidOperationException($"Service {typeof(T).Name} not registered"));

    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(IServiceProvider) || serviceType == typeof(AppServices)) return this;
        if (serviceType == typeof(IEnumerable<IFeatureModule>) || serviceType == typeof(IReadOnlyList<IFeatureModule>)) return VisibleModules;
        Func<IServiceProvider, object>? factory;
        lock (_gate)
        {
            if (_instances.TryGetValue(serviceType, out var existing)) return existing;
            if (!_factories.TryGetValue(serviceType, out factory)) return ProviderService(serviceType);
        }
        var created = factory(this);
        lock (_gate)
        {
            if (_instances.TryGetValue(serviceType, out var raced)) return raced;
            _instances[serviceType] = created;
            _factories.Remove(serviceType);
        }
        return created;
    }

    private object? ProviderService(Type t)
    {
        try { return _provider?.GetService(t); }
        catch (InvalidOperationException) { return null; } // some containers throw for unknown types
    }
}
