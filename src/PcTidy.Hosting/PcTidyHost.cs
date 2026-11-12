using PcTidy.Core;
using PcTidy.Engine;
using PcTidy.Platform;
using PcTidy.Safety;

namespace PcTidy.Hosting;

/// <summary>
/// The composition root: builds every core service for the real PC or for a sandbox (testbed).
/// Used by the App, the Rescue tool, the elevated helper and the end-to-end tests, so all of them
/// run exactly the same safety checks and engine.
/// </summary>
public sealed class PcTidyHost : IServiceProvider, IDisposable
{
    public required IKnownLocations Locations { get; init; }
    public required AppSettings Settings { get; init; }
    public required ISafetyGuard Safety { get; init; }
    public required IInUseChecker InUse { get; init; }
    public required IFileWalker Walker { get; init; }
    public required ISystemInfo System { get; init; }
    public required IRunJournal Journal { get; init; }
    public required IQuarantineStore Quarantine { get; init; }
    public required IExecutor Executor { get; init; }
    public required IUndoEngine Undo { get; init; }
    public required IReadOnlyList<IActionHandler> Handlers { get; init; }
    public IElevationService? Elevation { get; init; }
    public IRestorePointService? RestorePoints { get; init; }
    public bool IsSandbox => Locations.IsSandbox;

    private readonly Dictionary<Type, object> _extra = new();
    private RemovalMeasure? _removal;

    /// <summary>Registers an extra service (IFlowHost, feature modules, ...).</summary>
    public PcTidyHost Add<T>(T service) where T : class { _extra[typeof(T)] = service; return this; }

    public object? GetService(Type t)
    {
        if (_extra.TryGetValue(t, out var s)) return s;
        if (t == typeof(IKnownLocations)) return Locations;
        if (t == typeof(AppSettings)) return Settings;
        if (t == typeof(ISafetyGuard)) return Safety;
        if (t == typeof(IInUseChecker)) return InUse;
        if (t == typeof(IFileWalker)) return Walker;
        if (t == typeof(ISystemInfo)) return System;
        if (t == typeof(IRunJournal)) return Journal;
        if (t == typeof(IQuarantineStore)) return Quarantine;
        if (t == typeof(IExecutor)) return Executor;
        if (t == typeof(IUndoEngine)) return Undo;
        if (t == typeof(IElevationService)) return Elevation;
        if (t == typeof(IRestorePointService)) return RestorePoints;
        if (t == typeof(IRemovalMeasure)) return _removal ??= new RemovalMeasure(Settings);
        if (t == typeof(PcTidyHost) || t == typeof(IServiceProvider)) return this;
        return null;
    }

    /// <summary>Context for scanners, built fresh per scan.</summary>
    public PcTidy.Core.Model.ScanContext CreateScanContext(PcTidy.Core.Model.ScanDepth depth, IReadOnlyList<string>? roots = null, string? preset = null)
        => new()
        {
            Locations = Locations, Safety = Safety, InUse = InUse, Settings = Settings, Walker = Walker,
            Depth = depth, Roots = roots, Preset = preset, System = System,
            RunningProcesses = SafeRunning(),
        };

    private IReadOnlySet<string> SafeRunning()
    {
        try { return System.RunningProcesses(); } catch { return new HashSet<string>(); }
    }

    /// <summary>The real PC. Settings come from the data folder (default %LOCALAPPDATA%\PC Tidy).</summary>
    public static PcTidyHost CreateReal(AppSettings? settings = null)
    {
        settings ??= AppSettings.Load(AppSettings.ResolveDataRoot());
        var locations = new WindowsKnownLocations(settings);
        var guard = new SafetyGuard(locations, settings);
        var inUse = new RestartManagerInUseChecker();
        var handlers = EngineFactory.CreateFileHandlers().Concat(PlatformHandlers.CreateAll()).ToList();
        var elevation = new ElevationService(locations);
        var restore = new AdminRestorePointService(elevation);
        var engine = EngineFactory.Create(locations, guard, inUse, settings, PlatformHandlers.CreateAll(), restore, elevation);
        return new PcTidyHost
        {
            Locations = locations, Settings = settings, Safety = guard, InUse = inUse,
            Walker = new ManagedFileWalker(), System = new WindowsSystemInfo(),
            Journal = engine.Journal, Quarantine = engine.Quarantine, Executor = engine.Executor, Undo = engine.Undo,
            Handlers = handlers, Elevation = elevation, RestorePoints = restore,
        };
    }

    /// <summary>
    /// A fake PC under <paramref name="root"/> (testbed). Admin steps run in-process (never UAC),
    /// system tools only simulate, restore points are faked. Data folder = &lt;root&gt;\Data.
    /// </summary>
    public static PcTidyHost CreateSandbox(string root, SandboxProfile? profile = null, Action<AppSettings>? configure = null)
    {
        var locations = new SandboxKnownLocations(root, profile ?? SandboxProfile.Default);
        Directory.CreateDirectory(locations.DataRoot);
        var settings = new AppSettings { DataRoot = locations.DataRoot, OnboardingDone = true };
        configure?.Invoke(settings);
        var guard = new SafetyGuard(locations, settings);
        var inUse = new RestartManagerInUseChecker();
        var handlers = EngineFactory.CreateFileHandlers().Concat(PlatformHandlers.CreateAll()).ToList();
        QuarantineStore? quarantineForContext = null;
        var inProcess = new ElevatedBatchExecutor(handlers, () => new HandlerContext
        {
            RunId = "", Locations = locations, Safety = guard, InUse = inUse,
            Quarantine = quarantineForContext!, Settings = settings, Mode = PcTidy.Core.Model.ExecutionMode.Real, IsElevated = true,
        });
        var elevation = new ElevationService(locations, inProcess);
        var restore = new RestorePointService(locations);
        var engine = EngineFactory.Create(locations, guard, inUse, settings, PlatformHandlers.CreateAll(), restore, elevation);
        quarantineForContext = engine.Quarantine;
        return new PcTidyHost
        {
            Locations = locations, Settings = settings, Safety = guard, InUse = inUse,
            Walker = new ManagedFileWalker(), System = new SandboxSystemInfo(locations),
            Journal = engine.Journal, Quarantine = engine.Quarantine, Executor = engine.Executor, Undo = engine.Undo,
            Handlers = handlers, Elevation = elevation, RestorePoints = restore,
        };
    }

    /// <summary>For App.Main: run as the elevated helper if launched with --elevated. Returns null otherwise.</summary>
    public static int? TryRunElevatedHelper(string[] args)
        => ElevatedHost.IsElevatedInvocation(args) ? CreateReal().RunElevatedHelper(args) : null;

    /// <summary>
    /// True while a run, resume, undo or roll back is in progress (ask the user before closing the window).
    /// </summary>
    public bool IsRunInProgress => Executor is Executor e && e.IsRunning;

    /// <summary>
    /// Runs this process as the elevated helper (admin steps for the parent app). Returns the exit code.
    /// The helper trusts nothing the user can write: quarantine, registry backups, tool logs and its ledger live in
    /// <see cref="AdminDataFolder"/> (SYSTEM + Administrators only), its own SafetyGuard ignores loosening settings
    /// (folders the user allowed for ask-first areas), and the delete cap uses the default limits.
    /// </summary>
    public int RunElevatedHelper(string[] args)
    {
        if (!ElevatedHost.IsElevatedInvocation(args)) return ElevatedHost.ExitBadArguments;
        AdminDataFolder admin;
        try { admin = AdminDataFolder.OpenOrCreate(AdminDataFolder.DefaultRoot); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or global::System.Security.SecurityException or InvalidOperationException)
        {
            return ElevatedHost.ExitError; // no trustworthy place for its records: the app reports the steps as not run
        }
        using (admin)
        {
            var helper = CreateHelperServices(Locations, Settings, InUse, admin);
            return ElevatedHost.Run(args, Handlers, helper.Context, helper.Policy);
        }
    }

    /// <summary>What the admin helper's delete-cap message ends with: confirming the size in the app doesn't lift it.</summary>
    internal const string HelperCapAdvice = "PC Tidy's admin helper keeps this limit for admin steps. Tick fewer admin items and run them in parts.";

    /// <summary>The helper's context factory and policy (see <see cref="RunElevatedHelper"/>).</summary>
    internal static (Func<HandlerContext> Context, ElevatedHelperPolicy Policy) CreateHelperServices(
        IKnownLocations locations, AppSettings userSettings, IInUseChecker inUse, AdminDataFolder admin)
    {
        var settings = HardenedSettings(userSettings);
        var guard = new SafetyGuard(locations, settings);
        var quarantine = QuarantineStore.CreateAdminStore(admin, locations, settings);
        var handlerLocations = new AdminDataLocations(locations, admin.Root);
        var limits = new EngineSettings();
        var policy = new ElevatedHelperPolicy
        {
            Ledger = new HelperLedger(admin.Root, admin.Verify),
            // Per request. The app splits a request the helper refuses with this message into smaller ones, so only a
            // single step that is over the limit by itself stays refused.
            DeleteCap = (items, ct) => DeleteCap.Problem(items, limits.MaxRemoveItemsPerRun, limits.MaxRemoveBytesPerRun, ct, HelperCapAdvice),
            // Whole-run total across the 1,000-step requests, kept in the helper's own ledger. Admin removals can only
            // happen inside the admin cleanup folders (Windows temp, error reports, logs…), so the run limit is generous
            // but still bounded, and the user's own override never reaches the helper.
            RunCap = (200_000, 500L * 1024 * 1024 * 1024),
        };
        return (() => new HandlerContext
        {
            RunId = "", Locations = handlerLocations, Safety = guard, InUse = inUse,
            Quarantine = quarantine, Settings = settings, Mode = PcTidy.Core.Model.ExecutionMode.Real, IsElevated = true,
        }, policy);
    }

    /// <summary>
    /// The user's settings minus everything that would loosen a rule (allowed ask-first folders, engine overrides).
    /// Settings that only protect more (extra protected folders, scan exclusions) are kept.
    /// </summary>
    internal static AppSettings HardenedSettings(AppSettings s) => new()
    {
        DataRoot = s.DataRoot,
        QuarantineDays = s.QuarantineDays,
        ExtraProtectedFolders = new List<string>(s.ExtraProtectedFolders),
        ScanExclusions = new List<string>(s.ScanExclusions),
        AllowedAskFirstFolders = new List<string>(),
    };

    public void Dispose() => (Elevation as IDisposable)?.Dispose();
}
