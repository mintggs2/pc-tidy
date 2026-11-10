using PcTidy.Features.Insights.Scripts;
using Xunit;

namespace PcTidy.Features.Insights.Tests;

/// <summary>
/// "Scan this script" warning signs and finder-value rules. Every fixture is made-up text, read only; each one writes a
/// marker file if it ever ran, and the marker must never appear.
/// </summary>
public class WarningSignTests
{
    private static Task<(ScriptReport Report, string Marker)> Scan(string name, TestbedFolder tb) => ScriptAnalyzerTests.ScanFixtureAsync(name, tb);

    private static ScriptFinding Sign(ScriptReport r, string whatStart) => Assert.Single(r.Findings, f => f.What.StartsWith(whatStart, StringComparison.Ordinal));

    // ------------------------------------------------------------------ 1. opaque score

    [Fact]
    public async Task Opaque_score_with_a_random_number_is_a_red_flag()
    {
        using var tb = new TestbedFolder();
        var (report, marker) = await Scan("opaque-score.ps1", tb);

        var score = Sign(report, "Shows an overall score");
        Assert.Equal((ScriptArea.WarningSign, 5, true), (score.Area, score.Line, score.RedFlag));
        Assert.Contains("picked at random", score.Why);
        Assert.Equal(ScriptOverall.Dangerous, report.Overall);
        Assert.Contains("warning sign", report.Headline);
        Assert.False(File.Exists(marker), "The script must never run.");
    }

    [Fact]
    public void Typed_in_score_says_so_and_a_points_breakdown_is_not_flagged()
    {
        var typed = ScriptAnalyzer.Analyze("x.ps1", "Write-Host \"Optimization score: 97%\"");
        Assert.Contains("typed into the script", Sign(typed, "Shows an overall score").Why);

        var batch = ScriptAnalyzer.Analyze("x.bat", "@echo off\necho Your PC is now 100%% optimized!");
        Assert.Single(batch.Findings, f => f.What.StartsWith("Shows an overall score", StringComparison.Ordinal));

        var itemised = ScriptAnalyzer.Analyze("x.ps1", string.Join('\n',
            "Write-Host 'Telemetry off: +5 points'", "Write-Host 'Animations off: +3 points'", "Write-Host \"Score: $total/100\""));
        Assert.DoesNotContain(itemised.Findings, f => f.Area == ScriptArea.WarningSign);
    }

    // ------------------------------------------------------------------ 2. apply all

    [Fact]
    public async Task Apply_all_menu_is_a_red_flag()
    {
        using var tb = new TestbedFolder();
        var (report, marker) = await Scan("apply-all.bat", tb);

        var all = Sign(report, "Has an \"apply all\" step");
        Assert.Equal((3, true, ScriptVerdict.Unknown), (all.Line, all.RedFlag, all.Verdict));
        Assert.Contains(report.Findings, f => f.Line == 9 && f.Verdict == ScriptVerdict.Harmful); // LargeSystemCache
        Assert.False(File.Exists(marker), "The script must never run.");
    }

    [Theory]
    [InlineData("x.ps1", "function Invoke-OneClickOptimize { }")]
    [InlineData("x.ps1", "Write-Host 'Optimize everything now'")]
    [InlineData("x.bat", "echo  A) Run all optimizations at once")]
    public void Apply_all_wordings_are_recognised(string file, string text)
        => Assert.Single(ScriptAnalyzer.Analyze(file, text).Findings, f => f.What.StartsWith("Has an \"apply all\" step", StringComparison.Ordinal));

    // ------------------------------------------------------------------ 3. restore point order

    [Fact]
    public async Task Restore_point_after_the_changes_is_a_red_flag()
    {
        using var tb = new TestbedFolder();
        var (report, marker) = await Scan("late-restore-point.ps1", tb);

        var late = Sign(report, "Makes the restore point only after");
        Assert.Equal((4, true), (late.Line, late.RedFlag));
        Assert.Contains("Line 2", late.Why);
        Assert.False(File.Exists(marker), "The script must never run.");
    }

    [Fact]
    public void Restore_point_announced_but_never_made_is_a_red_flag()
    {
        var report = ScriptAnalyzer.Analyze("x.ps1", "Write-Host 'Creating a restore point...'\n"
            + "Set-ItemProperty -Path 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced' -Name HideFileExt -Value 0");
        Assert.True(Sign(report, "Says it makes a restore point").RedFlag);
    }

    [Fact]
    public void Many_changes_and_no_restore_point_is_a_note_not_a_red_flag()
    {
        const string key = "reg add \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced\" /v HideFileExt /t REG_DWORD /d 0 /f";
        var report = ScriptAnalyzer.Analyze("x.bat", string.Join('\n', Enumerable.Repeat(key, WarningSigns.ManyChanges)));
        var note = Sign(report, $"Changes {WarningSigns.ManyChanges} system settings without making a restore point");
        Assert.Equal((ScriptVerdict.Situational, false), (note.Verdict, note.RedFlag));
        Assert.Equal(ScriptOverall.KnownSettings, report.Overall); // still only known settings
        Assert.Contains("restore point", report.Advice);
        Assert.StartsWith($"Known settings only. {WarningSigns.ManyChanges} changes found", report.Headline);

        // Fewer changes: nothing to say.
        Assert.DoesNotContain(ScriptAnalyzer.Analyze("x.bat", key).Findings, f => f.Area == ScriptArea.WarningSign);
    }

    // ------------------------------------------------------------------ 4. split / obfuscated to dodge antivirus

    [Fact]
    public async Task Split_and_obfuscated_payload_raises_every_hiding_flag()
    {
        using var tb = new TestbedFolder();
        var (report, marker) = await Scan("split-payload.ps1", tb);

        Assert.Contains(report.RedFlags, f => f.Line == 2 && f.Area == ScriptArea.Defender && f.What.Contains("its own folder"));
        Assert.Contains(report.RedFlags, f => f.Line == 3 && f.Area == ScriptArea.Defender && f.What.Contains("the temporary folder"));
        var pieces = Sign(report, "Keeps scrambled data in pieces");
        Assert.Equal((ScriptArea.HiddenCode, 4, true), (pieces.Area, pieces.Line, pieces.RedFlag));
        // Downloaded into $code on line 8, run (as i`e`x) on line 9: one red flag on the download line.
        var dl = Assert.Single(report.Findings, f => f.Area == ScriptArea.Download);
        Assert.Equal((8, ScriptVerdict.Harmful, true), (dl.Line, dl.Verdict, dl.RedFlag));
        Assert.Contains("runs it (line 9)", dl.What);
        Assert.Contains(report.RedFlags, f => f.Line == 9 && f.What.StartsWith("Breaks command names into pieces", StringComparison.Ordinal));
        Assert.Contains(report.RedFlags, f => f.Line == 9 && f.What.StartsWith("Runs text as code", StringComparison.Ordinal));
        Assert.Contains(report.RedFlags, f => f.Line == 10 && f.What.StartsWith("Spells text out as character codes", StringComparison.Ordinal));
        Assert.Equal(ScriptOverall.Dangerous, report.Overall);
        Assert.False(File.Exists(marker), "The script must never run.");
    }

    [Fact]
    public async Task Files_joined_into_a_program_are_a_red_flag()
    {
        using var tb = new TestbedFolder();
        var (report, marker) = await Scan("parts-join.bat", tb);
        var join = Sign(report, "Joins several files into one");
        Assert.Equal((3, true), (join.Line, join.RedFlag));
        Assert.False(File.Exists(marker), "The script must never run.");
    }

    [Theory]
    [InlineData("x.ps1", "& ('ie'+'x') $payload")]
    [InlineData("x.bat", "p^o^w^e^r^s^h^e^l^l -nop -c Write-Host hi")]
    [InlineData("x.vbs", "Set o = CreateObject(Chr(87) & Chr(83) & Chr(99) & Chr(114) & Chr(105))")]
    public void Obfuscated_command_names_are_noticed(string file, string text)
        => Assert.Contains(ScriptAnalyzer.Analyze(file, text).RedFlags, f => f.Area == ScriptArea.HiddenCode);

    [Fact]
    public void Encoded_command_is_still_decoded_one_level()
    {
        // "Set-MpPreference -DisableRealtimeMonitoring $true", UTF-16LE Base64 (the same payload as fps-boost.ps1).
        var report = ScriptAnalyzer.Analyze("x.bat", "powershell -EncodedCommand UwBlAHQALQBNAHAAUAByAGUAZgBlAHIAZQBuAGMAZQAgAC0ARABpAHMAYQBiAGwAZQBSAGUAYQBsAHQAaQBtAGUATQBvAG4AaQB0AG8AcgBpAG4AZwAgACQAdAByAHUAZQA=");
        Assert.Contains(report.Findings, f => f.FromDecodedPayload && f.MatchedId == "security.defender-off" && f.What.EndsWith("(hidden)", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ 5. SmartScreen coaching

    [Fact]
    public async Task SmartScreen_click_through_text_is_a_red_flag_even_in_a_comment()
    {
        using var tb = new TestbedFolder();
        var (report, marker) = await Scan("smartscreen-howto.ps1", tb);
        var ss = Sign(report, "Tells you how to click past the SmartScreen warning");
        Assert.Equal((2, true), (ss.Line, ss.RedFlag));
        Assert.False(File.Exists(marker), "The script must never run.");

        var reg = ScriptAnalyzer.Analyze("x.reg", "Windows Registry Editor Version 5.00\n; Windows protected your PC? Click More info and Run anyway.\n");
        Assert.Contains(reg.RedFlags, f => f.Area == ScriptArea.WarningSign);
    }

    // ------------------------------------------------------------------ 6. values the bad-tweak finder resets

    [Fact]
    public async Task Finder_values_map_to_the_bad_tweak_checks()
    {
        using var tb = new TestbedFolder();
        var (report, marker) = await Scan("finder-values.ps1", tb);
        void Has(int line, string? id, ScriptVerdict verdict) =>
            Assert.True(report.Findings.Any(f => f.Line == line && f.MatchedId == id && f.Verdict == verdict),
                $"line {line}: expected {id} {verdict}; got " + string.Join(" | ", report.Findings.Where(f => f.Line == line).Select(f => $"{f.MatchedId} {f.Verdict} {f.What}")));

        Has(2, "security.lsa-protection-off", ScriptVerdict.Harmful);
        Has(3, "security.defender-only-realtime", ScriptVerdict.Harmful);
        Assert.Equal(2, report.Findings.Count(f => f.Line == 3 && f.Area == ScriptArea.Defender)); // both switches on that line
        Has(4, "security.defender-protection-reduced", ScriptVerdict.Harmful);
        Has(5, "update.paused-by-policy", ScriptVerdict.Harmful);
        Has(6, "update.paused-by-policy", ScriptVerdict.Harmful);
        Has(7, "boot.safeboot-left-on", ScriptVerdict.Harmful);
        Has(8, "scheduler.realtime-priority", ScriptVerdict.Harmful);
        Has(9, null, ScriptVerdict.Placebo); // run as administrator (Games research: no gain, a security cost)
        Assert.Contains(report.Findings, f => f.Line == 9 && f.What == "Makes examplegame.exe always run as administrator");
        Has(10, null, ScriptVerdict.Situational); // read-only GameUserSettings.ini
        Assert.Contains(report.Findings, f => f.Line == 10 && f.What.Contains("GameUserSettings.ini") && f.Area == ScriptArea.Files);
        Has(11, "cpu.stray-power-plan", ScriptVerdict.Placebo);
        Has(12, "windows.system-devices-disabled", ScriptVerdict.Harmful);
        Has(13, "windows.system-devices-disabled", ScriptVerdict.Harmful);
        Assert.Contains(report.Findings, f => f.Line == 13 && f.What.Contains("High precision event timer"));
        Assert.All(report.Findings, f => Assert.False(string.IsNullOrWhiteSpace(f.Why)));
        Assert.False(File.Exists(marker), "The script must never run.");
    }

    [Theory]
    [InlineData("powercfg -duplicatescheme 11111111-2222-3333-4444-555555555555", ScriptVerdict.Placebo, "cpu.stray-power-plan")]
    [InlineData("powercfg -duplicatescheme e9a42b02-d5df-448d-aa00-03f14749eb61", ScriptVerdict.Placebo, "power.ultimate-performance")]
    [InlineData("powercfg -duplicatescheme 381b4222-f694-41f0-9685-ff5bb260df2e", ScriptVerdict.Situational, null)] // a copy of Balanced
    [InlineData("Set-MpPreference -DisableRealtimeMonitoring $true", ScriptVerdict.Harmful, "security.defender-off")]
    [InlineData("Set-MpPreference -DisableRealtimeMonitoring $false", ScriptVerdict.Proven, "security.defender-off")]
    public void Power_plan_and_Defender_lines_map_to_the_research(string text, ScriptVerdict verdict, string? id)
    {
        var f = Assert.Single(ScriptAnalyzer.Analyze("x.bat", text).Findings);
        Assert.Equal((verdict, id), (f.Verdict, f.MatchedId));
    }

    [Fact]
    public void Safe_mode_switched_on_and_off_again_is_situational()
    {
        var report = ScriptAnalyzer.Analyze("x.bat", "bcdedit /set {current} safeboot minimal\nshutdown /r /t 0\nbcdedit /deletevalue {current} safeboot");
        var on = Assert.Single(report.Findings, f => f.Line == 1);
        Assert.Equal((ScriptVerdict.Situational, "boot.safeboot-left-on"), (on.Verdict, on.MatchedId));
        Assert.Contains("line 3", on.Why);
    }

    // ------------------------------------------------------------------ negatives: normal admin scripts stay calm

    [Fact]
    public async Task Careful_admin_script_raises_no_warning_sign_or_red_flag()
    {
        using var tb = new TestbedFolder();
        var (report, marker) = await Scan("careful-admin.ps1", tb);

        Assert.DoesNotContain(report.Findings, f => f.Area == ScriptArea.WarningSign);
        Assert.Empty(report.RedFlags);
        Assert.DoesNotContain(report.Findings, f => f.Verdict == ScriptVerdict.Harmful);
        Assert.NotEqual(ScriptOverall.Dangerous, report.Overall);
        // The settings it writes are back-to-default or untested values, not the finder's bad ones.
        Assert.Contains(report.Findings, f => f.What.Contains("RunAsPPL") && f.Verdict != ScriptVerdict.Harmful);
        Assert.Contains(report.Findings, f => f.What.Contains("DeferQualityUpdatesPeriodInDays") && f.Verdict != ScriptVerdict.Harmful);
        Assert.Contains(report.Findings, f => f.MatchedId == "security.defender-off" && f.Verdict == ScriptVerdict.Proven);
        Assert.False(File.Exists(marker), "The script must never run.");
    }

    [Fact]
    public async Task Batch_restore_point_in_a_subroutine_called_first_is_not_late()
    {
        using var tb = new TestbedFolder();
        var (report, marker) = await Scan("careful-admin.bat", tb);

        Assert.DoesNotContain(report.Findings, f => f.Area == ScriptArea.WarningSign);
        Assert.Empty(report.RedFlags);
        Assert.Equal(ScriptOverall.KnownSettings, report.Overall);
        Assert.False(File.Exists(marker), "The script must never run.");
    }

    [Fact]
    public void Restore_point_made_first_then_a_change_is_not_flagged()
    {
        var report = ScriptAnalyzer.Analyze("x.ps1", "Checkpoint-Computer -Description 'Before' -RestorePointType MODIFY_SETTINGS\n"
            + "Set-ItemProperty -Path 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced' -Name HideFileExt -Value 0");
        Assert.DoesNotContain(report.Findings, f => f.Area == ScriptArea.WarningSign);
        Assert.Equal(ScriptOverall.KnownSettings, report.Overall);
    }

    [Fact]
    public void Menu_script_that_lets_you_pick_the_order_is_not_flagged()
    {
        var report = ScriptAnalyzer.Analyze("x.bat", string.Join('\n',
            "@echo off", ":menu", "echo 1 = settings, 2 = restore point", "set /p c=Pick:", "if \"%c%\"==\"1\" goto settings", "if \"%c%\"==\"2\" goto restore", "goto :eof",
            ":settings", "reg add \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced\" /v HideFileExt /t REG_DWORD /d 0 /f", "goto menu",
            ":restore", "wmic /namespace:\\\\root\\default path SystemRestore call CreateRestorePoint \"Before\", 100, 7", "goto menu"));
        Assert.DoesNotContain(report.Findings, f => f.Area == ScriptArea.WarningSign);
    }

    [Theory]
    [InlineData("x.bat", "copy /b notes.txt +,,")]                                         // the "touch" idiom
    [InlineData("x.bat", "copy /b intro.txt + body.txt letter.txt")]                         // joining text files
    [InlineData("x.ps1", "Write-Host \"Hello`nWorld`tDone\"")]                               // escapes, not obfuscation
    [InlineData("x.ps1", "$name = 'Get-' + 'Date'")]
    [InlineData("x.ps1", "Write-Host 'For more info, see the README.'")]
    [InlineData("x.ps1", "# Turn SmartScreen back on (Windows' default)")]
    [InlineData("x.ps1", "Write-Host 'Install all updates, then run all tests.'")]
    [InlineData("x.ps1", "Write-Host \"CPU usage: 45%\"")]
    [InlineData("x.ps1", "Write-Host \"Battery health: 85%\"")]
    [InlineData("x.ps1", "attrib +r +s \"C:\\Team\\desktop.ini\"")]
    [InlineData("x.ps1", "$data = Invoke-RestMethod https://api.example.invalid/v1/status\nWrite-Host $data.version")]
    public void Normal_lines_raise_no_red_flag(string file, string text)
    {
        var report = ScriptAnalyzer.Analyze(file, text);
        Assert.Empty(report.RedFlags);
        Assert.DoesNotContain(report.Findings, f => f.Area is ScriptArea.WarningSign or ScriptArea.HiddenCode || f.Verdict == ScriptVerdict.Harmful);
    }

    [Theory]
    [InlineData("reg add \"HKLM\\SYSTEM\\CurrentControlSet\\Control\\Lsa\" /v RunAsPPL /t REG_DWORD /d 1 /f")]
    [InlineData("reg add \"HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate\" /v DeferFeatureUpdatesPeriodInDays /t REG_DWORD /d 0 /f")]
    [InlineData("reg add \"HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Image File Execution Options\\examplegame.exe\\PerfOptions\" /v CpuPriorityClass /t REG_DWORD /d 3 /f")]
    [InlineData("Get-PnpDevice -FriendlyName 'Example USB Receiver' | Disable-PnpDevice -Confirm:$false")]
    [InlineData("Add-MpPreference -ExclusionPath 'D:\\Builds' -WhatIf")]
    public void Lookalike_values_are_not_the_finder_verdict(string text)
    {
        var report = ScriptAnalyzer.Analyze("x.ps1", text);
        Assert.DoesNotContain(report.Findings, f => f.MatchedId is "security.lsa-protection-off" or "update.paused-by-policy" or "scheduler.realtime-priority" or "windows.system-devices-disabled");
        // A plain exclusion is still flagged, but not as "its own folder" or "the temporary folder".
        Assert.DoesNotContain(report.Findings, f => f.What.Contains("its own folder") || f.What.Contains("the temporary folder"));
    }

    [Fact]
    public void Very_long_lines_are_read_within_the_timeouts()
    {
        // A 1.5 MB single line of text: every rule finishes (or reports the line as unreadable), nothing throws.
        var text = "Write-Host '" + new string('a', 1_500_000) + "'";
        var report = ScriptAnalyzer.Analyze("x.ps1", text);
        Assert.NotNull(report.Headline);
    }
}
