using Microsoft.Win32;
using PcTidy.Core.Model;
using RegistryHive = PcTidy.Core.Model.RegistryHive;
using Xunit;

namespace PcTidy.Platform.Tests;

public class StartupApprovedValueTests
{
    [Fact]
    public void Enabled_and_disabled_bytes_match_task_manager()
    {
        Assert.Equal(new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, StartupApprovedValue.Enabled());

        var when = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var off = StartupApprovedValue.Disabled(when);
        Assert.Equal(12, off.Length);
        Assert.Equal(new byte[] { 3, 0, 0, 0 }, off[..4]);
        Assert.Equal(when.ToFileTimeUtc(), BitConverter.ToInt64(off, 4));
    }

    [Theory]
    [InlineData(new byte[] { 2, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 6, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 3, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 1, 0, 0, 0 }, false)]
    [InlineData(new byte[0], true)]
    public void First_byte_parity_decides_on_or_off(byte[] value, bool enabled)
        => Assert.Equal(enabled, StartupApprovedValue.IsEnabled(value));

    [Fact]
    public void Missing_value_means_on() => Assert.True(StartupApprovedValue.IsEnabled(null));
}

[Collection("Registry")]
public class StartupApprovedHandlerTests
{
    private static PlanItem Item(string name, bool enable, string location = "Run", string? hive = null)
    {
        var args = new Dictionary<string, string> { ["location"] = location, ["name"] = name };
        if (hive is not null) args["hive"] = hive;
        return new PlanItem { Kind = ActionKind.StartupItemSetEnabled, FeatureId = "test", Description = "test", Args = args, Enable = enable };
    }

    [Fact]
    public async Task Disable_never_switched_item_then_undo_removes_the_value_and_created_key()
    {
        using var sandbox = new TestSandbox();
        using var reg = new TestRegistryKey();
        var handler = new StartupApprovedHandler(reg.Sub("StartupApproved"));

        var done = await handler.ExecuteAsync(Steps.Of(Item("Contoso Updater", enable: false)), sandbox.Context(), default);

        Assert.Equal(StepStatus.Done, done.Status);
        Assert.False(done.RegistryPrior!.ValueExisted);
        using (var k = Registry.CurrentUser.OpenSubKey(reg.Sub(@"StartupApproved\Run"))!)
        {
            var bytes = (byte[])k.GetValue("Contoso Updater")!;
            Assert.Equal(3, bytes[0]);
            Assert.Equal(12, bytes.Length);
        }
        Assert.True(await handler.WasCompletedAsync(done, sandbox.Context(), default));

        var undone = await handler.UndoAsync(done, sandbox.Context(), default);

        Assert.Equal(StepStatus.Undone, undone.Status);
        Assert.False(TestRegistryKey.Exists(reg.Sub("StartupApproved")));
    }

    [Fact]
    public async Task Enable_restores_exact_prior_bytes_on_undo()
    {
        using var sandbox = new TestSandbox();
        using var reg = new TestRegistryKey();
        var prior = new byte[] { 3, 0, 0, 0, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x01 };
        using (var k = Registry.CurrentUser.CreateSubKey(reg.Sub(@"StartupApproved\Run32"))) k.SetValue("Tool", prior, RegistryValueKind.Binary);
        var handler = new StartupApprovedHandler(reg.Sub("StartupApproved"));

        var done = await handler.ExecuteAsync(Steps.Of(Item("Tool", enable: true, location: "run32")), sandbox.Context(), default);
        Assert.Equal(StepStatus.Done, done.Status);
        using (var k = Registry.CurrentUser.OpenSubKey(reg.Sub(@"StartupApproved\Run32"))!)
            Assert.Equal(StartupApprovedValue.Enabled(), (byte[])k.GetValue("Tool")!);

        var undone = await handler.UndoAsync(done, sandbox.Context(), default);
        Assert.Equal(StepStatus.Undone, undone.Status);
        using (var k = Registry.CurrentUser.OpenSubKey(reg.Sub(@"StartupApproved\Run32"))!)
            Assert.Equal(prior, (byte[])k.GetValue("Tool")!);
    }

    [Fact]
    public async Task Already_in_the_wanted_state_is_skipped()
    {
        using var sandbox = new TestSandbox();
        using var reg = new TestRegistryKey();
        var handler = new StartupApprovedHandler(reg.Sub("StartupApproved"));

        var result = await handler.ExecuteAsync(Steps.Of(Item("Never switched", enable: true)), sandbox.Context(), default);

        Assert.Equal(StepStatus.Skipped, result.Status);
        Assert.False(TestRegistryKey.Exists(reg.Sub("StartupApproved")));
    }

    [Theory]
    [InlineData("Services", "x", null)]
    [InlineData("Run", "", null)]
    [InlineData("Run", "x", "HKCR")]
    public void Bad_arguments_are_rejected(string location, string name, string? hive)
        => Assert.False(new StartupApprovedHandler().TryGetTarget(Item(name, false, location, hive), out _, out _));

    [Fact]
    public async Task Default_location_is_simulated_in_the_sandbox()
    {
        using var sandbox = new TestSandbox();
        var handler = new StartupApprovedHandler();

        var user = await handler.ExecuteAsync(Steps.Of(Item($"PCTidyTest-{Guid.NewGuid():N}", false)), sandbox.Context(), default);
        var machine = await handler.ExecuteAsync(Steps.Of(Item($"PCTidyTest-{Guid.NewGuid():N}", false, hive: "HKLM")), sandbox.Context(), default);

        Assert.Equal(StepStatus.Simulated, user.Status);
        Assert.Equal(StepStatus.Simulated, machine.Status);
    }
}
