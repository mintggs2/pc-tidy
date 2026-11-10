using Microsoft.Win32;
using PcTidy.Features.Schedule.Common;
using PcTidy.Features.Schedule.Drift;
using RegistryHive = PcTidy.Core.Model.RegistryHive;

namespace PcTidy.Features.Schedule.Tests;

/// <summary>
/// Tweak drift against a real (test-only) registry key: HKCU\Software\PCTidyTest\schedule\&lt;guid&gt;, deleted afterwards.
/// The test writes the "current" values itself; the checker only reads.
/// </summary>
public sealed class DriftTests : IDisposable
{
    private const string Area = @"Software\PCTidyTest\schedule";
    private readonly string _key = Area + "\\" + Guid.NewGuid().ToString("N")[..8];

    public DriftTests()
    {
        using var k = Registry.CurrentUser.CreateSubKey(_key);
        k.SetValue("StillSet", 1, RegistryValueKind.DWord);
        k.SetValue("ResetByUpdate", 1, RegistryValueKind.DWord);         // PC Tidy set 0
        k.SetValue("CameBack", 1, RegistryValueKind.DWord);              // PC Tidy removed it
        k.SetValue("Hex", 0x10, RegistryValueKind.DWord);                // set as "0x10"
        k.SetValue("Text", "Hidden", RegistryValueKind.String);
        k.SetValue("Later", 2, RegistryValueKind.DWord);                 // an older run set 1, a newer one 2
        k.SetValue("UndoneRun", 7, RegistryValueKind.DWord);             // only an undone run set it
        k.SetValue("Simulated", 7, RegistryValueKind.DWord);
        k.SetValue("OtherFeature", 7, RegistryValueKind.DWord);
        // "Removed" stays absent (PC Tidy removed it, still gone); "Missing" is absent (PC Tidy set it).
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_key, throwOnMissingSubKey: false);
        using var area = Registry.CurrentUser.OpenSubKey(Area);
        if (area is { SubKeyCount: 0, ValueCount: 0 }) Registry.CurrentUser.DeleteSubKey(Area, throwOnMissingSubKey: false);
    }

    [Fact]
    public async Task Reports_values_changed_back_since_the_last_finished_run_and_nothing_else()
    {
        var t = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var journal = new FakeJournal();
        journal.Runs.Add(Runs.Run("tweaks", t, RunStatus.Completed,
            Runs.Step(0, Runs.SetDword(_key, "StillSet", "1")),
            Runs.Step(1, Runs.SetDword(_key, "ResetByUpdate", "0")),
            Runs.Step(2, Runs.DeleteValue(_key, "CameBack")),
            Runs.Step(3, Runs.DeleteValue(_key, "Removed")),
            Runs.Step(4, Runs.SetDword(_key, "Hex", "0x10")),
            Runs.Step(5, Runs.SetString(_key, "Text", "Hidden")),
            Runs.Step(6, Runs.SetDword(_key, "Missing", "1")),
            Runs.Step(7, Runs.SetDword(_key, "Later", "1")),
            Runs.Step(8, Runs.SetDword(_key, "Skipped", "1"), StepStatus.Skipped)));
        journal.Runs.Add(Runs.Run("tweaks", t.AddDays(3), RunStatus.CompletedWithSkips, Runs.Step(0, Runs.SetDword(_key, "Later", "2"))));
        journal.Runs.Add(Runs.Run("tweaks", t.AddDays(4), RunStatus.Undone, Runs.Step(0, Runs.SetDword(_key, "UndoneRun", "0"))));
        journal.Runs.Add(Runs.Run("tweaks", t.AddDays(5), RunStatus.Simulated, Runs.Step(0, Runs.SetDword(_key, "Simulated", "0"), StepStatus.Simulated)) with { Mode = ExecutionMode.Simulate });
        journal.Runs.Add(Runs.Run("speed", t.AddDays(5), RunStatus.Completed, Runs.Step(0, Runs.SetDword(_key, "OtherFeature", "0", feature: "speed"))));

        var report = await new TweakDriftChecker(journal, WindowsRegistryReader.Instance).CheckAsync(CancellationToken.None);

        Assert.Equal(8, report.Checked); // StillSet, ResetByUpdate, CameBack, Removed, Hex, Text, Missing, Later
        Assert.Equal(new[] { "Remove CameBack", "Set Missing", "Set ResetByUpdate" }, report.Drifted.Select(d => d.Description));
        var reset = report.Drifted.Single(d => d.Value.ValueName == "ResetByUpdate");
        Assert.Equal("0", reset.AppliedText);
        Assert.Equal("1", reset.CurrentText);
        Assert.Equal("removed", report.Drifted.Single(d => d.Value.ValueName == "CameBack").AppliedText);
        Assert.Equal("not set", report.Drifted.Single(d => d.Value.ValueName == "Missing").CurrentText);
        Assert.Equal("3 of your 8 tweaks were changed back, usually by a Windows update.", report.Headline);
        Assert.Contains("Nothing was changed", report.Advice);

        // The checker only read: the values are exactly as the test left them.
        using var k = Registry.CurrentUser.OpenSubKey(_key)!;
        Assert.Equal(1, k.GetValue("ResetByUpdate"));
        Assert.Equal(1, k.GetValue("CameBack"));
        Assert.Null(k.GetValue("Missing"));
    }

    [Fact]
    public async Task No_tweak_runs_means_nothing_to_check()
    {
        var report = await new TweakDriftChecker(new FakeJournal(), WindowsRegistryReader.Instance).CheckAsync(CancellationToken.None);
        Assert.Equal(0, report.Checked);
        Assert.Null(report.Headline);
    }

    [Theory]
    [InlineData(RegistryKind.DWord, "0x1F", 31, true)]
    [InlineData(RegistryKind.DWord, "4294967295", -1, true)]
    [InlineData(RegistryKind.DWord, "1", 0, false)]
    [InlineData(RegistryKind.String, "a", "a", true)]
    [InlineData(RegistryKind.String, "a", "A", false)]
    public void Data_comparison_by_kind(RegistryKind kind, string data, object current, bool same)
    {
        var spec = new RegistryValueSpec { Hive = RegistryHive.CurrentUser, KeyPath = "k", ValueName = "v", Kind = kind, Data = data };
        Assert.Equal(same, TweakDriftChecker.SameData(spec, current));
    }

    [Fact]
    public void Multi_string_and_binary_compare_by_content()
    {
        var multi = new RegistryValueSpec { Hive = RegistryHive.CurrentUser, KeyPath = "k", ValueName = "v", Kind = RegistryKind.MultiString, Data = "a\nb" };
        Assert.True(TweakDriftChecker.SameData(multi, new[] { "a", "b" }));
        var bin = new RegistryValueSpec { Hive = RegistryHive.CurrentUser, KeyPath = "k", ValueName = "v", Kind = RegistryKind.Binary, Data = "01 ff 00" };
        Assert.True(TweakDriftChecker.SameData(bin, new byte[] { 1, 255, 0 }));
        Assert.False(TweakDriftChecker.SameData(bin, new byte[] { 1, 255 }));
    }
}
