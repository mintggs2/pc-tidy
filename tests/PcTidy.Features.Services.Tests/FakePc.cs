using PcTidy.Features.Services.Sources;

namespace PcTidy.Features.Services.Tests;

/// <summary>
/// A standard fake PC used by most tests. Only RpcSs (Protected) and DiagTrack (Proven, privacy, Disabled) come from
/// the catalog; every other name is made up so the tests don't depend on the research table's other entries.
/// </summary>
public static class FakePc
{
    public const string ThirdPartyAuto = "ContosoSync";
    public const string ThirdPartyStopped = "FabrikamUpdate";
    public const string Leftover = "NorthwindAgent";
    public const string TempWorker = "PcTidyTestWorker";
    public const string AntiCheat = "EasyAntiCheat_PcTidyTest";
    public const string SecurityVendor = "PcTidyTestAvShield";
    public const string WindowsUnknown = "PcTidyTestWinSvc";
    public const string PerUserCopy = "PcTidyTestUserSvc_1a2b3c";
    public const string Driver = "pctidytestdrv";

    public static FakeServiceWorld Build(IKnownLocations l, int rpcStart = 2, int diagStart = 2)
    {
        var sys32 = Path.Combine(l.WindowsDir, "System32");
        var contoso = Path.Combine(l.ProgramFiles, "Contoso", "Sync", "ContosoSync.exe");
        var fabrikam = Path.Combine(l.ProgramFilesX86, "Fabrikam", "FabrikamUpdate.exe");
        var gone = Path.Combine(l.ProgramFiles, "Northwind", "Agent.exe");
        var temp = Path.Combine(l.UserTemp, "worker", "svcworker.exe");
        var av = Path.Combine(l.ProgramFiles, "ShieldCo", "shield.exe");
        var eac = Path.Combine(l.ProgramFilesX86, "EasyAntiCheat", "EasyAntiCheat.exe");
        var winExe = Path.Combine(sys32, "pctidytestwin.exe");
        var userDll = Path.Combine(sys32, "pctidytestuser.dll");
        var driver = Path.Combine(sys32, "drivers", Driver + ".sys");

        return new FakeServiceWorld()
            .Add(Svc.Shared("RpcSs", rpcStart, Path.Combine(sys32, "rpcss.dll")), FixedFileFacts.Microsoft(Path.Combine(sys32, "rpcss.dll")))
            .Add(Svc.Shared("DiagTrack", diagStart, Path.Combine(sys32, "diagtrack.dll"), dependsOn: "RpcSs"), FixedFileFacts.Microsoft(Path.Combine(sys32, "diagtrack.dll")))
            .Add(Svc.Own(ThirdPartyAuto, 2, contoso, ServiceRunState.Running, pid: 3300, delayed: true, display: "Contoso Sync Service"), FixedFileFacts.Signed(contoso, "Contoso Ltd."))
            .Add(Svc.Own(ThirdPartyStopped, 2, fabrikam, display: "Fabrikam Updater"), FixedFileFacts.Signed(fabrikam, "Fabrikam, Inc."))
            .Add(Svc.Own(Leftover, 2, gone, display: "Northwind Agent"))
            .Add(Svc.Own(TempWorker, 2, temp, ServiceRunState.Running, pid: 4400), FixedFileFacts.Unsigned(temp))
            .Add(Svc.Own(SecurityVendor, 2, av, ServiceRunState.Running, pid: 4500), FixedFileFacts.Signed(av, "ESET, spol. s r.o."))
            .Add(Svc.Own(AntiCheat, 2, eac), FixedFileFacts.Signed(eac, "Example Games"))
            .Add(Svc.Own(WindowsUnknown, 2, winExe, ServiceRunState.Running, pid: 1000), FixedFileFacts.Microsoft(winExe))
            .Add(Svc.Shared(PerUserCopy, 2, userDll) with { ServiceType = 0xE0 }, FixedFileFacts.Microsoft(userDll))
            .Add(new ServiceRecord { Name = Driver, DisplayName = "Test filter driver", ServiceType = 0x1, Start = 1, State = ServiceRunState.Running, ImagePath = $@"\SystemRoot\System32\drivers\{Driver}.sys" },
                FixedFileFacts.Signed(driver, "Contoso Ltd."))
            .WithUsage(new ProcessUsage(1000, 40L << 20, TimeSpan.FromSeconds(75)), new ProcessUsage(3300, 60L << 20, TimeSpan.FromSeconds(3)));
    }

    public static FakeServiceWorld WithUsage(this FakeServiceWorld w, params ProcessUsage[] usage)
    {
        foreach (var u in usage) w.Usage[u.Pid] = u;
        return w;
    }
}
