using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Insights.Model;
using PcTidy.Features.Insights.Source;
using PcTidy.Hosting;
using Xunit;

namespace PcTidy.Features.Insights.Tests;

/// <summary>The cards added from the community-tweaks re-check: refresh rate below max, runtimes, BitLocker, Smart App Control.</summary>
public class ResearchCardTests
{
    private static async Task<InsightsReport> ScanAsync(TestbedFolder tb, FakeSource source, bool laptop = false)
    {
        if (laptop) tb.Sim("Intel", laptop: true); // SandboxSystemInfo reads <root>\Sim\sim.json
        using var host = PcTidyHost.CreateSandbox(tb.Root, SandboxProfile.Minimal);
        return await new InsightsScanner(_ => source, new FixedClock()).ScanReportAsync(host.CreateScanContext(ScanDepth.Quick), null, CancellationToken.None);
    }

    private static InsightCard Card(InsightsReport r, string id) => r.Find(id) ?? throw new Xunit.Sdk.XunitException("Missing card " + id);

    [Theory]
    [InlineData(120, 165, true)]   // fast monitor below its top rate, not only 60 Hz
    [InlineData(60, 144, true)]
    [InlineData(59, 60, false)]    // 59.94 vs 60 Hz is the same mode
    [InlineData(144, 144, false)]
    public async Task Any_display_below_its_top_rate_is_spotted(int current, int max, bool attention)
    {
        using var tb = new TestbedFolder();
        var src = new FakeSource { Displays = { new DisplayFacts("Monitor", current, max, 2560, 1440) } };
        var card = Card(await ScanAsync(tb, src), InsightsIds.RefreshRate);
        Assert.Equal(attention ? InsightStatus.Attention : InsightStatus.Good, card.Status);
        if (attention) Assert.Contains($"{max} Hz", card.Headline);
    }

    [Fact]
    public async Task Missing_runtimes_are_named_with_microsoft_links_and_nothing_is_installed()
    {
        using var tb = new TestbedFolder();
        var src = new FakeSource { Runtimes = new RuntimeFacts { VisualCpp64 = TriState.No, VisualCpp32 = TriState.Yes, DirectX9Extras = TriState.No } };
        var card = Card(await ScanAsync(tb, src), InsightsIds.Runtimes);
        Assert.Equal(InsightStatus.Attention, card.Status);
        Assert.Contains("Visual C++ (64-bit)", card.Headline);
        Assert.Contains(card.Steps, s => s.Contains("winget install Microsoft.VCRedist.2015+.x64"));
        Assert.Contains(card.Steps, s => s.Contains("Microsoft.DotNet.DesktopRuntime."));
        Assert.Empty(card.Items); // the user's choice: PC Tidy installs nothing
        Assert.All(card.Links, l => Assert.True(l.Target.StartsWith("https://learn.microsoft.com", StringComparison.Ordinal)
                                                 || l.Target.StartsWith("https://www.microsoft.com", StringComparison.Ordinal)
                                                 || l.Target.StartsWith("https://dotnet.microsoft.com", StringComparison.Ordinal)));

        var allThere = new FakeSource { Runtimes = new RuntimeFacts { VisualCpp64 = TriState.Yes, VisualCpp32 = TriState.Yes, DirectX9Extras = TriState.Yes, DotNetDesktopVersions = new[] { 8 } } };
        var good = Card(await ScanAsync(tb, allThere), InsightsIds.Runtimes);
        Assert.Equal(InsightStatus.Good, good.Status);
        Assert.Empty(good.Steps);

        Assert.False(Card(await ScanAsync(tb, new FakeSource()), InsightsIds.Runtimes).IsAvailable);
    }

    [Fact]
    public async Task BitLocker_card_asks_to_check_the_recovery_key_and_never_changes_anything()
    {
        using var tb = new TestbedFolder();
        var on = Card(await ScanAsync(tb, new FakeSource { Security = new SecurityFacts { BitLocker = TriState.Yes } }), InsightsIds.BitLocker);
        Assert.Contains("recovery key", on.Headline);
        Assert.Contains(on.Links, l => l.Target == "https://aka.ms/myrecoverykey");
        Assert.Empty(on.Items);

        var admin = Card(await ScanAsync(tb, new FakeSource { Security = new SecurityFacts { BitLockerNeedsAdmin = true } }), InsightsIds.BitLocker);
        Assert.Equal(InsightStatus.NeedsAdmin, admin.Status);

        var offLaptop = Card(await ScanAsync(tb, new FakeSource { Security = new SecurityFacts { BitLocker = TriState.No } }, laptop: true), InsightsIds.BitLocker);
        Assert.Contains("Device encryption", offLaptop.WhatToDo);
    }

    [Theory]
    [InlineData(1, InsightStatus.Good, "keep it on")]
    [InlineData(2, InsightStatus.Info, "checking")]
    [InlineData(0, InsightStatus.Info, "off")]
    public async Task Smart_app_control_says_dont_turn_it_off(int state, InsightStatus status, string headline)
    {
        using var tb = new TestbedFolder();
        var card = Card(await ScanAsync(tb, new FakeSource { Security = new SecurityFacts { SmartAppControl = state } }), InsightsIds.SmartAppControl);
        Assert.Equal(status, card.Status);
        Assert.Contains(headline, card.Headline);
        Assert.Contains("Settings can't turn it back on", card.Explanation);
        Assert.Empty(card.Items);
        Assert.Contains(card.Links, l => l.Target == "ms-settings:windowsdefender");
    }

    [Fact]
    public async Task Smart_app_control_is_not_available_when_windows_has_none()
    {
        using var tb = new TestbedFolder();
        Assert.False(Card(await ScanAsync(tb, new FakeSource()), InsightsIds.SmartAppControl).IsAvailable);
    }
}
