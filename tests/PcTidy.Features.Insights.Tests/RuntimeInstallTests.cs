using PcTidy.Catalog.Insights;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Insights.Model;
using PcTidy.Features.Insights.Planning;
using PcTidy.Features.Insights.Source;
using PcTidy.Hosting;
using Xunit;

namespace PcTidy.Features.Insights.Tests;

/// <summary>
/// "Install missing runtimes" (v3.1 build list B4): a winget.install row only for each runtime that's actually missing,
/// only allow-listed Microsoft ids, never ticked, not elevated by PC Tidy, not reversible, and only when winget is there.
/// </summary>
public class RuntimeInstallTests
{
    private static async Task<InsightCard> RuntimesCardAsync(TestbedFolder tb, RuntimeFacts facts)
    {
        using var host = PcTidyHost.CreateSandbox(tb.Root, SandboxProfile.Minimal);
        var report = await new InsightsScanner(_ => new FakeSource { Runtimes = facts }, new FixedClock())
            .ScanReportAsync(host.CreateScanContext(ScanDepth.Quick), null, CancellationToken.None);
        return report.Find(InsightsIds.Runtimes)!;
    }

    [Fact]
    public void The_allow_list_holds_exactly_the_five_microsoft_runtimes()
    {
        Assert.Equal(new[]
        {
            "Microsoft.VCRedist.2015+.x64", "Microsoft.VCRedist.2015+.x86", "Microsoft.DirectX",
            "Microsoft.DotNet.DesktopRuntime.8", "Microsoft.DotNet.DesktopRuntime.9",
        }, RuntimeInstalls.All.Select(r => r.WingetId));
        Assert.All(RuntimeInstalls.All, r => Assert.True(RuntimeInstalls.IsAllowed(r.WingetId)));
        foreach (var bad in new[] { "microsoft.directx", "Microsoft.DirectX --override /q", "Microsoft.DotNet.DesktopRuntime.10", "Contoso.App", "", null })
            Assert.False(RuntimeInstalls.IsAllowed(bad), bad);
    }

    [Fact]
    public void A_plan_item_can_only_be_built_for_an_allow_listed_id()
    {
        var item = InsightPlans.InstallRuntime(RuntimeInstalls.For(RuntimeKind.VisualCpp64));
        Assert.Equal(ActionKind.RunSystemTool, item.Kind);
        Assert.Equal(ToolIds.WingetInstall, item.ToolId);
        Assert.Equal("Microsoft.VCRedist.2015+.x64", Assert.Single(item.Args!).Value);
        Assert.False(item.NeedsAdmin);
        Assert.False(item.Reversible);
        Assert.Contains("Downloads from Microsoft through winget. You accept Microsoft's licence terms for Visual C++ 2015-2022 (64-bit).", item.Description);

        Assert.Throws<ArgumentException>(() => InsightPlans.InstallRuntime(new RuntimeInstall(RuntimeKind.DirectX, "Contoso.FakeDirectX", "DirectX")));
    }

    [Fact]
    public async Task Only_missing_runtimes_get_a_row_and_nothing_is_ticked()
    {
        using var tb = new TestbedFolder();
        var card = await RuntimesCardAsync(tb, new RuntimeFacts
        {
            VisualCpp64 = TriState.No, VisualCpp32 = TriState.Yes, DirectX9Extras = TriState.No, DotNetDesktopVersions = new[] { 8 }, WingetPresent = true,
        });

        var ids = card.Items.SelectMany(i => i.Actions).Select(a => a.Args!["id"]).ToList();
        Assert.Equal(new[] { "Microsoft.VCRedist.2015+.x64", "Microsoft.DirectX", "Microsoft.DotNet.DesktopRuntime.9" }, ids);
        Assert.All(card.Items, i =>
        {
            Assert.False(i.DefaultSelected);
            Assert.Equal(Badge.Check, i.Badge);
            Assert.Null(i.DisabledReason);
            Assert.StartsWith("Downloads from Microsoft through winget. You accept Microsoft's licence terms for ", i.Reason);
            var a = Assert.Single(i.Actions);
            Assert.Equal(ToolIds.WingetInstall, a.ToolId);
            Assert.True(RuntimeInstalls.IsAllowed(a.Args!["id"]));
            Assert.False(a.NeedsAdmin);
            Assert.False(a.Reversible);
        });
        Assert.Empty(card.Steps); // the rows replace the typed commands

        // The plan built from the ticked rows holds only allow-listed ids.
        var plan = PlanBuilder.FromSelection(InsightsIds.FeatureId, card.Title, card.Items);
        Assert.Equal(3, plan.Items.Count);
        Assert.All(plan.Items, p => Assert.True(RuntimeInstalls.IsAllowed(p.Args!["id"])));
        Assert.False(plan.RequiresAdmin);
    }

    [Fact]
    public async Task Nothing_is_offered_when_everything_is_installed()
    {
        using var tb = new TestbedFolder();
        var card = await RuntimesCardAsync(tb, new RuntimeFacts
        {
            VisualCpp64 = TriState.Yes, VisualCpp32 = TriState.Yes, DirectX9Extras = TriState.Yes, DotNetDesktopVersions = new[] { 8, 9, 10 }, WingetPresent = true,
        });
        Assert.Equal(InsightStatus.Good, card.Status);
        Assert.Empty(card.Items);
    }

    [Fact]
    public async Task Without_winget_nothing_is_offered_and_the_manual_steps_stay()
    {
        using var tb = new TestbedFolder();
        var card = await RuntimesCardAsync(tb, new RuntimeFacts { VisualCpp64 = TriState.No, VisualCpp32 = TriState.No, DirectX9Extras = TriState.No, WingetPresent = false });
        Assert.Empty(card.Items);
        Assert.NotEmpty(card.Steps);
        Assert.Contains("Microsoft", card.WhatToDo);
    }

    [Fact]
    public async Task Unknown_runtimes_are_never_treated_as_missing()
    {
        using var tb = new TestbedFolder();
        var card = await RuntimesCardAsync(tb, new RuntimeFacts { VisualCpp64 = TriState.Unknown, VisualCpp32 = TriState.Yes, DirectX9Extras = TriState.Unknown, DotNetDesktopVersions = new[] { 8, 9 }, WingetPresent = true });
        Assert.Empty(card.Items);
    }

    [Fact]
    public async Task An_unreadable_dotnet_folder_says_couldnt_check_and_offers_no_dotnet_install()
    {
        using var tb = new TestbedFolder();
        var facts = new RuntimeFacts { VisualCpp64 = TriState.Yes, VisualCpp32 = TriState.Yes, DirectX9Extras = TriState.Yes, DotNetKnown = false, WingetPresent = true };
        var card = await RuntimesCardAsync(tb, facts);
        Assert.Empty(card.Items);
        Assert.Empty(Cards.RuntimeCards.MissingInstalls(facts));
        Assert.Contains(card.Facts, f => f.Label == ".NET Desktop Runtime" && f.Value == "Couldn't check");
        Assert.DoesNotContain(card.Steps, s => s.Contains("DotNet", StringComparison.Ordinal));
        Assert.Contains("couldn't check which .NET versions", card.WhatToDo);

        // Read fine and really none: both .NET rows are offered (unticked).
        var none = await RuntimesCardAsync(tb, facts with { DotNetKnown = true });
        Assert.Equal(2, none.Items.Count(i => i.Title.Contains(".NET")));
        Assert.All(none.Items, i => Assert.False(i.DefaultSelected));
    }
}
