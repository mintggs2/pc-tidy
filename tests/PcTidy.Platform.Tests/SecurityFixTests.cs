using System.Collections;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using PcTidy.Core;
using PcTidy.Core.Model;
using Xunit;
using static PcTidy.Platform.Tests.V2Items;

namespace PcTidy.Platform.Tests;

/// <summary>
/// Regressions for the v2 safety review (malware running as the user, waiting for a genuine PC Tidy admin prompt).
/// Files only under testbed\secfix\platform, registry only under HKCU\Software\PCTidyTest\secfix; nothing elevates,
/// no Windows tool runs (fakes only), real system folders are only READ (their permissions).
/// </summary>
public class SecurityFixTests
{
    private static RawSecurityDescriptor Sd(string sddl) => new(sddl);
    private static AclSafety.Node Node(string sddl, AclSafety.Level level = AclSafety.Level.Contents) => new("X:\\thing", level, Sd(sddl));

    // ---------------------------------------------------------------- C1: who may change the program folder

    private const string ProgramFilesLike = "O:BAG:SYD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)(A;OICI;0x1200a9;;;AC)(A;OICIIO;GA;;;CO)";

    [Fact]
    public void Admin_only_folders_pass_and_any_write_for_a_normal_account_fails()
    {
        Assert.Null(AclSafety.Evaluate(new[] { Node(ProgramFilesLike) }));
        Assert.Null(AclSafety.Evaluate(new[] { Node("O:S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464D:PAI(A;;FA;;;S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464)(A;;0x1200a9;;;BU)") }));
        // Users / Authenticated Users / Everyone / INTERACTIVE with modify, write, add-file or full control.
        foreach (var ace in new[] { "(A;OICI;0x1301bf;;;BU)", "(A;;FA;;;AU)", "(A;;0x2;;;WD)", "(A;;FW;;;IU)", "(A;;0x4;;;BU)", "(A;;WD;;;BU)", "(A;;SD;;;AU)" })
            Assert.NotNull(AclSafety.Evaluate(new[] { Node("O:BAG:SYD:PAI(A;;FA;;;BA)" + ace) }));
        // The current user (a portable copy in their own folder).
        using var me = WindowsIdentity.GetCurrent();
        Assert.NotNull(AclSafety.Evaluate(new[] { Node($"O:BAG:SYD:PAI(A;;FA;;;BA)(A;;0x1301bf;;;{me.User!.Value})") }));
    }

    [Fact]
    public void Owner_null_dacl_and_inherit_only_entries_are_judged_correctly()
    {
        using var me = WindowsIdentity.GetCurrent();
        Assert.Contains("owned by", AclSafety.Evaluate(new[] { Node($"O:{me.User!.Value}D:PAI(A;;FA;;;BA)") })); // owner may always change the permissions
        Assert.NotNull(AclSafety.Evaluate(new[] { Node("O:BAG:SYD:NO_ACCESS_CONTROL") }));
        Assert.NotNull(AclSafety.Evaluate(new[] { new AclSafety.Node("X:\\gone", AclSafety.Level.Contents, null) }));
        // An inherit-only entry doesn't apply to the object itself (it is checked on the children, which are read too).
        Assert.Null(AclSafety.Evaluate(new[] { Node("O:BAG:SYD:PAI(A;;FA;;;BA)(A;OICIIO;FA;;;BU)") }));
    }

    [Fact]
    public void A_drive_root_may_let_users_add_folders_but_not_swap_or_delete_them()
    {
        // C:\ as Windows ships it: Authenticated Users may create folders there (this folder only) and get modify on new ones.
        const string root = "O:SYG:SYD:PAI(A;OICI;FA;;;BA)(A;OICI;FA;;;SY)(A;OICI;0x1200a9;;;BU)(A;;0x4;;;AU)(A;OICIIO;SDGXGWGR;;;AU)";
        Assert.Null(AclSafety.Evaluate(new[] { Node(root, AclSafety.Level.Root) }));
        Assert.NotNull(AclSafety.Evaluate(new[] { Node(root, AclSafety.Level.Contents) })); // inside the program's own folder that would plant a DLL
        Assert.NotNull(AclSafety.Evaluate(new[] { Node("O:SYG:SYD:PAI(A;;FA;;;SY)(A;;0x40;;;AU)", AclSafety.Level.Root) })); // delete-child: swap the folder below
        // A parent folder an account can delete (rename) is unsafe; adding folders to it is fine.
        Assert.NotNull(AclSafety.Evaluate(new[] { Node("O:BAG:SYD:PAI(A;;FA;;;BA)(A;;0x1301bf;;;AU)", AclSafety.Level.Parent) }));
        Assert.Null(AclSafety.Evaluate(new[] { Node("O:BAG:SYD:PAI(A;;FA;;;BA)(A;;0x1200af;;;AU)", AclSafety.Level.Parent) }));
    }

    [Fact]
    public void Real_windows_folders_are_admin_only_and_a_user_folder_is_not()
    {
        // Read-only look at Windows' own permissions.
        var ps = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (File.Exists(ps)) Assert.Null(AclSafety.CheckProgram(ps));

        using var sandbox = new TestSandbox();
        var exe = Path.Combine(sandbox.Root, "PortableCopy", "PcTidy.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "fake");
        Assert.NotNull(AclSafety.CheckInstallFolder(exe)); // created by this (non-admin) account: never admin-only
        Assert.NotNull(AclSafety.CheckProgram(exe));
    }

    [Fact]
    public void Testbed_folder_with_a_hardened_acl_passes_until_users_get_write_access()
    {
        using var sandbox = new TestSandbox();
        using var me = WindowsIdentity.GetCurrent();
        var folder = Path.Combine(sandbox.Root, "Hardened");
        var exe = Path.Combine(folder, "PcTidy.exe");
        var dll = Path.Combine(folder, "native.dll");
        Directory.CreateDirectory(folder);
        File.WriteAllText(exe, "fake");
        File.WriteAllText(dll, "fake");
        var dir = new DirectoryInfo(folder);
        try
        {
            // Like Program Files\PC Tidy, except that this test's own account stands in for Administrators as owner
            // (only an elevated process can make Administrators the owner).
            var hardened = new DirectorySecurity();
            hardened.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            hardened.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            hardened.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            hardened.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
            hardened.AddAccessRule(new FileSystemAccessRule(me.User!, FileSystemRights.ReadAndExecute | FileSystemRights.ChangePermissions, inherit, PropagationFlags.None, AccessControlType.Allow));
            dir.SetAccessControl(hardened);
            foreach (var f in new[] { exe, dll }) ResetToInherited(f);

            var trustMe = new[] { me.User! };
            AclSafety.Node[] Contents() => new[]
            {
                AclSafety.Read(folder, AclSafety.Level.Contents, isDirectory: true),
                AclSafety.Read(exe, AclSafety.Level.Contents, isDirectory: false),
                AclSafety.Read(dll, AclSafety.Level.Contents, isDirectory: false),
            };
            Assert.Null(AclSafety.Evaluate(Contents(), trustMe));

            // A DLL next to the program that Users may modify: the next admin prompt would load whatever is put in it.
            var dllAcl = new FileInfo(dll).GetAccessControl();
            dllAcl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Modify, AccessControlType.Allow));
            new FileInfo(dll).SetAccessControl(dllAcl);
            var reason = AclSafety.Evaluate(Contents(), trustMe);
            Assert.NotNull(reason);
            Assert.Contains("native.dll", reason);
        }
        finally
        {
            // Back to plain inherited permissions (this account owns the folder, so it may always do that).
            var plain = new DirectorySecurity();
            plain.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
            dir.SetAccessControl(plain);
            foreach (var f in new[] { exe, dll }) ResetToInherited(f);
        }
    }

    private static void ResetToInherited(string file)
    {
        var acl = new FileSecurity();
        acl.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
        new FileInfo(file).SetAccessControl(acl);
    }

    [Fact]
    public async Task No_admin_prompt_for_a_copy_that_isnt_installed_steps_are_skipped_with_the_reason()
    {
        using var sandbox = new TestSandbox();
        var launcher = new CountingLauncher();
        await using var service = new ElevationService(sandbox.Locations, null, launcher, allowLaunchInSandbox: true, TimeSpan.FromSeconds(5),
            preLaunchCheck: () => InstallIntegrity.PortableRefusal);
        var step = Steps.Of(new PlanItem { Kind = ActionKind.ServiceSetStartType, FeatureId = "t", Description = "t", NeedsAdmin = true, ServiceName = "X", ServiceStartType = ServiceStart.Manual });

        var results = await service.ExecuteElevatedAsync("run-p", new[] { step }, ExecutionMode.Real, undo: false, default);
        Assert.Equal(StepStatus.Skipped, results.Single().Status);
        Assert.Equal("Admin steps need PC Tidy to be installed (Program Files). Portable copies can't run admin steps safely.", results.Single().Message);
        var point = await service.EnsureRestorePointAsync("x", default);
        Assert.False(point.Created);
        Assert.StartsWith(InstallIntegrity.PortableRefusal, point.Problem);
        Assert.Equal(0, launcher.Launches); // Windows was never asked
    }

    private sealed class CountingLauncher : IHelperLauncher
    {
        public int Launches { get; private set; }
        public HelperProcess Launch(string pipeName, int parentPid) { Launches++; throw new ElevationDeclinedException("no"); }
    }

    [Fact]
    public async Task The_helper_refuses_to_start_with_an_unsafe_environment_or_from_a_changeable_folder()
    {
        using var sandbox = new TestSandbox();
        var args = new[] { ElevationProtocol.ElevatedSwitch, ElevationProtocol.NewPipeName(), Environment.ProcessId.ToString() };
        var noHandlers = Array.Empty<IActionHandler>();

        var env = await ElevatedHost.RunAsync(args, noHandlers, () => sandbox.Context(), null,
            new ElevatedHost.StartupChecks(() => new[] { "DOTNET_STARTUP_HOOKS" }, () => null));
        Assert.Equal(ElevatedHost.ExitUnsafeEnvironment, env);
        var folder = await ElevatedHost.RunAsync(args, noHandlers, () => sandbox.Context(), null,
            new ElevatedHost.StartupChecks(() => Array.Empty<string>(), () => "Users may change it"));
        Assert.Equal(ElevatedHost.ExitUntrustedLocation, folder);

        var log = await File.ReadAllTextAsync(Path.Combine(sandbox.Locations.DataRoot, "logs", "helper.log"));
        Assert.Contains("DOTNET_STARTUP_HOOKS", log);
        Assert.Contains("admin-only", log);
    }

    [Fact]
    public void Runtime_code_loading_variables_are_found_and_harmless_ones_are_not()
    {
        var vars = new Hashtable(StringComparer.OrdinalIgnoreCase)
        {
            ["DOTNET_STARTUP_HOOKS"] = @"C:\Users\tester\hook.dll",
            ["CORECLR_ENABLE_PROFILING"] = "1",
            ["CORECLR_PROFILER_PATH_64"] = @"C:\Users\tester\p.dll",
            ["COR_PROFILER"] = "{00000000-0000-0000-0000-000000000000}",
            ["cor_enable_profiling"] = "1",
            ["DOTNET_ADDITIONAL_DEPS"] = "x",
            ["DOTNET_SHARED_STORE"] = "x",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_gcServer"] = "1",
            ["PATH"] = @"C:\Windows",
            ["CORECLR_PROFILER_PATH_32"] = "",
        };
        var found = HelperEnvironment.UnsafeVariables(vars);
        Assert.Equal(new[] { "COR_PROFILER", "CORECLR_ENABLE_PROFILING", "CORECLR_PROFILER_PATH_64", "cor_enable_profiling", "DOTNET_ADDITIONAL_DEPS", "DOTNET_SHARED_STORE", "DOTNET_STARTUP_HOOKS" }
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase), found);
        Assert.Contains("DOTNET_STARTUP_HOOKS", HelperEnvironment.Refusal(found));
        Assert.Null(HelperEnvironment.Refusal(Array.Empty<string>()));
    }

    // ---------------------------------------------------------------- H2: the helper's children get a clean environment

    [Fact]
    public void Programs_started_by_the_helper_get_a_fixed_clean_environment()
    {
        using var sandbox = new TestSandbox();
        var temp = Path.Combine(sandbox.Locations.DataRoot, "temp");
        var block = HelperEnvironment.CleanBlock(temp);
        var sys = Environment.SystemDirectory;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.Equal($@"{sys};{windows};{sys}\Wbem;{sys}\WindowsPowerShell\v1.0", block["PATH"]);
        Assert.Equal($@"{sys}\WindowsPowerShell\v1.0\Modules", block["PSModulePath"]);
        Assert.Equal(temp, block["TEMP"]);
        Assert.Equal(temp, block["TMP"]);
        Assert.Equal(windows, block["SystemRoot"]);
        Assert.Equal(windows, block["windir"]);
        Assert.Equal(Path.Combine(sys, "cmd.exe"), block["ComSpec"]);
        Assert.Contains(".EXE", block["PATHEXT"]);
        Assert.DoesNotContain(block.Keys, k => k.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("COR", StringComparison.OrdinalIgnoreCase) || k.StartsWith("COMPlus_", StringComparison.OrdinalIgnoreCase));

        // The review's planted module folder (testbed\review-v2-safety\psmod) on the user's PSModulePath is gone.
        var psi = new ProcessStartInfo(Path.Combine(sys, "WindowsPowerShell", "v1.0", "powershell.exe"));
        psi.Environment["PSModulePath"] = @"D:\planted\psmod;" + block["PSModulePath"];
        psi.Environment["DOTNET_STARTUP_HOOKS"] = "hook.dll";
        psi.Environment["CORECLR_ENABLE_PROFILING"] = "1";
        psi.Environment["PATH"] = @"C:\Users\tester\bin;" + block["PATH"];
        HelperEnvironment.Apply(psi, block);
        Assert.Equal(block.Count, psi.Environment.Count);
        Assert.Equal(block["PSModulePath"], psi.Environment["PSModulePath"]);
        Assert.Equal(block["PATH"], psi.Environment["PATH"]);
        Assert.False(psi.Environment.ContainsKey("DOTNET_STARTUP_HOOKS"));
        Assert.False(psi.Environment.ContainsKey("CORECLR_ENABLE_PROFILING"));
    }

    [Fact]
    public void Delivery_optimization_imports_windows_own_module_by_full_path()
    {
        Assert.True(SystemToolCatalog.TryResolve(Steps.Tool(ToolIds.DeliveryOptimizationClear), out var plan, out var problem), problem);
        var script = plan.Commands.Single().Arguments.Last();
        var module = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "Modules", "DeliveryOptimization", "DeliveryOptimization.psd1");
        Assert.Contains($"Import-Module -Name '{module}'", script);
        Assert.Contains(@"DeliveryOptimization\Delete-DeliveryOptimizationCache", script);
        Assert.Contains("-NoProfile", plan.Commands.Single().Arguments);
    }

    // ---------------------------------------------------------------- C2 / H1: uninstallers

    private const string CodeA = "{11111111-2222-3333-4444-555555555555}";
    private const string CodeB = "{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}";

    private static PlanItem Uninstall(Dictionary<string, string> args, bool admin = true) => Item(ActionKind.RunUninstaller, args, admin: admin, reversible: false);

    [Fact]
    public async Task Uninstall_details_must_all_belong_to_the_same_listed_app()
    {
        using var sandbox = new TestSandbox();
        var fake = new FakeUninstall();
        const string otherVendor = @"""C:\Program Files\Other\uninst.exe"" /S";
        fake.Files.Add(@"C:\Program Files\Other\uninst.exe");
        fake.Listed.Add(new UninstallEntry("HKLM", CodeA, "Contoso", "MsiExec.exe /X" + CodeA, null));
        fake.Listed.Add(new UninstallEntry("HKLM", "Other", "Other", otherVendor, null));
        var handler = new UninstallerHandler(fake);

        // Contoso's MSI code with another app's command: refused, nothing runs.
        var mixed = await handler.ExecuteAsync(Steps.Of(Uninstall(new() { ["msiProductCode"] = CodeA, ["uninstallString"] = otherVendor })), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Skipped, mixed.Status);
        Assert.Equal(UninstallerHandler.NotSameApp, mixed.Message);
        // The review's case B: an MSI code nobody lists + any winget id, in the helper: nothing runs (no winget either).
        fake.Files.Add(fake.WingetPath);
        var unlisted = await handler.ExecuteAsync(Steps.Of(Uninstall(new() { ["msiProductCode"] = CodeB, ["wingetId"] = "Any.Package" })), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Skipped, unlisted.Status);
        Assert.Empty(fake.Ran);

        // The listed pair runs that entry's own command only.
        var ok = await handler.ExecuteAsync(Steps.Of(Uninstall(new() { ["msiProductCode"] = CodeA, ["uninstallString"] = "MsiExec.exe /X" + CodeA })), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Done, ok.Status);
        Assert.Equal(new[] { "/x", CodeA, "/norestart" }, fake.Ran.Single().Arguments);
    }

    [Fact]
    public async Task Winget_never_runs_in_the_admin_helper_only_in_the_app()
    {
        using var sandbox = new TestSandbox();
        var fake = new FakeUninstall();
        fake.Files.Add(fake.WingetPath);
        var handler = new UninstallerHandler(fake);
        var item = Uninstall(new() { ["wingetId"] = "Contoso.App", ["displayName"] = "Contoso" });

        var helper = await handler.ExecuteAsync(Steps.Of(item), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Skipped, helper.Status);
        Assert.Equal(UninstallerHandler.WingetNotInHelper, helper.Message);
        Assert.Empty(fake.Ran);

        var app = await handler.ExecuteAsync(Steps.Of(item with { NeedsAdmin = false }), sandbox.Context(), default);
        Assert.Equal(StepStatus.Done, app.Status);
        Assert.Equal(UninstallVia.Winget, fake.Runs.Single().Command.Via);
        Assert.False(fake.Runs.Single().AsAdmin);

        // A listed app with an MSI code and a winget id: the helper uses msiexec and never winget.
        fake.Ran.Clear();
        fake.Runs.Clear();
        fake.Listed.Add(new UninstallEntry("HKLM", CodeA, "Contoso", null, null));
        var both = await handler.ExecuteAsync(Steps.Of(Uninstall(new() { ["msiProductCode"] = CodeA, ["wingetId"] = "Contoso.App" })), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Done, both.Status);
        Assert.Equal(UninstallVia.Msi, fake.Ran.Single().Via);
    }

    [Fact]
    public async Task A_vendor_uninstaller_in_a_changeable_folder_never_runs_with_admin_rights()
    {
        using var sandbox = new TestSandbox();
        // The review's case A: a machine-wide game on D:\ where Authenticated Users have modify.
        const string vendor = @"""D:\Games\Foo\unins000.exe"" /SILENT";
        var fake = new FakeUninstall();
        fake.Files.Add(@"D:\Games\Foo\unins000.exe");
        fake.Changeable[@"D:\Games\Foo\unins000.exe"] = "Authenticated Users may change D:\\Games\\Foo.";
        fake.Listed.Add(new UninstallEntry("HKLM", "Foo_is1", "Foo", vendor, null));
        var handler = new UninstallerHandler(fake);
        var item = Uninstall(new() { ["uninstallString"] = vendor, ["displayName"] = "Foo" });

        var done = await handler.ExecuteAsync(Steps.Of(item), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Done, done.Status);
        Assert.Empty(fake.Runs); // never started by the admin helper itself
        var launched = fake.LaunchedWithoutAdmin.Single();
        Assert.Equal(@"D:\Games\Foo\unins000.exe", launched.ExePath);
        Assert.Equal("/SILENT", launched.RawArguments);
        Assert.True(launched.WithoutAdmin);

        // Still listed after the wait: an honest failure, still never elevated.
        fake.RemoveEntryOnLaunch = false;
        fake.Listed.Add(new UninstallEntry("HKLM", "Foo_is1", "Foo", vendor, null));
        var stays = await handler.ExecuteAsync(Steps.Of(item), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Failed, stays.Status);
        Assert.Contains("without admin rights", stays.Message);
        Assert.Empty(fake.Runs);
    }

    [Fact]
    public async Task An_admin_only_vendor_uninstaller_runs_elevated_with_the_last_moment_recheck()
    {
        using var sandbox = new TestSandbox();
        const string vendor = @"""C:\Program Files\Contoso\uninstall.exe"" /S";
        var fake = new FakeUninstall();
        fake.Files.Add(@"C:\Program Files\Contoso\uninstall.exe");
        fake.Listed.Add(new UninstallEntry("HKLM", "Contoso", "Contoso", null, vendor)); // matched by its QuietUninstallString
        var handler = new UninstallerHandler(fake);
        var item = Uninstall(new() { ["uninstallString"] = vendor });

        var done = await handler.ExecuteAsync(Steps.Of(item), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Done, done.Status);
        Assert.True(fake.Runs.Single().AsAdmin);
        Assert.Equal("/S", fake.Runs.Single().Command.RawArguments);

        // It became changeable between the check and the start: handed to Explorer instead, never run as admin.
        fake.Runs.Clear();
        fake.Listed.Add(new UninstallEntry("HKLM", "Contoso", "Contoso", null, vendor));
        fake.ChangeableAtStart.Add(@"C:\Program Files\Contoso\uninstall.exe");
        var raced = await handler.ExecuteAsync(Steps.Of(item), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Done, raced.Status);
        Assert.Empty(fake.Runs);
        Assert.Single(fake.LaunchedWithoutAdmin);
    }

    // ---------------------------------------------------------------- H3 / M5: driver backup and restore

    private static void FakeExport(FakeToolBackend env, Dictionary<string, Dictionary<string, string>> packages)
        => env.OnRun = c =>
        {
            if (c.Arguments[0] != "/export-driver") return;
            foreach (var (package, files) in packages)
                foreach (var (name, content) in files)
                {
                    var path = Path.Combine(c.Arguments[2], package, name);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, content);
                }
        };

    private static readonly Dictionary<string, Dictionary<string, string>> TwoPackages = new()
    {
        ["net.inf_amd64_1"] = new() { ["net.inf"] = "[Version]\r\nnetdrv.sys", ["netdrv.sys"] = "driver bytes", ["net.cat"] = "catalog" },
        ["usb.inf_amd64_2"] = new() { ["usb.inf"] = "[Version]\r\nusbdrv.sys", ["usbdrv.sys"] = "usb bytes" },
    };

    private static async Task<(SystemToolHandler Handler, FakeToolBackend Env, string UserFolder)> ExportedAsync(TestSandbox sandbox, Dictionary<string, Dictionary<string, string>> packages)
    {
        var env = new FakeToolBackend(sandbox.Root);
        FakeExport(env, packages);
        var handler = new SystemToolHandler(env);
        var userFolder = Path.Combine(sandbox.Root, "UserData", "backups", "drivers", "2026");
        var done = await handler.ExecuteAsync(Steps.Of(Steps.Tool(ToolIds.PnpExportDrivers, new() { ["folder"] = userFolder }) with { Reversible = true }, 1), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Done, done.Status);
        env.Ran.Clear();
        env.OnRun = null;
        return (handler, env, userFolder);
    }

    private static Task<StepRecord> RestoreAsync(SystemToolHandler handler, TestSandbox sandbox, string folder)
        => handler.ExecuteAsync(Steps.Of(Steps.Tool(ToolIds.PnpAddDrivers, new() { ["folder"] = folder }), 2), sandbox.Context(elevated: true), default);

    [Fact]
    public async Task Driver_export_records_a_manifest_in_the_admin_store_and_copies_without_overwriting()
    {
        using var sandbox = new TestSandbox();
        var userFolder = Path.Combine(sandbox.Root, "UserData", "backups", "drivers", "2026");
        var mine = Path.Combine(userFolder, "usb.inf_amd64_2", "usb.inf");
        Directory.CreateDirectory(Path.GetDirectoryName(mine)!);
        File.WriteAllText(mine, "the user's own file");

        var (_, _, folder) = await ExportedAsync(sandbox, TwoPackages);
        Assert.Equal(userFolder, folder);
        var manifests = Directory.GetFiles(Path.Combine(sandbox.Locations.DataRoot, "backups", "drivers", "manifests"), "*.json");
        var manifest = System.Text.Json.JsonSerializer.Deserialize<DriverBackupManifest>(File.ReadAllText(manifests.Single()))!;
        Assert.Equal(5, manifest.Files.Count);
        Assert.Equal(new[] { @"net.inf_amd64_1\net.inf", @"usb.inf_amd64_2\usb.inf" }, manifest.Infs);
        Assert.All(manifest.Files, f => Assert.Equal(64, f.Sha256.Length));
        Assert.Equal("driver bytes", File.ReadAllText(Path.Combine(userFolder, "net.inf_amd64_1", "netdrv.sys")));
        Assert.Equal("the user's own file", File.ReadAllText(mine)); // never overwritten
    }

    [Fact]
    public async Task Driver_restore_installs_only_packages_that_exactly_match_a_recorded_backup()
    {
        using var sandbox = new TestSandbox();
        var (handler, env, userFolder) = await ExportedAsync(sandbox, TwoPackages);
        string? installedFrom = null;
        var staged = new List<string>();
        env.OnRun = c =>
        {
            installedFrom = c.Arguments[1];
            staged.AddRange(Directory.GetFiles(Path.GetDirectoryName(c.Arguments[1])!, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(Path.GetDirectoryName(c.Arguments[1])!, f)));
        };

        // Untouched backup: both packages, from the admin-only staging copy.
        var all = await RestoreAsync(handler, sandbox, userFolder);
        Assert.Equal(StepStatus.Done, all.Status);
        Assert.StartsWith(Path.Combine(sandbox.Locations.DataRoot, "staging", "drivers"), installedFrom);
        Assert.Equal(5, staged.Count);
        Assert.False(Directory.Exists(Path.GetDirectoryName(installedFrom))); // staging removed afterwards

        // One package changed in the user's folder, plus a package PC Tidy never exported: only the intact one is installed.
        File.WriteAllText(Path.Combine(userFolder, "usb.inf_amd64_2", "usbdrv.sys"), "malware");
        Directory.CreateDirectory(Path.Combine(userFolder, "evil.inf_amd64_9"));
        File.WriteAllText(Path.Combine(userFolder, "evil.inf_amd64_9", "evil.inf"), "[Version]");
        staged.Clear();
        var some = await RestoreAsync(handler, sandbox, userFolder);
        Assert.Equal(StepStatus.Done, some.Status);
        Assert.Equal(new[] { @"net.inf_amd64_1\net.cat", @"net.inf_amd64_1\net.inf", @"net.inf_amd64_1\netdrv.sys" }, staged.OrderBy(s => s));
        Assert.Contains("usb.inf_amd64_2", some.Message);
        Assert.Contains("evil.inf_amd64_9", some.Message);

        // Nothing intact left: nothing runs.
        File.WriteAllText(Path.Combine(userFolder, "net.inf_amd64_1", "net.inf"), "changed");
        env.Ran.Clear();
        var none = await RestoreAsync(handler, sandbox, userFolder);
        Assert.Equal(StepStatus.Skipped, none.Status);
        Assert.Empty(env.Ran);
    }

    [Fact]
    public async Task Driver_restore_refuses_a_folder_without_a_recorded_backup_and_vulnerable_drivers()
    {
        using var sandbox = new TestSandbox();
        var env = new FakeToolBackend(sandbox.Root);
        var handler = new SystemToolHandler(env);
        var random = Path.Combine(sandbox.Root, "Downloads", "drv");
        Directory.CreateDirectory(Path.Combine(random, "pkg"));
        File.WriteAllText(Path.Combine(random, "pkg", "x.inf"), "[Version]");
        var refused = await RestoreAsync(handler, sandbox, random); // the review's case C: pnputil /add-driver from Downloads
        Assert.Equal(StepStatus.Skipped, refused.Status);
        Assert.Empty(env.Ran);

        // A vulnerable driver PC Tidy itself exported is still not put back (by file name, and when an INF names it).
        var (h2, env2, userFolder) = await ExportedAsync(sandbox, new()
        {
            ["rtcore.inf_amd64_1"] = new() { ["rtcore.inf"] = "[Version]", ["RTCore64.sys"] = "vulnerable" },
            ["named.inf_amd64_2"] = new() { ["named.inf"] = "[SourceDisksFiles]\r\ngdrv.sys=1" },
        });
        var vulnerable = await RestoreAsync(h2, sandbox, userFolder);
        Assert.Equal(StepStatus.Skipped, vulnerable.Status);
        Assert.Contains("RTCore64.sys", vulnerable.Message);
        Assert.Contains("gdrv.sys", vulnerable.Message);
        Assert.Empty(env2.Ran);
    }

    [Fact]
    public void No_follow_writer_never_overwrites_and_refuses_links()
    {
        using var sandbox = new TestSandbox();
        var folder = Path.Combine(sandbox.Root, "Target", "Sub");
        using (var w = NoFollowWriter.Open(folder))
        {
            Assert.True(w.CreateFile("a.txt", new MemoryStream("first"u8.ToArray())));
            Assert.False(w.CreateFile("a.txt", new MemoryStream("second"u8.ToArray())));
            using var sub = w.Sub("deeper");
            Assert.True(sub.CreateFile("b.txt", new MemoryStream("b"u8.ToArray())));
        }
        Assert.Equal("first", File.ReadAllText(Path.Combine(folder, "a.txt")));
        Assert.Equal("b", File.ReadAllText(Path.Combine(folder, "deeper", "b.txt")));
        Assert.Throws<IOException>(() => NoFollowWriter.Open(@"\\server\share\x"));

        var link = Path.Combine(sandbox.Root, "Target", "link");
        try { Directory.CreateSymbolicLink(link, Path.Combine(sandbox.Root, "Elsewhere")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; } // no symlink privilege on this account
        Directory.CreateDirectory(Path.Combine(sandbox.Root, "Elsewhere"));
        Assert.ThrowsAny<IOException>(() => NoFollowWriter.Open(Path.Combine(link, "x")));
        Assert.False(Directory.Exists(Path.Combine(sandbox.Root, "Elsewhere", "x")));
        Directory.Delete(link);
    }

    // ---------------------------------------------------------------- M3: device entries

    [Theory]
    [InlineData(DevicePresence.Present, StepStatus.Skipped)]
    [InlineData(DevicePresence.Missing, StepStatus.Skipped)]
    [InlineData(DevicePresence.Unknown, StepStatus.Skipped)]
    [InlineData(DevicePresence.NotPresent, StepStatus.Done)]
    public async Task Only_device_entries_that_are_not_connected_are_removed(DevicePresence presence, StepStatus expected)
    {
        using var sandbox = new TestSandbox();
        const string id = @"PCI\VEN_8086&DEV_A0FC&SUBSYS_00000000&REV_20\3&11583659&0&FB";
        var env = new FakeToolBackend(sandbox.Root);
        env.Devices[id] = presence;
        var result = await new SystemToolHandler(env).ExecuteAsync(Steps.Of(Steps.Tool(ToolIds.PnpRemoveDevice, new() { ["instanceId"] = id })), sandbox.Context(elevated: true), default);
        Assert.Equal(expected, result.Status);
        Assert.Equal(expected == StepStatus.Done ? 1 : 0, env.Ran.Count);
    }

    // ---------------------------------------------------------------- M4: restore point space

    [Fact]
    public async Task Restore_point_space_never_shrinks_below_use_plus_the_next_point()
    {
        using var sandbox = new TestSandbox();
        const long GB = 1L << 30;
        var env = new FakeToolBackend(sandbox.Root);
        var handler = new SystemToolHandler(env);
        StepRecord Resize(int pct) => Steps.Of(Steps.Tool(ToolIds.ShadowStorageResize, new() { ["percent"] = pct.ToString(), ["drive"] = "C:" }));

        Assert.Equal(StepStatus.Skipped, (await handler.ExecuteAsync(Resize(10), sandbox.Context(elevated: true), default)).Status); // can't read: unchanged
        env.Shadow['C'] = new ShadowStorageFacts(500 * GB, 40 * GB, 60 * GB, 4); // 40 GB in 4 points: needs 50 GB = 10%
        var tooSmall = await handler.ExecuteAsync(Resize(8), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Skipped, tooSmall.Status);
        Assert.Contains("10%", tooSmall.Message);
        Assert.Empty(env.Ran);
        Assert.Equal(StepStatus.Done, (await handler.ExecuteAsync(Resize(10), sandbox.Context(elevated: true), default)).Status);
        Assert.Equal(50 * GB, SystemToolHandler.MinimumShadowBytes(env.Shadow['C']));
        Assert.Equal(0, SystemToolHandler.MinimumShadowBytes(new ShadowStorageFacts(500 * GB, 0, 0, 0)));

        Assert.True(SystemToolCatalog.TryResolve(Steps.Tool(ToolIds.ShadowStorageResize, new() { ["percent"] = "10" }), out var plan, out _));
        Assert.False(plan.Reversible); // permanent: Windows may delete restore points
    }

    // ---------------------------------------------------------------- M1: LZX compression

    [Fact]
    public async Task Compression_is_per_user_and_never_touches_windows_programs_games_or_cloud_folders()
    {
        using var sandbox = new TestSandbox(SandboxProfile.Default);
        var k = sandbox.Locations;
        Assert.True(SystemToolCatalog.TryResolve(Steps.Tool(ToolIds.CompactLzx, new() { ["folder"] = @"C:\Windows\System32" }), out var plan, out _));
        Assert.True(plan.PerUser);
        Assert.False(plan.NeedsAdmin);

        var env = new FakeToolBackend(sandbox.Root);
        var handler = new SystemToolHandler(env);
        StepRecord Compact(string folder) => Steps.Of(Steps.Tool(ToolIds.CompactLzx, new() { ["folder"] = folder }) with { Reversible = true });

        // The review's case D: compact /exe:lzx on System32 sent to the admin helper.
        var inHelper = await handler.ExecuteAsync(Compact(Path.Combine(k.WindowsDir, "System32")), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Skipped, inHelper.Status);

        foreach (var bad in new[]
                 {
                     Path.Combine(k.WindowsDir, "System32"), Path.Combine(k.ProgramFiles, "Contoso"), Path.Combine(k.ProgramData, "Contoso"),
                     Path.Combine(k.CloudRoots[0], "Old projects"), k.UserProfile /* holds OneDrive */, Path.Combine(k.SteamLibraries[1], "steamapps", "common", "Game"),
                     Path.Combine(sandbox.Root, "Tools", "EasyAntiCheat"), Path.GetPathRoot(sandbox.Root)!,
                 })
        {
            var result = await handler.ExecuteAsync(Compact(bad), sandbox.Context(), default);
            Assert.True(result.Status == StepStatus.Skipped, bad);
        }
        Assert.Empty(env.Ran);

        var mine = await handler.ExecuteAsync(Compact(Path.Combine(k.Downloads, "Old installers")), sandbox.Context(), default);
        Assert.Equal(StepStatus.Done, mine.Status);
    }
}
