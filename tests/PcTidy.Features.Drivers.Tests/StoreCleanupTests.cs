using PcTidy.Core.Model;
using PcTidy.Features.Drivers.Analysis;
using PcTidy.Features.Drivers.Scanning;

namespace PcTidy.Features.Drivers.Tests;

/// <summary>The RAPR rule exactly as researched: older + unused + newer sibling, oem only, important classes held back, 14-day rule.</summary>
public class StoreCleanupTests
{
    private static async Task<IReadOnlyDictionary<string, StoreVerdict>> VerdictsAsync()
    {
        var inv = await Fixtures.InventoryAsync();
        return StoreCleanupRules.Evaluate(inv.Packages, inv.Devices.Concat(inv.Disconnected).Concat(inv.ProblemDevices))
            .ToDictionary(d => d.Package.PublishedName, d => d.Verdict, StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("oem289.inf", StoreVerdict.Old)]            // older camera package, unused, newer siblings exist
    [InlineData("oem126.inf", StoreVerdict.InUse)]          // used by a present device (only per the device list)
    [InlineData("oem311.inf", StoreVerdict.Newest)]         // newest is never removed, even when unused
    [InlineData("oem198.inf", StoreVerdict.OnlyOne)]        // the only package of its kind
    [InlineData("oem327.inf", StoreVerdict.OnlyOne)]        // same INF name, different provider: a different family
    [InlineData("oem231.inf", StoreVerdict.InUse)]          // used by a disconnected device
    [InlineData("oem245.inf", StoreVerdict.InUse)]          // used by a device that has a problem
    [InlineData("oem152.inf", StoreVerdict.InUse)]
    [InlineData("oem160.inf", StoreVerdict.InUse)]
    [InlineData("oem219.inf", StoreVerdict.InUse)]
    [InlineData("oem139.inf", StoreVerdict.SameVersion)]    // identical twins: neither is older
    [InlineData("oem206.inf", StoreVerdict.SameVersion)]
    [InlineData("oem103.inf", StoreVerdict.UnknownVersion)] // can't prove it's older
    [InlineData("oem258.inf", StoreVerdict.Newest)]         // its only sibling has an unreadable version
    [InlineData("oem364.inf", StoreVerdict.OldImportant)]   // storage (SCSIAdapter) class
    [InlineData("oem187.inf", StoreVerdict.InUse)]
    [InlineData("oem145.inf", StoreVerdict.OldImportant)]   // Keyboard (input) class
    [InlineData("oem273.inf", StoreVerdict.InUse)]
    [InlineData("oem117.inf", StoreVerdict.OldImportant)]   // Display class
    [InlineData("oem342.inf", StoreVerdict.InUse)]
    [InlineData("litcamstudio.inf", StoreVerdict.NotThirdParty)]
    public async Task Each_package_gets_the_researched_verdict(string inf, StoreVerdict expected)
        => Assert.Equal(expected, (await VerdictsAsync())[inf]);

    [Fact]
    public async Task Never_the_only_newest_or_in_use_package_and_only_oem_infs()
    {
        var inv = await Fixtures.InventoryAsync();
        var decisions = StoreCleanupRules.Evaluate(inv.Packages, inv.Devices.Concat(inv.Disconnected));
        var inUse = StoreCleanupRules.InUseInfs(inv.Packages, inv.Devices.Concat(inv.Disconnected));
        foreach (var old in decisions.Where(d => d.IsOld))
        {
            Assert.Matches(@"^oem\d+\.inf$", old.Package.PublishedName);
            Assert.DoesNotContain(old.Package.PublishedName, inUse);
            Assert.Empty(old.Package.Devices);
            Assert.NotNull(old.NewerPackage);
            Assert.True(StoreCleanupRules.Compare(old.NewerPackage!.Version, old.Package.Version) > 0);
            var family = inv.Packages.Where(p => StoreCleanupRules.GroupKey(p) == StoreCleanupRules.GroupKey(old.Package)).ToList();
            Assert.True(family.Count > 1, "never the only one");
            Assert.Contains(family, p => !ReferenceEquals(p, old.Package) && StoreCleanupRules.Compare(p.Version, old.Package.Version) > 0);
        }
    }

    [Fact]
    public async Task Old_packages_are_offered_with_backup_first_and_never_force()
    {
        var report = await Fixtures.ReportAsync();
        var item = Assert.Single(report.Result.Group(DriverTabs.Store, "old")!.Items);

        Assert.Contains("litcamstudio.inf", item.Title);
        Assert.Equal(Badge.Check, item.Badge);
        Assert.False(item.DefaultSelected);
        Assert.Null(item.DisabledReason);
        Assert.Equal(38797312, item.SizeBytes);
        Assert.Collection(item.Actions,
            export => { Assert.Equal(ToolIds.PnpExportDrivers, export.ToolId); Assert.StartsWith(Path.Combine(Fixtures.DataRoot, "backups", "drivers"), export.Args!["folder"]); },
            delete =>
            {
                Assert.Equal(ToolIds.PnpDeleteDriver, delete.ToolId);
                Assert.Equal("oem289.inf", delete.Args!["inf"]);
                Assert.Single(delete.Args!);
                Assert.False(delete.Reversible);
                Assert.True(delete.NeedsAdmin);
                Assert.True(delete.IsSystemChange);
                Assert.Equal(item.SizeBytes, delete.ExpectedSize);
            });
    }

    [Fact]
    public async Task Important_classes_are_shown_but_not_pickable_unless_asked()
    {
        var off = (await Fixtures.ReportAsync()).Result.Group(DriverTabs.Store, "important")!;
        Assert.Equal(new[] { "oem117.inf", "oem145.inf", "oem364.inf" }.OrderBy(x => x),
            off.Items.Select(i => i.Actions.Last().Args!["inf"]).OrderBy(x => x));
        Assert.All(off.Items, i => Assert.NotNull(i.DisabledReason));

        var on = (await Fixtures.ReportAsync(options: new DriverScanOptions { IncludeImportantClasses = true })).Result.Group(DriverTabs.Store, "important")!;
        Assert.All(on.Items, i => { Assert.Null(i.DisabledReason); Assert.False(i.DefaultSelected); Assert.Equal(Badge.Check, i.Badge); });
    }

    [Fact]
    public async Task Nothing_is_offered_within_14_days_of_a_driver_change()
    {
        var report = await Fixtures.ReportAsync(tweak: inv => inv with { LastDriverChangeUtc = FixedClock.ScenarioNowUtc.AddDays(-3) });
        var items = report.Result.Groups.Where(g => DriverTabs.Of(g.Id) == DriverTabs.Store).SelectMany(g => g.Items).ToList();

        Assert.NotEmpty(items);
        Assert.All(items, i => Assert.Contains("Wait until", i.DisabledReason));
        Assert.Contains("Wait until", report.TabNotes[DriverTabs.Store]);
    }

    [Fact]
    public async Task Cleanup_is_offered_again_after_14_days()
    {
        var report = await Fixtures.ReportAsync(tweak: inv => inv with { LastDriverChangeUtc = FixedClock.ScenarioNowUtc.AddDays(-15) });
        Assert.Null(Assert.Single(report.Result.Group(DriverTabs.Store, "old")!.Items).DisabledReason);
    }

    [Fact]
    public async Task Unknown_last_change_time_holds_cleanup_back()
    {
        var report = await Fixtures.ReportAsync(tweak: inv => inv with { LastDriverChangeUtc = null });
        Assert.All(report.Result.Group(DriverTabs.Store, "old")!.Items, i => Assert.NotNull(i.DisabledReason));
    }

    [Fact]
    public void Quiet_period_boundaries()
    {
        var now = FixedClock.ScenarioNowUtc;
        Assert.True(StoreCleanupRules.IsInQuietPeriod(now.AddDays(-13.9), now, out _));
        Assert.False(StoreCleanupRules.IsInQuietPeriod(now.AddDays(-14), now, out var from));
        Assert.Equal(now, from);
        Assert.True(StoreCleanupRules.IsInQuietPeriod(null, now, out var unknown));
        Assert.Null(unknown);
    }

    [Fact]
    public async Task Only_candidate_packages_are_measured()
    {
        var inv = await Fixtures.InventoryAsync();
        // machine.json also has a size for oem342.inf, which is in use: it is never asked for.
        Assert.Equal(new[] { "oem117.inf", "oem145.inf", "oem289.inf", "oem364.inf" }, inv.PackageSizes.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }
}
