using PcTidy.Catalog;
using PcTidy.Core.Model;
using PcTidy.Safety.Rules;
using Xunit;

namespace PcTidy.Safety.Tests;

/// <summary>
/// v3.1 guard rules: the fix-it tools (service.restart, wu.resetcomponents, msi.reregister, winget.install), the
/// safe-boot BCD values, DeviceSetEnabled, and patterned registry allowances. Check() only reads; nothing changes.
/// </summary>
public sealed class V31RulesTests : IDisposable
{
    private readonly TestSandbox _s = new();
    public void Dispose() => _s.Dispose();

    private static PlanItem Tool(string id, bool admin = true, bool systemChange = false, bool reversible = false, params (string K, string V)[] args) => new()
    {
        Kind = ActionKind.RunSystemTool, FeatureId = "pccare", Description = "test", ToolId = id, NeedsAdmin = admin, IsSystemChange = systemChange,
        Reversible = reversible, Args = args.Length == 0 ? null : args.ToDictionary(a => a.K, a => a.V),
    };

    private static void Allowed(SafetyGuard guard, PlanItem item) { var v = guard.Check(item); Assert.True(v.Allowed, $"{item.ToolId ?? item.Kind.ToString()}: {v.Reason}"); }
    private static void Blocked(SafetyGuard guard, PlanItem item) { var v = guard.Check(item); Assert.False(v.Allowed, $"{item.ToolId ?? item.Kind.ToString()} should be blocked"); Assert.False(string.IsNullOrWhiteSpace(v.Reason)); }
    private void Allowed(PlanItem item) => Allowed(_s.Guard, item);
    private void Blocked(PlanItem item) => Blocked(_s.Guard, item);

    // ---------------------------------------------------------------- service.restart

    [Fact]
    public void Service_restart_allows_only_the_listed_services_with_admin_and_marked_permanent()
    {
        foreach (var name in new[] { "WlanSvc", "bthserv", "BTAGService", "wuauserv", "BITS", "CryptSvc", "msiserver", "Audiosrv", "AudioEndpointBuilder", "wlansvc" })
            Allowed(Tool(ToolIds.ServiceRestart, args: ("name", name)));
        foreach (var bad in new[] { "WinDefend", "mpssvc", "SecurityHealthService", "EventLog", "RpcSs", "Spooler", "WlanSvc;calc", "", "BITS " + "x" })
            Blocked(Tool(ToolIds.ServiceRestart, args: ("name", bad)));
        Blocked(Tool(ToolIds.ServiceRestart));                                                         // no name
        Blocked(Tool(ToolIds.ServiceRestart, admin: false, args: ("name", "BITS")));                   // needs admin
        Blocked(Tool(ToolIds.ServiceRestart, reversible: true, args: ("name", "BITS")));               // nothing to undo: must say so
        Blocked(Tool(ToolIds.ServiceRestart, args: new[] { ("name", "BITS"), ("startType", "Disabled") })); // never a start type
    }

    // ---------------------------------------------------------------- wu.resetcomponents

    [Fact]
    public void Windows_update_reset_takes_no_settings_at_all_and_needs_admin_and_a_restore_point()
    {
        Allowed(Tool(ToolIds.WindowsUpdateReset, systemChange: true));
        Allowed(Tool(ToolIds.WindowsUpdateReset, systemChange: true) with { Args = new() });
        foreach (var arg in new[] { ("folder", @"C:\Windows\System32"), ("path", @"C:\Windows\SoftwareDistribution"), ("x", ""), ("services", "wuauserv") })
            Blocked(Tool(ToolIds.WindowsUpdateReset, systemChange: true, args: arg));
        Blocked(Tool(ToolIds.WindowsUpdateReset, systemChange: false));
        Blocked(Tool(ToolIds.WindowsUpdateReset, admin: false, systemChange: true));
        Blocked(Tool(ToolIds.WindowsUpdateReset, systemChange: true, reversible: true));
    }

    [Fact]
    public void The_windows_update_exception_opens_nothing_else_in_the_windows_folder()
    {
        // The reset's folders are fixed in the platform's code; the guard still refuses every ordinary change there.
        var sd = Path.Combine(_s.K.WindowsDir, "SoftwareDistribution");
        Directory.CreateDirectory(sd);
        Blocked(new PlanItem { Kind = ActionKind.QuarantinePath, FeatureId = "pccare", Description = "t", SourcePath = sd, IsDirectory = true });
        Blocked(new PlanItem { Kind = ActionKind.RenamePath, FeatureId = "pccare", Description = "t", SourcePath = sd, DestinationPath = sd + ".old" });
        Blocked(new PlanItem { Kind = ActionKind.MovePath, FeatureId = "pccare", Description = "t", SourcePath = sd, DestinationPath = Path.Combine(_s.K.Downloads, "sd") });
    }

    // ---------------------------------------------------------------- msi.reregister

    [Fact]
    public void Msi_reregister_needs_admin_and_takes_no_settings()
    {
        Allowed(Tool(ToolIds.MsiReregister));
        Blocked(Tool(ToolIds.MsiReregister, admin: false));
        Blocked(Tool(ToolIds.MsiReregister, args: ("package", "x.msi")));
        Blocked(Tool(ToolIds.MsiReregister, reversible: true));
    }

    // ---------------------------------------------------------------- winget.install

    [Fact]
    public void Winget_install_allows_only_microsofts_runtimes_and_never_with_admin_rights()
    {
        foreach (var id in new[] { "Microsoft.VCRedist.2015+.x64", "Microsoft.VCRedist.2015+.x86", "Microsoft.DirectX", "Microsoft.DotNet.DesktopRuntime.8", "Microsoft.DotNet.DesktopRuntime.9" })
            Allowed(Tool(ToolIds.WingetInstall, admin: false, args: ("id", id)));
        foreach (var bad in new[] { "Mozilla.Firefox", "Microsoft.Edge", "Microsoft.DotNet.DesktopRuntime.7", "Microsoft.DotNet.Runtime.8", "Microsoft.VCRedist.2013.x64",
                     "Microsoft.DirectX --override /x", "Microsoft.DirectX;calc", "" })
            Blocked(Tool(ToolIds.WingetInstall, admin: false, args: ("id", bad)));
        Blocked(Tool(ToolIds.WingetInstall, admin: true, args: ("id", "Microsoft.DirectX")));       // the admin helper never runs winget
        Blocked(Tool(ToolIds.WingetInstall, admin: false, reversible: true, args: ("id", "Microsoft.DirectX")));
        Blocked(Tool(ToolIds.WingetInstall, admin: false, args: new[] { ("id", "Microsoft.DirectX"), ("source", "msstore") }));
        Blocked(Tool(ToolIds.WingetInstall, admin: false));
    }

    [Fact]
    public void Every_v31_tool_id_is_known_and_marked_permanent()
    {
        foreach (var id in new[] { ToolIds.ServiceRestart, ToolIds.WindowsUpdateReset, ToolIds.MsiReregister, ToolIds.WingetInstall })
        {
            Assert.Contains(id, SystemToolRules.KnownIds);
            Assert.Contains(id, SystemToolRules.Permanent);
        }
    }

    // ---------------------------------------------------------------- safe-boot leftovers

    [Fact]
    public void Safe_boot_flags_may_be_removed_but_nothing_new_opens_up()
    {
        Allowed(Tool(ToolIds.BcdDeleteValue, reversible: true, args: ("name", "safeboot")));
        Allowed(Tool(ToolIds.BcdDeleteValue, reversible: true, args: ("name", "SafeBootAlternateShell")));
        foreach (var bad in new[] { "nx", "hypervisorlaunchtype", "bootmenupolicy", "safeboot minimal", "recoveryenabled" })
            Blocked(Tool(ToolIds.BcdDeleteValue, reversible: true, args: ("name", bad)));
    }

    // ---------------------------------------------------------------- DeviceSetEnabled

    private sealed class FakeClasses(DeviceClassInfo info) : IDeviceClassSource
    {
        public List<string> Asked { get; } = new();
        public DeviceClassInfo Find(string instanceId) { Asked.Add(instanceId); return info; }
    }

    private const string Camera = @"USB\VID_1234&PID_ABCD&MI_00\6&1A2B3C4D&0&0000";

    private static PlanItem Device(string id = Camera, bool? enable = true, bool admin = true, bool systemChange = true, string? cls = null) => new()
    {
        Kind = ActionKind.DeviceSetEnabled, FeatureId = "pccare", Description = "Turn the camera back on", Enable = enable, NeedsAdmin = admin,
        IsSystemChange = systemChange, Args = cls is null ? new() { ["instanceId"] = id } : new() { ["instanceId"] = id, ["class"] = cls },
    };

    private SafetyGuard GuardWith(DeviceClassInfo info) => new(_s.K, _s.Settings, deviceClasses: new FakeClasses(info));

    [Fact]
    public void Device_enable_is_allowed_for_an_ordinary_device_with_admin_and_a_restore_point()
    {
        var guard = GuardWith(new DeviceClassInfo(DeviceClassState.Found, "Camera", new Guid("ca3e7ab9-b4c3-4ae6-8251-579ef933890f")));
        Allowed(guard, Device());
        Allowed(guard, Device(cls: "Camera"));
        Blocked(guard, Device(admin: false));
        Blocked(guard, Device(systemChange: false));
        Blocked(guard, Device() with { Args = new() { ["instanceId"] = Camera, ["force"] = "true" } });
    }

    [Fact]
    public void Device_disable_is_refused_outside_undo()
    {
        var guard = GuardWith(new DeviceClassInfo(DeviceClassState.Found, "Camera"));
        Blocked(guard, Device(enable: false));
        Blocked(guard, Device(enable: null));
    }

    [Theory]
    [InlineData("DiskDrive")]
    [InlineData("Volume")]
    [InlineData("VolumeSnapshot")]
    [InlineData("SCSIAdapter")]
    [InlineData("HDC")]
    [InlineData("System")]
    [InlineData("Firmware")]
    [InlineData("Processor")]
    [InlineData("Computer")]
    [InlineData("SecurityDevices")]
    [InlineData("diskdrive")]
    public void Device_enable_is_refused_for_classes_that_could_affect_boot(string cls)
    {
        Blocked(GuardWith(new DeviceClassInfo(DeviceClassState.Found, cls)), Device());
        // The feature's own claim is checked too, even when Windows reports an ordinary class.
        Blocked(GuardWith(new DeviceClassInfo(DeviceClassState.Found, "Camera")), Device(cls: cls));
    }

    [Theory]
    [InlineData("4d36e967-e325-11ce-bfc1-08002be10318")] // DiskDrive
    [InlineData("4d36e97b-e325-11ce-bfc1-08002be10318")] // SCSIAdapter
    [InlineData("4d36e97d-e325-11ce-bfc1-08002be10318")] // System
    [InlineData("d94ee5d8-d189-4994-83d2-f68d7d41b0e6")] // SecurityDevices
    public void Device_enable_is_refused_by_protected_class_guid_even_without_a_class_name(string guid)
        => Blocked(GuardWith(new DeviceClassInfo(DeviceClassState.Found, null, new Guid(guid))), Device());

    [Theory]
    [InlineData("")]
    [InlineData("USB")]
    [InlineData(@"\USB\VID_1")]
    [InlineData(@"USB\\VID_1")]
    [InlineData(@"USB\..\..\Control")]
    [InlineData(@"USB\.\VID_1")]
    [InlineData(@"USB\VID 1\0")]
    [InlineData("USB\\VID_1\\0\" /force")]
    [InlineData(@"USB\VID_1\0|calc")]
    [InlineData("USB\\VID_1\\0\n")]
    public void Device_enable_is_refused_for_malformed_instance_ids(string id)
    {
        var classes = new FakeClasses(new DeviceClassInfo(DeviceClassState.Found, "Camera"));
        var guard = new SafetyGuard(_s.K, _s.Settings, deviceClasses: classes);
        Blocked(guard, Device(id));
        Assert.Empty(classes.Asked); // a bad id is never used to read the registry
    }

    [Fact]
    public void Device_enable_is_refused_when_the_class_cant_be_read()
    {
        Blocked(GuardWith(new DeviceClassInfo(DeviceClassState.Unreadable)), Device());
        // A practice PC has no real devices: a missing one is fine there (the real handler only simulates).
        Allowed(GuardWith(new DeviceClassInfo(DeviceClassState.Missing)), Device());
    }

    // ---------------------------------------------------------------- patterned registry allowances

    // A test-only key shaped like a network adapter class key, so no real catalog entry can answer for it.
    private const string NetClass = @"SOFTWARE\ContosoV31Test\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";

    private static RegistryAllowance AdapterPower() => new("test.adapter-power", RegistryHive.LocalMachine, NetClass + @"\0000", "PnPCapabilities",
        RegistryKind.DWord, new[] { "24" }, AllowDelete: true, NeedsAdmin: true)
    {
        KeyPathPattern = @"^SOFTWARE\\ContosoV31Test\\Control\\Class\\\{4d36e972-e325-11ce-bfc1-08002be10318\}\\\d{4}$",
    };

    private static RegistryAllowance GpuPreference() => new("test.gpu-preference", RegistryHive.CurrentUser, @"Software\ContosoV31Test\UserGpuPreferences", null,
        RegistryKind.String, Array.Empty<string>(), DataPattern: @"^GpuPreference=[0-2];$")
    {
        ValueNamePattern = @"^[A-Za-z]:\\[^<>""|?*\r\n]+\.exe$",
    };

    private static PlanItem Reg(ActionKind kind, RegistryHive hive, string key, string? name, RegistryKind type = RegistryKind.DWord, string? data = null,
        bool admin = false, bool systemChange = false) => new()
    {
        Kind = kind, FeatureId = "pccare", Description = "t", NeedsAdmin = admin, IsSystemChange = systemChange,
        Registry = new RegistryValueSpec { Hive = hive, KeyPath = key, ValueName = name, Kind = type, Data = data },
    };

    [Fact]
    public void Unanchored_patterns_never_match()
    {
        Assert.False(RegistryCatalog.FullMatch(@"SYSTEM\\.*", @"SYSTEM\X"));
        Assert.False(RegistryCatalog.FullMatch(@"^SYSTEM\\.*", @"SYSTEM\X"));
        Assert.False(RegistryCatalog.FullMatch(@"SYSTEM\\.*$", @"SYSTEM\X"));
        Assert.True(RegistryCatalog.FullMatch(@"^SYSTEM\\.*$", @"SYSTEM\X"));

        var loose = new RegistryAllowance("test.loose", RegistryHive.CurrentUser, @"Software\ContosoV31Test\loose", "V", RegistryKind.DWord, new[] { "1" })
            { KeyPathPattern = @"Software\\ContosoV31Test\\.*" };
        using (RegistryCatalog.WithTestAllowances(loose))
        {
            Assert.Null(RegistryCatalog.Find(RegistryHive.CurrentUser, @"Software\ContosoV31Test\loose\x", "V"));
            Blocked(Reg(ActionKind.RegistrySetValue, RegistryHive.CurrentUser, @"Software\ContosoV31Test\loose\x", "V", data: "1"));
        }
    }

    [Fact]
    public void Patterns_match_case_insensitively_and_time_out_safely()
    {
        Assert.True(RegistryCatalog.FullMatch(@"^system\\currentcontrolset$", @"SYSTEM\CurrentControlSet"));
        var started = DateTime.UtcNow;
        Assert.False(RegistryCatalog.FullMatch(@"^(a+)+$", new string('a', 40) + "!"));   // catastrophic backtracking: times out → no
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Full_match_is_strict_about_line_breaks_control_characters_and_non_ascii_digits()
    {
        foreach (var input in new[]
                 {
                     "SYSTEM\\X\\0007\n",                 // $ in .NET also matches before a final line break
                     "SYSTEM\\X\\0007\r\n",
                     "SYSTEM\\X\\0007\0",
                     "SYSTEM\\X\\00\u000107",
                     "SYSTEM\\X\\\u0661\u0662\u0663\u0664", // Arabic-Indic digits: \d in .NET matches them
                     "SYSTEM\\X\\\uFF10\uFF10\uFF10\uFF17", // full-width digits
                     "SYSTEM\\X\\\u0966\u0966\u0966\u0967", // Devanagari digits
                 })
        {
            Assert.False(RegistryCatalog.FullMatch(@"^SYSTEM\\X\\\d{4}$", input), input);
            Assert.False(RegistryCatalog.FullMatch(@"^SYSTEM\\X\\[\d]{4}$", input), input);
        }
        Assert.False(RegistryCatalog.FullMatch(@"^SYSTEM\\X\\.*$", "SYSTEM\\X\\a\n"));
        Assert.True(RegistryCatalog.FullMatch(@"^SYSTEM\\X\\\d{4}$", @"SYSTEM\X\0007"));
        Assert.True(RegistryCatalog.FullMatch(@"^SYSTEM\\X\\[\d]{4}$", @"SYSTEM\X\0007"));
    }

    [Fact]
    public void Full_match_needs_a_real_anchor_and_keeps_alternatives_inside_it()
    {
        Assert.False(RegistryCatalog.FullMatch(@"^SYSTEM\\X\$", @"SYSTEM\X$"));          // an escaped $ is not an anchor
        Assert.True(RegistryCatalog.FullMatch(@"^SYSTEM\\X\\\$$", @"SYSTEM\X\$"));      // an escaped $, then the anchor
        Assert.False(RegistryCatalog.FullMatch(@"^a$|^b$", "a\nb"));
        Assert.True(RegistryCatalog.FullMatch(@"^a|b$", "b"));
        Assert.False(RegistryCatalog.FullMatch(@"^a|b$", "ab"));                        // the anchors hold for the whole pattern
        Assert.False(RegistryCatalog.FullMatch(@"^(unclosed$", "x"));                   // a broken pattern never matches
        Assert.True(RegistryCatalog.FullMatch(@"^\D\d$", "x7"));
        Assert.False(RegistryCatalog.FullMatch(@"^\D\d$", "x\u0667"));
    }

    [Fact]
    public void The_guard_refuses_line_breaks_and_nul_in_value_names_and_patterned_data()
    {
        using var _ = RegistryCatalog.WithTestAllowances(GpuPreference(), AdapterPower());
        const string key = @"Software\ContosoV31Test\UserGpuPreferences";
        PlanItem Gpu(string name, string data) => Reg(ActionKind.RegistrySetValue, RegistryHive.CurrentUser, key, name, RegistryKind.String, data);

        Allowed(Gpu(@"C:\Games\Contoso\game.exe", "GpuPreference=2;"));
        Blocked(Gpu("C:\\Games\\Contoso\\game.exe\n", "GpuPreference=2;"));
        Blocked(Gpu("C:\\Games\\Contoso\\game.exe\0", "GpuPreference=2;"));
        Blocked(Gpu(@"C:\Games\Contoso\game.exe", "GpuPreference=2;\n"));
        Blocked(Gpu(@"C:\Games\Contoso\game.exe", "GpuPreference=2;\0"));
        Assert.False(SafetyGuard.DataMatches(@"^GpuPreference=\d;$", "GpuPreference=\u0662;"));
        Assert.True(SafetyGuard.DataMatches(@"^GpuPreference=\d;$", "GpuPreference=2;"));
        Assert.False(SafetyGuard.DataMatches(@"^gpupreference=\d;$", "GpuPreference=2;")); // data stays case-sensitive

        // A key ending in Arabic-Indic digits is no adapter key.
        Blocked(Reg(ActionKind.RegistrySetValue, RegistryHive.LocalMachine, NetClass + "\\\u0661\u0662\u0663\u0664", "PnPCapabilities", data: "24", admin: true, systemChange: true));
        Allowed(Reg(ActionKind.RegistrySetValue, RegistryHive.LocalMachine, NetClass + @"\1234", "PnPCapabilities", data: "24", admin: true, systemChange: true));
    }

    [Fact]
    public void Device_class_lists_include_security_accelerators()
    {
        Assert.Contains("SecurityAccelerator", ProtectedDeviceClasses.Names);
        Assert.Contains(new Guid("268c95a1-edfe-11d3-95c3-0010dc4050a5"), ProtectedDeviceClasses.Guids);

        Blocked(GuardWith(new DeviceClassInfo(DeviceClassState.Found, "SecurityAccelerator")), Device());
        Blocked(GuardWith(new DeviceClassInfo(DeviceClassState.Found, null, new Guid("268c95a1-edfe-11d3-95c3-0010dc4050a5"))), Device());
    }

    [Fact]
    public void Exact_entries_win_over_patterned_ones()
    {
        var exact = new RegistryAllowance("test.exact", RegistryHive.LocalMachine, NetClass + @"\0003", "PnPCapabilities", RegistryKind.DWord, new[] { "0" }, NeedsAdmin: true);
        var patterned = AdapterPower();
        Assert.Same(exact, RegistryCatalog.Find(new[] { patterned, exact }, RegistryHive.LocalMachine, NetClass + @"\0003", "pnpcapabilities"));
        Assert.Same(patterned, RegistryCatalog.Find(new[] { patterned, exact }, RegistryHive.LocalMachine, NetClass + @"\0004", "PnPCapabilities"));

        using (RegistryCatalog.WithTestAllowances(patterned, exact))
        {
            // The exact entry decides for 0003 (only "0"); the pattern decides for the other adapters (only "24").
            Allowed(Reg(ActionKind.RegistrySetValue, RegistryHive.LocalMachine, NetClass + @"\0003", "PnPCapabilities", data: "0", admin: true, systemChange: true));
            Blocked(Reg(ActionKind.RegistrySetValue, RegistryHive.LocalMachine, NetClass + @"\0003", "PnPCapabilities", data: "24", admin: true, systemChange: true));
            Allowed(Reg(ActionKind.RegistrySetValue, RegistryHive.LocalMachine, NetClass + @"\0012", "PnPCapabilities", data: "0x18", admin: true, systemChange: true));
        }
    }

    [Fact]
    public void Patterned_hklm_entries_still_need_admin_and_a_restore_point()
    {
        using var _ = RegistryCatalog.WithTestAllowances(AdapterPower());
        PlanItem Set(string key, string? name = "PnPCapabilities", string data = "24", bool admin = true, bool sys = true)
            => Reg(ActionKind.RegistrySetValue, RegistryHive.LocalMachine, key, name, data: data, admin: admin, systemChange: sys);

        Allowed(Set(NetClass + @"\0007"));
        Allowed(Set(NetClass.ToLowerInvariant() + @"\0007"));                         // case-insensitive
        Allowed(Reg(ActionKind.RegistryDeleteValue, RegistryHive.LocalMachine, NetClass + @"\0007", "PnPCapabilities", admin: true, systemChange: true));
        Blocked(Set(NetClass + @"\0007", admin: false));
        Blocked(Set(NetClass + @"\0007", sys: false));
        Blocked(Set(NetClass + @"\0007", data: "0"));                                  // only the listed data
        Blocked(Set(NetClass + @"\0007", name: "DriverDesc"));                         // only the listed value
        Blocked(Set(NetClass + @"\00071"));
        Blocked(Set(NetClass + @"\0007\Ndi"));
        Blocked(Set(NetClass + @"\Properties"));
        Blocked(Reg(ActionKind.RegistryDeleteKey, RegistryHive.LocalMachine, NetClass + @"\0007", null, admin: true, systemChange: true));
        Blocked(Reg(ActionKind.RegistrySetValue, RegistryHive.CurrentUser, NetClass + @"\0007", "PnPCapabilities", data: "24"));
    }

    [Fact]
    public void Key_paths_are_normalized_before_any_pattern_is_tried()
    {
        using var _ = RegistryCatalog.WithTestAllowances(AdapterPower());
        PlanItem Set(string key) => Reg(ActionKind.RegistrySetValue, RegistryHive.LocalMachine, key, "PnPCapabilities", data: "24", admin: true, systemChange: true);

        Allowed(Set(@"\" + NetClass + @"\0007\"));                                      // leading/trailing slashes are only trimmed
        Blocked(Set(NetClass + @"\0007\..\0008"));
        Blocked(Set(NetClass + @"\..\..\Services\Tcpip\0007"));
        Blocked(Set(NetClass + @"\.\0007"));
        Blocked(Set(@"HKLM\" + NetClass + @"\0007"));
        Blocked(Set(@"HKEY_LOCAL_MACHINE\" + NetClass + @"\0007"));
        Blocked(Set(@"HKLM:\" + NetClass + @"\0007"));
        Blocked(Set(NetClass + "\\0007\n"));
    }

    [Fact]
    public void Patterned_value_names_need_a_matching_name_and_anchored_data()
    {
        var gpu = GpuPreference();
        using var _ = RegistryCatalog.WithTestAllowances(gpu);
        PlanItem Set(string name, string data) => Reg(ActionKind.RegistrySetValue, RegistryHive.CurrentUser, gpu.KeyPath, name, RegistryKind.String, data);

        Allowed(Set(@"C:\Games\Contoso Racer\racer.exe", "GpuPreference=2;"));
        Allowed(Reg(ActionKind.RegistryDeleteValue, RegistryHive.CurrentUser, gpu.KeyPath, @"D:\Games\x.EXE"));
        Blocked(Set(@"C:\Games\Contoso Racer\racer.exe", "GpuPreference=2;SwapEffectUpgradeEnable=1;"));
        Blocked(Set(@"C:\Games\Contoso Racer\racer.exe", "GpuPreference=3;"));
        Blocked(Set(@"C:\Games\Contoso Racer\racer.bat", "GpuPreference=2;"));
        Blocked(Set(@"racer.exe", "GpuPreference=2;"));
        Blocked(Set(@"C:\Games\*.exe", "GpuPreference=2;"));
        Blocked(Reg(ActionKind.RegistrySetValue, RegistryHive.CurrentUser, gpu.KeyPath, null, RegistryKind.String, "GpuPreference=2;"));
        Blocked(Reg(ActionKind.RegistryDeleteKey, RegistryHive.CurrentUser, gpu.KeyPath, null));
        Blocked(Reg(ActionKind.RegistrySetValue, RegistryHive.CurrentUser, gpu.KeyPath + @"\Sub", @"C:\x.exe", RegistryKind.String, "GpuPreference=2;"));

        // A data pattern without anchors is a no, never a partial match.
        var loose = gpu with { DataPattern = "GpuPreference=[0-2];", ValueNamePattern = gpu.ValueNamePattern, KeyPath = @"Software\ContosoV31Test\Loose" };
        using (RegistryCatalog.WithTestAllowances(loose))
            Blocked(Reg(ActionKind.RegistrySetValue, RegistryHive.CurrentUser, loose.KeyPath, @"C:\x.exe", RegistryKind.String, "GpuPreference=2;"));
    }

    [Fact]
    public void Test_allowances_are_gone_after_dispose()
    {
        using (RegistryCatalog.WithTestAllowances(AdapterPower()))
            Assert.NotNull(RegistryCatalog.Find(RegistryHive.LocalMachine, NetClass + @"\0001", "PnPCapabilities"));
        Assert.Null(RegistryCatalog.Find(RegistryHive.LocalMachine, NetClass + @"\0001", "PnPCapabilities"));
        Assert.DoesNotContain(RegistryCatalog.All, a => a.Source.StartsWith("test.", StringComparison.Ordinal));
    }
}
