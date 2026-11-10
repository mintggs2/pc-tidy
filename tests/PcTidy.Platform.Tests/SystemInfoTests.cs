using PcTidy.Core;
using PcTidy.Core.Model;
using Xunit;

namespace PcTidy.Platform.Tests;

/// <summary>Read-only facts about this PC. Asserts only what is true on every Windows 10/11 PC.</summary>
public class WindowsSystemInfoTests
{
    private readonly ISystemInfo _info = new WindowsSystemInfo();

    [Fact]
    public void Windows_build_and_system_drive_are_reported()
    {
        Assert.True(_info.WindowsBuild >= 19041);
        var system = Assert.Single(_info.Drives, d => d.IsSystem);
        Assert.True(system.TotalBytes > 0);
        Assert.InRange(system.FreeBytes, 0, system.TotalBytes);
    }

    /// <summary>DMV-2: USB, SD, MMC and FireWire disks are external (they can be unplugged), whatever DriveType says.</summary>
    [Theory]
    [InlineData((ushort)7, true)]   // USB
    [InlineData((ushort)12, true)]  // SD
    [InlineData((ushort)13, true)]  // MMC
    [InlineData((ushort)4, true)]   // 1394
    [InlineData((ushort)11, false)] // SATA
    [InlineData((ushort)17, false)] // NVMe
    [InlineData((ushort)8, false)]  // RAID
    [InlineData((ushort)0, false)]  // unknown: not treated as external
    public void Bus_types_that_can_be_unplugged_are_external(ushort busType, bool external)
        => Assert.Equal(external, WindowsSystemInfo.IsExternalBus(busType));

    [Fact]
    public void The_system_drive_is_never_external()
        => Assert.False(Assert.Single(_info.Drives, d => d.IsSystem).IsExternal);

    [Fact]
    public void Running_processes_include_this_one_in_lower_case()
    {
        var names = _info.RunningProcesses();
        Assert.Contains(System.Diagnostics.Process.GetCurrentProcess().ProcessName.ToLowerInvariant(), names);
        Assert.All(names, n => Assert.Equal(n.ToLowerInvariant(), n));
    }

    [Fact]
    public void Slow_facts_never_throw()
    {
        _ = _info.PrimaryGpu;
        Assert.NotNull(_info.GpuNames);
        _ = _info.IsLaptop;
        _ = _info.HasBattery;
        _ = _info.IsAdmin;
        _ = _info.RebootPending;
    }

    [Theory]
    [InlineData(@"PCI\VEN_10DE&DEV_2684", GpuVendor.Nvidia)]
    [InlineData(@"PCI\VEN_1002&DEV_744C", GpuVendor.Amd)]
    [InlineData(@"PCI\VEN_8086&DEV_A780", GpuVendor.Intel)]
    [InlineData(@"ROOT\BasicDisplay\VEN_1414", GpuVendor.Microsoft)]
    [InlineData(@"PCI\VEN_5333&DEV_0001", GpuVendor.Other)]
    [InlineData(null, GpuVendor.Unknown)]
    public void Gpu_vendor_comes_from_the_pci_vendor_id(string? pnp, GpuVendor vendor)
        => Assert.Equal(vendor, WindowsSystemInfo.VendorFromPnpId(pnp));

    [Fact]
    public async Task Defender_status_read_never_throws()
    {
        var status = await DefenderStatusReader.ReadAsync(default);
        if (status?.SignaturesUpdatedUtc is { } t) Assert.True(t.Year >= 1980);
    }

    [Fact]
    public void Platform_handlers_cover_every_platform_action_kind_once()
    {
        var handlers = PlatformHandlers.CreateAll();
        var kinds = new[]
        {
            ActionKind.RegistrySetValue, ActionKind.RegistryDeleteValue, ActionKind.RegistryDeleteKey, ActionKind.ServiceSetStartType,
            ActionKind.ScheduledTaskSetEnabled, ActionKind.StartupItemSetEnabled, ActionKind.EmptyRecycleBin, ActionKind.RunSystemTool, ActionKind.OpenExternal,
        };
        foreach (var kind in kinds)
        {
            var item = new PlanItem { Kind = kind, FeatureId = "t", Description = "t" };
            Assert.Single(handlers, h => h.CanHandle(item));
        }
        Assert.DoesNotContain(handlers, h => h.CanHandle(new PlanItem { Kind = ActionKind.QuarantinePath, FeatureId = "t", Description = "t" }));
    }
}
