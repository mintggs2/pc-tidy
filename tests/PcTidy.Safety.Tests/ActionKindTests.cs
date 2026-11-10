using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Safety.Detection;
using PcTidy.Safety.Rules;
using Xunit;

namespace PcTidy.Safety.Tests;

/// <summary>Every ActionKind has a rule; Check() never acts.</summary>
public sealed class ActionKindTests : IDisposable
{
    private readonly TestSandbox _s = new();
    public void Dispose() => _s.Dispose();

    private static PlanItem Item(ActionKind kind, string feature = "tweaks") => new() { Kind = kind, FeatureId = feature, Description = "test" };

    private void Allowed(PlanItem item) { var v = _s.Guard.Check(item); Assert.True(v.Allowed, $"{item.Kind}: {v.Reason}"); }
    private void Blocked(PlanItem item) { var v = _s.Guard.Check(item); Assert.False(v.Allowed, $"{item.Kind} should be blocked"); Assert.False(string.IsNullOrWhiteSpace(v.Reason)); }

    [Fact]
    public void Every_action_kind_is_handled_and_incomplete_items_are_blocked()
    {
        foreach (var kind in Enum.GetValues<ActionKind>())
        {
            var v = _s.Guard.Check(Item(kind));
            if (kind is ActionKind.EmptyRecycleBin) continue; // has no fields to be incomplete (checked below)
            Assert.False(v.Allowed, $"{kind} with no details must be blocked");
            Assert.DoesNotContain("doesn't know this kind", v.Reason);
        }
        Blocked(Item((ActionKind)999));
        Blocked(Item(ActionKind.QuarantinePath, feature: " ") with { SourcePath = _s.File(Path.Combine(_s.K.Downloads, "a")) });
    }

    [Fact]
    public void Moves_check_both_ends()
    {
        var f = _s.File(Path.Combine(_s.K.Downloads, "a.txt"));
        Allowed(_s.Move(f, Path.Combine(_s.K.Downloads, "Docs", "a.txt")));
        Allowed(_s.Move(f, Path.Combine(_s.K.Downloads, "b.txt"), kind: ActionKind.RenamePath));
        Blocked(_s.Move(f, Path.Combine(_s.K.WindowsDir, "a.txt")));
        Blocked(_s.Move(f, Path.Combine(_s.K.SteamLibraries[0], "steamapps", "a.txt")));
        Blocked(_s.Move(f, Path.Combine(_s.K.DataRoot, "a.txt")));
        Blocked(_s.Move(Path.Combine(_s.K.ProgramFiles, "App", "a.dll"), Path.Combine(_s.K.Downloads, "a.dll")));
        var folder = _s.Dir(Path.Combine(_s.K.Downloads, "Folder"));
        Blocked(_s.Move(folder, Path.Combine(folder, "Inner")));
        Blocked(Item(ActionKind.MovePath) with { SourcePath = f });
        Blocked(Item(ActionKind.MovePath) with { DestinationPath = f });
    }

    [Fact]
    public void Move_with_junction_never_involves_cloud_folders()
    {
        var folder = _s.Dir(Path.Combine(_s.K.Downloads, "Big"));
        Allowed(_s.Move(folder, Path.Combine(_s.Root, "D", "Big"), "drivemove", ActionKind.MoveFolderWithJunction));
        _s.Settings.AllowedAskFirstFolders.Add(_s.K.CloudRoots[0]);
        Blocked(_s.Move(folder, Path.Combine(_s.K.CloudRoots[0], "Big"), "drivemove", ActionKind.MoveFolderWithJunction));
    }

    [Fact]
    public void Create_directory_and_text_files()
    {
        Allowed(Item(ActionKind.CreateDirectory, "organize") with { DestinationPath = Path.Combine(_s.K.Downloads, "New") });
        Blocked(Item(ActionKind.CreateDirectory, "organize") with { DestinationPath = Path.Combine(_s.K.ProgramFiles, "New") });
        Allowed(Item(ActionKind.WriteTextFile, "schedule") with { DestinationPath = Path.Combine(_s.K.Downloads, "report.md") });
        Blocked(Item(ActionKind.WriteTextFile, "schedule") with { DestinationPath = Path.Combine(_s.K.Downloads, "run.bat") });
        Blocked(Item(ActionKind.WriteTextFile, "schedule") with { DestinationPath = Path.Combine(_s.K.Downloads, "run.ps1") });
        Blocked(Item(ActionKind.WriteTextFile, "schedule") with { DestinationPath = Path.Combine(_s.K.WindowsDir, "report.txt") });
    }

    [Fact]
    public void Zip_archive_and_extract()
    {
        var folder = _s.Dir(Path.Combine(_s.K.Downloads, "Old"));
        Allowed(Item(ActionKind.ArchiveToZip, "organize") with { SourcePath = folder, DestinationPath = Path.Combine(_s.K.Downloads, "Old.zip") });
        Blocked(Item(ActionKind.ArchiveToZip, "organize") with { SourcePath = folder, DestinationPath = Path.Combine(_s.K.Downloads, "Old.exe") });
        Blocked(Item(ActionKind.ArchiveToZip, "organize") with { SourcePath = folder, DestinationPath = Path.Combine(folder, "Old.zip") });
        var zip = _s.File(Path.Combine(_s.K.Downloads, "pack.zip"));
        Allowed(Item(ActionKind.ExtractZip, "organize") with { SourcePath = zip, DestinationPath = Path.Combine(_s.K.Downloads, "pack") });
        Blocked(Item(ActionKind.ExtractZip, "organize") with { SourcePath = zip, DestinationPath = Path.Combine(_s.K.WindowsDir, "pack") });
    }

    [Fact]
    public void Online_only_files_are_never_read_for_zipping()
    {
        var f = _s.File(Path.Combine(_s.K.Downloads, "big.iso"));
        File.SetAttributes(f, FileAttributes.Offline);
        Blocked(Item(ActionKind.ArchiveToZip, "organize") with { SourcePath = f, DestinationPath = Path.Combine(_s.K.Downloads, "big.zip") });
        var zip = _s.File(Path.Combine(_s.K.Downloads, "online.zip"));
        File.SetAttributes(zip, FileAttributes.Offline);
        Blocked(Item(ActionKind.ExtractZip, "organize") with { SourcePath = zip, DestinationPath = Path.Combine(_s.K.Downloads, "online") });
    }

    [Fact]
    public void Registry_only_under_the_test_key_for_v1()
    {
        RegistryValueSpec Spec(string key, string? value = "V", RegistryHive hive = RegistryHive.CurrentUser) =>
            new() { Hive = hive, KeyPath = key, ValueName = value, Data = "1" };

        Allowed(Item(ActionKind.RegistrySetValue) with { Registry = Spec(@"Software\PCTidyTest\safety") });
        Allowed(Item(ActionKind.RegistryDeleteValue) with { Registry = Spec(@"\Software\\PCTidyTest\safety\deep\") });
        Allowed(Item(ActionKind.RegistryDeleteKey) with { Registry = Spec(@"Software\PCTidyTest\safety", null) });
        Blocked(Item(ActionKind.RegistryDeleteKey) with { Registry = Spec(@"Software\PCTidyTest", null) });
        Blocked(Item(ActionKind.RegistrySetValue) with { Registry = Spec(@"Software\PCTidyTest") });
        Blocked(Item(ActionKind.RegistrySetValue) with { Registry = Spec(@"Software\PCTidyTestX\a") });
        Blocked(Item(ActionKind.RegistrySetValue) with { Registry = Spec(@"Software\PCTidyTest\..\Microsoft") });
        Blocked(Item(ActionKind.RegistrySetValue) with { Registry = Spec(@"HKEY_CURRENT_USER\Software\PCTidyTest\a") });
        Blocked(Item(ActionKind.RegistrySetValue) with { Registry = Spec(@"Software\PCTidyTest\a", hive: RegistryHive.LocalMachine) });
        Blocked(Item(ActionKind.RegistrySetValue) with { Registry = Spec(@"Software\Microsoft\Windows\CurrentVersion\Run") });
        Blocked(Item(ActionKind.RegistrySetValue) with { Registry = Spec(@"Software\PCTidyTest\a", value: null) });
        Blocked(Item(ActionKind.RegistrySetValue));
    }

    [Fact]
    public void Registry_table_accepts_exact_keys_for_later_tweaks()
    {
        const string adv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
        var policy = SafetyPolicy.Default with
        {
            Registry = SafetyPolicy.Default.Registry.Append(new RegistryRule(RegistryHive.CurrentUser, adv, ValueName: "HideFileExt")).ToList(),
        };
        var guard = new SafetyGuard(_s.K, _s.Settings, policy);
        var ok = Item(ActionKind.RegistrySetValue) with { Registry = new() { Hive = RegistryHive.CurrentUser, KeyPath = adv, ValueName = "HideFileExt", Data = "0" } };
        Assert.True(guard.Check(ok).Allowed);
        Assert.False(guard.Check(ok with { Registry = ok.Registry! with { ValueName = "Hidden" } }).Allowed);
        Assert.False(guard.Check(ok with { Kind = ActionKind.RegistryDeleteKey, Registry = ok.Registry! with { ValueName = null } }).Allowed);
        Assert.False(guard.Check(ok with { Registry = ok.Registry! with { KeyPath = adv + @"\Sub" } }).Allowed);
    }

    [Theory]
    [InlineData("wuauserv")]
    [InlineData("UsoSvc")]
    [InlineData("BITS")]
    [InlineData("WaaSMedicSvc")]
    [InlineData("WinDefend")]
    [InlineData("mpssvc")]
    [InlineData("BFE")]
    [InlineData("VSS")]
    [InlineData("swprv")]
    [InlineData("EasyAntiCheat")]
    [InlineData("EasyAntiCheat_EOS")]
    [InlineData("BEService")]
    [InlineData("vgc")]
    [InlineData("vgk")]
    [InlineData("XblAuthManager")]
    [InlineData("XboxGipSvc")]
    [InlineData("W32Time")]
    [InlineData("wuauSERV")]
    public void Never_touch_services_are_blocked(string name)
    {
        var v = _s.Guard.Check(Item(ActionKind.ServiceSetStartType) with { ServiceName = name, ServiceStartType = ServiceStart.Disabled });
        Assert.False(v.Allowed);
        Assert.Contains(name, v.Reason);
    }

    [Fact]
    public void Other_services_follow_the_shape_rules()
    {
        Allowed(Item(ActionKind.ServiceSetStartType) with { ServiceName = "Fax", ServiceStartType = ServiceStart.Manual });
        Blocked(Item(ActionKind.ServiceSetStartType) with { ServiceName = "Fax" });
        Blocked(Item(ActionKind.ServiceSetStartType) with { ServiceName = "Fax", ServiceStartType = ServiceStart.Boot });
        Blocked(Item(ActionKind.ServiceSetStartType) with { ServiceName = @"..\Fax", ServiceStartType = ServiceStart.Manual });
    }

    [Fact]
    public void Scheduled_tasks_and_startup_items()
    {
        Allowed(Item(ActionKind.ScheduledTaskSetEnabled, "speed") with { TaskPath = @"\Vendor\Updater", Enable = false });
        Blocked(Item(ActionKind.ScheduledTaskSetEnabled, "speed") with { TaskPath = @"\Microsoft\Windows\Defrag\ScheduledDefrag", Enable = false });
        Blocked(Item(ActionKind.ScheduledTaskSetEnabled, "speed") with { TaskPath = @"/microsoft/windows/x", Enable = false });
        Blocked(Item(ActionKind.ScheduledTaskSetEnabled, "speed") with { TaskPath = @"\Vendor\..\Microsoft\x", Enable = false });
        Blocked(Item(ActionKind.ScheduledTaskSetEnabled, "speed") with { TaskPath = @"\Vendor\Updater" });
        Allowed(Item(ActionKind.StartupItemSetEnabled, "speed") with { Args = new() { ["location"] = "HKCU Run", ["name"] = "Updater" }, Enable = false });
        Blocked(Item(ActionKind.StartupItemSetEnabled, "speed") with { Args = new() { ["name"] = "Updater" }, Enable = false });
    }

    [Fact]
    public void Recycle_bin_must_be_marked_permanent()
    {
        Allowed(Item(ActionKind.EmptyRecycleBin, "cleanup") with { Reversible = false });
        Blocked(Item(ActionKind.EmptyRecycleBin, "cleanup"));
    }

    [Fact]
    public void Every_known_tool_id_is_recognised_and_unknown_ones_are_not()
    {
        Assert.Contains(ToolIds.SfcScan, SystemToolRules.KnownIds);
        Assert.True(SystemToolRules.KnownIds.Count >= 26);
        Blocked(Item(ActionKind.RunSystemTool) with { ToolId = "cmd.exe" });
        Blocked(Item(ActionKind.RunSystemTool) with { ToolId = "format" });
        Blocked(Item(ActionKind.RunSystemTool));
    }

    [Fact]
    public void Tool_arguments_are_checked()
    {
        PlanItem Tool(string id, bool reversible = true, params (string K, string V)[] args) => Item(ActionKind.RunSystemTool) with
        {
            ToolId = id, Reversible = reversible, Args = args.ToDictionary(a => a.K, a => a.V),
        };
        Allowed(Tool(ToolIds.SfcScan));
        Allowed(Tool(ToolIds.FlushDns));
        Allowed(Tool(ToolIds.DismAnalyzeComponentStore));
        Allowed(Tool(ToolIds.DismStartComponentCleanup, reversible: false));
        Blocked(Tool(ToolIds.DismStartComponentCleanup, reversible: true));
        Blocked(Tool(ToolIds.DismStartComponentCleanup, false, ("extra", "/ResetBase")));
        Allowed(Tool(ToolIds.CleanmgrHandler, false, ("handler", "Thumbnail Cache")));
        Blocked(Tool(ToolIds.CleanmgrHandler, false, ("handler", "Downloads Folder")));
        Allowed(Tool(ToolIds.HibernateType, true, ("type", "reduced")));
        Blocked(Tool(ToolIds.HibernateType, true, ("type", "off & del C:\\x")));
        Allowed(Tool(ToolIds.WingetUpgrade, true, ("id", "Mozilla.Firefox")));
        Blocked(Tool(ToolIds.WingetUpgrade, true, ("id", "x; rm -rf")));
        Allowed(Tool(ToolIds.PnpDeleteDriver, false, ("inf", "oem42.inf")));
        Blocked(Tool(ToolIds.PnpDeleteDriver, false, ("inf", "nvlddmkm.inf")));
        Blocked(Tool(ToolIds.PnpDeleteDriver, false, ("inf", "oem42.inf"), ("mode", "/force")));
        Allowed(Tool(ToolIds.ShadowStorageResize, false, ("percent", "8")));   // shrinking can delete snapshots: permanent
        Blocked(Tool(ToolIds.ShadowStorageResize, true, ("percent", "8")));
        Blocked(Tool(ToolIds.ShadowStorageResize, false, ("percent", "2")));
        Allowed(Tool(ToolIds.BcdDeleteValue, true, ("name", "useplatformclock")));
        Blocked(Tool(ToolIds.BcdDeleteValue, true, ("name", "bootstatuspolicy")));
        Allowed(Tool(ToolIds.DevCacheClean, false, ("tool", "npm")));
        Blocked(Tool(ToolIds.DevCacheClean, false, ("tool", "everything")));
        Allowed(Tool(ToolIds.DockerPrune, false, ("what", "builder")));
        Allowed(Tool(ToolIds.PnpExportDrivers, true, ("folder", Path.Combine(_s.K.Downloads, "Drivers"))));
        Blocked(Tool(ToolIds.PnpExportDrivers, true, ("folder", Path.Combine(_s.K.WindowsDir, "Drivers"))));
    }

    [Fact]
    public void Known_folder_redirect_rules()
    {
        PlanItem Redirect(Guid id, string dest) => Item(ActionKind.KnownFolderRedirect, "drivemove") with
            { Args = new() { ["folderId"] = id.ToString() }, DestinationPath = dest };
        var d = Path.Combine(_s.Root, "D");
        Allowed(Redirect(KnownFolders.Downloads, Path.Combine(d, "Users", "tester", "Downloads")));
        Blocked(Redirect(KnownFolders.Downloads, d + "\\"));                              // drive root disaster
        Blocked(Redirect(KnownFolders.Documents, Path.Combine(d, "Docs")));               // Documents is in OneDrive here
        Blocked(Redirect(KnownFolders.Videos, Path.Combine(_s.K.CloudRoots[0], "Videos"))); // into a cloud folder
        Blocked(Redirect(KnownFolders.Videos, Path.Combine(_s.K.Downloads, "Videos")));   // inside another user folder
        _s.File(Path.Combine(d, "Full", "x.txt"));
        Blocked(Redirect(KnownFolders.Music, Path.Combine(d, "Full")));                    // not empty
        Blocked(Redirect(Guid.NewGuid(), Path.Combine(d, "X")));
    }

    [Fact]
    public void Store_apps_uninstallers_and_external_targets()
    {
        Allowed(Item(ActionKind.AppxRemoveForUser, "apps") with { Args = new() { ["packageFullName"] = "Clipchamp.Clipchamp_3.0.0.0_neutral__yxz26nhyzhsrt" } });
        Blocked(Item(ActionKind.AppxRemoveForUser, "apps") with { Args = new() { ["packageFullName"] = "Microsoft.WindowsStore_22401.1400.6.0_x64__8wekyb3d8bbwe" } });
        Blocked(Item(ActionKind.AppxRemoveForUser, "apps") with { Args = new() { ["packageFullName"] = "Microsoft.MicrosoftEdge.Stable_1_neutral__8wekyb3d8bbwe" } });

        Allowed(Item(ActionKind.RunUninstaller, "apps") with { Reversible = false, Args = new() { ["msiProductCode"] = "{11111111-2222-3333-4444-555555555555}" } });
        Blocked(Item(ActionKind.RunUninstaller, "apps") with { Args = new() { ["uninstallString"] = "x" } });
        Blocked(Item(ActionKind.RunUninstaller, "apps") with { Reversible = false, Args = new() { ["msiProductCode"] = "not-a-guid" } });

        foreach (var ok in new[] { "ms-settings:storagesense", "steam://open/settings", "https://example.org/help" })
            Allowed(Item(ActionKind.OpenExternal, "help") with { Args = new() { ["target"] = ok } });
        foreach (var bad in new[] { @"C:\Windows\System32\cmd.exe", "file:///C:/x.exe", @"\\server\share\x.exe", "javascript:alert(1)", "notepad.exe" })
            Blocked(Item(ActionKind.OpenExternal, "help") with { Args = new() { ["target"] = bad } });
    }

    [Fact]
    public void Config_edits_and_shredding()
    {
        var ini = _s.File(Path.Combine(_s.K.Downloads, "Game Settings", "settings.ini"));
        var content = new Dictionary<string, string> { ["content"] = "a=1" };
        Allowed(Item(ActionKind.EditTextFile, "games") with { SourcePath = ini, Args = content });
        Blocked(Item(ActionKind.EditTextFile, "games") with { SourcePath = ini });
        Blocked(Item(ActionKind.EditTextFile, "games") with { SourcePath = _s.File(Path.Combine(_s.K.Downloads, "x.bat")), Args = content });
        Blocked(Item(ActionKind.EditTextFile, "games") with { SourcePath = Path.Combine(_s.K.WindowsDir, "win.ini"), Args = content });
        Blocked(Item(ActionKind.EditTextFile, "games") with { SourcePath = Path.Combine(_s.K.ClaudeDir, "settings.json"), Args = content });

        var secret = _s.File(Path.Combine(_s.K.Downloads, "secret.txt"));
        Allowed(Item(ActionKind.ShredFile, "privacy") with { SourcePath = secret, Reversible = false });
        Blocked(Item(ActionKind.ShredFile, "privacy") with { SourcePath = secret });
        Blocked(Item(ActionKind.ShredFile, "privacy") with { SourcePath = _s.Dir(Path.Combine(_s.K.Downloads, "Dir")), Reversible = false });
        Blocked(Item(ActionKind.ShredFile, "privacy") with { SourcePath = Path.Combine(_s.K.WindowsDir, "notepad.exe"), Reversible = false });
        Blocked(Item(ActionKind.ShredFile, "privacy") with { SourcePath = _s.File(Path.Combine(_s.K.CloudRoots[0], "a.txt")), Reversible = false });
    }

    [Fact]
    public void Profiles_features_volumes_apps_and_store_repair()
    {
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        Allowed(Item(ActionKind.UserProfileDelete, "pccare") with { Reversible = false, Args = new() { ["sid"] = "S-1-5-21-1-2-3-1001" } });
        Blocked(Item(ActionKind.UserProfileDelete, "pccare") with { Reversible = false, Args = new() { ["sid"] = me } });
        Blocked(Item(ActionKind.UserProfileDelete, "pccare") with { Reversible = false, Args = new() { ["sid"] = "S-1-5-18" } });

        Allowed(Item(ActionKind.OptionalFeatureRemove, "apps") with { Args = new() { ["name"] = "WorkFolders-Client", ["kind"] = "feature" } });
        Blocked(Item(ActionKind.OptionalFeatureRemove, "apps") with { Args = new() { ["name"] = "Windows-Defender-Default-Definitions", ["kind"] = "feature" } });
        Blocked(Item(ActionKind.OptionalFeatureRemove, "apps") with { Args = new() { ["name"] = "x", ["kind"] = "driver" } });

        Blocked(Item(ActionKind.VolumeOptimize, "pccare") with { Args = new() { ["drive"] = "C" } }); // sandbox has no real drives

        Allowed(Item(ActionKind.CloseApp, "games") with { Args = new() { ["pid"] = "4242", ["processName"] = "chrome.exe" } });
        foreach (var never in new[] { "lsass", "explorer.exe", "MsMpEng", "EasyAntiCheat_EOS", "vgc", "csrss" })
            Blocked(Item(ActionKind.CloseApp, "games") with { Args = new() { ["pid"] = "4242", ["processName"] = never } });
        Blocked(Item(ActionKind.CloseApp, "games") with { Args = new() { ["pid"] = "4", ["processName"] = "chrome" } });

        Allowed(Item(ActionKind.StoreRepair, "pccare") with { Args = new() { ["wsreset"] = "1" } });
        Allowed(Item(ActionKind.RestorePointDelete, "pccare") with { Reversible = false, Args = new() { ["sequenceNumber"] = "12" } });
        Blocked(Item(ActionKind.RestorePointDelete, "pccare") with { Args = new() { ["sequenceNumber"] = "12" } });
        Allowed(Item(ActionKind.WlanProfileDelete, "pccare") with { Args = new() { ["profileName"] = "Cafe", ["interfaceGuid"] = Guid.NewGuid().ToString() } });
        Allowed(Item(ActionKind.ShellExtensionSetEnabled, "search") with { Enable = false, Args = new() { ["clsid"] = Guid.NewGuid().ToString("B") } });
        Allowed(Item(ActionKind.CookiesDeleteExcept, "privacy") with { Args = new() { ["browser"] = "edge", ["profileDir"] = _s.Dir(Path.Combine(_s.K.LocalAppData, "Edge", "Default")) } });
        Blocked(Item(ActionKind.CookiesDeleteExcept, "privacy") with { Args = new() { ["browser"] = "edge", ["profileDir"] = Path.Combine(_s.K.ProgramFiles, "Edge") } });
    }

    [Fact]
    public void Power_hosts_and_shell_verbs()
    {
        Allowed(Item(ActionKind.PowerPlanSet, "speed") with { Args = new() { ["schemeGuid"] = "381b4222-f694-41f0-9685-ff5bb260df2e" } });
        Blocked(Item(ActionKind.PowerPlanSet, "speed") with { Args = new() { ["schemeGuid"] = "balanced" } });
        Allowed(Item(ActionKind.HostsCommentOut, "pccare") with { Args = new() { ["lines"] = "0.0.0.0 example.org" } });
        Allowed(Item(ActionKind.ShellVerbSetInstalled, "search") with { Enable = true, Args = new() { ["verb"] = "PCTidyWhatIs", ["command"] = "x" } });
        Allowed(Item(ActionKind.ShellVerbSetInstalled, "search") with { Enable = false, Args = new() { ["verb"] = "PCTidyWhatIs" } });
        Blocked(Item(ActionKind.ShellVerbSetInstalled, "search") with { Enable = true, Args = new() { ["verb"] = @"..\open", ["command"] = "x" } });
    }
}
