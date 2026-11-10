using System.Diagnostics;
using PcTidy.Catalog.Services;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Safety.Rules;
using Xunit;

namespace PcTidy.Safety.Tests;

/// <summary>
/// The final safety re-review fixes the guard owns: SVC-1 (one protected-service list shared with the handler; only
/// the Services section changes Windows' own services) and AIC-2 (a Claude Code chat in use is re-checked when its
/// quarantine step runs). Everything runs in a testbed sandbox with fake service data; nothing real is changed.
/// </summary>
public sealed class FinalSafetyFixTests : IDisposable
{
    private readonly TestSandbox _s = new();

    public void Dispose() => _s.Dispose();

    private sealed class FakeServiceImages(Dictionary<string, ServiceImage> map) : IServiceImageSource
    {
        public ServiceImage Find(string serviceName)
            => map.TryGetValue(serviceName, out var i) ? i : new ServiceImage(ServiceImageState.Missing, false);
    }

    private static PlanItem Service(string feature, string name, ServiceStart start) => new()
    {
        Kind = ActionKind.ServiceSetStartType, FeatureId = feature, Description = "t", ServiceName = name, ServiceStartType = start,
        NeedsAdmin = true, IsSystemChange = true, Reversible = true,
    };

    private SafetyGuard GuardWith(Dictionary<string, ServiceImage> images) => new(_s.K, _s.Settings, serviceImages: new FakeServiceImages(images));

    // ------------------------------------------------------------------ SVC-1

    [Fact]
    public void Only_the_Services_section_changes_Windows_own_uncatalogued_services()
    {
        var images = new Dictionary<string, ServiceImage>(StringComparer.OrdinalIgnoreCase)
        {
            ["ContosoSync"] = new(ServiceImageState.Found, false, @"""C:\Program Files\Contoso\sync.exe"" -service"),
            ["PcTidyTestWinSvc"] = new(ServiceImageState.Found, true, @"%SystemRoot%\system32\svchost.exe -k netsvcs"),
            ["PcTidyTestBroken"] = new(ServiceImageState.Unreadable, false),
        };
        var guard = GuardWith(images);

        foreach (var feature in new[] { "speed", "tweaks" })
        {
            Assert.True(guard.Check(Service(feature, "ContosoSync", ServiceStart.Manual)).Allowed, feature);
            var windows = guard.Check(Service(feature, "PcTidyTestWinSvc", ServiceStart.Manual));
            Assert.False(windows.Allowed, feature);
            Assert.Contains("Windows' own", windows.Reason);
            Assert.False(guard.Check(Service(feature, "PcTidyTestBroken", ServiceStart.Manual)).Allowed, feature); // unreadable = no
        }
        // The Services section keeps its own catalog rules (unknown → Manual only).
        Assert.True(guard.Check(Service("services", "PcTidyTestWinSvc", ServiceStart.Manual)).Allowed);
        // The bad-tweak finder may still switch a Windows service back to its default (Automatic can't weaken anything).
        Assert.True(guard.Check(Service("tweaks.badtweaks", "PcTidyTestWinSvc", ServiceStart.Automatic)).Allowed);
        // Missing on a practice PC: its services live only in the practice data.
        Assert.True(guard.Check(Service("speed", "PcTidyTestNotInstalled", ServiceStart.Manual)).Allowed);
    }

    [Theory]
    [InlineData(@"%SystemRoot%\System32\svchost.exe -k LocalSystemNetworkRestricted -p", true)]
    [InlineData(@"""C:\Windows\System32\spoolsv.exe""", true)]
    [InlineData(@"\SystemRoot\System32\drivers\tcpip.sys", true)]
    [InlineData(@"System32\drivers\x.sys", true)]
    [InlineData(@"\??\C:\Windows\system32\drivers\y.sys", true)]
    [InlineData(@"""C:\Program Files\Contoso\sync.exe"" -service", false)]
    [InlineData(@"C:\Program Files (x86)\Vendor\svc.exe", false)]
    [InlineData(@"C:\WindowsApps\fake.exe", false)]
    public void The_image_path_says_whether_a_service_is_Windows_own(string imagePath, bool windows)
        => Assert.Equal(windows, RegistryServiceImages.IsUnderWindows(imagePath, @"C:\Windows"));

    [Fact]
    public void The_guard_and_the_handler_share_one_protected_list()
    {
        Assert.Same(ProtectedServices.Names, SafetyPolicy.Default.NeverTouchServices);
        foreach (var name in new[] { "MDCoreSvc", "webthreatdefsvc", "webthreatdefusersvc_1a2b", "DoSvc", "RicochetAntiCheat", "Netlogon", "PlugPlay" })
            Assert.True(ProtectedServices.IsProtected(name), name);
        Assert.True(ProtectedServices.WeakerThanDefault("WinDefend", ServiceStart.Manual));
        Assert.True(ProtectedServices.WeakerThanDefault("wuauserv", ServiceStart.Disabled));
        Assert.False(ProtectedServices.WeakerThanDefault("wuauserv", ServiceStart.Manual));
        Assert.False(ProtectedServices.WeakerThanDefault("BITS", ServiceStart.AutomaticDelayed));
    }

    // ------------------------------------------------------------------ AIC-2

    private PlanItem QuarantineChat(string path) => new()
    {
        Kind = ActionKind.QuarantinePath, FeatureId = "aichats", Description = "t", SourcePath = path, Reversible = true,
    };

    [Fact]
    public void A_Claude_Code_chat_a_running_session_is_writing_stays_when_its_step_runs()
    {
        var project = Path.Combine(_s.K.ClaudeDir, "projects", "C--work-app");
        var chat = _s.File(Path.Combine(project, "1111aaaa-0000-4000-8000-000000000001.jsonl"), "{}\n");
        var sidecar = _s.File(Path.Combine(project, "1111aaaa-0000-4000-8000-000000000001", "subagents", "agent-1.jsonl"), "{}\n");
        var other = _s.File(Path.Combine(project, "2222bbbb-0000-4000-8000-000000000002.jsonl"), "{}\n");
        foreach (var f in new[] { chat, sidecar, other }) File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-3));
        Assert.True(_s.Guard.Check(QuarantineChat(chat)).Allowed);

        // This test process stands in for a running Claude Code.
        var sessions = _s.Dir(Path.Combine(_s.K.ClaudeDir, "sessions"));
        var me = Environment.ProcessId;
        _s.File(Path.Combine(sessions, $"{me}.json"), $"{{\"pid\":{me},\"sessionId\":\"1111aaaa-0000-4000-8000-000000000001\",\"cwd\":\"C:\\\\work\\\\app\"}}");

        var live = _s.Guard.Check(QuarantineChat(chat));
        Assert.False(live.Allowed);
        Assert.Contains("Claude Code is running this chat", live.Reason);
        Assert.False(_s.Guard.Check(QuarantineChat(sidecar)).Allowed);
        Assert.True(_s.Guard.Check(QuarantineChat(other)).Allowed);

        // A marker whose process has ended doesn't count.
        File.Delete(Path.Combine(sessions, $"{me}.json"));
        _s.File(Path.Combine(sessions, "999999.json"), "{\"pid\":2147483000,\"sessionId\":\"1111aaaa-0000-4000-8000-000000000001\"}");
        Assert.True(_s.Guard.Check(QuarantineChat(chat)).Allowed);

        // A running session we can't place keeps chats changed in the last day.
        _s.File(Path.Combine(sessions, $"{me}.json"), "{ not json");
        Assert.True(_s.Guard.Check(QuarantineChat(chat)).Allowed); // three days old
        File.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddHours(-2));
        Assert.False(_s.Guard.Check(QuarantineChat(other)).Allowed);
    }
}
