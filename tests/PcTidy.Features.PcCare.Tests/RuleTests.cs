using PcTidy.Catalog.PcCare;
using PcTidy.Features.PcCare.Analysis;
using PcTidy.Features.PcCare.Model;

namespace PcTidy.Features.PcCare.Tests;

public class AntivirusAnalyzerTests
{
    private static SecurityFacts Facts(DefenderStatus? d = null, DefenderPolicy? p = null, params AntivirusProduct[] products)
        => new(d, p ?? DefenderPolicy.None, products, SecurityCenterAvailable: true, Array.Empty<DriverService>());

    private static readonly AntivirusProduct DefenderOn = new("Windows Defender", 0x061100, @"%ProgramFiles%\Windows Defender\MsMpeng.exe");
    private static readonly AntivirusProduct DefenderSnoozed = new("Windows Defender", 401664, @"%ProgramFiles%\Windows Defender\MsMpeng.exe");
    private static readonly AntivirusProduct OtherOn = new("Other Antivirus", 0x041000, @"C:\Program Files\Other\report.exe");

    [Fact]
    public void Defender_off_by_policy_with_nothing_else_is_detected_and_no_scan_is_offered()
    {
        var v = AntivirusAnalyzer.Analyze(Facts(null, new DefenderPolicy(true, true, false, true), DefenderSnoozed));
        Assert.Equal(AvSituation.DefenderOffByPolicy, v.Situation);
        Assert.True(v.PolicyTurnsDefenderOff);
        Assert.False(v.CanQuickScan);
    }

    [Fact]
    public void Another_antivirus_is_respected_even_when_a_policy_turned_Defender_off()
    {
        var v = AntivirusAnalyzer.Analyze(Facts(null, new DefenderPolicy(true, false, false, false), DefenderSnoozed, OtherOn));
        Assert.Equal(AvSituation.ThirdPartyOn, v.Situation);
        Assert.Equal(new[] { "Other Antivirus" }, v.ActiveThirdParty);
        Assert.True(v.PolicyTurnsDefenderOff);
        Assert.False(v.CanQuickScan);
    }

    [Fact]
    public void Defender_on_allows_a_quick_scan()
    {
        var v = AntivirusAnalyzer.Analyze(Facts(new DefenderStatus { AntivirusEnabled = true, RealTimeProtectionEnabled = true, RunningMode = "Normal" }, null, DefenderOn));
        Assert.Equal(AvSituation.DefenderOn, v.Situation);
        Assert.True(v.CanQuickScan);
    }

    [Fact]
    public void Defender_in_passive_mode_is_not_in_charge()
        => Assert.NotEqual(AvSituation.DefenderOn,
            AntivirusAnalyzer.Analyze(Facts(new DefenderStatus { AntivirusEnabled = true, RealTimeProtectionEnabled = true, RunningMode = "Passive" })).Situation);

    [Fact]
    public void Nothing_on_and_no_policy_is_no_active_antivirus()
        => Assert.Equal(AvSituation.NoActiveAntivirus,
            AntivirusAnalyzer.Analyze(Facts(new DefenderStatus { AntivirusEnabled = false, RealTimeProtectionEnabled = false }, null, DefenderSnoozed)).Situation);

    [Fact]
    public void No_information_at_all_is_unknown()
        => Assert.Equal(AvSituation.Unknown,
            AntivirusAnalyzer.Analyze(new SecurityFacts(null, DefenderPolicy.None, Array.Empty<AntivirusProduct>(), false, Array.Empty<DriverService>())).Situation);
}

public class LeftoverRuleTests
{
    private static readonly Guid Card = Scenario.WifiCard;

    [Fact]
    public void The_connected_network_is_never_offered()
    {
        Assert.False(LeftoverRules.ForWifi(new WlanProfile("Home", Card, "Wi-Fi", true, FixedClock.Now)).Offered);
        Assert.True(LeftoverRules.ForWifi(new WlanProfile("Hotel", Card, "Wi-Fi", false, FixedClock.Now.AddDays(-400))).Offered);
    }

    [Fact]
    public void Old_means_unused_for_six_months_or_unknown()
    {
        Assert.True(LeftoverRules.IsOld(new WlanProfile("a", Card, "", false, null), FixedClock.Now));
        Assert.True(LeftoverRules.IsOld(new WlanProfile("a", Card, "", false, FixedClock.Now.AddDays(-200)), FixedClock.Now));
        Assert.False(LeftoverRules.IsOld(new WlanProfile("a", Card, "", false, FixedClock.Now.AddDays(-10)), FixedClock.Now));
    }

    private static UserProfileFacts P(string sid, bool loaded = false, bool special = false, bool exists = false, bool thisPc = true)
        => new(sid, @"C:\Users\x", loaded, special, null, exists, thisPc);

    [Fact]
    public void Only_a_local_profile_whose_account_is_gone_is_offered()
        => Assert.True(LeftoverRules.ForUserProfile(P(Scenario.GoneSid), Scenario.CurrentSid).Offered);

    [Theory]
    [InlineData(true, false, false, true)]   // signed in (loaded)
    [InlineData(false, true, false, true)]   // special
    [InlineData(false, false, true, true)]   // account still exists
    [InlineData(false, false, false, false)] // work/school account PC Tidy can't check
    public void Loaded_special_existing_and_domain_profiles_are_never_offered(bool loaded, bool special, bool exists, bool thisPc)
    {
        var e = LeftoverRules.ForUserProfile(P(Scenario.GoneSid, loaded, special, exists, thisPc), Scenario.CurrentSid);
        Assert.False(e.Offered);
        Assert.False(string.IsNullOrWhiteSpace(e.DisabledReason));
    }

    [Fact]
    public void Your_own_profile_is_never_offered_even_if_it_looks_like_a_leftover()
        => Assert.False(LeftoverRules.ForUserProfile(P(Scenario.CurrentSid), Scenario.CurrentSid).Offered);

    [Fact]
    public void Without_knowing_who_you_are_nothing_is_offered()
        => Assert.False(LeftoverRules.ForUserProfile(P(Scenario.GoneSid), null).Offered);

    [Theory]
    [InlineData("S-1-5-18")]
    [InlineData("S-1-5-80-1234")]
    [InlineData("not a sid")]
    public void System_and_odd_sids_are_never_offered(string sid)
        => Assert.False(LeftoverRules.ForUserProfile(P(sid), Scenario.CurrentSid).Offered);
}

public class BrowserAuditTests
{
    [Fact]
    public void Hijacked_search_home_and_startup_pages_are_found_and_known_engines_are_not()
    {
        var findings = BrowserAudit.Settings(Scenario.OwnerLike().Browsers!);
        Assert.Contains(findings, f => f.Area == HijackArea.SearchEngine && f.Value.Contains("search.searchfun.example"));
        Assert.Contains(findings, f => f.Area == HijackArea.HomePage && f.Value == "start.searchfun.example");
        var startup = Assert.Single(findings, f => f.Area == HijackArea.StartupPages);
        Assert.Equal("deals.example-ads.net", startup.Value); // google.com is fine
        Assert.DoesNotContain(findings, f => f.Profile.Browser == "Edge"); // {bing:baseURL} template + new tab page
    }

    [Theory]
    [InlineData("https://www.google.de/search?q={searchTerms}", true)]
    [InlineData("https://www.google.co.uk/", true)]
    [InlineData("https://duckduckgo.com/?q={searchTerms}", true)]
    [InlineData("chrome://newtab/", true)]
    [InlineData("{google:baseURL}search?q={searchTerms}", true)]
    [InlineData("https://google.com.evil.example/", false)]
    [InlineData("https://search.searchfun.example/?q={searchTerms}", false)]
    public void Known_search_engines(string url, bool known) => Assert.Equal(known, KnownSearchEngines.IsKnownUrl(url));

    [Fact]
    public void Extensions_are_flagged_for_risky_permissions_outside_installs_and_policy()
    {
        var findings = BrowserAudit.Extensions(Scenario.OwnerLike().Browsers!);
        var coupon = Assert.Single(findings);
        Assert.Equal("Coupon Helper", coupon.Extension.Name);
        Assert.Contains(coupon.Flags, f => f.Text.Contains("another program") && f.Serious);
        Assert.Contains(coupon.Flags, f => f.Text.Contains("program installed on your PC"));
        Assert.Contains(coupon.Flags, f => f.Text.Contains("every website"));
    }

    [Fact]
    public void Store_extensions_are_listed_only_for_high_risk_permissions()
    {
        var adBlocker = new BrowserExtension("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Ad blocker", true, 1, true, new[] { "<all_urls>", "cookies", "webRequest" });
        var debuggerOne = new BrowserExtension("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Tab tool", true, 1, true, new[] { "debugger", "<all_urls>" });
        var sideloaded = new BrowserExtension("cccccccccccccccccccccccccccccccc", "Side", false, 1, true, new[] { "storage" });
        Assert.Empty(BrowserAudit.FlagsFor(adBlocker, forced: false));
        Assert.Contains(BrowserAudit.FlagsFor(debuggerOne, forced: false), f => f.Serious && f.Text.Contains("developer tool"));
        Assert.Contains(BrowserAudit.FlagsFor(sideloaded, forced: false), f => f.Text.Contains("not from the browser's store"));
    }

    [Fact]
    public void A_policy_installed_extension_is_marked_forced()
    {
        var ext = new BrowserExtension("ffffffffffffffffffffffffffffffff", "Forced One", false, 7, true, Array.Empty<string>());
        var facts = new BrowserFacts(new[] { new BrowserProfile("Chrome", "Default", "", null, true, null, Array.Empty<string>(), null, null, new[] { ext }) },
            Array.Empty<ForcedExtension>(), Array.Empty<BrowserPolicy>(), Array.Empty<InstalledProgram>());
        Assert.True(Assert.Single(BrowserAudit.Extensions(facts)).ForcedByPolicy);
    }

    [Fact]
    public void Unwanted_programs_match_whole_name_prefixes_only()
    {
        Assert.NotNull(PupCatalog.Match("Wave Browser"));
        Assert.NotNull(PupCatalog.Match("OneLaunch 5.2"));
        Assert.Null(PupCatalog.Match("Wave Browsers Pro Studio"));
        Assert.Null(PupCatalog.Match("Conduitive Notes"));
        Assert.Single(BrowserAudit.UnwantedPrograms(Scenario.OwnerLike().Browsers!));
    }

    [Theory]
    [InlineData(@"\??\C:\Program Files\FanTool\WinRing0x64.sys", "WinRing0x64.sys")]
    [InlineData(@"System32\drivers\RTCore64.sys", "RTCore64.sys")]
    [InlineData("\"C:\\Tools\\gdrv.sys\"", "gdrv.sys")]
    public void Vulnerable_drivers_are_matched_by_file_name(string imagePath, string file)
        => Assert.Equal(file, VulnerableDriverCatalog.Match(imagePath)!.FileName, ignoreCase: true);

    [Fact]
    public void Ordinary_drivers_are_not_matched() => Assert.Null(VulnerableDriverCatalog.Match(@"System32\drivers\disk.sys"));
}
