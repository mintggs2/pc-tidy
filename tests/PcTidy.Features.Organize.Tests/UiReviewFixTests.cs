using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using PcTidy.Core;
using PcTidy.Features.Organize.Planning;
using PcTidy.Features.Organize.Takeout;
using PcTidy.Features.Organize.Triage;
using PcTidy.Features.Organize.UI;
using Xunit;

namespace PcTidy.Features.Organize.Tests;

/// <summary>v3 UI review fixes: Takeout's default "Sort into" (UI3-26), tab-aware header (UI3-27), Esc in triage (UI3-30).</summary>
public class UiReviewFixTests
{
    private sealed class Services(Sandbox sb, IFlowHost flow) : IServiceProvider
    {
        public object? GetService(Type t)
            => t == typeof(IKnownLocations) ? sb.Locations
             : t == typeof(ISafetyGuard) ? sb.Safety
             : t == typeof(IInUseChecker) ? sb.InUse
             : t == typeof(IFileWalker) ? new ManagedFileWalker()
             : t == typeof(AppSettings) ? sb.AppSettings
             : t == typeof(IFlowHost) ? flow
             : null;
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try { action(); } catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException("UI failed: " + error);
    }

    [Fact]
    public void Takeout_suggests_Pictures_only_when_it_is_not_in_a_cloud_folder()
    {
        using var local = new Sandbox(SandboxProfile.Minimal);
        Assert.Equal(TakeoutPlanner.DefaultDestination(local.Locations), TakeoutPlanner.SuggestedDestination(local.Locations, local.Safety, local.AppSettings));

        // Default profile: Pictures is redirected into OneDrive, so nothing is picked for the user.
        using var cloud = new Sandbox(SandboxProfile.Default);
        Assert.Null(TakeoutPlanner.SuggestedDestination(cloud.Locations, cloud.Safety, cloud.AppSettings));
        Assert.True(TakeoutPlanner.DestinationNeedsPermission(TakeoutPlanner.DefaultDestination(cloud.Locations), cloud.Locations, cloud.Safety, cloud.AppSettings));

        // Once the user allowed that OneDrive folder in Settings, it's fine to suggest it.
        cloud.AppSettings.AllowedAskFirstFolders.Add(cloud.Locations.Pictures);
        Assert.Equal(TakeoutPlanner.DefaultDestination(cloud.Locations), TakeoutPlanner.SuggestedDestination(cloud.Locations, cloud.Safety, cloud.AppSettings));
    }

    [Fact]
    public void Takeout_with_Pictures_in_OneDrive_asks_for_a_folder_and_never_offers_to_sort_zero()
    {
        using var sb = new Sandbox(SandboxProfile.Default);
        var vm = new TakeoutViewModel(new FakeFlowHost(), new TakeoutPlanner(sb.Safety, sb.InUse, new ManagedFileWalker()), sb.Locations, sb.Safety, sb.AppSettings);
        Assert.Null(vm.DestinationFolder);
        Assert.Equal("Not chosen yet", vm.DestinationText);
        Assert.Contains("OneDrive", vm.DestinationProblem);

        vm.SourceFolder = sb.Downloads;
        Assert.False(vm.ScanCommand.CanExecute(null)); // no destination yet: no scan that would sort nothing
        Assert.Equal("Choose where the photos go", vm.EmptyTitle);
        Assert.Equal("Choose where the sorted photos go", vm.Summary);

        // Picking a OneDrive folder that isn't allowed keeps asking (with the reason), still no scan.
        vm.DestinationFolder = Path.Combine(sb.Locations.Pictures, "Sorted");
        Assert.Contains("OK", vm.DestinationProblem);
        Assert.False(vm.ScanCommand.CanExecute(null));

        // A local folder clears it.
        vm.DestinationFolder = Path.Combine(sb.Locations.UserProfile, "Photos");
        Assert.Null(vm.DestinationProblem);
        Assert.True(vm.ScanCommand.CanExecute(null));
        Assert.Equal("Sort photos", vm.SortButtonText);
    }

    [Fact]
    public void Takeout_stay_title_says_what_stays()
    {
        static SkippedEntry E(string name) => new(Path.Combine(@"C:\t", name), @"C:\t", false, SkipKind.LeaveInPlace, "why", 1);
        Assert.Equal("15 photos and 2 info files stay where they are",
            TakeoutViewModel.StayTitle(Enumerable.Range(0, 15).Select(i => E($"IMG_{i}.jpg")).Append(E("a.json")).Append(E("b.json")).ToList()));
        Assert.Equal("1 photo stays where it is", TakeoutViewModel.StayTitle(new[] { E("IMG_1.jpg") }));
        Assert.Equal("", TakeoutViewModel.StayTitle(Array.Empty<SkippedEntry>()));
    }

    [Fact]
    public void Header_follows_the_open_tab_and_the_folder_picker_only_shows_where_it_applies()
    {
        using var sb = new Sandbox(SandboxProfile.Minimal);
        RunSta(() =>
        {
            var view = (FrameworkElement)new OrganizeModule().CreatePage(new Services(sb, new FakeFlowHost()))!;
            view.Measure(new Size(1000, 700));
            view.Arrange(new Rect(0, 0, 1000, 700));
            var vm = (OrganizeViewModel)view.DataContext;
            Assert.Equal("Organize Downloads", vm.HeaderTitle);
            Assert.True(vm.ShowFolderPicker && vm.ShowSortTools);

            vm.SelectedTab = OrganizeViewModel.TakeoutTab;
            Assert.Equal("Sort a Google Takeout export", vm.HeaderTitle);
            Assert.Contains("year and month", vm.HeaderSubtitle);
            Assert.False(vm.ShowFolderPicker);
            Assert.False(vm.ShowSortTools);

            vm.SelectedTab = OrganizeViewModel.RenameTab;
            Assert.Equal("Rename files in Downloads", vm.HeaderTitle);
            Assert.True(vm.ShowFolderPicker);
            Assert.False(vm.ShowSortTools);
        });
    }

    [Fact]
    public void Escape_in_triage_asks_before_dropping_decisions_and_closes_at_once_without_any()
    {
        using var sb = new Sandbox(SandboxProfile.Minimal);
        sb.File("a.jpg", ageDays: 1); sb.File("b.pdf", ageDays: 2);
        Assert.False(TriageViewModel.TryMap(Key.Escape, out _)); // Esc is "stop", not "finish"

        var session = TriageSession.Load(sb.Downloads, OrganizeSettings.Load(sb.AppSettings), new ManagedFileWalker(), sb.Safety, DateTime.UtcNow);
        var vm = new TriageViewModel(session);
        int cancelled = 0, finished = 0;
        vm.Cancelled += () => cancelled++;
        vm.Finished += () => finished++;

        vm.RequestStop(); // nothing decided: just close
        Assert.Equal(1, cancelled);
        Assert.False(vm.IsAskingToStop);

        vm.Press(TriageKey.Keep);
        vm.RequestStop();
        Assert.True(vm.IsAskingToStop);
        Assert.Contains("1 decision", vm.StopQuestion);
        Assert.Equal(TriageOutcome.Ignored, vm.Press(TriageKey.Keep)); // keys wait for the answer

        vm.RequestStop(); // a second Esc keeps sorting
        Assert.False(vm.IsAskingToStop);
        Assert.Equal(1, cancelled);

        vm.RequestStop();
        vm.ReviewDecisionsCommand.Execute(null);
        Assert.Equal(1, finished);
    }
}
