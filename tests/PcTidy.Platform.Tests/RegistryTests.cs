using Microsoft.Win32;
using PcTidy.Core.Model;
using RegistryHive = PcTidy.Core.Model.RegistryHive;
using Xunit;

namespace PcTidy.Platform.Tests;

public class RegistryValueCodecTests
{
    [Theory]
    [InlineData("16", "16")]
    [InlineData("0x10", "16")]
    [InlineData("0xFFFFFFFF", "4294967295")]
    [InlineData("4294967295", "4294967295")]
    [InlineData("-1", "4294967295")]
    [InlineData("0", "0")]
    public void DWord_normalizes_to_unsigned_decimal(string input, string expected)
        => Assert.Equal(expected, RegistryValueCodec.Normalize(RegistryKind.DWord, input));

    [Fact]
    public void QWord_binary_and_multistring_round_trip()
    {
        Assert.Equal("18446744073709551615", RegistryValueCodec.Normalize(RegistryKind.QWord, "0xFFFFFFFFFFFFFFFF"));
        Assert.Equal("0102AB", RegistryValueCodec.Normalize(RegistryKind.Binary, "01 02,ab"));
        Assert.Equal("a\nb", RegistryValueCodec.Normalize(RegistryKind.MultiString, "a\r\nb"));
        Assert.Empty((string[])RegistryValueCodec.Parse(RegistryKind.MultiString, ""));
        Assert.Equal("%TEMP%\\x", RegistryValueCodec.Normalize(RegistryKind.ExpandString, "%TEMP%\\x"));
    }

    [Theory]
    [InlineData(RegistryKind.DWord, "4294967296")]
    [InlineData(RegistryKind.DWord, "abc")]
    [InlineData(RegistryKind.DWord, "0xZZ")]
    [InlineData(RegistryKind.Binary, "0")]
    [InlineData(RegistryKind.Binary, "GG")]
    public void Bad_data_is_rejected(RegistryKind kind, string data)
        => Assert.Throws<FormatException>(() => RegistryValueCodec.Parse(kind, data));

    [Fact]
    public void Unsupported_kinds_map_to_null()
    {
        Assert.Null(RegistryValueCodec.FromWin32(RegistryValueKind.None));
        Assert.Null(RegistryValueCodec.FromWin32(RegistryValueKind.Unknown));
        Assert.Equal(RegistryKind.QWord, RegistryValueCodec.FromWin32(RegistryValueKind.QWord));
    }
}

[Collection("Registry")]
public class RegistryHandlerTests
{
    private readonly RegistryHandler _handler = new();

    [Fact]
    public async Task Set_value_on_missing_key_then_undo_removes_value_and_created_keys()
    {
        using var sandbox = new TestSandbox();
        using var reg = new TestRegistryKey();
        var keyPath = reg.Sub(@"new\deeper");
        var step = Steps.Of(Steps.RegSet(keyPath, "Speed", RegistryKind.DWord, "0x2"));

        var done = await _handler.ExecuteAsync(step, sandbox.Context(), default);

        Assert.Equal(StepStatus.Done, done.Status);
        Assert.False(done.RegistryPrior!.KeyExisted);
        Assert.False(done.RegistryPrior.ValueExisted);
        using (var k = Registry.CurrentUser.OpenSubKey(keyPath)) Assert.Equal(2, k!.GetValue("Speed"));
        Assert.True(await _handler.WasCompletedAsync(done, sandbox.Context(), default));

        var undone = await _handler.UndoAsync(done, sandbox.Context(), default);

        Assert.Equal(StepStatus.Undone, undone.Status);
        Assert.False(TestRegistryKey.Exists(reg.Sub("new")));
    }

    [Fact]
    public async Task Set_existing_value_records_exact_prior_and_undo_restores_it()
    {
        using var sandbox = new TestSandbox();
        using var reg = new TestRegistryKey();
        using (var k = reg.Open()) k.SetValue("Path", "%USERPROFILE%\\old", RegistryValueKind.ExpandString);

        var done = await _handler.ExecuteAsync(Steps.Of(Steps.RegSet(reg.KeyPath, "Path", RegistryKind.String, "new")), sandbox.Context(), default);

        Assert.Equal(StepStatus.Done, done.Status);
        Assert.Equal(RegistryKind.ExpandString, done.RegistryPrior!.Kind);
        Assert.Equal("%USERPROFILE%\\old", done.RegistryPrior.Data); // not expanded
        using (var k = reg.Open()) Assert.Equal(RegistryValueKind.String, k.GetValueKind("Path"));

        var undone = await _handler.UndoAsync(done, sandbox.Context(), default);

        Assert.Equal(StepStatus.Undone, undone.Status);
        using (var k = reg.Open())
        {
            Assert.Equal(RegistryValueKind.ExpandString, k.GetValueKind("Path"));
            Assert.Equal("%USERPROFILE%\\old", k.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
        }
    }

    [Fact]
    public async Task Value_already_set_is_skipped()
    {
        using var sandbox = new TestSandbox();
        using var reg = new TestRegistryKey();
        using (var k = reg.Open()) k.SetValue("On", 1, RegistryValueKind.DWord);

        var result = await _handler.ExecuteAsync(Steps.Of(Steps.RegSet(reg.KeyPath, "On", RegistryKind.DWord, "0x1")), sandbox.Context(), default);

        Assert.Equal(StepStatus.Skipped, result.Status);
    }

    [Fact]
    public async Task Unusual_value_types_are_left_alone()
    {
        using var sandbox = new TestSandbox();
        using var reg = new TestRegistryKey();
        using (var k = reg.Open()) k.SetValue("Odd", Array.Empty<byte>(), RegistryValueKind.None);

        var result = await _handler.ExecuteAsync(Steps.Of(Steps.RegSet(reg.KeyPath, "Odd", RegistryKind.DWord, "1")), sandbox.Context(), default);

        Assert.Equal(StepStatus.Skipped, result.Status);
        using (var k = reg.Open()) Assert.Equal(RegistryValueKind.None, k.GetValueKind("Odd"));
    }

    [Fact]
    public async Task Delete_value_then_undo_puts_it_back()
    {
        using var sandbox = new TestSandbox();
        using var reg = new TestRegistryKey();
        using (var k = reg.Open()) k.SetValue("List", new[] { "a", "b" }, RegistryValueKind.MultiString);

        var done = await _handler.ExecuteAsync(Steps.Of(Steps.RegDeleteValue(reg.KeyPath, "List")), sandbox.Context(), default);
        Assert.Equal(StepStatus.Done, done.Status);
        using (var k = reg.Open()) Assert.Null(k.GetValue("List"));

        var undone = await _handler.UndoAsync(done, sandbox.Context(), default);
        Assert.Equal(StepStatus.Undone, undone.Status);
        using (var k = reg.Open()) Assert.Equal(new[] { "a", "b" }, (string[])k.GetValue("List")!);

        var again = await _handler.ExecuteAsync(Steps.Of(Steps.RegDeleteValue(reg.KeyPath, "Missing")), sandbox.Context(), default);
        Assert.Equal(StepStatus.Skipped, again.Status);
    }

    [Fact]
    public async Task Delete_key_exports_a_backup_first_and_undo_imports_it()
    {
        using var sandbox = new TestSandbox();
        using var reg = new TestRegistryKey();
        var app = reg.Sub("Vendor App");
        using (var k = Registry.CurrentUser.CreateSubKey(app + @"\Sub"))
        {
            k.SetValue("Q", 5L, RegistryValueKind.QWord);
            k.SetValue("B", new byte[] { 1, 2, 3 }, RegistryValueKind.Binary);
        }
        var ctx = sandbox.Context();

        var done = await _handler.ExecuteAsync(Steps.Of(Steps.RegDeleteKey(app), index: 7), ctx, default);

        Assert.Equal(StepStatus.Done, done.Status);
        Assert.False(TestRegistryKey.Exists(app));
        var backup = done.RegistryPrior!.RegExportPath!;
        Assert.Equal(Path.Combine(sandbox.Locations.DataRoot, "backups", "reg", $"{ctx.RunId}-7.reg"), backup);
        Assert.True(File.Exists(backup));

        var undone = await _handler.UndoAsync(done, ctx, default);

        Assert.Equal(StepStatus.Undone, undone.Status);
        using var restored = Registry.CurrentUser.OpenSubKey(app + @"\Sub")!;
        Assert.Equal(5L, restored.GetValue("Q"));
        Assert.Equal(new byte[] { 1, 2, 3 }, (byte[])restored.GetValue("B")!);
    }

    [Fact]
    public async Task Undo_refuses_backups_outside_the_backup_folder_or_touching_other_keys()
    {
        using var sandbox = new TestSandbox();
        using var reg = new TestRegistryKey();
        var app = reg.Sub("App");
        var outside = Path.Combine(sandbox.Root, "evil.reg");
        File.WriteAllText(outside, $"Windows Registry Editor Version 5.00\r\n\r\n[HKEY_CURRENT_USER\\{app}]\r\n");
        var step = Steps.Of(Steps.RegDeleteKey(app), status: StepStatus.Done) with
        {
            RegistryPrior = new RegistryPriorState { KeyExisted = true, RegExportPath = outside },
        };

        var result = await _handler.UndoAsync(step, sandbox.Context(), default);
        Assert.Equal(StepStatus.UndoFailed, result.Status);

        var inside = Path.Combine(sandbox.Locations.DataRoot, "backups", "reg", "x.reg");
        Directory.CreateDirectory(Path.GetDirectoryName(inside)!);
        File.WriteAllText(inside, $"Windows Registry Editor Version 5.00\r\n\r\n[HKEY_CURRENT_USER\\{app}]\r\n[HKEY_CURRENT_USER\\Software\\Elsewhere]\r\n");
        Assert.False(RegFileBackup.IsValidBackupFor(inside, RegistryHive.CurrentUser, app, out _));
        File.WriteAllText(inside, $"Windows Registry Editor Version 5.00\r\n\r\n[-HKEY_CURRENT_USER\\{app}]\r\n");
        Assert.False(RegFileBackup.IsValidBackupFor(inside, RegistryHive.CurrentUser, app, out _));
        File.WriteAllText(inside, $"Windows Registry Editor Version 5.00\r\n\r\n[HKEY_CURRENT_USER\\{app}]\r\n[HKEY_CURRENT_USER\\{app}\\Child]\r\n");
        Assert.True(RegFileBackup.IsValidBackupFor(inside, RegistryHive.CurrentUser, app, out _));
        Assert.False(TestRegistryKey.Exists(app));
    }

    /// <summary>
    /// ELV-1 + UNDO-1: whatever the (user-writable) journal says, undo never re-creates a Defender policy or a Windows
    /// Update pause/block policy. The keys sit in the test area, standing for HKLM the way practice runs redirect them.
    /// </summary>
    [Theory]
    [InlineData(@"HKLM\SOFTWARE\Policies\Microsoft\Windows Defender", "DisableAntiSpyware", "Defender")]
    [InlineData(@"HKLM\SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection", "DisableRealtimeMonitoring", "Defender")]
    [InlineData(@"HKLM\SOFTWARE\Policies\Microsoft\Windows Defender\Exclusions\Paths", @"C:\Games", "Defender")]
    [InlineData(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoUpdate", "Windows Update")]
    [InlineData(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "PauseQualityUpdatesStartTime", "Windows Update")]
    [InlineData(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "DeferFeatureUpdatesPeriodInDays", "Windows Update")]
    public async Task Undo_never_puts_back_a_policy_that_switches_Defender_or_Windows_Update_off(string logicalKey, string name, string what)
    {
        using var sandbox = new TestSandbox();
        using var reg = new TestRegistryKey();
        var keyPath = reg.Sub(logicalKey);
        // A tampered (or honest) journal: "this value existed with data 1 before the repair removed it".
        var step = Steps.Of(Steps.RegDeleteValue(keyPath, name), status: StepStatus.Done) with
        {
            RegistryPrior = new RegistryPriorState { KeyExisted = true, ValueExisted = true, Kind = RegistryKind.DWord, Data = "1" },
        };

        var result = await _handler.UndoAsync(step, sandbox.Context(), default);

        Assert.Equal(StepStatus.UndoFailed, result.Status);
        Assert.Contains($"didn't put this {(what == "Defender" ? "Microsoft Defender" : what)} policy back", result.Message);
        Assert.False(TestRegistryKey.Exists(keyPath));
        Assert.NotNull(PcTidy.Catalog.Tweaks.ProtectionPolicyValues.WhyNeverRestore(RegistryHive.LocalMachine, logicalKey[5..], name)); // the real HKLM path too
    }

    [Fact]
    public async Task Undo_still_puts_back_ordinary_values_next_to_the_protection_policies()
    {
        using var sandbox = new TestSandbox();
        using var reg = new TestRegistryKey();
        var keyPath = reg.Sub(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate");
        using (var k = Registry.CurrentUser.CreateSubKey(keyPath)) k.SetValue("SetActiveHours", 1, RegistryValueKind.DWord);
        var done = await _handler.ExecuteAsync(Steps.Of(Steps.RegDeleteValue(keyPath, "SetActiveHours")), sandbox.Context(), default);
        Assert.Equal(StepStatus.Done, done.Status);
        Assert.Equal(StepStatus.Undone, (await _handler.UndoAsync(done, sandbox.Context(), default)).Status);
        using (var k = Registry.CurrentUser.OpenSubKey(keyPath)) Assert.Equal(1, k!.GetValue("SetActiveHours"));
        Assert.Null(PcTidy.Catalog.Tweaks.ProtectionPolicyValues.WhyNeverRestore(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "Anything"));
    }

    [Theory]
    [InlineData(RegistryHive.CurrentUser, @"Software\Vendor", true)]
    [InlineData(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Foo", true)]
    [InlineData(RegistryHive.LocalMachine, @"SOFTWARE\Classes\CLSID\{x}", true)]
    [InlineData(RegistryHive.CurrentUser, @"Software\Classes\foo\bar", true)]
    [InlineData(RegistryHive.CurrentUser, @"Software\Vendor\App", false)]
    public void Shared_and_top_level_keys_are_never_deleted(RegistryHive hive, string key, bool never)
        => Assert.Equal(never, RegistryHandler.IsNeverDeleted(hive, key, out _));

    [Fact]
    public async Task Sandbox_simulates_anything_outside_the_test_key()
    {
        using var sandbox = new TestSandbox();
        var notTest = $@"Software\PCTidyNotATest{Guid.NewGuid():N}";

        var set = await _handler.ExecuteAsync(Steps.Of(Steps.RegSet(notTest, "X", RegistryKind.DWord, "1")), sandbox.Context(), default);
        var hklm = await _handler.ExecuteAsync(Steps.Of(Steps.RegSet(@"SOFTWARE\PCTidyTest\secfix\platform", "X", RegistryKind.DWord, "1", RegistryHive.LocalMachine)), sandbox.Context(), default);

        Assert.Equal(StepStatus.Simulated, set.Status);
        Assert.Equal(StepStatus.Simulated, hklm.Status);
        Assert.False(TestRegistryKey.Exists(notTest));
    }

    [Fact]
    public async Task Simulate_mode_reads_but_never_writes()
    {
        using var sandbox = new TestSandbox();
        using var reg = new TestRegistryKey();
        using (var k = reg.Open()) k.SetValue("V", 3, RegistryValueKind.DWord);

        var result = await _handler.ExecuteAsync(Steps.Of(Steps.RegSet(reg.KeyPath, "V", RegistryKind.DWord, "9")), sandbox.Context(ExecutionMode.Simulate), default);

        Assert.Equal(StepStatus.Simulated, result.Status);
        Assert.Contains("Now: 3", result.Message);
        using (var k = reg.Open()) Assert.Equal(3, k.GetValue("V"));
    }

    [Fact]
    public async Task Bad_specs_fail_without_touching_anything()
    {
        using var sandbox = new TestSandbox();
        using var reg = new TestRegistryKey();
        var noSpec = new PlanItem { Kind = ActionKind.RegistrySetValue, FeatureId = "t", Description = "t" };
        var hivePrefixed = Steps.RegSet(@"HKEY_CURRENT_USER\Software\PCTidyTest\secfix\platform\x", "V", RegistryKind.DWord, "1");
        var badData = Steps.RegSet(reg.KeyPath, "V", RegistryKind.DWord, "lots");

        Assert.Equal(StepStatus.Failed, (await _handler.ExecuteAsync(Steps.Of(noSpec), sandbox.Context(), default)).Status);
        Assert.Equal(StepStatus.Failed, (await _handler.ExecuteAsync(Steps.Of(hivePrefixed), sandbox.Context(), default)).Status);
        Assert.Equal(StepStatus.Failed, (await _handler.ExecuteAsync(Steps.Of(badData), sandbox.Context(), default)).Status);
        using var k = reg.Open();
        Assert.Null(k.GetValue("V"));
    }
}

[CollectionDefinition("Registry", DisableParallelization = true)]
public class RegistryCollection;
