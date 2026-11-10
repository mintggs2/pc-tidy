using PcTidy.Features.Services.Model;
using PcTidy.Features.Services.Rules;
using PcTidy.Features.Services.Scanning;
using PcTidy.Features.Services.Sources;
using PcTidy.UI.Flow;

namespace PcTidy.Features.Services.Tests;

/// <summary>Profiles, the bad-services repair and the REAL SafetyGuard over everything the scanners emit.</summary>
public class ProfileRepairAndGuardTests
{
    [Fact]
    public void Profiles_contain_only_proven_entries_that_list_them()
    {
        foreach (var p in ServiceProfileBuilder.All)
            Assert.All(ServiceProfileBuilder.CatalogMembers(p.Id), e =>
            {
                Assert.Equal(ServiceVerdict.Proven, e.Verdict);
                Assert.Contains(p.Id, e.Profiles);
            });
        Assert.Contains(ServiceProfileBuilder.CatalogMembers(ServiceProfiles.Privacy), e => e.Name == "DiagTrack");
    }

    [Fact]
    public async Task Privacy_profile_ticks_DiagTrack_Disabled_only_under_the_privacy_preset()
    {
        using var folder = new TestFolder("profile");
        using var host = new FakeServicesHost(Path.Combine(folder.Path, "pc"), l => FakePc.Build(l));
        var world = host.World;
        var scanner = new ServicesScanner(_ => world.Environment());

        var privacy = await scanner.ScanAsync(host.Context(OptimizePresets.Privacy), null, CancellationToken.None);
        var group = privacy.Groups.Single(g => g.Id == ServicesIds.PrivacyGroup);
        var diag = group.Items.Single(i => i.Actions.Any(a => a.ServiceName == "DiagTrack"));
        Assert.Equal(ServiceStart.Disabled, diag.Actions.Single().ServiceStartType);
        Assert.True(OptimizePresets.IsTickableByDefault(diag) == (diag.Badge == Badge.Safe));

        var gamer = await scanner.ScanAsync(host.Context(OptimizePresets.Gamer), null, CancellationToken.None);
        Assert.All(gamer.Groups.Single(g => g.Id == ServicesIds.PrivacyGroup).Items, i => Assert.False(i.DefaultSelected));

        // Each service appears once, so the numbers shown match the plan.
        var names = privacy.AllItems.SelectMany(i => i.Actions).Select(a => a.ServiceName).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        // Third-party services are left to Speed's startup list in Optimize-all.
        Assert.DoesNotContain(privacy.Groups, g => g.Id == ServicesScanner.ThirdPartyGroup);
        var standalone = await scanner.ScanAsync(host.Context(), null, CancellationToken.None);
        Assert.Contains(standalone.Groups.Single(g => g.Id == ServicesScanner.ThirdPartyGroup).Items, i => i.Actions[0].ServiceName == FakePc.ThirdPartyAuto);
    }

    [Fact]
    public async Task Empty_profiles_say_there_is_nothing_to_do()
    {
        using var folder = new TestFolder("profile");
        using var host = new FakeServicesHost(Path.Combine(folder.Path, "pc"), l => FakePc.Build(l));
        var world = host.World;
        var result = await new ServicesScanner(_ => world.Environment()).ScanAsync(host.Context(OptimizePresets.Gamer), null, CancellationToken.None);
        foreach (var p in ServiceProfileBuilder.All.Where(p => !ServiceProfileBuilder.CatalogMembers(p.Id).Any()))
        {
            var state = ServiceProfileBuilder.State(p, Array.Empty<ServiceInfo>(), host.Sandbox.System);
            if (!state.AppliesHere) continue;
            Assert.Equal("No proven service changes; nothing to do.", state.Status);
            var g = result.Groups.Single(x => x.Id == ServicesIds.GroupFor(p.Id));
            var info = Assert.Single(g.Items);
            Assert.Empty(info.Actions);
            Assert.NotNull(info.DisabledReason);
        }
    }

    [Fact]
    public void Laptop_battery_profile_only_applies_with_a_battery()
    {
        Assert.True(ServiceProfileBuilder.AppliesTo(ServiceProfiles.LaptopBattery, null));
        using var folder = new TestFolder("profile");
        var desktop = new SandboxSystemInfo(new SandboxKnownLocations(Path.Combine(folder.Path, "pc"), SandboxProfile.Minimal));
        Assert.False(ServiceProfileBuilder.AppliesTo(ServiceProfiles.LaptopBattery, desktop));
        var simDir = Path.Combine(folder.Path, "laptop", "Sim");
        Directory.CreateDirectory(simDir);
        File.WriteAllText(Path.Combine(simDir, "sim.json"), "{ \"isLaptop\": true }");
        var laptop = new SandboxSystemInfo(new SandboxKnownLocations(Path.Combine(folder.Path, "laptop"), SandboxProfile.Minimal));
        Assert.True(ServiceProfileBuilder.AppliesTo(ServiceProfiles.LaptopBattery, laptop));
    }

    [Fact]
    public void Repair_finds_protected_services_set_weaker_than_the_windows_default()
    {
        using var folder = new TestFolder("repair");
        var l = new SandboxKnownLocations(Path.Combine(folder.Path, "pc"), SandboxProfile.Minimal);
        var weak = ServiceInventory.Read(FakePc.Build(l, rpcStart: 3).Environment(), l, false, null, CancellationToken.None);
        var repair = Assert.Single(ServiceRepairScanner.Find(weak.Services));
        Assert.Equal("RpcSs", repair.Service.Name);
        Assert.Equal(ServiceStart.Manual, repair.Current);
        Assert.Equal(ServiceStart.Automatic, repair.Target);
        Assert.Contains("Windows ships it as Automatic", repair.Why);

        var healthy = ServiceInventory.Read(FakePc.Build(l).Environment(), l, false, null, CancellationToken.None);
        Assert.Empty(ServiceRepairScanner.Find(healthy.Services));
    }

    [Fact]
    public void Repair_uses_the_guards_own_defaults_for_services_the_catalog_doesnt_list()
    {
        var name = Safety.Rules.ServiceDefaults.KnownServices.FirstOrDefault(n => ServiceCatalog.Find(n) is null);
        if (name is null) return; // every guard-listed service is in the catalog now
        using var folder = new TestFolder("repair");
        var l = new SandboxKnownLocations(Path.Combine(folder.Path, "pc"), SandboxProfile.Minimal);
        var dll = Path.Combine(l.WindowsDir, "System32", "guarded.dll");
        var world = new FakeServiceWorld().Add(Svc.Shared(name, 4, dll, ServiceRunState.Stopped), FixedFileFacts.Microsoft(dll));
        var inv = ServiceInventory.Read(world.Environment(), l, false, null, CancellationToken.None);
        var repair = Assert.Single(ServiceRepairScanner.Find(inv.Services));
        Assert.True(Safety.Rules.ServiceDefaults.IsDefaultFor(name, repair.Target));
    }

    [Fact]
    public async Task Everything_the_scanners_emit_passes_the_real_SafetyGuard()
    {
        using var folder = new TestFolder("guard");
        using var host = new FakeServicesHost(Path.Combine(folder.Path, "pc"), l => FakePc.Build(l, rpcStart: 3), expert: true);
        var world = host.World;
        var module = new ServicesModule(_ => world.Environment());
        foreach (var scanner in module.Scanners)
        {
            var result = await scanner.ScanAsync(host.Context(), null, CancellationToken.None);
            foreach (var item in result.AllItems.Where(i => i.DisabledReason is null))
            {
                Assert.False(string.IsNullOrWhiteSpace(item.Reason), item.Id);
                Assert.True(!item.DefaultSelected || item.Badge == Badge.Safe, item.Id);
                foreach (var a in item.Actions)
                {
                    Assert.True(a.NeedsAdmin && a.IsSystemChange && a.Reversible, a.Description);
                    Assert.Equal(scanner.FeatureId, a.FeatureId);
                    var verdict = host.Safety.Check(a);
                    Assert.True(verdict.Allowed, $"{a.Description} → {verdict.Reason}");
                }
            }
        }
        // And the page's per-service options too (every option of every service, not just the scanner's pick).
        var inv = ServiceInventory.Read(world.Environment(), host.Sandbox.Locations, true, null, CancellationToken.None);
        foreach (var s in inv.Services)
            foreach (var o in s.Assessment.Options)
            {
                var item = ServicePlanItems.SetStart(ServicesIds.Module, s, o.Target);
                Assert.True(host.Safety.Check(item).Allowed, $"{item.Description} → {host.Safety.Check(item).Reason}");
            }
    }

    [Theory]
    [InlineData("DiagTrack", ServiceStart.Disabled, "services", true)]         // Proven → its recommendation
    [InlineData("DiagTrack", ServiceStart.Automatic, "services", true)]        // back to the Windows default
    [InlineData("ContosoUnknownSvc", ServiceStart.Manual, "services", true)]   // unknown → Manual only
    [InlineData("ContosoUnknownSvc", ServiceStart.Disabled, "services", false)]
    [InlineData("RpcSs", ServiceStart.Manual, "services", false)]              // protected: never
    [InlineData("RpcSs", ServiceStart.Automatic, "services.repair", true)]     // repair: exact default
    [InlineData("RpcSs", ServiceStart.Manual, "services.repair", false)]
    [InlineData("RpcSs", ServiceStart.Disabled, "services.repair", false)]
    [InlineData("EasyAntiCheat_PcTidyTest", ServiceStart.Manual, "services", false)]
    public void Guard_rules_for_the_section(string name, ServiceStart target, string feature, bool allowed)
    {
        using var folder = new TestFolder("guard");
        using var host = new FakeServicesHost(Path.Combine(folder.Path, "pc"), new FakeServiceWorld());
        var item = new PlanItem
        {
            Kind = ActionKind.ServiceSetStartType, FeatureId = feature, Description = "test", ServiceName = name, ServiceStartType = target,
            NeedsAdmin = true, IsSystemChange = true, Reversible = true,
        };
        Assert.Equal(allowed, host.Safety.Check(item).Allowed);
        // Items missing NeedsAdmin / IsSystemChange / Reversible are refused for the section.
        if (feature.StartsWith("services")) Assert.False(host.Safety.Check(item with { IsSystemChange = false }).Allowed);
    }
}
