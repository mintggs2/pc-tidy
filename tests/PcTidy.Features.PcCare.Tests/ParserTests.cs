using System.Buffers.Binary;
using PcTidy.Catalog.PcCare;
using PcTidy.Features.PcCare.Model;
using PcTidy.Features.PcCare.Parsing;

namespace PcTidy.Features.PcCare.Tests;

public class SecurityCenterStateTests
{
    [Theory]
    [InlineData(0x061100u, ProductRunState.On, true, false)]      // Defender on, up to date
    [InlineData(0x062100u, ProductRunState.Snoozed, true, false)] // Defender switched off by policy (the owner's PC: 401664)
    [InlineData(0x060100u, ProductRunState.Off, true, false)]
    [InlineData(0x041000u, ProductRunState.On, false, false)]     // third-party on
    [InlineData(0x041010u, ProductRunState.On, false, true)]      // third-party on, definitions out of date
    [InlineData(0x043000u, ProductRunState.Expired, false, false)]
    public void Product_state_bits_decode(uint state, ProductRunState run, bool microsoft, bool outOfDate)
    {
        var d = SecurityCenterState.Decode(state);
        Assert.Equal(run, d.State);
        Assert.Equal(microsoft, d.IsMicrosoft);
        Assert.Equal(outOfDate, d.SignaturesOutOfDate);
        Assert.Equal(run == ProductRunState.On, d.IsActive);
    }

    [Fact]
    public void The_owners_value_401664_is_not_active()
        => Assert.False(SecurityCenterState.Decode(401664).IsActive);

    [Fact]
    public void Defender_is_recognised_by_its_executable_not_its_translated_name()
    {
        Assert.True(SecurityCenterState.IsDefender(@"%ProgramFiles%\Windows Defender\MsMpeng.exe", "Microsoft Defender Antivirus (localized)"));
        Assert.False(SecurityCenterState.IsDefender(@"C:\Program Files\OtherAV\report.exe", "Other AV"));
    }
}

public class MinidumpHeaderTests
{
    private static byte[] Dump64(uint code, params ulong[] p)
    {
        var b = new byte[0x2000];
        "PAGEDU64"u8.CopyTo(b);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x38), code);
        for (int i = 0; i < p.Length; i++) BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(0x40 + 8 * i), p[i]);
        return b;
    }

    [Fact]
    public void A_64_bit_header_gives_the_stop_code_and_parameters()
    {
        var h = MinidumpHeader.TryParse(Dump64(0x116, 0xFFFF_A000_1234_5678, 0xFFFF_F800_0000_0001, 0, 0xD));
        Assert.NotNull(h);
        Assert.True(h!.Is64Bit);
        Assert.Equal(0x116u, h.Code);
        Assert.Equal(new ulong[] { 0xFFFF_A000_1234_5678, 0xFFFF_F800_0000_0001, 0, 0xD }, h.Parameters);
        Assert.Equal("VIDEO_TDR_FAILURE", BugcheckCatalog.Find(h.Code)!.Name);
    }

    [Fact]
    public void A_32_bit_header_is_read_from_its_own_offsets()
    {
        var b = new byte[0x1000];
        "PAGEDUMP"u8.CopyTo(b);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x28), 0xD1);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x2C), 7);
        var h = MinidumpHeader.TryParse(b);
        Assert.Equal(0xD1u, h!.Code);
        Assert.False(h.Is64Bit);
        Assert.Equal(7ul, h.Parameters[0]);
    }

    [Theory]
    [InlineData("MDMP")]        // a user-mode dump, not a kernel dump
    [InlineData("PAGEXXXX")]
    public void Other_files_are_not_guessed_at(string magic)
    {
        var b = new byte[0x100];
        System.Text.Encoding.ASCII.GetBytes(magic).CopyTo(b, 0);
        Assert.Null(MinidumpHeader.TryParse(b));
    }

    [Fact]
    public void A_truncated_file_is_ignored() => Assert.Null(MinidumpHeader.TryParse(Dump64(0x50).AsSpan(0, 0x40)));

    [Fact]
    public void The_extended_parameter_bit_maps_to_the_base_code()
        => Assert.Equal("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", BugcheckCatalog.Find(0x1000007E)!.Name);
}

public class CrashEventMapperTests
{
    private static readonly DateTime T = FixedClock.Now;

    [Fact]
    public void BugCheck_1001_reads_the_stop_code_from_its_hex_property()
    {
        var e = CrashEventMapper.Map(new RawEvent(CrashEventMapper.BugCheckProvider, 1001, T,
            new object?[] { "0x0000009f (0x0000000000000003, 0xffffc50b7a2d4060, 0xfffff8017ae82750, 0xffffc50b7d0c48a0)", @"C:\Windows\Minidump\x.dmp", "id" }));
        Assert.Equal(CrashKind.BlueScreen, e!.Kind);
        Assert.Equal(0x9Fu, e.BugcheckCode);
    }

    [Fact]
    public void Kernel_Power_41_without_a_code_is_an_unexpected_restart()
    {
        var e = CrashEventMapper.Map(new RawEvent(CrashEventMapper.KernelPowerProvider, 41, T, new object?[] { 0u, 0ul, 0ul, 0ul, 0ul, false, 0u }));
        Assert.Equal(CrashKind.UnexpectedShutdown, e!.Kind);
        Assert.Null(e.BugcheckCode);
    }

    [Fact]
    public void Kernel_Power_41_with_a_code_keeps_it()
    {
        var e = CrashEventMapper.Map(new RawEvent(CrashEventMapper.KernelPowerProvider, 41, T, new object?[] { 278u, 1ul }));
        Assert.Equal(0x116u, e!.BugcheckCode);
    }

    [Theory]
    [InlineData(18, CrashKind.HardwareError)]
    [InlineData(19, CrashKind.HardwareCorrected)]
    [InlineData(47, CrashKind.HardwareCorrected)]
    public void WHEA_ids_map_to_hardware_kinds(int id, CrashKind kind)
        => Assert.Equal(kind, CrashEventMapper.Map(new RawEvent(CrashEventMapper.WheaProvider, id, T, Array.Empty<object?>()))!.Kind);

    [Fact]
    public void Application_Error_1000_uses_its_structured_properties()
    {
        var e = CrashEventMapper.Map(new RawEvent(CrashEventMapper.AppErrorProvider, 1000, T,
            new object?[] { "game.exe", "1.0.0.0", "65f0a1b2", "nvwgf2umx.dll", "32.0.15.6094", "6700aabb", "c0000005", "0000000000a1b2c3" }));
        Assert.Equal(CrashKind.AppCrash, e!.Kind);
        Assert.Equal("game.exe", e.AppName);
        Assert.Equal("nvwgf2umx.dll", e.ModuleName);
        Assert.Equal("0xC0000005", e.ExceptionCode);
    }

    [Fact]
    public void Application_Hang_1002_names_the_app()
        => Assert.Equal("editor.exe", CrashEventMapper.Map(new RawEvent(CrashEventMapper.AppHangProvider, 1002, T, new object?[] { "editor.exe", "2.0" }))!.AppName);

    [Theory]
    [InlineData("Microsoft-Windows-Kernel-Power", 42)]   // sleep, not a crash
    [InlineData("Application Error", 1001)]
    [InlineData("Some Other Provider", 1001)]
    public void Unrelated_events_are_ignored(string provider, int id)
        => Assert.Null(CrashEventMapper.Map(new RawEvent(provider, id, T, new object?[] { "0x0000009f" })));

    [Fact]
    public void The_same_crash_from_two_sources_is_counted_once()
    {
        var list = CrashEventMapper.Dedupe(new[]
        {
            new CrashEvent(CrashKind.AppCrash, T, AppName: "a.exe"),
            new CrashEvent(CrashKind.AppCrash, T.AddSeconds(20), AppName: "A.EXE"),
            new CrashEvent(CrashKind.AppCrash, T.AddMinutes(10), AppName: "a.exe"),
        });
        Assert.Equal(2, list.Count);
    }
}

public class WingetTableParserTests
{
    [Fact]
    public void Rows_are_read_from_the_right_and_cut_off_ids_are_not_guessed()
    {
        var w = WingetTableParser.Parse(Fixtures.Text("winget-upgrade.txt"), 0);
        Assert.Equal(WingetState.Ready, w.State);
        Assert.Equal(7, w.Upgrades.Count);
        Assert.Equal(1, w.UnreadableRows); // Example.BuildTools.2022.LongPackage…
        var sdk = Assert.Single(w.Upgrades, u => u.Id == "Example.SDK.4");
        Assert.Equal("Example SDK 4.0.101 (x64)", sdk.Name);
        Assert.Equal("4.0.101", sdk.InstalledVersion);
        Assert.Equal("4.0.104", sdk.AvailableVersion);
        Assert.Equal("< 1.2.0", Assert.Single(w.Upgrades, u => u.Id == "Contoso.OldTool").InstalledVersion);
        Assert.Equal("msstore", Assert.Single(w.Upgrades, u => u.Id == "9NXAMPLE0001").Source);
        Assert.DoesNotContain(w.Upgrades, u => u.Id.Contains('…') || u.Id == "Contoso.Pinned"); // stops at the first table
    }

    [Fact]
    public void Translated_headers_dont_matter()
    {
        var w = WingetTableParser.Parse(Fixtures.Text("winget-localized.txt"), 0);
        Assert.Equal(new[] { "Mozilla.Firefox", "Notepad++.Notepad++" }, w.Upgrades.Select(u => u.Id));
        Assert.Equal("Mozilla Firefox (x64 de)", w.Upgrades[0].Name);
    }

    [Fact]
    public void A_table_with_only_cut_off_rows_is_unreadable()
    {
        var w = WingetTableParser.Parse(Fixtures.Text("winget-cutoff.txt"), 0);
        Assert.Equal(WingetState.Unreadable, w.State);
        Assert.Empty(w.Upgrades);
        Assert.Equal(2, w.UnreadableRows);
    }

    [Fact]
    public void No_table_and_success_means_no_updates()
    {
        var w = WingetTableParser.Parse("No available upgrade found.\r\n", 0);
        Assert.Equal(WingetState.Ready, w.State);
        Assert.Empty(w.Upgrades);
    }

    [Fact]
    public void No_table_and_an_error_means_failed()
        => Assert.Equal(WingetState.Failed, WingetTableParser.Parse("Agreements not accepted.", unchecked((int)0x8A150046)).State);

    [Theory]
    [InlineData("Name With Spaces  Bad Id With Space  1.0  2.0  winget")]
    [InlineData("App  NoDotId  1.0  2.0  winget")]
    [InlineData("App  Contoso.App  1.0  2.0  othersource")]
    public void Odd_rows_are_rejected(string line) => Assert.Null(WingetTableParser.TryParseRow(line));
}

public class ChromiumProfileParserTests
{
    private static ExtensionManifest? Manifest(string id, string? path) => id switch
    {
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" => new ExtensionManifest("Coupon Helper", "https://updates.example-ads.net/crx", Array.Empty<string>()),
        "cccccccccccccccccccccccccccccccc" => new ExtensionManifest("Edge Store Extension", "https://edge.microsoft.com/extensionwebstorebase/v1/crx", Array.Empty<string>()),
        "dddddddddddddddddddddddddddddddd" => new ExtensionManifest("My Dev Extension", null, new[] { "tabs" }),
        _ => null, // eeee...: no manifest left on disk
    };

    private static BrowserProfile Parse()
        => ChromiumProfileParser.Parse("Chrome", "Person 1", @"C:\profile", Fixtures.Text("Preferences.json"), Fixtures.Text("Secure Preferences.json"), Manifest);

    [Fact]
    public void Home_startup_and_search_are_read()
    {
        var p = Parse();
        Assert.Equal("http://start.searchfun.example/home", p.Homepage);
        Assert.False(p.HomepageIsNewTab);
        Assert.Equal(4, p.RestoreOnStartup);
        Assert.Equal(2, p.StartupUrls.Count);
        Assert.Equal("Search Fun", p.SearchName);
        Assert.StartsWith("https://search.searchfun.example/", p.SearchUrl);
    }

    [Fact]
    public void Extensions_merge_both_files_and_skip_built_in_and_stale_ones()
    {
        var p = Parse();
        Assert.Equal(new[] { "Coupon Helper", "Edge Store Extension", "Good Ad Blocker", "My Dev Extension" }, p.Extensions.Select(e => e.Name));
        var coupon = p.Extensions.Single(e => e.Name == "Coupon Helper");
        Assert.Equal(3, coupon.Location);
        Assert.False(coupon.FromStore);
        Assert.Contains("nativeMessaging", coupon.Permissions);
        Assert.Contains("*://*/*", coupon.Permissions);
        Assert.True(coupon.Enabled);
        var edge = p.Extensions.Single(e => e.Name == "Edge Store Extension");
        Assert.True(edge.FromStore);   // store decided by update URL
        Assert.False(edge.Enabled);    // disable_reasons = 1
        Assert.True(p.Extensions.Single(e => e.Name == "Good Ad Blocker").FromStore);
    }

    [Fact]
    public void Broken_json_gives_an_empty_profile_not_an_error()
    {
        var p = ChromiumProfileParser.Parse("Edge", "Default", @"C:\p", "{ not json", null);
        Assert.Empty(p.Extensions);
        Assert.Null(p.SearchUrl);
        Assert.True(p.HomepageIsNewTab);
    }

    [Fact]
    public void Manifest_names_from_locales_are_resolved()
    {
        var m = ChromiumProfileParser.ParseManifest("""{ "name": "__MSG_appName__", "default_locale": "en", "permissions": ["proxy"], "host_permissions": ["<all_urls>"] }""",
            locale => locale == "en" ? """{ "appName": { "message": "Speedy VPN" } }""" : null);
        Assert.Equal("Speedy VPN", m!.Name);
        Assert.Equal(new[] { "proxy", "<all_urls>" }, m.Permissions);
    }
}

public class SmallParserTests
{
    [Fact]
    public void Nvidia_smi_csv_handles_commas_and_missing_fans()
    {
        var list = NvidiaSmiCsv.Parse("NVIDIA Example GPU 1000, 54, 0\nGPU, with comma, 61, [N/A]\ngarbage line\n");
        Assert.Equal(2, list.Count);
        Assert.Equal(new GpuReading("NVIDIA Example GPU 1000", 54, 0), list[0]);
        Assert.Equal("GPU, with comma", list[1].Name);
        Assert.Null(list[1].FanPercent);
    }

    [Fact]
    public void Dns_query_is_well_formed_and_replies_are_matched_by_id()
    {
        var q = DnsPacket.BuildQuery(0x1234, "example.com");
        Assert.Equal(new byte[] { 0x12, 0x34, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0, 7 }, q[..13]);
        Assert.Equal(new byte[] { 0, 0, 1, 0, 1 }, q[^5..]);

        var reply = (byte[])q.Clone();
        reply[2] = 0x81; reply[3] = 0x80; // response, recursion available, NOERROR
        Assert.True(DnsPacket.IsAnswer(reply, 0x1234));
        Assert.False(DnsPacket.IsAnswer(reply, 0x1235));
        reply[3] = 0x82; // SERVFAIL
        Assert.False(DnsPacket.IsAnswer(reply, 0x1234));
        Assert.False(DnsPacket.IsAnswer(q, 0x1234)); // a query isn't a reply
    }

    [Fact]
    public void Median_is_the_middle_value()
    {
        Assert.Equal(20, DnsPacket.Median(new double[] { 50, 10, 20 }));
        Assert.Equal(15, DnsPacket.Median(new double[] { 10, 20 }));
        Assert.Null(DnsPacket.Median(Array.Empty<double>()));
    }
}
