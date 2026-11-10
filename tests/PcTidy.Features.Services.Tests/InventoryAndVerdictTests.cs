using PcTidy.Features.Services.Model;
using PcTidy.Features.Services.Rules;
using PcTidy.Features.Services.Scanning;
using PcTidy.Features.Services.Sources;

namespace PcTidy.Features.Services.Tests;

public class InventoryAndVerdictTests
{
    private static (ServiceInventoryResult Result, SandboxKnownLocations Locations) Scan(TestFolder folder, bool expert = false, Func<IKnownLocations, FakeServiceWorld>? world = null)
    {
        var l = new SandboxKnownLocations(Path.Combine(folder.Path, "pc"), SandboxProfile.Minimal);
        var w = (world ?? (x => FakePc.Build(x)))(l);
        return (ServiceInventory.Read(w.Environment(), l, expert, null, CancellationToken.None), l);
    }

    private static ServiceInfo Get(ServiceInventoryResult r, string name) => r.All.Single(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Drivers_are_listed_separately_and_never_offered()
    {
        using var folder = new TestFolder("inv");
        var (r, _) = Scan(folder);
        var driver = Assert.Single(r.Drivers);
        Assert.Equal(FakePc.Driver, driver.Name);
        Assert.DoesNotContain(r.Services, s => s.IsDriver);
        Assert.Equal(ServiceVerdictKind.Driver, driver.Assessment.Verdict);
        Assert.False(driver.Assessment.CanChange);
        Assert.Equal(ServicePolicy.DriverReason, driver.Assessment.NotOfferedReason);
    }

    [Fact]
    public void Shared_hosts_report_the_dll_publisher_and_how_many_services_share_the_process()
    {
        using var folder = new TestFolder("inv");
        var (r, l) = Scan(folder);
        var diag = Get(r, "DiagTrack");
        Assert.Equal(Path.Combine(l.WindowsDir, "System32", "diagtrack.dll"), diag.BinaryPath);
        Assert.True(diag.Facts.IsMicrosoft);
        Assert.False(diag.IsThirdParty);
        Assert.NotNull(diag.Usage);
        Assert.Equal(3, diag.Usage!.SharedWith); // RpcSs, the Windows test service and the per-user copy share pid 1000
        Assert.Contains("shared with 3 other services", diag.Usage.Text);
        Assert.Contains("RpcSs", diag.Record.DependsOn);
        Assert.Contains("DiagTrack", Get(r, "RpcSs").Dependents);
        Assert.Equal(0, Get(r, FakePc.ThirdPartyAuto).Usage!.SharedWith);
    }

    [Fact]
    public void Catalog_verdicts_and_offers_follow_the_rules()
    {
        using var folder = new TestFolder("inv");
        var (r, _) = Scan(folder);

        var diag = Get(r, "DiagTrack"); // Proven, recommended Disabled
        Assert.Equal(ServiceVerdictKind.Proven, diag.Assessment.Verdict);
        Assert.Equal(ServiceStart.Disabled, diag.Assessment.Recommended!.Target);
        Assert.Equal(diag.Entry!.WhatItDoes, diag.WhatItDoes);

        var rpc = Get(r, "RpcSs"); // Protected: never offered, greyed with the reason
        Assert.Equal(ServiceVerdictKind.Protected, rpc.Assessment.Verdict);
        Assert.False(rpc.Assessment.CanChange);
        Assert.False(string.IsNullOrWhiteSpace(rpc.Assessment.NotOfferedReason));
    }

    [Fact]
    public void Proven_service_already_at_its_recommendation_offers_the_windows_default_back()
    {
        using var folder = new TestFolder("inv");
        var (r, _) = Scan(folder, world: l => FakePc.Build(l, diagStart: 4));
        var diag = Get(r, "DiagTrack");
        var option = Assert.Single(diag.Assessment.Options);
        Assert.True(option.IsWindowsDefault);
        Assert.Equal(WindowsDefaults.Preferred(diag.Entry!.WindowsDefaults), option.Target);
    }

    [Fact]
    public void Unknown_third_party_services_get_Manual_only_and_only_when_they_start_with_Windows()
    {
        using var folder = new TestFolder("inv");
        var (r, _) = Scan(folder);
        var contoso = Get(r, FakePc.ThirdPartyAuto);
        Assert.Equal(ServiceVerdictKind.ThirdParty, contoso.Assessment.Verdict);
        Assert.Equal(new[] { ServiceStart.Manual }, contoso.Assessment.Options.Select(o => o.Target).ToArray());
        Assert.True(contoso.IsThirdParty);
        Assert.Equal("Contoso Ltd.", contoso.Facts.Publisher);
        Assert.All(r.Services, s => Assert.DoesNotContain(s.Assessment.Options, o => s.Entry is null && o.Target == ServiceStart.Disabled));
    }

    [Fact]
    public void Windows_services_without_a_verdict_security_and_anti_cheat_are_left_alone()
    {
        using var folder = new TestFolder("inv");
        var (r, _) = Scan(folder);
        var win = Get(r, FakePc.WindowsUnknown);
        Assert.Equal(ServiceVerdictKind.Windows, win.Assessment.Verdict);
        Assert.False(win.Assessment.CanChange);

        var eac = Get(r, FakePc.AntiCheat); // prefix pattern EasyAntiCheat*
        Assert.Equal(ServiceVerdictKind.Protected, eac.Assessment.Verdict);
        Assert.False(eac.Assessment.CanChange);

        var av = Get(r, FakePc.SecurityVendor); // protected publisher
        Assert.Equal(ServicePolicy.SecurityPublisherReason, av.Assessment.NotOfferedReason);
        Assert.False(av.Assessment.CanChange);
    }

    [Fact]
    public void Flags_unsigned_suspicious_leftover_and_auto_start_not_running()
    {
        using var folder = new TestFolder("inv");
        var (r, _) = Scan(folder);
        var worker = Get(r, FakePc.TempWorker);
        Assert.True(worker.Flags.HasFlag(ServiceFlags.Unsigned));
        Assert.True(worker.Flags.HasFlag(ServiceFlags.SuspiciousPath));
        Assert.True(worker.IsFlagged);

        var gone = Get(r, FakePc.Leftover);
        Assert.True(gone.Flags.HasFlag(ServiceFlags.Leftover));
        Assert.False(gone.Assessment.CanChange); // shown only, nothing destructive offered
        Assert.Equal(ServicePolicy.LeftoverReason, gone.Assessment.NotOfferedReason);

        var stopped = Get(r, FakePc.ThirdPartyStopped);
        Assert.True(stopped.Flags.HasFlag(ServiceFlags.AutoStartNotRunning));
        Assert.False(Get(r, FakePc.ThirdPartyAuto).Flags.HasFlag(ServiceFlags.AutoStartNotRunning));

        Assert.False(Get(r, "DiagTrack").IsFlagged);
    }

    [Fact]
    public void Per_user_copies_are_shown_only_and_templates_use_the_template_entry()
    {
        using var folder = new TestFolder("inv");
        var (r, _) = Scan(folder);
        var copy = Get(r, FakePc.PerUserCopy);
        Assert.Equal(ServicePolicy.PerUserReason, copy.Assessment.NotOfferedReason);

        var template = ServiceCatalog.All.FirstOrDefault(e => e.IsTemplate);
        if (template is null) return; // nothing to check against yet
        var key = template.Name[..^2];
        var record = new ServiceRecord { Name = key, ServiceType = 0x60, Start = 3 };
        Assert.Same(template, ServiceInventory.CatalogEntry(record));
        Assert.Same(template, ServiceInventory.CatalogEntry(record with { Name = key + "_9f9f", ServiceType = 0xE0 }));
    }

    [Fact]
    public void Situational_entries_unlock_only_in_expert_mode()
    {
        var entry = ServiceCatalog.All.FirstOrDefault(e => e.Verdict == ServiceVerdict.Situational && !e.IsTemplate && e.Recommended is { } rec
            && !e.WindowsDefaults.Contains(rec) && !ServicePolicy.IsNeverTouch(e.Name));
        if (entry is null) return; // no situational entry with a change yet
        using var folder = new TestFolder("inv");
        var start = entry.WindowsDefaults.Contains(ServiceStart.Automatic) ? 2 : 3;
        FakeServiceWorld World(IKnownLocations l)
        {
            var dll = Path.Combine(l.WindowsDir, "System32", "situational.dll");
            return new FakeServiceWorld().Add(Svc.Shared(entry.Name, start, dll), FixedFileFacts.Microsoft(dll));
        }
        var (off, _) = Scan(folder, expert: false, world: World);
        var (on, _) = Scan(folder, expert: true, world: World);
        Assert.False(Get(off, entry.Name).Assessment.CanChange);
        Assert.StartsWith("Expert mode only.", Get(off, entry.Name).Assessment.NotOfferedReason);
        Assert.Contains(Get(on, entry.Name).Assessment.Options, o => o.Target == entry.Recommended && o.IsRecommended);
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\Contoso\\svc.exe\" -k", @"C:\Program Files\Contoso\svc.exe")]
    [InlineData(@"C:\Program Files\Contoso\svc.exe /service", @"C:\Program Files\Contoso\svc.exe")]
    [InlineData(@"%SystemRoot%\system32\svchost.exe -k netsvcs", @"<win>\system32\svchost.exe")]
    [InlineData(@"\SystemRoot\System32\drivers\x.sys", @"<win>\System32\drivers\x.sys")]
    [InlineData(@"System32\drivers\x.sys", @"<win>\System32\drivers\x.sys")]
    [InlineData(@"\??\C:\Tools\x.sys", @"C:\Tools\x.sys")]
    public void Service_command_lines_resolve_inside_the_sandbox(string imagePath, string expected)
    {
        using var folder = new TestFolder("paths");
        var l = new SandboxKnownLocations(Path.Combine(folder.Path, "pc"), SandboxProfile.Minimal);
        Assert.Equal(expected.Replace("<win>", l.WindowsDir), ServiceBinary.ProgramOf(imagePath, l, realPc: false));
    }

    [Fact]
    public void Scanning_is_cancellable()
    {
        using var folder = new TestFolder("inv");
        var l = new SandboxKnownLocations(Path.Combine(folder.Path, "pc"), SandboxProfile.Minimal);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => ServiceInventory.Read(FakePc.Build(l).Environment(), l, false, null, cts.Token));
    }

    [Fact]
    public void Every_row_has_a_plain_english_description_and_reasons()
    {
        using var folder = new TestFolder("inv");
        var (r, _) = Scan(folder);
        Assert.All(r.All, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.WhatItDoes), s.Name);
            Assert.True(s.Assessment.CanChange || !string.IsNullOrWhiteSpace(s.Assessment.NotOfferedReason), s.Name);
        });
    }

    [Fact]
    public void The_sandbox_practice_pc_reads_nothing_real()
    {
        using var folder = new TestFolder("sim");
        var l = new SandboxKnownLocations(Path.Combine(folder.Path, "pc"), SandboxProfile.Minimal);
        var env = ServicesEnvironment.ForSandbox(l);
        Assert.False(env.IsRealPc);
        var r = ServiceInventory.Read(env, l, false, null, CancellationToken.None);
        Assert.Contains(r.Notes, n => n.Contains("Practice PC"));
        Assert.Contains(r.Services, s => s.Name == "DiagTrack");
        Assert.All(r.All.Where(s => s.BinaryPath is not null), s => Assert.StartsWith(l.Root, s.BinaryPath!, StringComparison.OrdinalIgnoreCase));

        // A practice PC's own services.json wins over the sample.
        Directory.CreateDirectory(Path.Combine(l.Root, "Sim"));
        File.WriteAllText(Path.Combine(l.Root, "Sim", "services.json"),
            "{ \"services\": [ { \"name\": \"OnlyOne\", \"start\": 2, \"imagePath\": \"%ProgramFiles%\\\\One\\\\one.exe\" } ], " +
            "\"files\": [ { \"path\": \"%ProgramFiles%\\\\One\\\\one.exe\", \"company\": \"One Co\", \"signer\": \"One Co\" } ] }");
        var own = ServiceInventory.Read(ServicesEnvironment.ForSandbox(l), l, false, null, CancellationToken.None);
        var only = Assert.Single(own.Services);
        Assert.Equal("One Co", only.Facts.Publisher);
        Assert.Equal(new[] { ServiceStart.Manual }, only.Assessment.Options.Select(o => o.Target).ToArray());
    }

    [Fact]
    public void Health_summary_counts_match_the_inventory()
    {
        using var folder = new TestFolder("inv");
        var (r, _) = Scan(folder, world: l => FakePc.Build(l, rpcStart: 3));
        var lines = ServicesHealthSummary.Lines(r).ToDictionary(x => x.Label, x => x.Value);
        Assert.Equal("1", lines["Not signed"]);
        Assert.Equal("1", lines["Program file missing (left over)"]);
        Assert.Equal("1", lines["Running from a temporary, AppData or Downloads folder"]);
        Assert.Equal("1", lines["Windows services switched off that shouldn't be"]);
        Assert.Equal("1", lines["Drivers"]);
    }

    [Fact]
    public void Recovery_settings_survive_the_journal_round_trip()
    {
        var r = new Platform.ServiceRecoveryConfig(3600, "Restarting", @"C:\x.cmd", new[] { new Platform.ServiceRecoveryAction(1, 5000), new Platform.ServiceRecoveryAction(3, 0) }, true);
        Assert.True(r.SameAs(Platform.ServiceRecoveryConfig.TryParse(r.Serialize())));
        Assert.False(r.SameAs(r with { ResetPeriodSeconds = 1 }));
        Assert.Null(Platform.ServiceRecoveryConfig.TryParse("{not json"));
        Assert.Null(Platform.ServiceRecoveryConfig.TryParse("{\"ResetPeriodSeconds\":1,\"Actions\":[{\"Type\":9,\"DelayMs\":0}],\"FailureActionsOnNonCrash\":false}"));
    }
}
