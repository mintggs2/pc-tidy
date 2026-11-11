using Microsoft.Win32;
using PcTidy.Core.Model;
using Xunit;
using static PcTidy.Platform.Tests.V2Items;
using RegistryHive = PcTidy.Core.Model.RegistryHive;

namespace PcTidy.Platform.Tests;

/// <summary>
/// v3.1 fix-it tools (service.restart, wu.resetcomponents, msi.reregister, winget.install), the safe-boot BCD values and
/// DeviceSetEnabled. Every service, folder, device and program here is a fake: nothing touches this PC.
/// </summary>
public class V31ToolTests
{
    private static string Sys(string exe) => Path.Combine(Environment.SystemDirectory, exe);

    private static PlanItem Tool(string id, Dictionary<string, string>? args = null, bool admin = true) => Steps.Tool(id, args) with { NeedsAdmin = admin };

    // ---------------------------------------------------------------- catalog: exact plans and refusals

    [Fact]
    public void Every_v31_tool_resolves_and_is_honestly_not_reversible()
    {
        using var sandbox = new TestSandbox();
        var env = new FakeToolBackend(sandbox.Root);
        env.Files.Add(Path.Combine(env.LocalAppData, "Microsoft", "WindowsApps", "winget.exe"));
        foreach (var (id, args) in new (string, Dictionary<string, string>?)[]
                 {
                     (ToolIds.ServiceRestart, new() { ["name"] = "wlansvc" }), (ToolIds.WindowsUpdateReset, null), (ToolIds.MsiReregister, null),
                     (ToolIds.WingetInstall, new() { ["id"] = "Microsoft.DotNet.DesktopRuntime.8" }),
                 })
        {
            Assert.True(SystemToolCatalog.TryResolve(Tool(id, args), env, out var plan, out var problem), problem);
            Assert.False(plan.Reversible);
            Assert.False(string.IsNullOrWhiteSpace(plan.Note));
            Assert.Contains(id, SystemToolCatalog.SupportedToolIds);
        }
    }

    [Fact]
    public void Service_restart_takes_only_allow_listed_names_in_their_exact_windows_spelling()
    {
        foreach (var name in new[] { "WlanSvc", "bthserv", "BTAGService", "wuauserv", "BITS", "CryptSvc", "msiserver", "Audiosrv", "AudioEndpointBuilder" })
        {
            Assert.True(SystemToolCatalog.TryResolve(Tool(ToolIds.ServiceRestart, new() { ["name"] = name.ToUpperInvariant() }), out var plan, out _));
            Assert.Equal(name, plan.ServiceName);
            Assert.True(plan.NeedsAdmin);
            Assert.False(plan.PerUser);
        }
        Assert.Equal(9, SystemToolCatalog.RestartableServices.Count);
    }

    public static IEnumerable<object[]> RefusedV31() => new[]
    {
        new object[] { ToolIds.ServiceRestart, new Dictionary<string, string> { ["name"] = "WinDefend" } },
        new object[] { ToolIds.ServiceRestart, new Dictionary<string, string> { ["name"] = "mpssvc" } },
        new object[] { ToolIds.ServiceRestart, new Dictionary<string, string> { ["name"] = "WlanSvc & calc" } },
        new object[] { ToolIds.ServiceRestart, new Dictionary<string, string> { ["name"] = "WlanSvc", ["start"] = "disabled" } },
        new object[] { ToolIds.ServiceRestart, new Dictionary<string, string>() },
        new object[] { ToolIds.WindowsUpdateReset, new Dictionary<string, string> { ["folder"] = @"C:\Windows\System32" } },
        new object[] { ToolIds.WindowsUpdateReset, new Dictionary<string, string> { ["x"] = "" } },
        new object[] { ToolIds.MsiReregister, new Dictionary<string, string> { ["extra"] = "/i x.msi" } },
        new object[] { ToolIds.WingetInstall, new Dictionary<string, string> { ["id"] = "Contoso.App" } },
        new object[] { ToolIds.WingetInstall, new Dictionary<string, string> { ["id"] = "Microsoft.VCRedist.2015+.x64 --override x" } },
        new object[] { ToolIds.WingetInstall, new Dictionary<string, string> { ["id"] = "Microsoft.DotNet.Runtime.8" } },
        new object[] { ToolIds.WingetInstall, new Dictionary<string, string> { ["id"] = "Microsoft.DirectX", ["source"] = "msstore" } },
    };

    [Theory]
    [MemberData(nameof(RefusedV31))]
    public void Bad_v31_arguments_are_refused(string toolId, Dictionary<string, string> args)
    {
        Assert.False(SystemToolCatalog.TryResolve(Tool(toolId, args), out _, out var problem));
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    [Fact]
    public void Winget_install_uses_exact_id_the_winget_source_silent_flags_and_never_the_admin_helper()
    {
        using var sandbox = new TestSandbox();
        var env = new FakeToolBackend(sandbox.Root);
        var winget = Path.Combine(env.LocalAppData, "Microsoft", "WindowsApps", "winget.exe");
        env.Files.Add(winget);
        foreach (var id in new[] { "Microsoft.VCRedist.2015+.x64", "Microsoft.VCRedist.2015+.x86", "Microsoft.DirectX", "Microsoft.DotNet.DesktopRuntime.8", "Microsoft.DotNet.DesktopRuntime.9" })
        {
            Assert.True(SystemToolCatalog.TryResolve(Tool(ToolIds.WingetInstall, new() { ["id"] = id }, admin: false), env, out var plan, out var problem), problem);
            var command = plan.Commands.Single();
            Assert.Equal(winget, command.ExePath);
            Assert.Equal($"install --id {id} --exact --source winget --silent --disable-interactivity --accept-source-agreements --accept-package-agreements",
                string.Join(' ', command.Arguments));
            Assert.True(plan.PerUser);
            Assert.False(plan.NeedsAdmin);
            Assert.Contains("licence terms", plan.Note);
            Assert.Contains(plan.Runtime!.Name, plan.Note);
        }
    }

    [Fact]
    public async Task Winget_install_is_refused_in_the_admin_helper_and_skipped_without_winget()
    {
        using var sandbox = new TestSandbox();
        var env = new FakeToolBackend(sandbox.Root);
        var handler = new SystemToolHandler(env);
        var item = Tool(ToolIds.WingetInstall, new() { ["id"] = "Microsoft.DirectX" }, admin: false);
        var missing = await handler.ExecuteAsync(Steps.Of(item), sandbox.Context(), default);
        Assert.Equal(StepStatus.Skipped, missing.Status);
        Assert.Contains("winget", missing.Message);

        env.Files.Add(Path.Combine(env.LocalAppData, "Microsoft", "WindowsApps", "winget.exe"));
        var elevated = await handler.ExecuteAsync(Steps.Of(item), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Skipped, elevated.Status);
        Assert.Empty(env.Ran);
    }

    [Theory]
    [InlineData(0, StepStatus.Done, "Installed")]
    [InlineData(unchecked((int)0x8A150061), StepStatus.Skipped, "already installed")]
    [InlineData(unchecked((int)0x8A15002B), StepStatus.Skipped, "already installed")]
    [InlineData(unchecked((int)0x8A150109), StepStatus.Done, "Restart your PC")]
    [InlineData(3010, StepStatus.Done, "Restart your PC")]
    [InlineData(unchecked((int)0x8A15010A), StepStatus.Failed, "needs a restart")]
    [InlineData(5, StepStatus.Failed, "couldn't install")]
    public async Task Winget_install_reads_wingets_return_codes(int code, StepStatus expected, string text)
    {
        using var sandbox = new TestSandbox();
        var env = new FakeToolBackend(sandbox.Root) { ExitCode = _ => code };
        env.Files.Add(Path.Combine(env.LocalAppData, "Microsoft", "WindowsApps", "winget.exe"));
        var result = await new SystemToolHandler(env).ExecuteAsync(Steps.Of(Tool(ToolIds.WingetInstall, new() { ["id"] = "Microsoft.VCRedist.2015+.x64" }, admin: false)), sandbox.Context(), default);
        Assert.Equal(expected, result.Status);
        Assert.Contains(text, result.Message);
    }

    [Fact]
    public async Task Previews_say_what_the_user_accepts_and_why_there_is_nothing_to_undo()
    {
        using var sandbox = new TestSandbox();
        var handler = new SystemToolHandler();
        var winget = await handler.SimulateAsync(Steps.Of(Tool(ToolIds.WingetInstall, new() { ["id"] = "Microsoft.DotNet.DesktopRuntime.9" }, admin: false)), sandbox.Context(ExecutionMode.Simulate), default);
        Assert.Equal(StepStatus.Simulated, winget.Status);
        Assert.Contains("you accept Microsoft's licence terms for Microsoft .NET Desktop Runtime 9", winget.Message);
        var restart = await handler.SimulateAsync(Steps.Of(Tool(ToolIds.ServiceRestart, new() { ["name"] = "Audiosrv" })), sandbox.Context(ExecutionMode.Simulate), default);
        Assert.Contains("Nothing to undo", restart.Message);
        Assert.Contains("Audiosrv", restart.Message);
        var reset = await handler.SimulateAsync(Steps.Of(Tool(ToolIds.WindowsUpdateReset)), sandbox.Context(ExecutionMode.Simulate), default);
        Assert.Contains("SoftwareDistribution", reset.Message);
        Assert.Contains("catroot2", reset.Message);
        Assert.Contains("isn't undone", reset.Message);
        var msi = await handler.SimulateAsync(Steps.Of(Tool(ToolIds.MsiReregister)), sandbox.Context(ExecutionMode.Simulate), default);
        Assert.Contains("msiexec.exe /unregister then", msi.Message);
        Assert.Contains("/regserver", msi.Message);
    }

    [Fact]
    public async Task Real_v31_tools_only_simulate_in_the_sandbox()
    {
        using var sandbox = new TestSandbox();
        var handler = new SystemToolHandler(); // the real backend: must never run anything here
        foreach (var item in new[] { Tool(ToolIds.ServiceRestart, new() { ["name"] = "BITS" }), Tool(ToolIds.WindowsUpdateReset), Tool(ToolIds.MsiReregister) })
        {
            var r = await handler.ExecuteAsync(Steps.Of(item), sandbox.Context(elevated: true), default);
            Assert.Equal(StepStatus.Simulated, r.Status);
            Assert.False(await handler.WasCompletedAsync(Steps.Of(item), sandbox.Context(elevated: true), default));
        }
    }

    // ---------------------------------------------------------------- service.restart

    [Fact]
    public async Task Service_restart_stops_then_starts_and_brings_back_running_dependents()
    {
        using var sandbox = new TestSandbox();
        var env = new FakeToolBackend(sandbox.Root);
        env.AddService("AudioEndpointBuilder", running: true, disabled: false, "Audiosrv");
        env.AddService("Audiosrv", running: true);
        var done = await new SystemToolHandler(env).ExecuteAsync(Steps.Of(Tool(ToolIds.ServiceRestart, new() { ["name"] = "AudioEndpointBuilder" })), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Done, done.Status);
        Assert.Contains("Restarted the Windows Audio Endpoint Builder service", done.Message);
        Assert.Equal(new[] { "stop:AudioEndpointBuilder", "start:AudioEndpointBuilder", "start:Audiosrv" }, env.ServiceLog);
        Assert.True(env.Services["Audiosrv"].Running);
    }

    [Fact]
    public async Task Service_restart_refuses_a_disabled_service_and_never_changes_its_start_type()
    {
        using var sandbox = new TestSandbox();
        var env = new FakeToolBackend(sandbox.Root);
        env.AddService("wuauserv", running: false, disabled: true);
        var r = await new SystemToolHandler(env).ExecuteAsync(Steps.Of(Tool(ToolIds.ServiceRestart, new() { ["name"] = "wuauserv" })), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Skipped, r.Status);
        Assert.Contains("Disabled", r.Message);
        Assert.Contains("doesn't change startup types", r.Message);
        Assert.Empty(env.ServiceLog);
        Assert.True(env.Services["wuauserv"].Disabled);
    }

    [Fact]
    public async Task Service_restart_reports_a_missing_or_stuck_service()
    {
        using var sandbox = new TestSandbox();
        var env = new FakeToolBackend(sandbox.Root);
        var handler = new SystemToolHandler(env);
        var missing = await handler.ExecuteAsync(Steps.Of(Tool(ToolIds.ServiceRestart, new() { ["name"] = "BTAGService" })), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Skipped, missing.Status);

        env.AddService("BTAGService");
        env.WontStop.Add("BTAGService");
        var stuck = await handler.ExecuteAsync(Steps.Of(Tool(ToolIds.ServiceRestart, new() { ["name"] = "BTAGService" })), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Failed, stuck.Status);
        Assert.Contains("wasn't restarted", stuck.Message);
        Assert.DoesNotContain("as it was", stuck.Message);
        Assert.True(env.Services["BTAGService"].Running);

        env.WontStop.Clear();
        env.WontStart.Add("BTAGService");
        var noStart = await handler.ExecuteAsync(Steps.Of(Tool(ToolIds.ServiceRestart, new() { ["name"] = "BTAGService" })), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Failed, noStart.Status);
        Assert.Contains("Restarting your PC", noStart.Message);
    }

    private static Task<StepRecord> Restart(FakeToolBackend env, TestSandbox sandbox, string name, CancellationToken ct = default)
        => new SystemToolHandler(env).ExecuteAsync(Steps.Of(Tool(ToolIds.ServiceRestart, new() { ["name"] = name })), sandbox.Context(elevated: true), ct);

    [Fact]
    public async Task Service_restart_that_times_out_starts_the_dependents_windows_stopped_again()
    {
        using var sandbox = new TestSandbox();
        var env = new FakeToolBackend(sandbox.Root);
        env.AddService("AudioEndpointBuilder", running: true, disabled: false, "Audiosrv");
        env.AddService("Audiosrv", running: true);
        env.WontStop.Add("AudioEndpointBuilder");

        var r = await Restart(env, sandbox, "AudioEndpointBuilder");
        Assert.Equal(StepStatus.Failed, r.Status);
        Assert.True(env.Services["Audiosrv"].Running); // stopped by Windows first, started again by PC Tidy
        Assert.True(env.Services["AudioEndpointBuilder"].Running);
        Assert.Contains("start:Audiosrv", env.ServiceLog);
        Assert.DoesNotContain("as it was", r.Message);
        Assert.Contains("started them again", r.Message);
    }

    [Fact]
    public async Task Service_restart_whose_stop_finishes_late_starts_the_service_and_dependents_again()
    {
        using var sandbox = new TestSandbox();
        var env = new FakeToolBackend(sandbox.Root);
        env.AddService("AudioEndpointBuilder", running: true, disabled: false, "Audiosrv");
        env.AddService("Audiosrv", running: true);
        env.StopsLate.Add("AudioEndpointBuilder");

        var r = await Restart(env, sandbox, "AudioEndpointBuilder");
        Assert.Equal(StepStatus.Failed, r.Status);
        Assert.True(env.Services["AudioEndpointBuilder"].Running);
        Assert.True(env.Services["Audiosrv"].Running);
        Assert.Contains("It's running now", r.Message);
    }

    [Fact]
    public async Task Service_restart_that_windows_refuses_still_starts_everything_again_and_says_what_is_stopped()
    {
        using var sandbox = new TestSandbox();
        var env = new FakeToolBackend(sandbox.Root);
        env.AddService("BITS", running: true, disabled: false, "ContosoAgent");
        env.AddService("ContosoAgent", running: true);
        env.StopThrows.Add("BITS");

        var r = await Restart(env, sandbox, "BITS");
        Assert.Equal(StepStatus.Failed, r.Status);
        Assert.True(env.Services["ContosoAgent"].Running);
        Assert.True(env.Services["BITS"].Running);

        // It stopped late and won't start again: the message says it isn't running, and the dependent is started anyway.
        env.StopThrows.Clear();
        env.StopsLate.Add("BITS");
        env.WontStart.Add("BITS");
        var down = await Restart(env, sandbox, "BITS");
        Assert.Equal(StepStatus.Failed, down.Status);
        Assert.Contains("isn't running now", down.Message);
        Assert.Contains("Restarting your PC", down.Message);
        Assert.True(env.Services["ContosoAgent"].Running);
    }

    [Fact]
    public async Task Service_restart_brings_back_dependents_of_dependents()
    {
        using var sandbox = new TestSandbox();
        var env = new FakeToolBackend(sandbox.Root);
        env.AddService("bthserv", running: true, disabled: false, "BTAGService");
        env.AddService("BTAGService", running: true, disabled: false, "ContosoHeadset", "ContosoIdle");
        env.AddService("ContosoHeadset", running: true);
        env.AddService("ContosoIdle", running: false);

        var done = await Restart(env, sandbox, "bthserv");
        Assert.Equal(StepStatus.Done, done.Status);
        Assert.Equal(new[] { "stop:bthserv", "start:bthserv", "start:BTAGService", "start:ContosoHeadset" }, env.ServiceLog);
        Assert.All(new[] { "bthserv", "BTAGService", "ContosoHeadset" }, s => Assert.True(env.Services[s].Running));
        Assert.False(env.Services["ContosoIdle"].Running); // it wasn't running before, so it isn't started
    }

    [Fact]
    public async Task Service_restart_cancelled_during_the_stop_still_starts_everything_again()
    {
        using var sandbox = new TestSandbox();
        var env = new FakeToolBackend(sandbox.Root);
        env.AddService("WlanSvc", running: true, disabled: false, "ContosoWifiHelper");
        env.AddService("ContosoWifiHelper", running: true);
        using var cts = new CancellationTokenSource();
        env.OnStop = _ => cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Restart(env, sandbox, "WlanSvc", cts.Token));
        Assert.True(env.Services["WlanSvc"].Running);
        Assert.True(env.Services["ContosoWifiHelper"].Running);
    }

    [Fact]
    public async Task Service_restart_needs_admin_and_has_nothing_to_undo()
    {
        using var sandbox = new TestSandbox();
        var env = new FakeToolBackend(sandbox.Root);
        env.AddService("bthserv");
        var handler = new SystemToolHandler(env);
        var done = await handler.ExecuteAsync(Steps.Of(Tool(ToolIds.ServiceRestart, new() { ["name"] = "bthserv" })), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Done, done.Status);
        var undo = await handler.UndoAsync(done, sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Skipped, undo.Status);
        Assert.Contains("nothing to undo", undo.Message);
    }

    // ---------------------------------------------------------------- wu.resetcomponents

    private static FakeToolBackend WuPc(TestSandbox sandbox, bool msiRunning = false)
    {
        var env = new FakeToolBackend(sandbox.Root);
        env.AddService("BITS");
        env.AddService("wuauserv");
        env.AddService("CryptSvc");
        env.AddService("msiserver", running: msiRunning);
        env.Dirs[Path.Combine(env.WindowsDirectory, "SoftwareDistribution")] = Array.Empty<string>();
        env.Dirs[Path.Combine(env.SystemDirectory, "catroot2")] = Array.Empty<string>();
        return env;
    }

    [Fact]
    public async Task Windows_update_reset_stops_renames_never_deletes_and_restores_each_services_running_state()
    {
        using var sandbox = new TestSandbox();
        var env = WuPc(sandbox);
        var done = await new SystemToolHandler(env).ExecuteAsync(Steps.Of(Tool(ToolIds.WindowsUpdateReset) with { IsSystemChange = true }), sandbox.Context(elevated: true), default);

        Assert.Equal(StepStatus.Done, done.Status);
        var sd = Path.Combine(env.WindowsDirectory, "SoftwareDistribution");
        var cr = Path.Combine(env.SystemDirectory, "catroot2");
        Assert.Equal(new[] { (sd, sd + ".old-20260304-050607"), (cr, cr + ".old-20260304-050607") }, env.Renamed);
        Assert.Contains(sd + ".old-20260304-050607", done.Message);
        Assert.Contains(cr + ".old-20260304-050607", done.Message);
        Assert.Equal(sd + ".old-20260304-050607|" + cr + ".old-20260304-050607", done.UndoData![SystemToolHandler.RenamedFoldersData]);
        // Stopped in Microsoft's order; msiserver wasn't running, so it is neither stopped nor started.
        Assert.Equal(new[] { "stop:BITS", "stop:wuauserv", "stop:CryptSvc", "start:BITS", "start:wuauserv", "start:CryptSvc" }, env.ServiceLog);
        Assert.False(env.Services["msiserver"].Running);
        Assert.True(env.Services["wuauserv"].Running);
        Assert.Empty(env.Ran); // no program, no shell, no delete
    }

    [Fact]
    public async Task Windows_update_reset_renames_nothing_when_a_service_wont_stop_and_starts_the_others_again()
    {
        using var sandbox = new TestSandbox();
        var env = WuPc(sandbox, msiRunning: true);
        env.WontStop.Add("CryptSvc");
        var r = await new SystemToolHandler(env).ExecuteAsync(Steps.Of(Tool(ToolIds.WindowsUpdateReset)), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Failed, r.Status);
        Assert.Contains("renamed nothing", r.Message);
        Assert.Empty(env.Renamed);
        Assert.DoesNotContain("stop:msiserver", env.ServiceLog);
        Assert.All(new[] { "BITS", "wuauserv", "msiserver" }, s => Assert.True(env.Services[s].Running));
    }

    [Fact]
    public async Task Windows_update_reset_with_a_folder_in_use_keeps_it_and_says_so()
    {
        using var sandbox = new TestSandbox();
        var env = WuPc(sandbox);
        var cr = Path.Combine(env.SystemDirectory, "catroot2");
        env.LockedFolders.Add(cr);
        var r = await new SystemToolHandler(env).ExecuteAsync(Steps.Of(Tool(ToolIds.WindowsUpdateReset)), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Done, r.Status);
        Assert.Contains("catroot2 was in use", r.Message);
        Assert.True(env.Dirs.ContainsKey(cr));
        Assert.Single(env.Renamed);

        var env2 = WuPc(sandbox);
        env2.LockedFolders.Add(Path.Combine(env2.WindowsDirectory, "SoftwareDistribution"));
        env2.LockedFolders.Add(Path.Combine(env2.SystemDirectory, "catroot2"));
        var none = await new SystemToolHandler(env2).ExecuteAsync(Steps.Of(Tool(ToolIds.WindowsUpdateReset)), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Failed, none.Status);
        Assert.True(env2.Services["wuauserv"].Running); // always started again
    }

    [Fact]
    public async Task Windows_update_reset_skips_when_a_service_is_missing_and_is_never_undone()
    {
        using var sandbox = new TestSandbox();
        var env = WuPc(sandbox);
        env.Services.Remove("msiserver");
        var handler = new SystemToolHandler(env);
        var r = await handler.ExecuteAsync(Steps.Of(Tool(ToolIds.WindowsUpdateReset)), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Skipped, r.Status);
        Assert.Empty(env.ServiceLog);

        env.AddService("msiserver", running: false);
        var done = await handler.ExecuteAsync(Steps.Of(Tool(ToolIds.WindowsUpdateReset)), sandbox.Context(elevated: true), default);
        var undo = await handler.UndoAsync(done, sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Skipped, undo.Status);
        Assert.Contains("old problem back", undo.Message);
        Assert.Contains(".old-20260304-050607", undo.Message);
        Assert.Equal(2, env.Renamed.Count); // undo renamed nothing back
    }

    [Theory]
    [InlineData("")]
    [InlineData("Windows")]
    [InlineData(@"\\server\share\Windows")]
    [InlineData(@"C:\Windows\..\Users")]
    public async Task Windows_update_reset_refuses_a_windows_folder_that_isnt_a_full_local_path(string windir)
    {
        using var sandbox = new TestSandbox();
        var env = WuPc(sandbox);
        var system = env.SystemDirectory;
        env.WindowsDirectory = windir;
        env.SystemDirectory = system;
        var r = await new SystemToolHandler(env).ExecuteAsync(Steps.Of(Tool(ToolIds.WindowsUpdateReset)), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Skipped, r.Status);
        Assert.Contains("changed nothing", r.Message);
        Assert.Empty(env.ServiceLog);
        Assert.Empty(env.Renamed);
    }

    [Fact]
    public async Task Windows_update_reset_refuses_a_system32_outside_the_windows_folder()
    {
        using var sandbox = new TestSandbox();
        var env = WuPc(sandbox);
        env.SystemDirectory = Path.Combine(sandbox.Root, "Elsewhere", "System32");
        var r = await new SystemToolHandler(env).ExecuteAsync(Steps.Of(Tool(ToolIds.WindowsUpdateReset)), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Skipped, r.Status);
        Assert.Empty(env.ServiceLog);
        Assert.Empty(env.Renamed);
    }

    [Fact]
    public async Task Windows_update_reset_never_renames_a_folder_that_is_a_link()
    {
        using var sandbox = new TestSandbox();
        var env = WuPc(sandbox);
        var sd = Path.Combine(env.WindowsDirectory, "SoftwareDistribution");
        env.ReparsePoints.Add(sd);
        var r = await new SystemToolHandler(env).ExecuteAsync(Steps.Of(Tool(ToolIds.WindowsUpdateReset)), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Skipped, r.Status);
        Assert.Contains("isn't a normal folder", r.Message);
        Assert.Empty(env.ServiceLog); // checked before anything is stopped
        Assert.Empty(env.Renamed);
        Assert.True(env.Dirs.ContainsKey(sd));
    }

    [Fact]
    public async Task Windows_update_reset_starts_dependents_of_dependents_again()
    {
        using var sandbox = new TestSandbox();
        var env = WuPc(sandbox);
        env.Services["BITS"].Dependents.Add("ContosoUpdater");
        env.AddService("ContosoUpdater", running: true, disabled: false, "ContosoTray");
        env.AddService("ContosoTray", running: true);
        var r = await new SystemToolHandler(env).ExecuteAsync(Steps.Of(Tool(ToolIds.WindowsUpdateReset)), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Done, r.Status);
        Assert.True(env.Services["ContosoUpdater"].Running);
        Assert.True(env.Services["ContosoTray"].Running);
    }

    // ---------------------------------------------------------------- msi.reregister

    [Fact]
    public async Task Msi_reregister_runs_unregister_then_regserver_from_system32()
    {
        using var sandbox = new TestSandbox();
        var env = new FakeToolBackend(sandbox.Root);
        var done = await new SystemToolHandler(env).ExecuteAsync(Steps.Of(Tool(ToolIds.MsiReregister)), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Done, done.Status);
        Assert.Equal(new[] { Sys("msiexec.exe") + " /unregister", Sys("msiexec.exe") + " /regserver" }, env.Ran.Select(c => c.ToString()), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Msi_reregister_always_registers_again_even_when_unregister_fails()
    {
        using var sandbox = new TestSandbox();
        var env = new FakeToolBackend(sandbox.Root) { ExitCode = c => c.Arguments[0] == "/unregister" ? 1603 : 0 };
        var done = await new SystemToolHandler(env).ExecuteAsync(Steps.Of(Tool(ToolIds.MsiReregister)), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Done, done.Status);
        Assert.Equal(2, env.Ran.Count);

        var env2 = new FakeToolBackend(sandbox.Root) { ExitCode = c => c.Arguments[0] == "/regserver" ? 1603 : 0 };
        var failed = await new SystemToolHandler(env2).ExecuteAsync(Steps.Of(Tool(ToolIds.MsiReregister)), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Failed, failed.Status);
        Assert.Contains("msiexec /regserver", failed.Message);
    }

    // ---------------------------------------------------------------- safe-boot leftovers (bcdedit.deletevalue)

    [Theory]
    [InlineData("safeboot", 0x25000080u, false, 0ul, "Minimal")]
    [InlineData("safeboot", 0x25000080u, false, 1ul, "Network")]
    [InlineData("safebootalternateshell", 0x26000081u, true, 1ul, "Yes")]
    public async Task Safe_boot_flags_are_removed_from_current_and_undo_puts_the_exact_value_back(string name, uint element, bool isBool, ulong value, string undoText)
    {
        Assert.Equal(element, SystemToolCatalog.BcdRemovable[name]);
        using var sandbox = new TestSandbox();
        var env = new FakeToolBackend(sandbox.Root);
        env.Bcd[element] = new BcdElementValue(isBool, value);
        env.OnRun = c => { if (c.Arguments.Contains("/deletevalue")) env.Bcd.Remove(element); };
        var handler = new SystemToolHandler(env);
        var item = Steps.Tool(ToolIds.BcdDeleteValue, new() { ["name"] = name }) with { Reversible = true };

        var done = await handler.ExecuteAsync(Steps.Of(item), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Done, done.Status);
        Assert.Equal(new[] { "/deletevalue", "{current}", name }, env.Ran.Single().Arguments);
        Assert.Equal(Sys("bcdedit.exe"), env.Ran.Single().ExePath, ignoreCase: true);
        Assert.Contains("Restart your PC", done.Message);

        var undone = await handler.UndoAsync(done, sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Undone, undone.Status);
        Assert.Equal(new[] { "/set", "{current}", name, undoText }, env.Ran.Last().Arguments);
    }

    [Fact]
    public async Task Safe_boot_removal_preview_and_undo_say_the_next_restart_starts_safe_mode_again()
    {
        using var sandbox = new TestSandbox();
        var env = new FakeToolBackend(sandbox.Root);
        env.Bcd[0x25000080u] = new BcdElementValue(false, 0);
        env.OnRun = c => { if (c.Arguments.Contains("/deletevalue")) env.Bcd.Remove(0x25000080u); };
        var handler = new SystemToolHandler(env);
        var item = Steps.Tool(ToolIds.BcdDeleteValue, new() { ["name"] = "safeboot" }) with { Reversible = true };

        var preview = await handler.SimulateAsync(Steps.Of(item), sandbox.Context(ExecutionMode.Simulate), default);
        Assert.Contains("Windows starts in Safe Mode again", preview.Message);
        var done = await handler.ExecuteAsync(Steps.Of(item), sandbox.Context(elevated: true), default);
        Assert.Contains("Safe Mode again", done.Message);
        var undone = await handler.UndoAsync(done, sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Undone, undone.Status);
        Assert.Contains("the next time you restart, Windows starts in Safe Mode", undone.Message);

        // The real backend in the sandbox: the undo preview says it too.
        var real = await new SystemToolHandler().UndoAsync(done, sandbox.Context(elevated: true), default);
        Assert.Contains("Safe Mode again", real.Message);

        // Other boot settings don't get the warning.
        var other = await handler.SimulateAsync(Steps.Of(Steps.Tool(ToolIds.BcdDeleteValue, new() { ["name"] = "useplatformclock" }) with { Reversible = true }), sandbox.Context(ExecutionMode.Simulate), default);
        Assert.DoesNotContain("Safe Mode", other.Message);
        Assert.Contains("Command Prompt", SystemToolCatalog.SafeBootUndoWarning("safebootalternateshell"));
        Assert.Equal("", SystemToolHandler.SafeBootRestoredNote("safebootalternateshell", "No"));
    }

    // ---------------------------------------------------------------- DeviceSetEnabled

    private const string Webcam = @"USB\VID_1234&PID_ABCD&MI_00\6&1A2B3C4D&0&0000";

    private static PlanItem Device(string id = Webcam, bool? enable = true)
        => Item(ActionKind.DeviceSetEnabled, new() { ["instanceId"] = id }, enable: enable, admin: true) with { IsSystemChange = true };

    private static FakeDevices DisabledCamera(string className = "Camera")
    {
        var fake = new FakeDevices();
        fake.Devices[Webcam] = new DeviceFacts(true, true, DeviceFacts.ProblemDisabled, className, null);
        return fake;
    }

    [Fact]
    public async Task Device_enable_records_the_disabled_state_and_undo_disables_it_again()
    {
        using var sandbox = new TestSandbox();
        var fake = DisabledCamera();
        var handler = new DeviceSetEnabledHandler(fake);

        var done = await handler.ExecuteAsync(Steps.Of(Device()), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Done, done.Status);
        Assert.Equal("/enable-device " + Webcam, fake.Ran.Single());
        Assert.Equal("disabled", done.UndoData![DeviceSetEnabledHandler.PriorStateData]);
        Assert.Equal("22", done.UndoData[DeviceSetEnabledHandler.PriorProblemData]);
        Assert.False(fake.Devices[Webcam].Disabled);
        Assert.True(await handler.WasCompletedAsync(done, sandbox.Context(elevated: true), default));

        // Through the journal format, as History and Rescue load it.
        var undone = await handler.UndoAsync(ElevationProtocol.Deserialize<StepRecord>(ElevationProtocol.Serialize(done)), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Undone, undone.Status);
        Assert.Equal("/disable-device " + Webcam, fake.Ran.Last());
        Assert.True(fake.Devices[Webcam].Disabled);
        Assert.DoesNotContain(fake.Ran, r => r.Contains("/force"));
    }

    [Fact]
    public async Task Device_disable_is_never_a_plan_step_only_an_undo()
    {
        using var sandbox = new TestSandbox();
        var fake = DisabledCamera();
        fake.Devices[Webcam] = fake.Devices[Webcam] with { HasProblem = false, ProblemCode = 0 };
        var handler = new DeviceSetEnabledHandler(fake);
        foreach (var enable in new bool?[] { false, null })
        {
            var r = await handler.ExecuteAsync(Steps.Of(Device(enable: enable)), sandbox.Context(elevated: true), default);
            Assert.Equal(StepStatus.Skipped, r.Status);
        }
        Assert.Empty(fake.Ran);

        // A finished step without the recorded "it was disabled" state can't be undone into a disable.
        var forged = Steps.Of(Device(), status: StepStatus.Done);
        var undo = await handler.UndoAsync(forged, sandbox.Context(elevated: true), default);
        Assert.NotEqual(StepStatus.Undone, undo.Status);
        Assert.Empty(fake.Ran);
    }

    [Theory]
    [InlineData("DiskDrive")]
    [InlineData("SCSIAdapter")]
    [InlineData("hdc")]
    [InlineData("System")]
    [InlineData("Firmware")]
    [InlineData("Processor")]
    [InlineData("SecurityDevices")]
    public async Task Device_enable_never_touches_boot_critical_classes(string className)
    {
        using var sandbox = new TestSandbox();
        var fake = DisabledCamera(className);
        var r = await new DeviceSetEnabledHandler(fake).ExecuteAsync(Steps.Of(Device()), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Skipped, r.Status);
        Assert.Empty(fake.Ran);
    }

    [Fact]
    public async Task Device_enable_by_class_guid_is_refused_too()
    {
        using var sandbox = new TestSandbox();
        var fake = new FakeDevices();
        fake.Devices[Webcam] = new DeviceFacts(true, true, 22, null, new Guid("4d36e967-e325-11ce-bfc1-08002be10318"));
        var r = await new DeviceSetEnabledHandler(fake).ExecuteAsync(Steps.Of(Device()), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Skipped, r.Status);
        Assert.Empty(fake.Ran);
    }

    [Theory]
    [InlineData("")]
    [InlineData("USB")]
    [InlineData(@"USB\..\ROOT")]
    [InlineData(@"USB\VID_1234\")]
    [InlineData(@"USB\VID 1234\0")]
    [InlineData("USB\\VID_1234\\0\" /force")]
    [InlineData(@"USB\VID_1234\0;calc")]
    public async Task Device_enable_refuses_malformed_ids(string id)
    {
        Assert.False(DeviceSetEnabledHandler.IsWellFormedInstanceId(id));
        using var sandbox = new TestSandbox();
        var fake = DisabledCamera();
        var r = await new DeviceSetEnabledHandler(fake).ExecuteAsync(Steps.Of(Device(id)), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Skipped, r.Status);
        Assert.Empty(fake.Ran);
    }

    [Fact]
    public async Task Device_enable_leaves_working_unplugged_and_otherwise_broken_devices_alone()
    {
        using var sandbox = new TestSandbox();
        var fake = new FakeDevices();
        var handler = new DeviceSetEnabledHandler(fake);
        Assert.Equal(StepStatus.Skipped, (await handler.ExecuteAsync(Steps.Of(Device()), sandbox.Context(elevated: true), default)).Status);
        fake.Devices[Webcam] = new DeviceFacts(true, false, 0, "Camera", null);
        Assert.Contains("already on", (await handler.ExecuteAsync(Steps.Of(Device()), sandbox.Context(elevated: true), default)).Message);
        fake.Devices[Webcam] = new DeviceFacts(true, true, 28, "Camera", null);
        Assert.Contains("different problem", (await handler.ExecuteAsync(Steps.Of(Device()), sandbox.Context(elevated: true), default)).Message);
        fake.Devices[Webcam] = new DeviceFacts(false, false, 0, "Camera", null);
        Assert.Contains("isn't connected", (await handler.ExecuteAsync(Steps.Of(Device()), sandbox.Context(elevated: true), default)).Message);
        Assert.Empty(fake.Ran);
    }

    [Fact]
    public async Task Device_enable_needs_windows_10_2004_and_admin()
    {
        using var sandbox = new TestSandbox();
        var fake = DisabledCamera();
        fake.WindowsBuild = 18363;
        var old = await new DeviceSetEnabledHandler(fake).ExecuteAsync(Steps.Of(Device()), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Skipped, old.Status);
        Assert.Contains("2004", old.Message);
        Assert.Empty(fake.Ran);
        Assert.Contains(ActionKind.DeviceSetEnabled, ElevatedBatchExecutor.AdminCapableKinds);
    }

    [Fact]
    public async Task Device_enable_fails_honestly_when_windows_refuses_and_reports_a_restart()
    {
        using var sandbox = new TestSandbox();
        var refused = DisabledCamera();
        refused.ExitCode = 1;
        var r = await new DeviceSetEnabledHandler(refused).ExecuteAsync(Steps.Of(Device()), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Failed, r.Status);

        var stays = DisabledCamera();
        stays.Applies = false;
        Assert.Equal(StepStatus.Failed, (await new DeviceSetEnabledHandler(stays).ExecuteAsync(Steps.Of(Device()), sandbox.Context(elevated: true), default)).Status);

        var restart = DisabledCamera();
        restart.ExitCode = 3010;
        var done = await new DeviceSetEnabledHandler(restart).ExecuteAsync(Steps.Of(Device()), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Done, done.Status);
        Assert.Contains("Restart your PC", done.Message);
    }

    [Fact]
    public async Task A_device_step_settled_after_a_crash_says_it_cant_be_undone_automatically_and_never_turns_the_device_off()
    {
        using var sandbox = new TestSandbox();
        var fake = DisabledCamera();
        fake.Devices[Webcam] = fake.Devices[Webcam] with { HasProblem = false, ProblemCode = 0 }; // pnputil ran, then PC Tidy stopped
        var handler = new DeviceSetEnabledHandler(fake);
        var helper = new ElevatedBatchExecutor(new PcTidy.Core.IActionHandler[] { handler }, () => sandbox.Context(elevated: true));

        var settled = (await helper.SettleAsync("run-d1", new[] { Steps.Of(Device(), 0, StepStatus.Started) }, default)).Single();
        Assert.Equal(StepStatus.Done, settled.Status);
        Assert.Contains("can't undo this one automatically", settled.Message);
        Assert.Null(settled.Undo(DeviceSetEnabledHandler.PriorStateData));

        var undo = await handler.UndoAsync(settled, sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Skipped, undo.Status);
        Assert.Contains("can't turn it off again automatically", undo.Message);
        Assert.Contains("Device Manager", undo.Message);
        Assert.Empty(fake.Ran);
        Assert.False(fake.Devices[Webcam].Disabled);

        // A step that ran normally still says nothing extra when settled, and its undo works.
        var fake2 = DisabledCamera();
        var handler2 = new DeviceSetEnabledHandler(fake2);
        var done = await handler2.ExecuteAsync(Steps.Of(Device()), sandbox.Context(elevated: true), default);
        Assert.Null(((ISettledStepNote)handler2).NoteWhenSettledDone(done));
    }

    [Fact]
    public void Device_class_lists_include_security_accelerators()
    {
        Assert.Contains("SecurityAccelerator", DeviceSetEnabledHandler.ProtectedClasses);
        Assert.Contains(new Guid("268c95a1-edfe-11d3-95c3-0010dc4050a5"), DeviceSetEnabledHandler.ProtectedClassGuids);
        Assert.True(DeviceSetEnabledHandler.IsProtected(new DeviceFacts(true, true, 22, null, new Guid("268c95a1-edfe-11d3-95c3-0010dc4050a5"))));
    }

    [Fact]
    public async Task Device_undo_is_skipped_when_the_device_is_off_already_or_gone()
    {
        using var sandbox = new TestSandbox();
        var fake = DisabledCamera();
        var handler = new DeviceSetEnabledHandler(fake);
        var done = await handler.ExecuteAsync(Steps.Of(Device()), sandbox.Context(elevated: true), default);
        fake.Devices[Webcam] = fake.Devices[Webcam] with { HasProblem = true, ProblemCode = 22 };
        Assert.Equal(StepStatus.Skipped, (await handler.UndoAsync(done, sandbox.Context(elevated: true), default)).Status);
        fake.Devices.Remove(Webcam);
        Assert.Equal(StepStatus.Skipped, (await handler.UndoAsync(done, sandbox.Context(elevated: true), default)).Status);
        Assert.Single(fake.Ran); // only the enable
    }

    // ---------------------------------------------------------------- patterned registry allowance: undo restores exactly

    [Collection("Registry")]
    public class PatternedRegistryUndo
    {
        // A value named after a program's path, like per-app GPU preferences (written only under HKCU\Software\PCTidyTest).
        private const string ExeName = @"C:\Games\Contoso Racer\racer.exe";

        private static PcTidy.Catalog.RegistryAllowance AdapterLike(TestRegistryKey reg) => new("test.adapter-like", RegistryHive.CurrentUser, reg.Sub("0000"),
            "PnPCapabilities", RegistryKind.DWord, new[] { "24" })
        {
            KeyPathPattern = "^" + System.Text.RegularExpressions.Regex.Escape(reg.KeyPath) + @"\\\d{4}$",
        };

        [Fact]
        public async Task A_pattern_only_key_is_never_created_by_a_change_or_its_undo()
        {
            using var sandbox = new TestSandbox();
            using var reg = new TestRegistryKey();
            using (reg.Open()) { }
            using var _ = PcTidy.Catalog.RegistryCatalog.WithTestAllowances(AdapterLike(reg));
            var handler = new RegistryHandler();

            // A subkey that isn't there: skipped, and nothing is created.
            var missing = await handler.ExecuteAsync(Steps.Of(Steps.RegSet(reg.Sub("0007"), "PnPCapabilities", RegistryKind.DWord, "24")), sandbox.Context(), default);
            Assert.Equal(StepStatus.Skipped, missing.Status);
            Assert.Contains("didn't create it", missing.Message);
            Assert.False(TestRegistryKey.Exists(reg.Sub("0007")));

            // A subkey that is there: written, and undone exactly.
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(reg.Sub("0003"))) k.SetValue("PnPCapabilities", 0, RegistryValueKind.DWord);
            var done = await handler.ExecuteAsync(Steps.Of(Steps.RegSet(reg.Sub("0003"), "PnPCapabilities", RegistryKind.DWord, "24")), sandbox.Context(), default);
            Assert.Equal(StepStatus.Done, done.Status);

            // The adapter's key went away before the undo: the undo doesn't bring it back.
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(reg.Sub("0003"));
            var undo = await handler.UndoAsync(done, sandbox.Context(), default);
            Assert.Equal(StepStatus.Skipped, undo.Status);
            Assert.False(TestRegistryKey.Exists(reg.Sub("0003")));

            // Without the pattern (an ordinary key), a missing key is still created as before.
            var plain = await handler.ExecuteAsync(Steps.Of(Steps.RegSet(reg.Sub(@"Plain\Sub"), "V", RegistryKind.DWord, "1")), sandbox.Context(), default);
            Assert.Equal(StepStatus.Done, plain.Status);
            Assert.True(TestRegistryKey.Exists(reg.Sub(@"Plain\Sub")));
        }

        [Fact]
        public async Task A_catalog_precondition_on_the_current_data_is_checked_right_before_the_change()
        {
            using var sandbox = new TestSandbox();
            using var reg = new TestRegistryKey();
            // Like IFEO CpuPriorityClass: removed only while it is 4 (Realtime).
            var onlyRealtime = new PcTidy.Catalog.RegistryAllowance("test.only-realtime", RegistryHive.CurrentUser, reg.KeyPath, "CpuPriorityClass",
                RegistryKind.DWord, Array.Empty<string>()) { OnlyWhenDataIs = new[] { "4" } };
            using var _ = PcTidy.Catalog.RegistryCatalog.WithTestAllowances(onlyRealtime);
            var handler = new RegistryHandler();
            var delete = new PlanItem
            {
                Kind = ActionKind.RegistryDeleteValue, FeatureId = "test", Description = "test",
                Registry = new RegistryValueSpec { Hive = RegistryHive.CurrentUser, KeyPath = reg.KeyPath, ValueName = "CpuPriorityClass", Kind = RegistryKind.DWord },
            };

            using (var k = reg.Open()) k.SetValue("CpuPriorityClass", 3, RegistryValueKind.DWord); // High, not Realtime: the user's own choice
            var left = await handler.ExecuteAsync(Steps.Of(delete), sandbox.Context(), default);
            Assert.Equal(StepStatus.Skipped, left.Status);
            Assert.Contains("left alone", left.Message);
            using (var k = reg.Open()) Assert.Equal(3, k.GetValue("CpuPriorityClass"));

            using (var k = reg.Open()) k.SetValue("CpuPriorityClass", 4, RegistryValueKind.DWord);
            var removed = await handler.ExecuteAsync(Steps.Of(delete), sandbox.Context(), default);
            Assert.Equal(StepStatus.Done, removed.Status);
            using (var k = reg.Open()) Assert.Null(k.GetValue("CpuPriorityClass"));
        }

        [Fact]
        public async Task Undo_deletes_a_value_that_was_absent()
        {
            using var sandbox = new TestSandbox();
            using var reg = new TestRegistryKey();
            var handler = new RegistryHandler();
            var done = await handler.ExecuteAsync(Steps.Of(Steps.RegSet(reg.KeyPath, ExeName, RegistryKind.String, "GpuPreference=2;")), sandbox.Context(), default);
            Assert.Equal(StepStatus.Done, done.Status);
            Assert.False(done.RegistryPrior!.ValueExisted);
            var undone = await handler.UndoAsync(done, sandbox.Context(), default);
            Assert.Equal(StepStatus.Undone, undone.Status);
            using var k = reg.Open();
            Assert.Null(k.GetValue(ExeName));
        }

        [Fact]
        public async Task Undo_restores_the_exact_recorded_value()
        {
            using var sandbox = new TestSandbox();
            using var reg = new TestRegistryKey();
            using (var k = reg.Open()) k.SetValue(ExeName, "GpuPreference=1;", RegistryValueKind.String);
            var handler = new RegistryHandler();
            var done = await handler.ExecuteAsync(Steps.Of(Steps.RegSet(reg.KeyPath, ExeName, RegistryKind.String, "GpuPreference=2;")), sandbox.Context(), default);
            Assert.Equal("GpuPreference=1;", done.RegistryPrior!.Data);
            var undone = await handler.UndoAsync(done, sandbox.Context(), default);
            Assert.Equal(StepStatus.Undone, undone.Status);
            using (var k = reg.Open())
            {
                Assert.Equal("GpuPreference=1;", k.GetValue(ExeName));
                Assert.Equal(RegistryValueKind.String, k.GetValueKind(ExeName));
            }

            // A DWORD before-value on a "network adapter"-style key is put back as the same DWORD.
            using (var k = reg.Open()) k.SetValue("PnPCapabilities", 0, RegistryValueKind.DWord);
            var dword = await handler.ExecuteAsync(Steps.Of(Steps.RegSet(reg.KeyPath, "PnPCapabilities", RegistryKind.DWord, "24")), sandbox.Context(), default);
            await handler.UndoAsync(dword, sandbox.Context(), default);
            using (var k = reg.Open())
            {
                Assert.Equal(0, k.GetValue("PnPCapabilities"));
                Assert.Equal(RegistryValueKind.DWord, k.GetValueKind("PnPCapabilities"));
            }
        }
    }
}
