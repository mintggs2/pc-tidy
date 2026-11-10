using System.Windows.Threading;
using Microsoft.Win32;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Tweaks.Reading;
using PcTidy.Hosting;
using CoreHive = PcTidy.Core.Model.RegistryHive;
using Win32Hive = Microsoft.Win32.RegistryHive;

namespace PcTidy.Features.Tweaks.Tests;

/// <summary>
/// A private registry area under HKCU\Software\PCTidyTest\tweaks\&lt;id&gt; laid out like the scanners' redirect
/// (&lt;root&gt;\HKLM\…, &lt;root&gt;\HKCU\…). Values are written with logical hives; Dispose deletes the whole area.
/// Only tests write here (feature code never writes the registry).
/// </summary>
public sealed class TestRegistry : IDisposable
{
    public TestRegistry(string? name = null)
    {
        Root = $@"{RegistryRedirect.TestArea}\tweaks\{name ?? "t"}-{Guid.NewGuid():N}";
        Redirect = RegistryRedirect.ToTestRoot(Root);
        Reader = new RegistryReader(Redirect);
    }

    public string Root { get; }
    public RegistryRedirect Redirect { get; }
    public RegistryReader Reader { get; }

    private static RegistryKey Hkcu => RegistryKey.OpenBaseKey(Win32Hive.CurrentUser, RegistryView.Registry64);

    public string Physical(CoreHive hive, string key) => Redirect.Map(hive, key).KeyPath;

    public void Set(CoreHive hive, string key, string name, object value, RegistryValueKind kind)
    {
        using var root = Hkcu;
        using var k = root.CreateSubKey(Physical(hive, key), writable: true)!;
        k.SetValue(name, value, kind);
    }

    public void Dword(CoreHive hive, string key, string name, int value) => Set(hive, key, name, value, RegistryValueKind.DWord);
    public void Text(CoreHive hive, string key, string name, string value) => Set(hive, key, name, value, RegistryValueKind.String);
    public void Lm(string key, string name, int value) => Dword(CoreHive.LocalMachine, key, name, value);
    public void Cu(string key, string name, int value) => Dword(CoreHive.CurrentUser, key, name, value);

    public void Key(CoreHive hive, string key)
    {
        using var root = Hkcu;
        root.CreateSubKey(Physical(hive, key), writable: true)?.Dispose();
    }

    /// <summary>A fake service key: Start (and optional ImagePath, Type 0x10 own process).</summary>
    public void Service(string name, int start, string? imagePath = null, bool delayed = false)
    {
        var key = $@"SYSTEM\CurrentControlSet\Services\{name}";
        Lm(key, "Start", start);
        Lm(key, "Type", 0x10);
        if (delayed) Lm(key, "DelayedAutostart", 1);
        if (imagePath is not null) Set(CoreHive.LocalMachine, key, "ImagePath", imagePath, RegistryValueKind.ExpandString);
    }

    public object? Get(CoreHive hive, string key, string name)
    {
        using var root = Hkcu;
        using var k = root.OpenSubKey(Physical(hive, key));
        return k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }

    public bool HasValue(CoreHive hive, string key, string name)
    {
        using var root = Hkcu;
        using var k = root.OpenSubKey(Physical(hive, key));
        return k is not null && k.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    public bool KeyExists(CoreHive hive, string key)
    {
        using var root = Hkcu;
        using var k = root.OpenSubKey(Physical(hive, key));
        return k is not null;
    }

    public void Dispose()
    {
        using var root = Hkcu;
        root.DeleteSubKeyTree(Root, throwOnMissingSubKey: false);
        // Remove the shared "tweaks" area too once the last test is done with it (another test may still use it).
        try
        {
            using var area = root.OpenSubKey($@"{RegistryRedirect.TestArea}\tweaks");
            if (area is { SubKeyCount: 0, ValueCount: 0 }) root.DeleteSubKey($@"{RegistryRedirect.TestArea}\tweaks", throwOnMissingSubKey: false);
        }
        catch (InvalidOperationException) { /* another test added a key meanwhile */ }
    }
}

/// <summary>A per-test folder under testbed\tweaks (deleted afterwards).</summary>
public sealed class TestFolder : IDisposable
{
    public static readonly string TestbedRoot = FindTestbed();

    public TestFolder(string? name = null) : this(name, null) { }

    /// <param name="area">Another testbed subfolder (e.g. "research-additions-3") instead of testbed\tweaks.</param>
    public TestFolder(string? name, string? area)
    {
        var root = area is null ? TestbedRoot : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(TestbedRoot)!, area);
        Path = System.IO.Path.Combine(root, (name ?? "t") + "-" + Guid.NewGuid().ToString("N")[..10]);
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
        var root = System.IO.Path.Combine(d.FullName, "testbed", "tweaks");
        Directory.CreateDirectory(root);
        return root;
    }
}

/// <summary>Builds scan environments and contexts for tests.</summary>
public static class TestEnv
{
    public static TweakEnvironment Create(TestRegistry reg, string folder, FixedSystemProbe? probe = null, BootConfigSnapshot? boot = null,
        IReadOnlyDictionary<string, string>? apps = null, IReadOnlyDictionary<string, bool>? tasks = null,
        int build = 26100, GpuVendor gpu = GpuVendor.Unknown, bool laptop = false)
        => new()
        {
            Registry = reg.Reader,
            Probe = probe ?? FixedSystemProbe.Unknown,
            Boot = new FixedBootConfigReader(boot),
            Apps = new FixedAppxInventory(apps),
            Tasks = new FixedTaskReader(tasks),
            HostsPath = TweakEnvironment.HostsFileIn(Path.Combine(folder, "Windows")),
            LocalAppData = Path.Combine(folder, "LocalAppData"),
            WindowsDir = Path.Combine(folder, "Windows"),
            WindowsBuild = build,
            Gpu = gpu,
            IsLaptop = laptop,
        };

    /// <summary>A minimal context (sandbox locations, real SafetyGuard) for scanners that get their environment injected.</summary>
    public static ScanContext Context(string folder, bool expert = false, string? preset = null)
    {
        var host = PcTidyHost.CreateSandbox(Path.Combine(folder, "pc"), SandboxProfile.Minimal, s => s.ExpertMode = expert);
        return host.CreateScanContext(ScanDepth.Quick, preset: preset);
    }
}

/// <summary>Records what a page hands to the shell.</summary>
public sealed class FakeFlowHost : IFlowHost
{
    public List<ActionPlan> Reviewed { get; } = new();
    public List<ActionPlan> Simulated { get; } = new();
    public List<ActionPlan> Approved { get; } = new();
    public List<string> Messages { get; } = new();

    public Task<RunRecord?> ReviewAndRunAsync(ActionPlan plan, CancellationToken ct) { Reviewed.Add(plan); return Task.FromResult<RunRecord?>(null); }
    public Task<RunRecord?> SimulateAsync(ActionPlan plan, CancellationToken ct) { Simulated.Add(plan); return Task.FromResult<RunRecord?>(null); }
    public Task<RunRecord?> RunApprovedAsync(ActionPlan plan, CancellationToken ct) { Approved.Add(plan); return Task.FromResult<RunRecord?>(null); }
    public void OpenInExplorer(string path) { }
    public void ShowMessage(string title, string message, bool isError = false) => Messages.Add(title + ": " + message);
}

public static class Sta
{
    public static void Run(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try { action(); } catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException("UI failed: " + error);
    }

    /// <summary>Runs the dispatcher until the task finishes (async commands continue on the UI thread).</summary>
    public static void Pump(Task task)
    {
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }
}
