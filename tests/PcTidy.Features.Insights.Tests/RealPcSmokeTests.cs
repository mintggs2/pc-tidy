using PcTidy.Core;
using PcTidy.Features.Insights.Source;
using Xunit;

namespace PcTidy.Features.Insights.Tests;

/// <summary>
/// The real source on this PC: every read is read-only (registry, WMI, event log, display APIs) and must never throw.
/// Paths come from a sandbox so no real folder is walked; nothing on the PC is changed.
/// </summary>
public class RealPcSmokeTests
{
    [Fact]
    public void Every_real_read_returns_without_throwing()
    {
        using var tb = new TestbedFolder();
        var src = new WindowsInsightsSource(new SandboxKnownLocations(tb.Root, SandboxProfile.Minimal));

        var id = src.ReadWindowsIdentity();
        Assert.True(id.Build >= 19041);
        Assert.NotNull(src.ReadStability(DateTime.UtcNow.AddDays(-30)));
        Assert.NotNull(src.ReadMemoryTests());
        Assert.NotNull(src.ReadBackup());
        Assert.NotNull(src.ReadDisplays());
        var mem = src.ReadMemory();
        if (mem.Value is { } m) Assert.All(m.Modules, x => Assert.True(x.CapacityBytes >= 0));
        Assert.NotNull(src.ReadFileSystems());
        Assert.True(src.ReadTimer().Frequency > 0);
        Assert.NotNull(src.ReadSecurity());
    }
}
