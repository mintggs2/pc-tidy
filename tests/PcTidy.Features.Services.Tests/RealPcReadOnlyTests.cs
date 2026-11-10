using PcTidy.Features.Services.Scanning;
using PcTidy.Features.Services.Sources;

namespace PcTidy.Features.Services.Tests;

/// <summary>
/// Opt-in (set PCTIDY_READ_REAL_SERVICES=1): checks the Service Control Manager calls against this PC. READ-ONLY:
/// services are listed and queried with query rights only, nothing is changed, and only counts are asserted (no
/// names or paths of this PC are recorded).
/// </summary>
public class RealPcReadOnlyTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("PCTIDY_READ_REAL_SERVICES") == "1";

    [Fact]
    public void Real_service_list_reads_configuration_descriptions_and_recovery()
    {
        if (!Enabled) return;
        var problems = new List<string>();
        var records = new WindowsServiceSource().ReadAll(problems, CancellationToken.None);
        Assert.True(records.Count > 50, $"only {records.Count} services");
        Assert.Contains(records, r => r.IsDriver);
        Assert.True(records.Count(r => r.StartKnown) > records.Count / 2);
        Assert.Contains(records, r => r.Description is not null);
        Assert.Contains(records, r => r.ServiceDll is not null);
        Assert.Contains(records, r => r.DependsOn.Count > 0);
        Assert.Contains(records, r => r.DelayedAutostart);
        Assert.Contains(records, r => r.TriggerStart);

        var rpc = new Platform.WindowsServiceControl().QueryRecovery("RpcSs");
        Assert.NotNull(rpc);

        var l = new PcTidy.Safety.WindowsKnownLocations(new AppSettings());
        var env = new ServicesEnvironment { Services = new WindowsServiceSource(), Files = new WindowsFileFacts(), Usage = new WindowsProcessUsage(), IsRealPc = true };
        var inventory = ServiceInventory.Read(env, l, false, null, CancellationToken.None);
        Assert.Contains(inventory.Services, s => s.Facts.IsMicrosoft);
        Assert.Contains(inventory.Services, s => s.Usage is { SharedWith: > 0 });
    }
}
