using PcTidy.Core;
using PcTidy.Core.Model;
using Xunit;

namespace PcTidy.Safety.Tests;

/// <summary>Regression tests for the v2 safety review (guard and catalog parts).</summary>
public sealed class V2ReviewFixTests : IDisposable
{
    private readonly TestSandbox _s = new();
    public void Dispose() => _s.Dispose();

    private static PlanItem Tool(string toolId, string feature, Dictionary<string, string> args, bool reversible = false) => new()
    {
        Kind = ActionKind.RunSystemTool, FeatureId = feature, Description = "t", ToolId = toolId, Args = args,
        NeedsAdmin = true, IsSystemChange = true, Reversible = reversible,
    };

    [Fact]
    public void Drivers_are_only_added_from_pc_tidys_own_backups()
    {
        var downloads = _s.Dir(Path.Combine(_s.K.Downloads, "drivers"));
        Assert.False(_s.Guard.Check(Tool(ToolIds.PnpAddDrivers, "drivers", new() { ["folder"] = downloads })).Allowed);
    }

    [Fact]
    public void Compact_checks_its_folder_like_a_destination()
    {
        PlanItem Compact(Dictionary<string, string> args) => Tool(ToolIds.CompactLzx, "insights", args, reversible: true) with { NeedsAdmin = false, IsSystemChange = false };
        var ok = _s.Dir(Path.Combine(_s.K.Downloads, "Old projects"));
        Assert.True(_s.Guard.Check(Compact(new() { ["folder"] = ok })).Allowed);
        Assert.False(_s.Guard.Check(Compact(new() { ["folder"] = ok }) with { NeedsAdmin = true }).Allowed); // never with admin rights
        var system = Path.Combine(_s.K.WindowsDir, "System32");
        Assert.False(_s.Guard.Check(Compact(new() { ["folder"] = system })).Allowed);
        Assert.False(_s.Guard.Check(Compact(new())).Allowed);
        if (_s.K.CloudRoots.Count > 0)
        {
            var cloud = _s.Dir(Path.Combine(_s.K.CloudRoots[0], "Old"));
            Assert.False(_s.Guard.Check(Compact(new() { ["folder"] = cloud })).Allowed);
        }
    }

    [Fact]
    public void Shrinking_restore_point_space_must_be_marked_permanent()
    {
        Assert.False(_s.Guard.Check(Tool(ToolIds.ShadowStorageResize, "pccare", new() { ["percent"] = "8", ["drive"] = "C:\\" }, reversible: true)).Allowed);
        Assert.True(_s.Guard.Check(Tool(ToolIds.ShadowStorageResize, "pccare", new() { ["percent"] = "8", ["drive"] = "C:\\" })).Allowed);
    }

    [Theory]
    [InlineData("Microsoft-Windows-Subsystem-Linux", false)]   // not in the catalog: never removed
    [InlineData("Microsoft-Hyper-V-All", false)]
    [InlineData("Windows-Defender-Default-Definitions", false)]
    public void Optional_features_outside_the_catalog_are_refused(string name, bool allowed)
    {
        var item = new PlanItem
        {
            Kind = ActionKind.OptionalFeatureRemove, FeatureId = "pccare", Description = "t",
            Args = new() { ["name"] = name, ["kind"] = "feature" }, NeedsAdmin = true, IsSystemChange = true, Reversible = true,
        };
        Assert.Equal(allowed, _s.Guard.Check(item).Allowed);
    }

    [Fact]
    public void Every_catalog_optional_feature_is_allowed()
    {
        foreach (var part in PcTidy.Catalog.PcCare.OptionalFeatureCatalog.All.Where(p => !p.Name.Contains('*')))
        {
            var item = new PlanItem
            {
                Kind = ActionKind.OptionalFeatureRemove, FeatureId = "pccare", Description = "t",
                Args = new() { ["name"] = part.Name, ["kind"] = part.Kind.ToString().ToLowerInvariant() }, NeedsAdmin = true, IsSystemChange = true, Reversible = true,
            };
            var v = _s.Guard.Check(item);
            // Kind names other than feature/capability are refused as incomplete; only check the name rule here.
            if (item.Args["kind"] is "feature" or "capability") Assert.True(v.Allowed, part.Name + ": " + v.Reason);
        }
    }

    [Fact]
    public void Token_string_allowance_uses_a_strict_pattern()
    {
        var a = PcTidy.Catalog.RegistryCatalog.All.FirstOrDefault(x => x.ValueName == "DirectXUserGlobalSettings");
        if (a is null) return;
        Assert.NotNull(a.DataPattern);
        Assert.Matches(a.DataPattern!, "SwapEffectUpgradeEnable=1;");
        Assert.DoesNotMatch(a.DataPattern!, "SwapEffectUpgradeEnable=1;\" & calc.exe");
        Assert.False(a.AllowDelete);
    }

    private static PlanItem Service(string feature, string name, ServiceStart start) => new()
    {
        Kind = ActionKind.ServiceSetStartType, FeatureId = feature, Description = "t", ServiceName = name, ServiceStartType = start,
        NeedsAdmin = true, IsSystemChange = true, Reversible = true,
    };

    [Fact]
    public void Services_section_only_sets_what_the_catalog_allows()
    {
        Assert.True(_s.Guard.Check(Service("services", "DiagTrack", ServiceStart.Disabled)).Allowed);     // Proven, recommended
        Assert.True(_s.Guard.Check(Service("services", "DiagTrack", ServiceStart.Automatic)).Allowed);    // back to the Windows default
        Assert.False(_s.Guard.Check(Service("services", "RpcSs", ServiceStart.Manual)).Allowed);          // protected
        Assert.True(_s.Guard.Check(Service("services", "SomeVendorUpdater", ServiceStart.Manual)).Allowed); // unknown third-party: Manual only
        Assert.False(_s.Guard.Check(Service("services", "SomeVendorUpdater", ServiceStart.Disabled)).Allowed);
        Assert.False(_s.Guard.Check(Service("services", "vgc", ServiceStart.Manual)).Allowed);            // anti-cheat
        var notReversible = Service("services", "DiagTrack", ServiceStart.Disabled) with { Reversible = false };
        Assert.False(_s.Guard.Check(notReversible).Allowed);
        // Other sections never switch a service off.
        Assert.False(_s.Guard.Check(Service("speed", "DiagTrack", ServiceStart.Disabled)).Allowed);
        Assert.False(_s.Guard.Check(Service("tweaks", "SomeVendorUpdater", ServiceStart.Disabled)).Allowed);
    }

    [Theory]
    [InlineData("MDCoreSvc")]
    [InlineData("webthreatdefsvc")]
    [InlineData("Appinfo")]
    [InlineData("DoSvc")]
    public void Other_sections_cannot_weaken_protected_services(string name)
    {
        Assert.False(_s.Guard.Check(Service("speed", name, ServiceStart.Manual)).Allowed);
        Assert.False(_s.Guard.Check(Service("tweaks", name, ServiceStart.Manual)).Allowed);
    }

    [Fact]
    public void Catalog_protected_services_only_go_back_to_their_default_by_repair()
    {
        var p = PcTidy.Catalog.Services.ServiceCatalog.All.FirstOrDefault(e => e.Verdict == PcTidy.Catalog.Services.ServiceVerdict.Protected
            && !e.IsTemplate && e.WindowsDefaults.Contains(ServiceStart.Automatic));
        if (p is null) return;
        Assert.False(_s.Guard.Check(Service("speed", p.Name, ServiceStart.Manual)).Allowed);
        Assert.False(_s.Guard.Check(Service("speed", p.Name, ServiceStart.Automatic)).Allowed);
        Assert.True(_s.Guard.Check(Service("services.repair", p.Name, ServiceStart.Automatic)).Allowed);
    }

    [Theory]
    [InlineData(".claude.json")]
    [InlineData(".claude.json.backup")]
    public void Claude_main_config_file_is_never_touched(string name)
    {
        var file = _s.File(Path.Combine(_s.K.UserProfile, name));
        var item = new PlanItem { Kind = ActionKind.QuarantinePath, FeatureId = "spacemap", Description = "t", SourcePath = file, ExpectedSize = new FileInfo(file).Length };
        Assert.False(_s.Guard.Check(item).Allowed);
    }

    [Fact]
    public void Per_user_template_keys_match_their_catalog_entry()
    {
        var template = PcTidy.Catalog.Services.ServiceCatalog.All.FirstOrDefault(e => e.IsTemplate);
        if (template is null) return;
        var key = template.Name[..^2];
        Assert.Same(template, PcTidy.Catalog.Services.ServiceCatalog.Find(key));
        Assert.Same(template, PcTidy.Catalog.Services.ServiceCatalog.Find(key + "_4f2a1"));
        Assert.Null(PcTidy.Catalog.Services.ServiceCatalog.Find(key + "Extra"));
    }

    [Fact]
    public void Services_repair_only_restores_windows_defaults()
    {
        Assert.True(_s.Guard.Check(Service("services.repair", "wuauserv", ServiceStart.Manual)).Allowed);
        Assert.False(_s.Guard.Check(Service("services.repair", "wuauserv", ServiceStart.Disabled)).Allowed);
        Assert.False(_s.Guard.Check(Service("services.repair", "mpssvc", ServiceStart.Manual)).Allowed);   // default is Automatic
        Assert.True(_s.Guard.Check(Service("services.repair", "mpssvc", ServiceStart.Automatic)).Allowed);
        // Not in the guard's own table: the catalog's Windows default (Manual) is accepted, Disabled never.
        if (PcTidy.Catalog.Services.ServiceCatalog.Find("TabletInputService") is { } tablet && tablet.WindowsDefaults.Contains(ServiceStart.Manual))
        {
            Assert.True(_s.Guard.Check(Service("services.repair", "TabletInputService", ServiceStart.Manual)).Allowed);
            Assert.False(_s.Guard.Check(Service("services.repair", "TabletInputService", ServiceStart.Disabled)).Allowed);
        }
    }

    [Fact]
    public void Service_catalog_never_offers_protected_harmful_or_placebo_and_profiles_are_proven_only()
    {
        foreach (var e in PcTidy.Catalog.Services.ServiceCatalog.All)
        {
            if (e.Verdict is PcTidy.Catalog.Services.ServiceVerdict.Protected or PcTidy.Catalog.Services.ServiceVerdict.Harmful or PcTidy.Catalog.Services.ServiceVerdict.Placebo)
                Assert.True(e.Recommended is null, e.Name);
            if (e.Profiles.Count > 0) Assert.Equal(PcTidy.Catalog.Services.ServiceVerdict.Proven, e.Verdict);
            Assert.NotEmpty(e.WindowsDefaults);
            Assert.DoesNotContain(ServiceStart.Disabled, e.WindowsDefaults.Where(_ => e.Verdict is PcTidy.Catalog.Services.ServiceVerdict.Protected));
        }
        Assert.Equal(PcTidy.Catalog.Services.ServiceCatalog.All.Count,
            PcTidy.Catalog.Services.ServiceCatalog.All.Select(e => e.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        // Anti-cheat services are protected.
        foreach (var ac in new[] { "vgc", "BEService", "EasyAntiCheat" })
            if (PcTidy.Catalog.Services.ServiceCatalog.Find(ac) is { } e) Assert.Equal(PcTidy.Catalog.Services.ServiceVerdict.Protected, e.Verdict);
    }

    [Fact]
    public void Set_only_tweak_values_may_not_be_deleted()
    {
        var setOnly = PcTidy.Catalog.RegistryCatalog.All
            .Where(x => x.Source.StartsWith("tweaks.", StringComparison.Ordinal) && x.AllowedData.Count > 0)
            .ToList();
        Assert.Contains(setOnly, x => !x.AllowDelete);
    }
}
