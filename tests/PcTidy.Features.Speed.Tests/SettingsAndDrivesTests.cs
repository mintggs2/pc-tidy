using PcTidy.Features.Speed.Drives;
using PcTidy.Features.Speed.Settings;
using PcTidy.Features.Speed.Sources;
using Hive = PcTidy.Core.Model.RegistryHive;

namespace PcTidy.Features.Speed.Tests;

public class SettingsTests
{
    private static readonly PowerScheme[] Schemes =
    {
        new(FixedPowerSource.Balanced, "Balanced", PowerPersonality.Balanced),
        new(FixedPowerSource.HighPerformance, "High performance", PowerPersonality.HighPerformance),
        new(FixedPowerSource.Ultimate, "Ultimate Performance", PowerPersonality.HighPerformance),
        new(FixedPowerSource.PowerSaver, "Power saver", PowerPersonality.PowerSaver),
    };

    private static IReadOnlyList<string> SwitchTargets(IEnumerable<ScanItem> items)
        => items.SelectMany(i => i.Actions).Where(a => a.Kind == ActionKind.PowerPlanSet).Select(a => a.Args!["schemeGuid"]).ToList();

    [Theory]
    [InlineData(true)]   // on battery
    [InlineData(null)]   // Windows can't tell: treated as battery
    public async Task Laptop_on_battery_is_never_offered_High_performance_or_Ultimate(bool? onBattery)
    {
        using var sb = new SpeedSandbox(simJson: "{\"isLaptop\": true}");
        Assert.True(sb.Host.System.IsLaptop);
        var power = new FixedPowerSource(FixedPowerSource.PowerSaver, onBattery, Schemes);
        var result = await new SettingsScanner(_ => Env.Fake(power: power)).ScanAsync(sb.Context(), null, CancellationToken.None);
        var targets = SwitchTargets(result.AllItems);
        Assert.DoesNotContain(FixedPowerSource.HighPerformance.ToString("D"), targets);
        Assert.DoesNotContain(FixedPowerSource.Ultimate.ToString("D"), targets);
        Assert.Contains(FixedPowerSource.Balanced.ToString("D"), targets);
        var ultimate = result.AllItems.Single(i => i.Title == "Switch to Ultimate Performance");
        Assert.Contains("battery", ultimate.DisabledReason);
        Assert.All(result.AllItems, i => Assert.False(i.DefaultSelected && i.Actions.Any(a => a.Kind == ActionKind.PowerPlanSet)));
    }

    [Fact]
    public async Task Plugged_in_laptop_and_desktop_may_pick_any_plan_but_only_Balanced_is_recommended()
    {
        using var desk = new SpeedSandbox(simJson: "{\"isLaptop\": false}");
        Assert.False(desk.Host.System.IsLaptop);
        var power = new FixedPowerSource(FixedPowerSource.HighPerformance, onBattery: null, Schemes);
        var result = await new SettingsScanner(_ => Env.Fake(power: power)).ScanAsync(desk.Context(), null, CancellationToken.None);
        var targets = SwitchTargets(result.AllItems);
        Assert.Contains(FixedPowerSource.Balanced.ToString("D"), targets);
        Assert.Contains(FixedPowerSource.Ultimate.ToString("D"), targets);
        Assert.Equal("High performance", result.AllItems.Single(i => i.Title == "Power plan").CurrentState);
        Assert.Contains("Recommended", result.AllItems.Single(i => i.Title == "Switch to Balanced").Reason);

        using var laptop = new SpeedSandbox(simJson: "{\"isLaptop\": true}");
        var plugged = new FixedPowerSource(FixedPowerSource.Balanced, onBattery: false, Schemes);
        var r2 = await new SettingsScanner(_ => Env.Fake(power: plugged)).ScanAsync(laptop.Context(), null, CancellationToken.None);
        Assert.Contains(FixedPowerSource.HighPerformance.ToString("D"), SwitchTargets(r2.AllItems));
    }

    [Fact]
    public void Battery_rule()
    {
        Assert.True(SettingsScanner.IsLaptopOnBattery(new TestSystemInfo { IsLaptop = true }, true));
        Assert.True(SettingsScanner.IsLaptopOnBattery(new TestSystemInfo { HasBattery = true }, null));
        Assert.False(SettingsScanner.IsLaptopOnBattery(new TestSystemInfo { IsLaptop = true }, false));
        Assert.False(SettingsScanner.IsLaptopOnBattery(new TestSystemInfo(), true));
        Assert.False(SettingsScanner.IsLaptopOnBattery(null, true));
    }

    [Fact]
    public async Task Game_Mode_off_is_a_safe_pre_ticked_fix_the_real_guard_allows()
    {
        using var sb = new SpeedSandbox();
        using var reg = new FakeRegistry();
        reg.Set(Hive.CurrentUser, SpeedRegistryAllowances.GameBarKey, SpeedRegistryAllowances.GameModeValue, 0);
        var result = await new SettingsScanner(_ => Env.Fake(registry: reg.View)).ScanAsync(sb.Context(), null, CancellationToken.None);
        var gm = result.AllItems.Single(i => i.Id == SettingsScanner.GameModeItemId);
        Assert.Equal("Off", gm.CurrentState);
        Assert.Equal(Badge.Safe, gm.Badge);
        Assert.True(gm.DefaultSelected);
        var a = Assert.Single(gm.Actions);
        Assert.Equal(ActionKind.RegistrySetValue, a.Kind);
        Assert.Equal("1", a.Registry!.Data);
        Assert.True(sb.Host.Safety.Check(a).Allowed, sb.Host.Safety.Check(a).Reason);

        using var reg2 = new FakeRegistry(); // no value = Windows default = on
        var on = await new SettingsScanner(_ => Env.Fake(registry: reg2.View)).ScanAsync(sb.Context(), null, CancellationToken.None);
        var gmOn = on.AllItems.Single(i => i.Id == SettingsScanner.GameModeItemId);
        Assert.Equal("On", gmOn.CurrentState);
        Assert.Empty(gmOn.Actions);
    }

    [Fact]
    public async Task GPU_scheduling_is_shown_only_when_supported_and_only_ever_turned_on()
    {
        using var sb = new SpeedSandbox();
        using var reg = new FakeRegistry();
        var none = await new SettingsScanner(_ => Env.Fake(registry: reg.View, gpu: new FixedGpuScheduling(null))).ScanAsync(sb.Context(), null, CancellationToken.None);
        Assert.DoesNotContain(none.AllItems, i => i.Id == "speed.settings.gpu-scheduling");
        var unsupported = await new SettingsScanner(_ => Env.Fake(registry: reg.View, gpu: new FixedGpuScheduling(new(false, false)))).ScanAsync(sb.Context(), null, CancellationToken.None);
        Assert.DoesNotContain(unsupported.AllItems, i => i.Id == "speed.settings.gpu-scheduling");

        var on = await new SettingsScanner(_ => Env.Fake(registry: reg.View, gpu: new FixedGpuScheduling(new(true, true)))).ScanAsync(sb.Context(), null, CancellationToken.None);
        var onItem = on.AllItems.Single(i => i.Id == "speed.settings.gpu-scheduling");
        Assert.Empty(onItem.Actions);
        Assert.Contains("Leave it on", onItem.Reason);

        reg.Set(Hive.LocalMachine, SpeedRegistryAllowances.GraphicsDriversKey, SpeedRegistryAllowances.GpuSchedulingValue, 1);
        var off = await new SettingsScanner(_ => Env.Fake(registry: reg.View, gpu: new FixedGpuScheduling(new(true, false)))).ScanAsync(sb.Context(), null, CancellationToken.None);
        var offItem = off.AllItems.Single(i => i.Id == "speed.settings.gpu-scheduling");
        var a = Assert.Single(offItem.Actions);
        Assert.Equal("2", a.Registry!.Data);
        Assert.True(a.NeedsAdmin && a.IsSystemChange);
        Assert.True(offItem.NeedsRestart);
        Assert.False(offItem.DefaultSelected);
        Assert.True(sb.Host.Safety.Check(a).Allowed, sb.Host.Safety.Check(a).Reason);
    }

    [Fact]
    public async Task Visual_effects_offers_best_performance_as_your_call()
    {
        using var sb = new SpeedSandbox();
        using var reg = new FakeRegistry();
        var result = await new SettingsScanner(_ => Env.Fake(registry: reg.View)).ScanAsync(sb.Context(), null, CancellationToken.None);
        var fx = result.AllItems.Single(i => i.Id == "speed.settings.visual-effects");
        Assert.Equal("Let Windows choose", fx.CurrentState);
        Assert.Equal(Badge.Check, fx.Badge);
        Assert.False(fx.DefaultSelected);
        var a = Assert.Single(fx.Actions);
        Assert.Equal("2", a.Registry!.Data);
        Assert.True(sb.Host.Safety.Check(a).Allowed, sb.Host.Safety.Check(a).Reason);
    }
}

public class DriveTests
{
    [Fact]
    public void An_SSD_is_never_defragmented()
    {
        var ssd = OptimizePlanner.Plan(@"C:\", DriveMedia.Ssd, trimOn: true)!;
        Assert.Equal(ActionKind.VolumeOptimize, ssd.Kind);
        Assert.Equal("C", ssd.Args!["drive"]);
        Assert.Equal("retrim", ssd.Args["expect"]);
        Assert.DoesNotContain("efrag", ssd.Description);

        Assert.Equal("defrag", OptimizePlanner.Plan(@"D:\", DriveMedia.Hdd, trimOn: true)!.Args!["expect"]);
        Assert.Null(OptimizePlanner.Plan(@"E:\", DriveMedia.Unknown, trimOn: true));        // can't tell → never risk it
        Assert.Null(OptimizePlanner.Plan(@"C:\", DriveMedia.Ssd, trimOn: false));           // ReTrim can't help with TRIM off
        Assert.Null(OptimizePlanner.Plan(@"F:\", DriveMedia.Removable, trimOn: true));
        Assert.Null(OptimizePlanner.Plan(@"D:\testbed\C\", DriveMedia.Hdd, trimOn: true));  // not a real drive root
    }

    [Fact]
    public async Task Physical_disk_media_wins_over_the_drive_list_and_mixed_volumes_are_left_alone()
    {
        using var sb = new SpeedSandbox();
        var system = new TestSystemInfo
        {
            Drives = new[]
            {
                new DriveDetails(@"C:\", "Windows", DriveMedia.Hdd, 500L << 30, 20L << 30, true),   // list says HDD, the disk says SSD
                new DriveDetails(@"D:\", "Games", DriveMedia.Hdd, 2000L << 30, 1500L << 30, false),
                new DriveDetails(@"E:\", "Pool", DriveMedia.Ssd, 1000L << 30, 1L << 30, false),     // spread over an SSD and an HDD
            },
        };
        var disks = new FixedDiskSource(new[]
        {
            new PhysicalDiskInfo("0", "Fast SSD", DriveMedia.Ssd, "NVMe", DiskHealth.Healthy, 512L << 30, new[] { 'C', 'E' }, null),
            new PhysicalDiskInfo("1", "Big HDD", DriveMedia.Hdd, "SATA", DiskHealth.Warning, 2000L << 30, new[] { 'D', 'E' }, new ReliabilityInfo(41, null, 0, 0, 1234)),
        });
        var tasks = new FixedTaskSource(states: new Dictionary<string, TaskState> { [DrivesScanner.DefragTaskPath] = new(true, DateTime.UtcNow.AddDays(-3), DateTime.UtcNow.AddDays(4)) });
        var env = Env.Fake(disks: disks, tasks: tasks);
        var result = await new DrivesScanner(_ => env).ScanAsync(sb.Context(system: system), null, CancellationToken.None);

        var optimize = result.AllItems.SelectMany(i => i.Actions).Where(a => a.Kind == ActionKind.VolumeOptimize).ToList();
        Assert.Equal("retrim", optimize.Single(a => a.Args!["drive"] == "C").Args!["expect"]);
        Assert.Equal("defrag", optimize.Single(a => a.Args!["drive"] == "D").Args!["expect"]);
        Assert.DoesNotContain(optimize, a => a.Args!["drive"] == "E");
        Assert.All(optimize, a => { Assert.True(a.NeedsAdmin); Assert.False(a.Reversible); });

        var health = result.Groups.Single(g => g.Id == DrivesScanner.HealthGroup).Items;
        Assert.Equal("Warning", health.Single(i => i.Title.StartsWith("Big HDD")).CurrentState);
        Assert.Equal("Available with admin", health.Single(i => i.Title.StartsWith("Fast SSD")).Details!["Temperature, wear, errors"]);
        Assert.False(health.Single(i => i.Title.StartsWith("Big HDD")).Details!.ContainsKey("Temperature")); // not admin → counters not read
        Assert.Contains(result.Notes, n => n.Contains("admin"));

        var space = result.Groups.Single(g => g.Id == DrivesScanner.SpaceGroup).Items;
        Assert.Contains("Almost full", space.Single(i => i.Detail == @"E:\").CurrentState);
        Assert.Contains("Plenty", space.Single(i => i.Detail == @"D:\").CurrentState);
        Assert.Equal("On", result.AllItems.Single(i => i.Id == "speed.drives.schedule").CurrentState);
    }

    [Fact]
    public async Task Admin_reads_counters_and_TRIM_state_comes_from_the_registry()
    {
        using var sb = new SpeedSandbox();
        using var reg = new FakeRegistry();
        reg.Set(Hive.LocalMachine, DrivesScanner.FileSystemKey, DrivesScanner.TrimValue, 1);
        var system = new TestSystemInfo { IsAdmin = true, Drives = new[] { new DriveDetails(@"C:\", "Windows", DriveMedia.Ssd, 500L << 30, 200L << 30, true) } };
        var disks = new FixedDiskSource(new[] { new PhysicalDiskInfo("0", "Fast SSD", DriveMedia.Ssd, "NVMe", DiskHealth.Healthy, 512L << 30, new[] { 'C' }, new ReliabilityInfo(38, 3, 0, 0, 900)) });
        var result = await new DrivesScanner(_ => Env.Fake(registry: reg.View, disks: disks)).ScanAsync(sb.Context(system: system), null, CancellationToken.None);
        var ssd = result.Groups.Single(g => g.Id == DrivesScanner.HealthGroup).Items.Single();
        Assert.Equal("38 °C", ssd.Details!["Temperature"]);
        Assert.Equal("3% used", ssd.Details["Wear"]);
        Assert.Equal("TRIM is off", result.AllItems.Single(i => i.Id == "speed.drives.trim").Title);
        // TRIM off: ReTrim isn't offered (and a defrag never is).
        Assert.DoesNotContain(result.AllItems.SelectMany(i => i.Actions), a => a.Kind == ActionKind.VolumeOptimize);
    }

    [Fact]
    public async Task Sandbox_drives_are_never_optimized_and_the_real_guard_refuses_them_too()
    {
        using var sb = new SpeedSandbox(SandboxProfile.Default);
        var result = await new DrivesScanner(_ => Env.Fake()).ScanAsync(sb.Context(), null, CancellationToken.None);
        Assert.DoesNotContain(result.AllItems.SelectMany(i => i.Actions), a => a.Kind == ActionKind.VolumeOptimize);
        Assert.Equal(2, result.Groups.Single(g => g.Id == DrivesScanner.SpaceGroup).Items.Count); // both simulated drives listed
        var real = OptimizePlanner.Plan(@"C:\", DriveMedia.Ssd, true)!;
        Assert.False(sb.Host.Safety.Check(real).Allowed); // a sandbox has no real drive letters
    }

    [Theory]
    [InlineData(100, 50, "Plenty of space")]
    [InlineData(1000, 80, "Getting full")]
    [InlineData(100, 9, "Getting full")]
    [InlineData(1000, 30, "Almost full")]
    [InlineData(100, 1, "Almost full")]
    public void Free_space_warnings(long totalGb, long freeGb, string expected)
        => Assert.Equal(expected, DrivesScanner.SpaceVerdict(totalGb << 30, freeGb << 30).State);
}
