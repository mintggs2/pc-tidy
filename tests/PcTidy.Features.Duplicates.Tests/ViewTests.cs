using System.Windows;
using PcTidy.Features.Duplicates.UI;

namespace PcTidy.Features.Duplicates.Tests;

/// <summary>The page builds, shows a real report at 1000 px, and its numbers match the plan it would hand over.</summary>
[Collection("default-pc")]
public sealed class ViewTests(DefaultPcFixture f)
{
    [Fact]
    public void Page_loads_a_report_and_totals_match_the_plan()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var module = new DuplicatesModule();
                var page = (DuplicatesView)module.CreatePage(f.Pc.Host)!;
                page.Model.Load(f.Report);
                page.Measure(new Size(1000, 800));
                page.Arrange(new Rect(0, 0, 1000, 800));
                var vm = page.Model;
                Assert.Equal(f.Report.ExactGroups.Count, vm.Groups.Count);
                // Ticked by default = the scanner's clear cases.
                var ticked = vm.SelectedItems();
                var expected = f.Report.ExactGroups.SelectMany(g => g.AutoTicked).ToList();
                Assert.Equal(expected.Count, ticked.Count);
                Assert.StartsWith($"Selected {ticked.Count:N0} item", vm.SelectionText);
                Assert.Contains(Core.Sizes.Format(ticked.Sum(i => i.ExpectedSize)), vm.SelectionText);

                // Choosing to keep the Downloads copy instead: it can't be ticked any more, and the old kept copy becomes removable.
                var group = vm.Groups.First(g => g.Group.AutoTicked.Count > 0);
                var downloads = group.Copies.First(c => group.Group.AutoTicked.Contains(c.Path));
                downloads.IsKept = true;
                Assert.False(downloads.CanRemove);
                Assert.False(downloads.IsChecked);
                Assert.Equal("You chose to keep this copy.", group.Group.KeepReason);
                Assert.Equal(expected.Count - 1, vm.SelectedItems().Count);

                vm.SelectClearCasesOnly();
                Assert.Equal(expected.Count - 1, vm.SelectedItems().Count);

                // One verb everywhere: the bar and the confirm dialog say "Remove" (passed as FlowOptions.PrimaryVerb).
                Assert.StartsWith("Remove ", DuplicatesViewModel.PrimaryVerb(vm.SelectedItems()));

                // The sections are segmented tabs, one per tab, labelled with the counts.
                var tabs = (System.Windows.Controls.TabControl)page.FindName("Tabs");
                var strip = (System.Windows.Controls.Panel)page.FindName("TabButtons");
                Assert.Equal(tabs.Items.Count, strip.Children.Count);
                Assert.Equal(vm.ExactTabTitle, ((System.Windows.Controls.RadioButton)strip.Children[0]).Content);
                ((System.Windows.Controls.RadioButton)strip.Children[1]).IsChecked = true;
                Assert.Equal(1, tabs.SelectedIndex);
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException(error.ToString());
    }
}
