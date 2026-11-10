using System.IO.Compression;
using System.Text;
using Microsoft.Win32;
using PcTidy.Setup.Install;
using PcTidy.Setup.Payload;
using PcTidy.Setup.Windows;

namespace PcTidy.Setup.Tests;

/// <summary>
/// A private fake PC under testbed\secfix\setup\&lt;name&gt;\ (Program Files, all-users Start menu and desktop, the user's
/// own Start menu, desktop and data folder) and a private registry area HKCU\Software\PCTidyTest\secfix\setup\&lt;name&gt;\
/// (the "machine" Installed-apps entry lives there too). Nothing real is touched: no Program Files, no HKLM, no real
/// Start menu, tasks or processes, no admin prompt (a fake elevation runs the admin part in-process), no real ACL change.
/// </summary>
public sealed class SetupBed : IDisposable
{
    public const string RegistryArea = @"secfix\setup";
    public static readonly string TestbedRoot = Path.Combine(FindCodeRoot(), "testbed", "secfix", "setup");

    public string Root { get; }
    public string RegistryName { get; }
    public FakeTasks Tasks { get; } = new();
    public FakeProcesses Processes { get; } = new();
    public FakeHardener Hardener { get; } = new();
    public FakeReboot Reboot { get; } = new();
    public FakeElevation Elevation { get; }
    public InstallEnvironment Env { get; private set; }

    public SetupBed(string name)
    {
        RegistryName = $"{name}-{Guid.NewGuid().ToString("N")[..6]}";
        Root = Path.Combine(TestbedRoot, RegistryName);
        Directory.CreateDirectory(Root);
        Env = new InstallEnvironment
        {
            InstallDir = Path.Combine(Root, @"Program Files\PC Tidy"),
            ProgramFilesRoot = Path.Combine(Root, "Program Files"),
            StartMenuPrograms = Path.Combine(Root, @"ProgramData\Start Menu\Programs"),
            Desktop = Path.Combine(Root, @"Public\Desktop"),
            UserStartMenuPrograms = Path.Combine(Root, @"Roaming\Start Menu\Programs"),
            UserDesktop = Path.Combine(Root, "Desktop"),
            DataRoot = Path.Combine(Root, @"Local\PC Tidy"),
            DefaultDataRoot = Path.Combine(Root, @"Local\PC Tidy"),
            Registry = RegistryPaths.ForTest(RegistryName, RegistryArea),
            Tasks = Tasks,
            Processes = Processes,
            Hardener = Hardener,
            Reboot = Reboot,
        };
        Elevation = new FakeElevation(this);
        Assert.True(Env.Registry.IsTest); // belt and braces: never the real keys, never HKLM
    }

    public void Use(Func<InstallEnvironment, InstallEnvironment> change) => Env = change(Env);

    /// <summary>A fake "PC Tidy Setup.exe": a stub followed by the payload, exactly as build-release.ps1 makes it.</summary>
    public (string Exe, PayloadInfo Payload, byte[] Stub) MakeSetupExe(string version = "1.0.0", IDictionary<string, string>? extraFiles = null, string? badEntry = null)
    {
        var zip = Path.Combine(Root, $"payload-{version}-{Guid.NewGuid().ToString("N")[..4]}.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            void Add(string entry, string content)
            {
                using var w = new StreamWriter(archive.CreateEntry(entry).Open(), Encoding.UTF8);
                w.Write(content);
            }
            Add(PayloadDescription.FileName, $$"""{ "version": "{{version}}" }""");
            Add(Product.AppExe, "fake app " + version);
            Add(Product.RescueExe, "fake rescue " + version);
            Add("README.md", "# PC Tidy");
            Add("LICENSE.txt", "MIT");
            Add(@"docs/PRIVACY.md", "Nothing leaves your PC.");
            foreach (var (k, v) in extraFiles ?? new Dictionary<string, string>()) Add(k, v);
            if (badEntry is not null) Add(badEntry, "escape attempt");
        }
        var stub = Encoding.ASCII.GetBytes("MZ fake setup stub " + new string('x', 4000));
        var stubPath = Path.Combine(Root, "stub.exe");
        File.WriteAllBytes(stubPath, stub);
        var exe = Path.Combine(Root, $"PC Tidy Setup {version}.exe");
        PayloadFile.Write(stubPath, zip, exe);
        return (exe, PayloadFile.Find(exe)!, stub);
    }

    public InstallRequest Request(PayloadInfo payload, bool startMenu = true, bool desktop = false) => new()
    {
        OpenPayload = payload.Open,
        UninstallerSource = payload.ExePath,
        UninstallerLength = payload.StubLength,
        StartMenuShortcuts = startMenu,
        DesktopShortcut = desktop,
    };

    /// <summary>The whole install as the window does it: the admin part through the (fake) elevation, then the user's part.</summary>
    public async Task<SetupResult> Install(PayloadInfo payload, bool startMenu = true, bool desktop = false)
    {
        var request = Request(payload, startMenu, desktop);
        Elevation.InstallRequest = request;
        return await new SetupFlow(Env, Elevation).InstallAsync(request);
    }

    /// <summary>The admin part only (what the elevated copy runs).</summary>
    public Task<SetupResult> InstallMachinePart(PayloadInfo payload, bool startMenu = true, bool desktop = false)
        => new Installer(Env).InstallAsync(Request(payload, startMenu, desktop));

    public async Task<SetupResult> Uninstall(bool keepData = true, string? runningUninstaller = null)
    {
        Elevation.SelfPath = runningUninstaller ?? Path.Combine(Root, "elsewhere", Product.UninstallerExe);
        return await new SetupFlow(Env, Elevation).UninstallAsync(keepData, runningUninstaller);
    }

    /// <summary>The machine-wide "Installed apps" entry (under the test key).</summary>
    public RegistryKey? UninstallKey() => Registry.CurrentUser.OpenSubKey(Env.Registry.UninstallKey);

    public RegistryKey TestKey(string relative) => Registry.CurrentUser.CreateSubKey($@"{RegistryPaths.TestRoot}\{RegistryArea}\{RegistryName}\{relative}", writable: true);

    public bool TestKeyExists(string relative)
    {
        using var k = Registry.CurrentUser.OpenSubKey($@"{RegistryPaths.TestRoot}\{RegistryArea}\{RegistryName}\{relative}");
        return k is not null;
    }

    public void Dispose()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree($@"{RegistryPaths.TestRoot}\{RegistryArea}\{RegistryName}", throwOnMissingSubKey: false); } catch { }
        DeleteIfEmpty($@"{RegistryPaths.TestRoot}\{RegistryArea}");
        DeleteIfEmpty($@"{RegistryPaths.TestRoot}\secfix");
        DeleteIfEmpty(RegistryPaths.TestRoot);
        try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); } catch { /* testbed is disposable */ }
    }

    private static void DeleteIfEmpty(string key)
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(key))
                if (k is null || k.SubKeyCount > 0 || k.ValueCount > 0) return;
            Registry.CurrentUser.DeleteSubKey(key, throwOnMissingSubKey: false);
        }
        catch { /* another test is using it */ }
    }

    private static string FindCodeRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "PcTidy.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("PcTidy.sln not found");
    }
}

/// <summary>
/// Stands in for Windows' admin prompt: records what Setup asked for and runs the admin part in-process with the test
/// environment (exactly the code the elevated copy runs), or says "declined".
/// </summary>
public sealed class FakeElevation(SetupBed bed) : ISetupElevation
{
    public bool IsElevated { get; set; }
    public bool Decline { get; set; }
    public List<IReadOnlyList<string>> Calls { get; } = new();
    public InstallRequest? InstallRequest { get; set; }
    public string SelfPath { get; set; } = "";

    public Task<int> RunElevatedAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        Calls.Add(arguments.ToList());
        if (Decline) return Task.FromResult(Program.ExitDeclined);
        return Task.Run(() => Program.RunElevatedPart(SetupArgs.Parse(arguments), bed.Env, elevated: true, SelfPath, InstallRequest), ct);
    }
}

public sealed class FakeHardener : IFolderHardener
{
    public List<string> Hardened { get; } = new();
    public List<string> ContentsHardened { get; } = new();
    public string? VerifyAnswer { get; set; }
    public void Harden(string installDir) => Hardened.Add(installDir);
    public void HardenContents(string installDir) => ContentsHardened.Add(installDir);
    public string? Verify(string appExe) => VerifyAnswer;
}

public sealed class FakeReboot : IRebootCleanup
{
    public List<string> Scheduled { get; } = new();
    public bool DeleteAtRestart(string path) { Scheduled.Add(path); return true; }
}

public sealed class FakeTasks : ITaskCleaner
{
    public List<string> Existing { get; } = new();
    public List<string> Deleted { get; } = new();
    public IReadOnlyList<string> FindPcTidyTasks() => Existing.ToList();
    public string? Delete(string taskPath) { Deleted.Add(taskPath); Existing.Remove(taskPath); return null; }
}

public sealed class FakeProcesses : IAppProcesses
{
    public List<RunningApp> Running { get; } = new();
    public bool ClosesWhenAsked { get; set; } = true;
    public List<string> Launched { get; } = new();
    /// <summary>After this many FindRunning calls, <see cref="LateStarter"/> appears (a copy started meanwhile).</summary>
    public int StartsAfterCalls
    {
        get => _startsAfter;
        set { _startsAfter = value; _calls = 0; }
    }
    private int _startsAfter = -1;
    public RunningApp? LateStarter { get; set; }
    private int _calls;

    public IReadOnlyList<RunningApp> FindRunning(string installDir)
    {
        if (_calls++ == StartsAfterCalls && LateStarter is not null) Running.Add(LateStarter);
        return Running.ToList();
    }
    public Task<IReadOnlyList<RunningApp>> CloseAsync(IReadOnlyList<RunningApp> apps, TimeSpan wait, CancellationToken ct)
    {
        if (ClosesWhenAsked) Running.Clear();
        return Task.FromResult<IReadOnlyList<RunningApp>>(Running.ToList());
    }
    public void Launch(string exePath) => Launched.Add(exePath);
    public List<(string Exe, string Arg)> RanQuiet { get; } = new();
    public bool RunQuiet(string exePath, string argument, TimeSpan wait) { RanQuiet.Add((exePath, argument)); return true; }
}
