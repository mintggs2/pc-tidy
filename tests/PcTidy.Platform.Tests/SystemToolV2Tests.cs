using PcTidy.Core;
using PcTidy.Core.Model;
using Xunit;

namespace PcTidy.Platform.Tests;

/// <summary>v2 tool ids: plan → exact command mapping, argument checks, prior state and undo, all with a fake backend that runs nothing.</summary>
public class SystemToolV2Tests
{
    private static string Sys(string exe) => Path.Combine(Environment.SystemDirectory, exe);

    public static IEnumerable<object[]> System32Mappings() => new[]
    {
        new object[] { ToolIds.SfcScan, null!, "sfc.exe", "/scannow" },
        new object[] { ToolIds.DismRestoreHealth, null!, "Dism.exe", "/Online /English /Cleanup-Image /RestoreHealth" },
        new object[] { ToolIds.WinsockReset, new Dictionary<string, string> { ["needsRestart"] = "true" }, "netsh.exe", "winsock reset" },
        new object[] { ToolIds.PnpDeleteDriver, new Dictionary<string, string> { ["inf"] = "OEM42.inf" }, "pnputil.exe", "/delete-driver oem42.inf" },
        new object[] { ToolIds.PnpRemoveDevice, new Dictionary<string, string> { ["instanceId"] = @"USB\VID_1234&PID_5678\0001" }, "pnputil.exe", @"/remove-device USB\VID_1234&PID_5678\0001" },
        new object[] { ToolIds.ShadowStorageResize, new Dictionary<string, string> { ["percent"] = "8", ["drive"] = "C:\\" }, "vssadmin.exe", "Resize ShadowStorage /For=C: /On=C: /MaxSize=8%" },
        new object[] { ToolIds.BcdDeleteValue, new Dictionary<string, string> { ["name"] = "useplatformclock" }, "bcdedit.exe", "/deletevalue {current} useplatformclock" },
        new object[] { ToolIds.NetshAutotuningNormal, null!, "netsh.exe", "interface tcp set global autotuninglevel=normal" },
        new object[] { ToolIds.WslShutdown, null!, "wsl.exe", "--shutdown" },
        new object[] { ToolIds.WslSetSparse, new Dictionary<string, string> { ["distro"] = "Ubuntu-22.04" }, "wsl.exe", "--manage Ubuntu-22.04 --set-sparse true" },
        new object[] { ToolIds.CipherWipeFreeSpace, new Dictionary<string, string> { ["drive"] = "D:\\" }, "cipher.exe", @"/w:D:\" },
        new object[] { ToolIds.PnpExportDrivers, new Dictionary<string, string> { ["folder"] = @"C:\Data\backups\drivers\2026" }, "pnputil.exe", @"/export-driver * C:\Data\backups\drivers\2026" },
        new object[] { ToolIds.PnpAddDrivers, new Dictionary<string, string> { ["folder"] = @"C:\Data\backups\drivers\2026" }, "pnputil.exe", @"/add-driver C:\Data\backups\drivers\2026\*.inf /subdirs /install" },
        new object[] { ToolIds.CompactLzx, new Dictionary<string, string> { ["folder"] = @"C:\Games\Old" }, "compact.exe", @"/c /s:C:\Games\Old /i /q /exe:lzx" },
    };

    [Theory]
    [MemberData(nameof(System32Mappings))]
    public void Tools_map_to_exact_system32_commands(string toolId, Dictionary<string, string>? args, string exe, string arguments)
    {
        Assert.True(SystemToolCatalog.TryResolve(Steps.Tool(toolId, args), out var plan, out var problem), problem);
        var command = plan.Commands.Single();
        Assert.Equal(Sys(exe), command.ExePath, ignoreCase: true);
        Assert.Equal(arguments, string.Join(' ', command.Arguments));
        Assert.DoesNotContain(command.Arguments, a => a.Contains("/force", StringComparison.OrdinalIgnoreCase));
    }

    public static IEnumerable<object[]> Refused() => new[]
    {
        new object[] { ToolIds.WingetUpgrade, new Dictionary<string, string> { ["id"] = "Contoso.App --override x" } },
        new object[] { ToolIds.WingetUpgrade, new Dictionary<string, string> { ["id"] = "Contoso.App", ["acceptAgreements"] = "yes" } },
        new object[] { ToolIds.PnpDeleteDriver, new Dictionary<string, string> { ["inf"] = "C:\\Windows\\INF\\usb.inf" } },
        new object[] { ToolIds.PnpDeleteDriver, new Dictionary<string, string> { ["inf"] = "oem1.inf", ["force"] = "/force" } },
        new object[] { ToolIds.ShadowStorageResize, new Dictionary<string, string> { ["percent"] = "4" } },
        new object[] { ToolIds.BcdDeleteValue, new Dictionary<string, string> { ["name"] = "nx" } },
        new object[] { ToolIds.BcdDeleteValue, new Dictionary<string, string> { ["name"] = "hypervisorlaunchtype" } },
        new object[] { ToolIds.DockerPrune, new Dictionary<string, string> { ["what"] = "volumes" } },
        new object[] { ToolIds.DevCacheClean, new Dictionary<string, string> { ["tool"] = "cargo" } },
        new object[] { ToolIds.PnpAddDrivers, new Dictionary<string, string> { ["folder"] = @"\\server\drivers" } },
        new object[] { ToolIds.PnpExportDrivers, new Dictionary<string, string> { ["folder"] = @"\\.\GLOBALROOT\Device\HarddiskVolume1\x" } },
        new object[] { ToolIds.CompactLzx, new Dictionary<string, string> { ["folder"] = @"\\?\C:\Windows" } },
        new object[] { ToolIds.WindowsUpdateInstallDrivers, new Dictionary<string, string> { ["updateIds"] = "abc;calc" } },
        new object[] { ToolIds.WslSetSparse, new Dictionary<string, string> { ["distro"] = "Ubuntu; rm" } },
        new object[] { ToolIds.CipherWipeFreeSpace, new Dictionary<string, string> { ["drive"] = "C:\\Windows" } },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void Bad_arguments_are_refused(string toolId, Dictionary<string, string> args)
    {
        Assert.False(SystemToolCatalog.TryResolve(Steps.Tool(toolId, args), out _, out var problem));
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    [Fact]
    public void Every_core_tool_id_is_supported()
    {
        var ids = typeof(ToolIds).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToList();
        Assert.All(ids, id => Assert.Contains(id, SystemToolCatalog.SupportedToolIds));
    }

    [Fact]
    public void Winget_accepts_agreements_only_when_asked()
    {
        using var sandbox = new TestSandbox();
        var env = new FakeToolBackend(sandbox.Root);
        var winget = Path.Combine(env.LocalAppData, "Microsoft", "WindowsApps", "winget.exe");
        env.Files.Add(winget);
        Assert.True(SystemToolCatalog.TryResolve(Steps.Tool(ToolIds.WingetUpgrade, new() { ["id"] = "Contoso.App" }), env, out var plain, out _));
        Assert.Equal("upgrade --id Contoso.App --exact --silent --disable-interactivity", string.Join(' ', plain.Commands[0].Arguments));
        Assert.True(plain.PerUser);
        Assert.True(SystemToolCatalog.TryResolve(Steps.Tool(ToolIds.WingetUpgrade, new() { ["id"] = "Contoso.App", ["acceptAgreements"] = "true" }), env, out var accepted, out _));
        Assert.EndsWith("--accept-source-agreements --accept-package-agreements", string.Join(' ', accepted.Commands[0].Arguments));
    }

    [Fact]
    public void Defender_scan_uses_the_newest_platform_folder()
    {
        using var sandbox = new TestSandbox();
        var env = new FakeToolBackend(sandbox.Root);
        var platform = Path.Combine(env.ProgramData, "Microsoft", "Windows Defender", "Platform");
        var older = Path.Combine(platform, "4.18.2109.6-0");
        var newer = Path.Combine(platform, "4.18.24090.11-0");
        env.Dirs[platform] = new[] { older, newer };
        env.Files.UnionWith(new[] { Path.Combine(older, "MpCmdRun.exe"), Path.Combine(newer, "MpCmdRun.exe") });
        Assert.True(SystemToolCatalog.TryResolve(Steps.Tool(ToolIds.DefenderQuickScan), env, out var plan, out _));
        Assert.Equal(Path.Combine(newer, "MpCmdRun.exe"), plan.Commands[0].ExePath);
        Assert.Equal("-Scan -ScanType 1", string.Join(' ', plan.Commands[0].Arguments));
    }

    [Fact]
    public void Npm_runs_through_node_without_cmd_and_missing_tools_say_so()
    {
        using var sandbox = new TestSandbox();
        var env = new FakeToolBackend(sandbox.Root);
        var nodeDir = Path.Combine(env.ProgramFiles, "nodejs");
        env.Files.UnionWith(new[] { Path.Combine(nodeDir, "npm.cmd"), Path.Combine(nodeDir, "node.exe"), Path.Combine(nodeDir, "node_modules", "npm", "bin", "npm-cli.js") });
        Assert.True(SystemToolCatalog.TryResolve(Steps.Tool(ToolIds.DevCacheClean, new() { ["tool"] = "npm" }), env, out var npm, out _));
        Assert.Equal(Path.Combine(nodeDir, "node.exe"), npm.Commands.Single().ExePath);
        Assert.Equal(new[] { Path.Combine(nodeDir, "node_modules", "npm", "bin", "npm-cli.js"), "cache", "verify" }, npm.Commands.Single().Arguments);
        Assert.DoesNotContain(npm.Commands, c => c.ExePath.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase));

        Assert.True(SystemToolCatalog.TryResolve(Steps.Tool(ToolIds.DevCacheClean, new() { ["tool"] = "pip" }), env, out var pip, out _));
        Assert.NotNull(pip.MissingProgram);

        env.Files.Add(Path.Combine(env.ProgramFiles, "dotnet", "dotnet.exe"));
        Assert.True(SystemToolCatalog.TryResolve(Steps.Tool(ToolIds.DevCacheClean, new() { ["tool"] = "nuget-http" }), env, out var nuget, out _));
        Assert.Equal(new[] { "http-cache", "temp", "plugins-cache" }, nuget.Commands.Select(c => c.Arguments[2]));
    }

    [Fact]
    public async Task Sandbox_real_backend_runs_nothing_and_shows_the_command()
    {
        using var sandbox = new TestSandbox();
        var handler = new SystemToolHandler();
        foreach (var id in new[] { ToolIds.SfcScan, ToolIds.FlushDns, ToolIds.DriverRollback, ToolIds.RestorePointCreate, ToolIds.CleanmgrStorageSense })
        {
            var args = id switch
            {
                ToolIds.DriverRollback => new Dictionary<string, string> { ["instanceId"] = @"PCI\VEN_1" },
                ToolIds.RestorePointCreate => new Dictionary<string, string> { ["description"] = "Before test" },
                _ => null,
            };
            var result = await handler.ExecuteAsync(Steps.Of(Steps.Tool(id, args)), sandbox.Context(elevated: true), default);
            Assert.Equal(StepStatus.Simulated, result.Status);
            Assert.Contains("Would run", result.Message);
        }
    }

    [Fact]
    public async Task Api_tools_call_the_backend_and_per_user_tools_never_run_in_the_helper()
    {
        using var sandbox = new TestSandbox();
        var env = new FakeToolBackend(sandbox.Root);
        var handler = new SystemToolHandler(env);
        Assert.Equal(StepStatus.Done, (await handler.ExecuteAsync(Steps.Of(Steps.Tool(ToolIds.FlushDns)), sandbox.Context(), default)).Status);
        Assert.True(env.DnsFlushed);
        Assert.Equal(StepStatus.Done, (await handler.ExecuteAsync(Steps.Of(Steps.Tool(ToolIds.WindowsUpdateInstallDrivers, new() { ["updateIds"] = Guid.NewGuid().ToString() })), sandbox.Context(elevated: true), default)).Status);
        Assert.Equal(StepStatus.Done, (await handler.ExecuteAsync(Steps.Of(Steps.Tool(ToolIds.DriverRollback, new() { ["instanceId"] = @"PCI\VEN_1" })), sandbox.Context(elevated: true), default)).Status);
        env.Rollback = new DriverRollbackResult(false, 5, false);
        Assert.Equal(StepStatus.Failed, (await handler.ExecuteAsync(Steps.Of(Steps.Tool(ToolIds.DriverRollback, new() { ["instanceId"] = @"PCI\VEN_1" })), sandbox.Context(elevated: true), default)).Status);
        Assert.Equal(StepStatus.Done, (await handler.ExecuteAsync(Steps.Of(Steps.Tool(ToolIds.RestorePointCreate, new() { ["description"] = "x" })), sandbox.Context(elevated: true), default)).Status);
        Assert.Equal(StepStatus.Done, (await handler.ExecuteAsync(Steps.Of(Steps.Tool(ToolIds.CleanmgrStorageSense)), sandbox.Context(), default)).Status);
        Assert.Equal(new[] { "ms-settings:storagesense" }, env.Opened);

        var wsl = await handler.ExecuteAsync(Steps.Of(Steps.Tool(ToolIds.WslShutdown)), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Skipped, wsl.Status);
        Assert.Empty(env.Ran);
    }

    [Fact]
    public async Task Bcd_delete_records_the_old_value_and_undo_sets_it_back()
    {
        using var sandbox = new TestSandbox();
        var env = new FakeToolBackend(sandbox.Root);
        env.Bcd[0x260000A2] = new BcdElementValue(true, 1);
        env.OnRun = c => { if (c.Arguments.Contains("/deletevalue")) env.Bcd.Remove(0x260000A2); };
        var handler = new SystemToolHandler(env);
        var item = Steps.Tool(ToolIds.BcdDeleteValue, new() { ["name"] = "useplatformclock" }) with { Reversible = true };

        var done = await handler.ExecuteAsync(Steps.Of(item), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Done, done.Status);
        Assert.Equal("bool:1", done.UndoData![SystemToolHandler.PriorBcdData]);
        Assert.True(await handler.WasCompletedAsync(done, sandbox.Context(elevated: true), default));

        var undone = await handler.UndoAsync(done, sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Undone, undone.Status);
        Assert.Equal(new[] { "/set", "{current}", "useplatformclock", "Yes" }, env.Ran.Last().Arguments);
    }

    [Fact]
    public async Task Bcd_delete_without_a_readable_old_value_is_honestly_not_undoable()
    {
        using var sandbox = new TestSandbox();
        var env = new FakeToolBackend(sandbox.Root) { BcdReadable = false };
        var handler = new SystemToolHandler(env);
        var done = await handler.ExecuteAsync(Steps.Of(Steps.Tool(ToolIds.BcdDeleteValue, new() { ["name"] = "tscsyncpolicy" })), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Done, done.Status);
        Assert.Contains("can't be undone", done.Message);
        var undo = await handler.UndoAsync(done, sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Skipped, undo.Status);
        Assert.Single(env.Ran);

        Assert.Equal("Enhanced", SystemToolHandler.BcdUndoValue("tscsyncpolicy", "int:2"));
        Assert.Equal("4", SystemToolHandler.BcdUndoValue("numproc", "int:4"));
        Assert.Null(SystemToolHandler.BcdUndoValue("numproc", "evil:4"));
    }

    [Fact]
    public async Task Autotuning_records_the_previous_level_for_undo()
    {
        using var sandbox = new TestSandbox();
        var env = new FakeToolBackend(sandbox.Root) { Autotuning = "disabled" };
        env.OnRun = c => { if (c.Arguments.Any(a => a.StartsWith("autotuninglevel="))) env.Autotuning = c.Arguments.Last()["autotuninglevel=".Length..]; };
        var handler = new SystemToolHandler(env);
        var done = await handler.ExecuteAsync(Steps.Of(Steps.Tool(ToolIds.NetshAutotuningNormal) with { Reversible = true }), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Done, done.Status);
        Assert.Equal("disabled", done.UndoData![SystemToolHandler.PriorAutotuningData]);
        Assert.Equal(StepStatus.Undone, (await handler.UndoAsync(done, sandbox.Context(elevated: true), default)).Status);
        Assert.Equal("disabled", env.Autotuning);
        Assert.Equal(StepStatus.Skipped, (await handler.UndoAsync(done with { UndoData = new() { [SystemToolHandler.PriorAutotuningData] = "x; calc" } }, sandbox.Context(elevated: true), default)).Status);
    }

    [Fact]
    public async Task Cipher_wipe_is_refused_on_ssds_and_unknown_drives()
    {
        using var sandbox = new TestSandbox();
        var env = new FakeToolBackend(sandbox.Root);
        env.Media['C'] = DriveMedia.Ssd;
        env.Media['D'] = DriveMedia.Hdd;
        var handler = new SystemToolHandler(env);
        Assert.Equal(StepStatus.Skipped, (await handler.ExecuteAsync(Steps.Of(Steps.Tool(ToolIds.CipherWipeFreeSpace, new() { ["drive"] = "C:\\" })), sandbox.Context(), default)).Status);
        Assert.Equal(StepStatus.Skipped, (await handler.ExecuteAsync(Steps.Of(Steps.Tool(ToolIds.CipherWipeFreeSpace, new() { ["drive"] = "E" })), sandbox.Context(), default)).Status);
        Assert.Empty(env.Ran);
        Assert.Equal(StepStatus.Done, (await handler.ExecuteAsync(Steps.Of(Steps.Tool(ToolIds.CipherWipeFreeSpace, new() { ["drive"] = "D" })), sandbox.Context(), default)).Status);
        Assert.Equal(@"/w:D:\", env.Ran.Single().Arguments.Single());
    }

    [Fact]
    public async Task Compact_undo_runs_compact_u_on_the_same_folder()
    {
        using var sandbox = new TestSandbox();
        var env = new FakeToolBackend(sandbox.Root);
        var handler = new SystemToolHandler(env);
        var folder = Path.Combine(sandbox.Root, "Games", "Old");
        var done = await handler.ExecuteAsync(Steps.Of(Steps.Tool(ToolIds.CompactLzx, new() { ["folder"] = folder }) with { NeedsAdmin = false, Reversible = true }), sandbox.Context(), default);
        Assert.Equal(StepStatus.Done, done.Status);
        Assert.Equal(StepStatus.Undone, (await handler.UndoAsync(done, sandbox.Context(), default)).Status);
        Assert.Equal($"/u /s:{folder} /i /q /exe", string.Join(' ', env.Ran.Last().Arguments));
    }

    [Fact]
    public async Task OneDrive_shutdown_is_verified_and_undo_starts_it_without_waiting()
    {
        using var sandbox = new TestSandbox();
        var env = new FakeToolBackend(sandbox.Root);
        var exe = Path.Combine(env.LocalAppData, "Microsoft", "OneDrive", "OneDrive.exe");
        env.Files.Add(exe);
        env.Running.Add("OneDrive");
        env.OnRun = c => { if (c.Arguments.Contains("/shutdown")) env.Running.Remove("OneDrive"); };
        var handler = new SystemToolHandler(env);
        var done = await handler.ExecuteAsync(Steps.Of(Steps.Tool(ToolIds.OneDriveShutdown) with { NeedsAdmin = false, Reversible = true }), sandbox.Context(), default);
        Assert.Equal(StepStatus.Done, done.Status);
        Assert.Equal(StepStatus.Undone, (await handler.UndoAsync(done, sandbox.Context(), default)).Status);
        Assert.Equal(new[] { "/background" }, env.Launched.Single().Arguments);
        Assert.Equal(exe, env.Launched.Single().ExePath);
    }

    [Fact]
    public async Task Driver_backup_in_the_helper_goes_to_the_admin_store_first_then_to_the_users_folder()
    {
        using var sandbox = new TestSandbox();
        var env = new FakeToolBackend(sandbox.Root);
        env.OnRun = c =>
        {
            if (c.Arguments[0] != "/export-driver") return;
            Directory.CreateDirectory(Path.Combine(c.Arguments[2], "net"));
            File.WriteAllText(Path.Combine(c.Arguments[2], "net", "oem3.inf"), "inf");
        };
        var handler = new SystemToolHandler(env);
        var userFolder = Path.Combine(sandbox.Root, "UserData", "backups", "drivers", "2026");
        var done = await handler.ExecuteAsync(Steps.Of(Steps.Tool(ToolIds.PnpExportDrivers, new() { ["folder"] = userFolder }) with { Reversible = true }), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Done, done.Status);
        var adminCopy = done.UndoData![SystemToolHandler.DriverAdminCopyData];
        Assert.StartsWith(Path.Combine(sandbox.Locations.DataRoot, "backups", "drivers"), adminCopy);
        Assert.Equal(adminCopy, env.Ran.Single().Arguments[2]);
        Assert.True(File.Exists(Path.Combine(userFolder, "net", "oem3.inf")));
    }

    [Fact]
    public async Task Driver_restore_in_the_helper_refuses_a_backup_folder_with_links()
    {
        using var sandbox = new TestSandbox();
        var env = new FakeToolBackend(sandbox.Root);
        var handler = new SystemToolHandler(env);
        var userFolder = Path.Combine(sandbox.Root, "UserData", "backups", "drivers", "2026");
        Directory.CreateDirectory(Path.Combine(userFolder, "net"));
        File.WriteAllText(Path.Combine(userFolder, "net", "oem3.inf"), "inf");

        // A link inside the backup folder: nothing is installed. (Manifest matching: SecurityFixTests.)
        var link = Path.Combine(userFolder, "link");
        try { Directory.CreateSymbolicLink(link, sandbox.Root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; } // no symlink privilege on this account
        env.Ran.Clear();
        var refused = await handler.ExecuteAsync(Steps.Of(Steps.Tool(ToolIds.PnpAddDrivers, new() { ["folder"] = userFolder })), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Skipped, refused.Status);
        Assert.Empty(env.Ran);
        Directory.Delete(link);
    }

    [Fact]
    public async Task Gradle_quarantines_only_old_unused_version_caches_and_undo_restores_them()
    {
        using var sandbox = new TestSandbox();
        var env = new FakeToolBackend(sandbox.Root);
        var caches = Path.Combine(env.UserProfile, ".gradle", "caches");
        foreach (var v in new[] { "7.6", "8.5", "8.10", "modules-2" }) Directory.CreateDirectory(Path.Combine(caches, v));
        Directory.SetLastWriteTimeUtc(Path.Combine(caches, "7.6"), DateTime.UtcNow.AddDays(-90));
        Directory.SetLastWriteTimeUtc(Path.Combine(caches, "8.5"), DateTime.UtcNow.AddDays(-2)); // used recently: kept
        var quarantine = new TestbedQuarantine(Path.Combine(sandbox.Root, "Q"));
        var handler = new SystemToolHandler(env);
        var ctx = sandbox.Context().With(quarantine);

        var done = await handler.ExecuteAsync(Steps.Of(Steps.Tool(ToolIds.DevCacheClean, new() { ["tool"] = "gradle" }) with { NeedsAdmin = false }), ctx, default);
        Assert.Equal(StepStatus.Done, done.Status);
        Assert.Equal(new[] { Path.Combine(caches, "7.6") }, quarantine.Entries.Select(e => e.OriginalPath));
        Assert.True(Directory.Exists(Path.Combine(caches, "8.10")));
        Assert.True(Directory.Exists(Path.Combine(caches, "modules-2")));

        var undone = await handler.UndoAsync(done, ctx, default);
        Assert.Equal(StepStatus.Undone, undone.Status);
        Assert.True(Directory.Exists(Path.Combine(caches, "7.6")));
    }
}
