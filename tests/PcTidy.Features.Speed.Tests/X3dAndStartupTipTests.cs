using PcTidy.Features.Speed;
using PcTidy.Features.Speed.Settings;
using PcTidy.Features.Speed.Sources;
using PcTidy.Features.Speed.Startup;

namespace PcTidy.Features.Speed.Tests;

/// <summary>Community tweaks §9.6: dual-CCD Ryzen X3D keeps Balanced; the startup-notification tip is guidance only.</summary>
public class X3dAndStartupTipTests
{
    private static readonly PowerScheme[] Schemes =
    {
        new(FixedPowerSource.Balanced, "Balanced", PowerPersonality.Balanced),
        new(FixedPowerSource.HighPerformance, "High performance", PowerPersonality.HighPerformance),
        new(FixedPowerSource.Ultimate, "Ultimate Performance", PowerPersonality.HighPerformance),
    };

    private static SpeedEnvironment WithCpu(SpeedEnvironment env, string brand) => new()
    {
        Registry = env.Registry, Files = env.Files, Tasks = env.Tasks, Processes = env.Processes, Clock = env.Clock, Power = env.Power,
        Gpu = env.Gpu, Disks = env.Disks, StoreApps = env.StoreApps, Locks = env.Locks, CpuSampleInterval = env.CpuSampleInterval,
        CpuBrand = () => brand,
    };

    [Fact]
    public async Task Dual_ccd_x3d_is_never_offered_a_maximum_performance_plan()
    {
        using var desk = new SpeedSandbox(simJson: "{\"isLaptop\": false}");
        var env = WithCpu(Env.Fake(power: new FixedPowerSource(FixedPowerSource.HighPerformance, onBattery: null, Schemes)), "AMD Ryzen 9 7950X3D 16-Core Processor");
        var result = await new SettingsScanner(_ => env).ScanAsync(desk.Context(), null, CancellationToken.None);

        var targets = result.AllItems.SelectMany(i => i.Actions).Where(a => a.Kind == ActionKind.PowerPlanSet).Select(a => a.Args!["schemeGuid"]).ToList();
        Assert.Equal(new[] { FixedPowerSource.Balanced.ToString("D") }, targets);
        Assert.Contains("Balanced", result.AllItems.Single(i => i.Title == "Switch to Ultimate Performance").DisabledReason);
        Assert.Contains("Game Bar", result.AllItems.Single(i => i.Title == "Power plan").Reason);
    }

    [Fact]
    public async Task Other_cpus_keep_the_normal_choice()
    {
        using var desk = new SpeedSandbox(simJson: "{\"isLaptop\": false}");
        var env = WithCpu(Env.Fake(power: new FixedPowerSource(FixedPowerSource.Balanced, onBattery: null, Schemes)), "AMD Ryzen 7 9800X3D 8-Core Processor");
        var result = await new SettingsScanner(_ => env).ScanAsync(desk.Context(), null, CancellationToken.None);
        Assert.Contains(result.AllItems.SelectMany(i => i.Actions), a => a.Kind == ActionKind.PowerPlanSet && a.Args!["schemeGuid"] == FixedPowerSource.HighPerformance.ToString("D"));
    }

    [Fact]
    public async Task Startup_scan_points_to_windows_startup_app_notification_without_writing_it()
    {
        using var sb = new SpeedSandbox();
        var result = await new StartupScanner(_ => Env.Fake()).ScanAsync(sb.Context(), null, CancellationToken.None);
        Assert.Contains(StartupScanner.StartupNotificationTip, result.Notes);
        Assert.DoesNotContain(result.AllItems.SelectMany(i => i.Actions), a => a.Registry?.KeyPath.Contains("Notifications", StringComparison.OrdinalIgnoreCase) == true);
    }
}
