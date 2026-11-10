using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.PcCare.Model;
using PcTidy.Features.PcCare.Scanning;
using PcTidy.Features.PcCare.Source;

namespace PcTidy.Features.PcCare.Tests;

/// <summary>A clock frozen at the scenario's "now" (10 Nov 2026 12:00 UTC).</summary>
internal sealed class FixedClock : TimeProvider
{
    public static readonly DateTime Now = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
    public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
}

internal static class Fixtures
{
    public static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
    public static string Text(string name) => File.ReadAllText(Path(name));
}

/// <summary>
/// A heavily tweaked PC, as a snapshot: Defender off by policy with no other antivirus, a WinRing0 driver installed,
/// a hijacked Chrome profile, crashes, restore points, old Wi-Fi networks and a leftover profile. Plus a bare one.
/// </summary>
internal static class Scenario
{
    public const string CurrentSid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    public const string GoneSid = "S-1-5-21-1111111111-2222222222-3333333333-1005";
    public const string DomainSid = "S-1-5-21-9999999999-8888888888-7777777777-2001";
    public static readonly Guid WifiCard = Guid.Parse("6f1c2c8e-7a4b-4f0e-9f61-2a1b3c4d5e6f");

    public static PcCareSnapshot OwnerLike() => new()
    {
        TakenUtc = FixedClock.Now,
        IsAdmin = true,
        Winget = new WingetFacts(WingetState.Ready, new[]
        {
            new WingetUpgrade("Git", "Git.Git", "2.41.0.1", "2.41.0.2", "winget"),
            new WingetUpgrade("Windows Terminal", "Microsoft.WindowsTerminal", "1.21.10351.0", "1.22.10731.0", "winget"),
        }, UnreadableRows: 1),
        Security = new SecurityFacts(
            Defender: null,
            Policy: new DefenderPolicy(DisableAntiSpyware: true, DisableAntiVirus: true, DisableRealtimeMonitoring: false, ServiceDisabled: true),
            Products: new[] { new AntivirusProduct("Windows Defender", 401664, "%ProgramFiles%\\Windows Defender\\MsMpeng.exe") },
            SecurityCenterAvailable: true,
            Drivers: new[]
            {
                new DriverService("WinRing0_1_2_0", @"\??\C:\Program Files\FanTool\WinRing0x64.sys", "WinRing0_1_2_0", true),
                new DriverService("disk", @"System32\drivers\disk.sys", "Disk Driver", true),
            }),
        Crashes = new CrashFacts(new[]
        {
            new CrashEvent(CrashKind.BlueScreen, FixedClock.Now.AddDays(-3), BugcheckCode: 0x116),
            new CrashEvent(CrashKind.UnexpectedShutdown, FixedClock.Now.AddDays(-3).AddMinutes(1), BugcheckCode: 0x116),
            new CrashEvent(CrashKind.UnexpectedShutdown, FixedClock.Now.AddDays(-10)),
            new CrashEvent(CrashKind.HardwareCorrected, FixedClock.Now.AddDays(-2)),
            new CrashEvent(CrashKind.AppCrash, FixedClock.Now.AddDays(-1), AppName: "game.exe", ModuleName: "nvwgf2umx.dll", ExceptionCode: "0xC0000005"),
            new CrashEvent(CrashKind.AppCrash, FixedClock.Now.AddDays(-5), AppName: "game.exe", ModuleName: "nvwgf2umx.dll"),
            new CrashEvent(CrashKind.AppHang, FixedClock.Now.AddDays(-90), AppName: "old.exe"),
        }, new[] { new MinidumpInfo(@"C:\Windows\Minidump\111026-1234-01.dmp", FixedClock.Now.AddDays(-3), 1 << 20, 0x116, new ulong[] { 1, 2, 3, 4 }) },
            ReadAccess.Ok, EventLogAvailable: true, FromReliabilityMonitor: false),
        Browsers = new BrowserFacts(
            new[]
            {
                new BrowserProfile("Chrome", "Person 1", @"C:\Users\tester\AppData\Local\Google\Chrome\User Data\Default", "http://start.searchfun.example/", false, 4,
                    new[] { "https://www.google.com/", "http://deals.example-ads.net/" }, "Search Fun", "https://search.searchfun.example/?q={searchTerms}",
                    new[]
                    {
                        new BrowserExtension("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Coupon Helper", false, 3, true, new[] { "nativeMessaging", "<all_urls>" }),
                        new BrowserExtension("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Plain Notes", true, 1, true, new[] { "storage" }),
                    }),
                new BrowserProfile("Edge", "Profile 1", @"C:\Users\tester\AppData\Local\Microsoft\Edge\User Data\Default", null, true, 5,
                    Array.Empty<string>(), "Bing", "{bing:baseURL}search?q={searchTerms}", Array.Empty<BrowserExtension>()),
            },
            new[] { new ForcedExtension("Chrome", "ffffffffffffffffffffffffffffffff", "https://updates.example/crx", "machine") },
            new[] { new BrowserPolicy("Chrome", "HomepageLocation", "http://start.searchfun.example/") },
            new[] { new InstalledProgram("Wave Browser", "Wavesor Software"), new InstalledProgram("Git", "The Git Development Community") }),
        Restore = new RestoreFacts(ReadAccess.Ok, new[]
        {
            new RestorePoint(41, "Windows Update", FixedClock.Now.AddDays(-20), 18),
            new RestorePoint(42, "Installed Some App", FixedClock.Now.AddDays(-6), 0),
            new RestorePoint(43, "PC Tidy: before Tweaks", FixedClock.Now.AddDays(-1), 12),
        }, new ShadowStorage("C:\\", 30L << 30, 100L << 30, 500L << 30), ProtectionOn: true),
        Store = new StoreFacts(true, "Microsoft.WindowsStore_8wekyb3d8bbwe"),
        Wlan = new WlanFacts(true, new[]
        {
            new WlanProfile("Home", WifiCard, "Wi-Fi", true, FixedClock.Now),
            new WlanProfile("Hotel Guest", WifiCard, "Wi-Fi", false, FixedClock.Now.AddDays(-400)),
            new WlanProfile("Cafe", WifiCard, "Wi-Fi", false, null),
        }),
        Profiles = new ProfileFacts(CurrentSid, new[]
        {
            new UserProfileFacts(CurrentSid, @"C:\Users\tester", true, false, FixedClock.Now, true, true),
            new UserProfileFacts(GoneSid, @"C:\Users\oldkid", false, false, FixedClock.Now.AddDays(-700), false, true),
            new UserProfileFacts(DomainSid, @"C:\Users\work.user", false, false, FixedClock.Now.AddDays(-30), false, false),
            new UserProfileFacts("S-1-5-18", @"C:\Windows\system32\config\systemprofile", true, true, null, true, false),
        }),
        Features = new FeatureFacts(
            new[] { new OptionalFeatureState("SMB1Protocol", true), new OptionalFeatureState("WorkFolders-Client", true), new OptionalFeatureState("NetFx3", true) },
            new[] { "App.StepsRecorder~~~~0.0.1.0" },
            new[] { "en-US", "de-DE" }),
        Network = new NetworkFacts(new[] { "192.168.1.1" }, new[]
        {
            new DnsServerResult("192.168.1.1", "Your DNS", true, 40, 10, 10),
            new DnsServerResult("1.1.1.1", "Cloudflare", false, 12, 10, 10),
            new DnsServerResult("8.8.8.8", "Google", false, 18, 10, 10),
            new DnsServerResult("9.9.9.9", "Quad9", false, null, 0, 10),
        }, HasVpnAdapter: true),
        Health = new HealthFacts
        {
            OsName = "Windows 11 Pro", DisplayVersion = "25H2", Build = 26200, LastBootUtc = FixedClock.Now.AddHours(-30),
            UpdatesPausedUntilUtc = FixedClock.Now.AddDays(-48), Disks = new[] { new DiskHealth("Fast SSD 1TB", "SSD", "Healthy", 1L << 40) },
            Gpus = new[] { new GpuReading("Simulated GPU", 54, 0) }, StartupItems = 14, OneDriveBackedUpFolders = 3, FileHistoryConfigured = false,
        },
    };

    public static PcCareBuildContext Context(string? dataRoot = null, IReadOnlyList<DriveDetails>? drives = null, IReadOnlyList<string>? redact = null) => new()
    {
        NowUtc = FixedClock.Now,
        DataRoot = dataRoot ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pctidy-pccare-tests-dataroot"),
        SystemDrive = "C:\\",
        Drives = drives ?? new[]
        {
            new DriveDetails("C:\\", "System", DriveMedia.Ssd, 500L << 30, 100L << 30, true),
            new DriveDetails("D:\\", "Data", DriveMedia.Hdd, 2L << 40, 1L << 40, false),
        },
        RedactTerms = redact ?? new[] { "tester" },
    };

    public static PcCareScanReport Build(PcCareSnapshot? s = null, PcCareBuildContext? c = null)
        => PcCareResultBuilder.Build(s ?? OwnerLike(), c ?? Context());

    public static ScanGroup? Group(this PcCareScanReport r, string tab, string name)
        => r.Result.Groups.FirstOrDefault(g => g.Id == PcCareTabs.GroupId(tab, name));

    public static IEnumerable<PlanItem> AllActions(this PcCareScanReport r) => r.Result.AllItems.SelectMany(i => i.Actions);
}

/// <summary>A per-test sandbox folder under testbed\pccare (removed afterwards).</summary>
internal sealed class TestbedFolder : IDisposable
{
    public TestbedFolder()
    {
        Root = System.IO.Path.Combine(FindRepoRoot(), "testbed", "pccare", Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    /// <summary>Writes &lt;root&gt;\Sim\pccare\snapshot.json (where the sandbox source reads from).</summary>
    public void InstallSnapshot(PcCareSnapshot snapshot)
    {
        var dir = System.IO.Path.Combine(Root, "Sim", "pccare");
        Directory.CreateDirectory(dir);
        File.WriteAllText(System.IO.Path.Combine(dir, "snapshot.json"), System.Text.Json.JsonSerializer.Serialize(snapshot, FixturePcCareSource.Json));
    }

    public void Dispose()
    {
        // TestbedGen's cleaner also clears read-only files and junctions the generator creates.
        try { PcTidy.TestbedGen.TestbedBuilder.Delete(Root); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(System.IO.Path.Combine(d.FullName, "PcTidy.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("PcTidy.sln not found above " + AppContext.BaseDirectory);
    }
}
