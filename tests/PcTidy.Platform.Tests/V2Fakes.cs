using PcTidy.Core;
using PcTidy.Core.Model;

namespace PcTidy.Platform.Tests;

// In-memory stand-ins for every v2/v3 backend. None of them touches Windows (TouchesRealSystem = false).

internal sealed class FakePowerSchemes : IPowerSchemeControl
{
    public HashSet<Guid> Schemes { get; } = new();
    public Guid? Active { get; set; }
    public bool TouchesRealSystem => false;
    public Guid? GetActive() => Active;
    public bool Exists(Guid scheme) => Schemes.Contains(scheme);
    public void SetActive(Guid scheme) => Active = scheme;
}

internal sealed class FakeRestorePoints : IRestorePointRemover
{
    public List<RestorePointEntry> Points { get; } = new();
    public string? Problem { get; set; }
    public List<uint> Removed { get; } = new();
    public bool TouchesRealSystem => false;
    public RestorePointListing List() => new(Points.ToList(), Problem);
    public uint Remove(uint sequenceNumber) { Removed.Add(sequenceNumber); Points.RemoveAll(p => p.SequenceNumber == sequenceNumber); return 0; }
}

internal sealed class FakeWlan : IWlanControl
{
    public Dictionary<(Guid, string), WlanProfileExport> Profiles { get; } = new();
    public bool TouchesRealSystem => false;
    public WlanProfileExport? Export(Guid interfaceGuid, string profileName) => Profiles.TryGetValue((interfaceGuid, profileName), out var p) ? p : null;
    public void Delete(Guid interfaceGuid, string profileName) => Profiles.Remove((interfaceGuid, profileName));
    public void Import(Guid interfaceGuid, string xml, uint flags)
    {
        var name = System.Xml.Linq.XDocument.Parse(xml).Root!.Elements().First(e => e.Name.LocalName == "name").Value;
        Profiles[(interfaceGuid, name)] = new WlanProfileExport(xml, flags);
    }
}

internal sealed class FakeProfiles : IUserProfileControl
{
    public Dictionary<string, UserProfileFacts> Profiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? CurrentUserSid { get; set; } = "S-1-5-21-1-2-3-1001";
    public List<string> Deleted { get; } = new();
    public bool TouchesRealSystem => false;
    public UserProfileFacts? Query(string sid) => Profiles.TryGetValue(sid, out var p) ? p : null;
    public void Delete(string sid) { Deleted.Add(sid); Profiles.Remove(sid); }
}

internal sealed class FakeFeatures : IOptionalFeatureControl
{
    public Dictionary<string, OptionalFeatureState> States { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int RemoveResult { get; set; }
    public List<string> Calls { get; } = new();
    public bool TouchesRealSystem => false;
    public OptionalFeatureState Query(string name, bool capability) => States.TryGetValue(name, out var s) ? s : OptionalFeatureState.Missing;
    public int Remove(string name, bool capability, string? logPath) { Calls.Add((capability ? "removeCapability:" : "disable:") + name); States[name] = OptionalFeatureState.Disabled; return RemoveResult; }
    public int EnableFeature(string name, string? logPath) { Calls.Add("enable:" + name); States[name] = OptionalFeatureState.Enabled; return 0; }
}

internal sealed class FakeVolumes : IVolumeOptimizer
{
    public Dictionary<char, DriveMedia> Drives { get; } = new();
    public List<(char, bool ReTrim, bool Defrag)> Calls { get; } = new();
    public bool TouchesRealSystem => false;
    public bool IsFixedDrive(char letter) => Drives.ContainsKey(letter);
    public DriveMedia MediaOf(char letter) => Drives.TryGetValue(letter, out var m) ? m : DriveMedia.Unknown;
    public uint Optimize(char letter, bool reTrim, bool defrag) { Calls.Add((letter, reTrim, defrag)); return 0; }
}

internal sealed class FakeApps : IAppWindowControl
{
    public Dictionary<int, ProcessFacts> Processes { get; } = new();
    public bool ClosesOnRequest { get; set; } = true;
    public int Windows { get; set; } = 1;
    public List<int> Posted { get; } = new();
    public bool TouchesRealSystem => false;
    public int CurrentSessionId => 1;
    public ProcessFacts? Query(int pid) => Processes.TryGetValue(pid, out var p) ? p : null;
    public int PostClose(int pid) { Posted.Add(pid); if (ClosesOnRequest && Windows > 0) Processes.Remove(pid); return Windows; }
    public Task<bool> WaitForExitAsync(ProcessFacts process, TimeSpan timeout, CancellationToken ct) => Task.FromResult(!Processes.ContainsKey(process.Pid));
}

internal sealed class FakeAppx : IAppxPackageControl
{
    public Dictionary<string, AppxPackageFacts> Installed { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> StagedFolders { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, AppxPackageFacts> Known { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Calls { get; } = new();
    public bool TouchesRealSystem => false;
    public AppxPackageFacts? Find(string packageFullName) => Installed.TryGetValue(packageFullName, out var p) ? p : null;
    public IReadOnlyList<AppxPackageFacts> FindFamily(string packageFamilyName) => Installed.Values.Where(p => p.FamilyName == packageFamilyName).ToList();
    public bool IsStaged(string installedLocation) => StagedFolders.Contains(installedLocation);
    public Task RemoveAsync(string packageFullName, CancellationToken ct) { Calls.Add("remove:" + packageFullName); Known[packageFullName] = Installed[packageFullName]; Installed.Remove(packageFullName); return Task.CompletedTask; }
    public Task RegisterAsync(string packageFullName, CancellationToken ct) { Calls.Add("register:" + packageFullName); if (Known.TryGetValue(packageFullName, out var p)) Installed[packageFullName] = p; return Task.CompletedTask; }
    public Task<int> ResetStoreCacheAsync(string logPath, CancellationToken ct) { Calls.Add("wsreset"); return Task.FromResult(0); }
}

internal sealed class FakeKnownFolders : IKnownFolderControl
{
    public Dictionary<Guid, string> Paths { get; } = new();
    public List<(Guid, string, bool CheckOnly)> Calls { get; } = new();
    /// <summary>Copies the folder's files like KF_REDIRECT_COPY_CONTENTS does (inside the testbed only).</summary>
    public bool CopyContents { get; set; } = true;
    public int CheckResult { get; set; }
    public bool TouchesRealSystem => false;
    public string? GetPath(Guid folderId) => Paths.TryGetValue(folderId, out var p) ? p : null;
    public RedirectOutcome Redirect(Guid folderId, string targetPath, bool checkOnly)
    {
        Calls.Add((folderId, targetPath, checkOnly));
        if (checkOnly) return new RedirectOutcome(CheckResult, CheckResult == 0 ? null : "Not allowed.");
        var from = Paths[folderId];
        if (CopyContents)
        {
            // Like a copy that skips what it can't read: every folder it can see, every file it can read.
            var visible = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (var dir in Directory.EnumerateDirectories(from, "*", visible))
                Directory.CreateDirectory(Path.Combine(targetPath, Path.GetRelativePath(from, dir)));
            foreach (var file in Directory.EnumerateFiles(from, "*", visible))
            {
                var to = Path.Combine(targetPath, Path.GetRelativePath(from, file));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                if (!File.Exists(to)) File.Copy(file, to);
            }
        }
        Paths[folderId] = targetPath;
        return new RedirectOutcome(0, null);
    }
}

internal sealed class FakeUninstall : IUninstallBackend
{
    public List<UninstallEntry> Listed { get; } = new();
    public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<UninstallCommand> Ran { get; } = new();
    public Func<UninstallCommand, int> ExitCode { get; set; } = _ => 0;
    public bool RemoveEntryOnRun { get; set; } = true;
    public bool TouchesRealSystem => false;
    public bool FileExists(string path) => Files.Contains(path);
    public string WingetPath => @"C:\Users\tester\AppData\Local\Microsoft\WindowsApps\winget.exe";
    public IReadOnlyList<UninstallEntry> Entries() => Listed.ToList();
    /// <summary>Programs an account without admin rights could change (path → reason).</summary>
    public Dictionary<string, string> Changeable { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Becomes changeable between the plan and the start (the re-check right before CreateProcess sees it).</summary>
    public HashSet<string> ChangeableAtStart { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<(UninstallCommand Command, bool AsAdmin)> Runs { get; } = new();
    public List<UninstallCommand> LaunchedWithoutAdmin { get; } = new();
    public bool RemoveEntryOnLaunch { get; set; } = true;
    public string? AdminRunProblem(string exePath) => Changeable.TryGetValue(exePath, out var why) ? why : null;
    public void LaunchWithoutAdmin(UninstallCommand command)
    {
        LaunchedWithoutAdmin.Add(command);
        if (RemoveEntryOnLaunch) Listed.Clear();
    }
    public Task<ProcessResult> RunAsync(UninstallCommand command, bool asAdmin, TimeSpan timeout, string? logPath, CancellationToken ct)
    {
        if (asAdmin && ChangeableAtStart.Contains(command.ExePath)) throw new UnsafeProgramException("changeable");
        Runs.Add((command, asAdmin));
        Ran.Add(command);
        var code = ExitCode(command);
        if (RemoveEntryOnRun && code == 0) Listed.Clear();
        return Task.FromResult(new ProcessResult(code, false, ""));
    }
    public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.CompletedTask;
}

internal sealed class FakeLauncher : IAppLauncher
{
    public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<(string, string)> Launched { get; } = new();
    public bool TouchesRealSystem => false;
    public bool FileExists(string path) => Files.Contains(path);
    public void Launch(string exePath, string arguments) => Launched.Add((exePath, arguments));
}

internal sealed class FakeQuickAccess : IQuickAccessControl
{
    public HashSet<string> Pinned { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Blocked { get; set; }
    public bool TouchesRealSystem => false;
    public bool? IsPinned(string folder) => Pinned.Contains(folder);
    public void Pin(string folder) { if (!Blocked) Pinned.Add(folder); }
    public void Unpin(string folder) => Pinned.Remove(folder);
}

internal sealed class FakeOwnTasks : IOwnTaskControl
{
    public Dictionary<string, string> Tasks { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool TouchesRealSystem => false;
    public string? GetXml(string name) => Tasks.TryGetValue(name, out var x) ? x : null;
    public void Register(string name, string xml) => Tasks[name] = xml;
    public void Delete(string name) => Tasks.Remove(name);
}

internal sealed class FakeVisualEffects : IVisualEffectsApi
{
    public Dictionary<string, bool> States { get; } = new();
    public List<IReadOnlyDictionary<string, bool>> Applied { get; } = new();
    public bool TouchesRealSystem => false;
    public IReadOnlyDictionary<string, bool> Read() => new Dictionary<string, bool>(States);
    public void Apply(IReadOnlyDictionary<string, bool> states) { Applied.Add(states); foreach (var (k, v) in states) States[k] = v; }
}

/// <summary>Records every program it is asked to run; runs nothing.</summary>
internal sealed class FakeToolBackend : ISystemToolBackend
{
    public FakeToolBackend(string root)
    {
        LocalAppData = Path.Combine(root, "Local");
        RoamingAppData = Path.Combine(root, "Roaming");
        ProgramFiles = Path.Combine(root, "ProgramFiles");
        ProgramData = Path.Combine(root, "ProgramData");
        UserProfile = Path.Combine(root, "Profile");
    }

    public bool TouchesRealSystem => false;
    public string LocalAppData { get; }
    public string RoamingAppData { get; }
    public string ProgramFiles { get; }
    public string ProgramData { get; }
    public string UserProfile { get; }
    public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string[]> Dirs { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Path_ { get; } = new();
    public Dictionary<string, string> Env { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ToolCommand> Ran { get; } = new();
    public List<ToolCommand> Launched { get; } = new();
    public Func<ToolCommand, int> ExitCode { get; set; } = _ => 0;
    public Action<ToolCommand>? OnRun { get; set; }
    public HashSet<string> Running { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Opened { get; } = new();
    public Dictionary<uint, BcdElementValue> Bcd { get; } = new();
    public bool BcdReadable { get; set; } = true;
    public string? Autotuning { get; set; }
    public Dictionary<char, DriveMedia> Media { get; } = new();
    public bool DnsFlushed { get; private set; }
    public UpdateInstallResult Wua { get; set; } = new(1, 1, 2, false);
    public DriverRollbackResult Rollback { get; set; } = new(true, 0, false);
    public RestorePointInfo RestorePoint { get; set; } = new(true, false, "test", DateTime.UtcNow, null);

    public bool FileExists(string path) => Files.Contains(path);
    public bool DirectoryExists(string path) => Dirs.ContainsKey(path);
    public IReadOnlyList<string> SubDirectories(string path) => Dirs.TryGetValue(path, out var d) ? d : Array.Empty<string>();
    public IReadOnlyList<string> SearchPath() => Path_;
    public string? Variable(string name) => Env.TryGetValue(name, out var v) ? v : null;

    public Task<ProcessResult> RunAsync(ToolCommand command, TimeSpan timeout, string? logPath, CancellationToken ct)
    {
        Ran.Add(command);
        OnRun?.Invoke(command);
        return Task.FromResult(new ProcessResult(ExitCode(command), false, ""));
    }

    public void Launch(ToolCommand command) => Launched.Add(command);
    public bool IsProcessRunning(string processName) => Running.Contains(processName);
    public void OpenUri(string uri) => Opened.Add(uri);
    public HibernationState? ReadHibernation() => null;
    public bool CleanmgrHandlerExists(string handler) => true;
    public IDisposable SelectCleanmgrHandler(string handler) => new Nothing();
    public BcdElementValue? ReadBcdElement(uint elementType, out bool readable) { readable = BcdReadable; return BcdReadable && Bcd.TryGetValue(elementType, out var v) ? v : null; }
    public string? ReadAutotuningLevel() => Autotuning;
    public DriveMedia MediaOf(char driveLetter) => Media.TryGetValue(driveLetter, out var m) ? m : DriveMedia.Unknown;
    public Dictionary<string, DevicePresence> Devices { get; } = new(StringComparer.OrdinalIgnoreCase);
    public DevicePresence DeviceState(string instanceId) => Devices.TryGetValue(instanceId, out var d) ? d : DevicePresence.Missing;
    public Dictionary<char, ShadowStorageFacts> Shadow { get; } = new();
    public ShadowStorageFacts? ReadShadowStorage(char driveLetter) => Shadow.TryGetValue(driveLetter, out var s) ? s : null;
    public bool FlushDnsCache() { DnsFlushed = true; return true; }
    public Task<UpdateInstallResult> InstallDriverUpdatesAsync(IReadOnlyList<string> updateIds, CancellationToken ct) => Task.FromResult(Wua);
    public DriverRollbackResult RollbackDriver(string instanceId) => Rollback;
    public Task<RestorePointInfo> CreateRestorePointAsync(IKnownLocations locations, string description, CancellationToken ct) => Task.FromResult(RestorePoint);

    // ---- v3.1: services and folder renames, all in memory
    private string? _windowsDirectory;
    private string? _systemDirectory;
    public string WindowsDirectory
    {
        get => _windowsDirectory ?? Path.Combine(Path.GetDirectoryName(LocalAppData)!, "Windows");
        set => _windowsDirectory = value;
    }
    public string SystemDirectory
    {
        get => _systemDirectory ?? Path.Combine(WindowsDirectory, "System32");
        set => _systemDirectory = value;
    }
    public DateTime LocalNow { get; set; } = new(2026, 3, 4, 5, 6, 7);
    public Dictionary<string, FakeService> Services { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>"stop:X" / "start:X" in the order they happened.</summary>
    public List<string> ServiceLog { get; } = new();
    /// <summary>Like the SCM: the running dependents stop first, then this one doesn't stop in time (it stays running).</summary>
    public HashSet<string> WontStop { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The dependents stop, the wait times out, and the service finishes stopping later (it ends up stopped).</summary>
    public HashSet<string> StopsLate { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The dependents stop, then Windows refuses the stop (InvalidOperationException, as ServiceController.Stop throws).</summary>
    public HashSet<string> StopThrows { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> WontStart { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Called after a service stopped (tests cancel here).</summary>
    public Action<string>? OnStop { get; set; }
    public HashSet<string> LockedFolders { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Folders that are links or junctions (reparse points), not real folders.</summary>
    public HashSet<string> ReparsePoints { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<(string From, string To)> Renamed { get; } = new();

    public FakeService AddService(string name, bool running = true, bool disabled = false, params string[] dependents)
        => Services[name] = new FakeService { Running = running, Disabled = disabled, Dependents = dependents.ToList() };

    public ServiceRunState? QueryService(string serviceName)
        => Services.TryGetValue(serviceName, out var s)
            ? new ServiceRunState(s.Running, s.Disabled, s.Dependents.Where(d => Services.TryGetValue(d, out var x) && x.Running).ToList())
            : null;

    public Task<bool> StopServiceAsync(string serviceName, TimeSpan timeout, CancellationToken ct)
    {
        ServiceLog.Add("stop:" + serviceName);
        var s = Services[serviceName];
        StopDependents(s, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { serviceName }); // like the SCM: dependents (and theirs) stop first
        if (StopThrows.Contains(serviceName)) throw new InvalidOperationException("The service can't accept a stop now.");
        if (WontStop.Contains(serviceName)) return Task.FromResult(false);
        s.Running = false;
        OnStop?.Invoke(serviceName);
        ct.ThrowIfCancellationRequested(); // a cancel that arrives while Windows is stopping it
        return Task.FromResult(!StopsLate.Contains(serviceName));
    }

    private void StopDependents(FakeService s, HashSet<string> seen)
    {
        foreach (var d in s.Dependents)
            if (seen.Add(d) && Services.TryGetValue(d, out var x))
            {
                StopDependents(x, seen);
                x.Running = false;
            }
    }

    public bool IsPlainDirectory(string path) => Dirs.ContainsKey(path) && !ReparsePoints.Contains(path);

    public Task<bool> StartServiceAsync(string serviceName, TimeSpan timeout, CancellationToken ct)
    {
        ServiceLog.Add("start:" + serviceName);
        if (WontStart.Contains(serviceName) || !Services.TryGetValue(serviceName, out var s) || s.Disabled) return Task.FromResult(false);
        s.Running = true;
        return Task.FromResult(true);
    }

    public void RenameFolder(string path, string newPath)
    {
        if (LockedFolders.Contains(path)) throw new IOException("The folder is in use.");
        if (!Dirs.Remove(path, out var contents)) throw new DirectoryNotFoundException(path);
        Dirs[newPath] = contents;
        Renamed.Add((path, newPath));
    }

    private sealed class Nothing : IDisposable { public void Dispose() { } }
}

internal sealed class FakeService
{
    public bool Running { get; set; }
    public bool Disabled { get; set; }
    public List<string> Dependents { get; set; } = new();
}

/// <summary>Devices in memory; pnputil calls are recorded and change the fake device as Windows would.</summary>
internal sealed class FakeDevices : IDeviceControl
{
    public bool TouchesRealSystem => false;
    public int WindowsBuild { get; set; } = 22631;
    public Dictionary<string, DeviceFacts> Devices { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Ran { get; } = new();
    public int ExitCode { get; set; }
    /// <summary>False: pnputil "succeeds" but the device stays as it was.</summary>
    public bool Applies { get; set; } = true;

    public DeviceFacts? Read(string instanceId) => Devices.TryGetValue(instanceId, out var d) ? d : null;

    public Task<ProcessResult> RunPnputilAsync(IReadOnlyList<string> arguments, string? logPath, CancellationToken ct)
    {
        Ran.Add(string.Join(' ', arguments));
        if (ExitCode is 0 or 3010 && Applies && Devices.TryGetValue(arguments[1], out var d))
            Devices[arguments[1]] = arguments[0] == "/enable-device"
                ? d with { HasProblem = false, ProblemCode = 0 }
                : d with { HasProblem = true, ProblemCode = DeviceFacts.ProblemDisabled };
        return Task.FromResult(new ProcessResult(ExitCode, false, ""));
    }
}

/// <summary>Quarantine that moves folders into a testbed folder and back.</summary>
internal sealed class TestbedQuarantine(string store) : IQuarantineStore
{
    public List<QuarantineEntry> Entries { get; } = new();

    public Task<QuarantineEntry> QuarantineAsync(string path, string runId, CancellationToken ct)
    {
        Directory.CreateDirectory(store);
        var stored = Path.Combine(store, Guid.NewGuid().ToString("N"));
        Directory.Move(path, stored);
        var e = new QuarantineEntry { Id = Path.GetFileName(stored), RunId = runId, OriginalPath = path, StoredPath = stored, IsDirectory = true, SizeBytes = 10 };
        Entries.Add(e);
        return Task.FromResult(e);
    }

    public Task<string> RestoreAsync(QuarantineEntry entry, CancellationToken ct)
    {
        Directory.Move(entry.StoredPath, entry.OriginalPath);
        return Task.FromResult(entry.OriginalPath);
    }

    public Task<IReadOnlyList<QuarantineEntry>> ListAsync(string? runId, CancellationToken ct) => Task.FromResult<IReadOnlyList<QuarantineEntry>>(Entries.Where(e => runId is null || e.RunId == runId).ToList());
    public List<string> Purged { get; } = new();
    public Task PurgeRunAsync(string runId, CancellationToken ct) { Purged.Add(runId); return Task.CompletedTask; }
    public Task<long> TotalBytesAsync(CancellationToken ct) => Task.FromResult(Entries.Sum(e => e.SizeBytes));
}

internal static class V2Items
{
    public static PlanItem Item(ActionKind kind, Dictionary<string, string>? args = null, bool? enable = null, bool admin = false, bool reversible = true, string feature = "test",
        string? source = null, string? destination = null) => new()
    {
        Kind = kind, FeatureId = feature, Description = "test", Args = args, Enable = enable, NeedsAdmin = admin, Reversible = reversible,
        SourcePath = source, DestinationPath = destination,
    };

    public static HandlerContext With(this HandlerContext ctx, IQuarantineStore quarantine) => new()
    {
        RunId = ctx.RunId, Locations = ctx.Locations, Safety = ctx.Safety, InUse = ctx.InUse, Quarantine = quarantine,
        Settings = ctx.Settings, Mode = ctx.Mode, IsElevated = ctx.IsElevated,
    };
}
