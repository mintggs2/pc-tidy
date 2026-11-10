using Microsoft.Win32;
using PcTidy.Core;
using PcTidy.Core.Model;
using RegistryHive = PcTidy.Core.Model.RegistryHive;

namespace PcTidy.Platform.Tests;

/// <summary>A fresh sandbox PC under testbed\secfix\platform\&lt;guid&gt;, deleted afterwards.</summary>
public sealed class TestSandbox : IDisposable
{
    public static readonly string TestbedRoot = Path.Combine(FindCodeRoot(), "testbed", "secfix", "platform");

    private static string FindCodeRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PcTidy.sln"))) return dir.FullName;
        throw new InvalidOperationException("PcTidy.sln not found above the test folder.");
    }

    public string Root { get; }
    public SandboxKnownLocations Locations { get; }

    public TestSandbox(SandboxProfile? profile = null)
    {
        Root = Path.Combine(TestbedRoot, Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Root);
        Locations = new SandboxKnownLocations(Root, profile ?? SandboxProfile.Minimal);
        Directory.CreateDirectory(Locations.DataRoot);
    }

    public HandlerContext Context(ExecutionMode mode = ExecutionMode.Real, bool elevated = false, ISafetyGuard? safety = null) => new()
    {
        RunId = "20260101-000000-test01",
        Locations = Locations,
        Safety = safety ?? new FakeSafety(),
        InUse = new NeverInUse(),
        Quarantine = new UnusedQuarantine(),
        Settings = new AppSettings { DataRoot = Locations.DataRoot },
        Mode = mode,
        IsElevated = elevated,
    };

    public void Dispose()
    {
        for (var i = 0; i < 5; i++)
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
                return;
            }
            catch (IOException) { Thread.Sleep(100); }
            catch (UnauthorizedAccessException) { Thread.Sleep(100); }
        }
    }
}

/// <summary>
/// A private key under HKCU\Software\PCTidyTest\secfix\platform\&lt;guid&gt; (the only registry area tests may write).
/// Deleted afterwards, together with the secfix\platform / secfix / PCTidyTest parents when they are left empty.
/// </summary>
public sealed class TestRegistryKey : IDisposable
{
    public const string AreaKey = @"Software\PCTidyTest\secfix\platform";
    public string KeyPath { get; } = $@"{AreaKey}\{Guid.NewGuid():N}";

    public RegistryKey Open(bool create = true)
        => create ? Registry.CurrentUser.CreateSubKey(KeyPath, writable: true) : Registry.CurrentUser.OpenSubKey(KeyPath, writable: true)!;

    public string Sub(string relative) => $@"{KeyPath}\{relative}";

    public static bool Exists(string keyPath)
    {
        using var k = Registry.CurrentUser.OpenSubKey(keyPath);
        return k is not null;
    }

    public void Dispose()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false); } catch { }
        DeleteIfEmpty(AreaKey);
        DeleteIfEmpty(@"Software\PCTidyTest\secfix");
        DeleteIfEmpty(SandboxPolicy.TestRegistryRoot);
    }

    private static void DeleteIfEmpty(string keyPath)
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(keyPath))
                if (k is null || k.SubKeyCount > 0 || k.ValueCount > 0) return;
            Registry.CurrentUser.DeleteSubKey(keyPath, throwOnMissingSubKey: false);
        }
        catch
        {
            // Another test (or another area's tests) is still using it.
        }
    }
}

public sealed class FakeSafety : ISafetyGuard
{
    public Func<PlanItem, SafetyVerdict> Verdict { get; set; } = _ => SafetyVerdict.Ok;
    public List<PlanItem> Checked { get; } = new();
    public SafetyVerdict ClassifyPath(string path) => SafetyVerdict.Ok;
    public SafetyVerdict Check(PlanItem item) { lock (Checked) Checked.Add(item); return Verdict(item); }
    public bool IsInCloudRoot(string path) => false;
    public bool IsInsideProjectOrAppFolder(string path, out string? why) { why = null; return false; }
}

public sealed class NeverInUse : IInUseChecker
{
    public Task<IReadOnlyList<InUseInfo>> CheckAsync(IReadOnlyList<string> paths, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<InUseInfo>>(paths.Select(p => new InUseInfo(p, false, Array.Empty<string>())).ToList());
    public bool IsLocked(string path) => false;
}

public sealed class UnusedQuarantine : IQuarantineStore
{
    public Task<QuarantineEntry> QuarantineAsync(string path, string runId, CancellationToken ct) => throw new NotSupportedException();
    public Task<string> RestoreAsync(QuarantineEntry entry, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<QuarantineEntry>> ListAsync(string? runId, CancellationToken ct) => Task.FromResult<IReadOnlyList<QuarantineEntry>>(Array.Empty<QuarantineEntry>());
    public Task PurgeRunAsync(string runId, CancellationToken ct) => Task.CompletedTask;
    public Task<long> TotalBytesAsync(CancellationToken ct) => Task.FromResult(0L);
}

/// <summary>Records what it was asked to do and changes nothing.</summary>
public sealed class RecordingHandler(params ActionKind[] kinds) : IActionHandler
{
    public List<(string Op, StepRecord Step, HandlerContext Ctx)> Calls { get; } = new();
    public Func<StepRecord, CancellationToken, Task>? BeforeExecute { get; set; }

    public bool CanHandle(PlanItem item) => kinds.Contains(item.Kind);

    public async Task<StepRecord> ExecuteAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        lock (Calls) Calls.Add(("execute", step, ctx));
        if (BeforeExecute is not null) await BeforeExecute(step, ct);
        return step with { Status = StepStatus.Done, Message = "done by fake", UndoData = new() { ["k"] = "v" } };
    }

    public Task<StepRecord> SimulateAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        lock (Calls) Calls.Add(("simulate", step, ctx));
        return Task.FromResult(step with { Status = StepStatus.Simulated, Message = "simulated by fake" });
    }

    public Task<StepRecord> UndoAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        lock (Calls) Calls.Add(("undo", step, ctx));
        return Task.FromResult(step with { Status = StepStatus.Undone, Message = "undone by fake" });
    }

    public Task<bool> WasCompletedAsync(StepRecord step, HandlerContext ctx, CancellationToken ct) => Task.FromResult(false);
}

public static class Steps
{
    public static StepRecord Of(PlanItem item, int index = 0, StepStatus status = StepStatus.Pending) => new() { Index = index, Item = item, Status = status };

    public static PlanItem RegSet(string keyPath, string? name, RegistryKind kind, string? data, RegistryHive hive = RegistryHive.CurrentUser) => new()
    {
        Kind = ActionKind.RegistrySetValue,
        FeatureId = "test",
        Description = "test",
        Registry = new RegistryValueSpec { Hive = hive, KeyPath = keyPath, ValueName = name, Kind = kind, Data = data },
    };

    public static PlanItem RegDeleteValue(string keyPath, string name) => new()
    {
        Kind = ActionKind.RegistryDeleteValue,
        FeatureId = "test",
        Description = "test",
        Registry = new RegistryValueSpec { Hive = RegistryHive.CurrentUser, KeyPath = keyPath, ValueName = name },
    };

    public static PlanItem RegDeleteKey(string keyPath, RegistryHive hive = RegistryHive.CurrentUser) => new()
    {
        Kind = ActionKind.RegistryDeleteKey,
        FeatureId = "test",
        Description = "test",
        Registry = new RegistryValueSpec { Hive = hive, KeyPath = keyPath },
    };

    public static PlanItem Tool(string toolId, Dictionary<string, string>? args = null) => new()
    {
        Kind = ActionKind.RunSystemTool,
        FeatureId = "test",
        Description = "test",
        ToolId = toolId,
        Args = args,
        NeedsAdmin = true,
        Reversible = false,
    };
}
