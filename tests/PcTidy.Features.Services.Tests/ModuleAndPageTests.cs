using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PcTidy.Features.Services.UI;
using PcTidy.Hosting;

namespace PcTidy.Features.Services.Tests;

public class ModuleAndPageTests
{
    [Fact]
    public void Module_matches_the_section_contract()
    {
        var m = new ServicesModule();
        Assert.Equal("services", m.Id);
        Assert.Equal("Services", m.Title);
        Assert.Equal(NavGroup.SpeedUp, m.Group);
        Assert.Equal(Release.V3, m.Release);
        Assert.True(Enum.TryParse<Wpf.Ui.Controls.SymbolRegular>(m.Icon, out _), m.Icon);
        // Not used by another section (Speed, Tweaks and the rest).
        Assert.DoesNotContain(m.Icon, new[] { "TopSpeed24", "Wrench24", "Broom24", "Apps24", "Games24", "HeartPulse24", "DeveloperBoard24", "Rocket24" });
        Assert.Equal(new[] { "services", "services.repair" }, m.Scanners.Select(s => s.FeatureId).ToArray());
        Assert.True(m.Scanner.IncludedInOptimizeAll);
        Assert.False(m.Repair.IncludedInOptimizeAll);
        Assert.All(m.Scanners, s => Assert.Equal(Release.V3, s.Release));
    }

    [Fact]
    public void Feature_code_never_changes_the_system_directly()
    {
        var src = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(TestFolder.TestbedRoot))!, "src", "PcTidy.Features.Services");
        Assert.True(Directory.Exists(src), src);
        var forbidden = new Regex(@"\b(File\.(Delete|Move|Copy|Replace|WriteAll\w*|AppendAll\w*|Create)|Directory\.(Delete|Move|CreateDirectory)|\.MoveTo\(|Process\.Start|ShellExecute)|\.(SetValue|DeleteValue|DeleteSubKey\w*|CreateSubKey)\s*\(|FileAccess\.(Write|ReadWrite)|ChangeServiceConfig|ControlService|StartService|DeleteService");
        var hits = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(@"\obj\") && !f.Contains(@"\bin\"))
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (f, i, line)))
            .Where(x => !x.line.TrimStart().StartsWith("//") && forbidden.IsMatch(x.line))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}: {x.line.Trim()}").ToList();
        Assert.Empty(hits);
    }

    private sealed class Provider(PcTidyHost host, IFlowHost flow) : IServiceProvider
    {
        public object? GetService(Type t) => t == typeof(IFlowHost) ? flow : host.GetService(t);
    }

    [Fact]
    public void Page_scans_filters_searches_ticks_a_profile_and_hands_plans_to_the_shell()
    {
        using var folder = new TestFolder("ui");
        using var host = new FakeServicesHost(Path.Combine(folder.Path, "pc"), l => FakePc.Build(l, rpcStart: 3));
        var flow = new FakeFlowHost();
        var module = new ServicesModule(_ => host.World.Environment());
        var provider = new Provider(host.Sandbox, flow);

        var (page, vm) = Sta.Run(() =>
        {
            var p = (FrameworkElement)module.CreatePage(provider)!;
            p.Measure(new Size(1000, 760));
            p.Arrange(new Rect(0, 0, 1000, 760));
            return (p, (ServicesViewModel)p.DataContext);
        });
        Assert.False(vm.HasScanned);
        Sta.RunAsync(() => vm.ScanCommand.ExecuteAsync(null));
        Sta.Run(() =>
        {
            Assert.True(vm.HasScanned);
            Assert.DoesNotContain(vm.Rows, r => r.Info.IsDriver);       // drivers hidden by default
            Assert.NotNull(vm.SelectedRow);                              // details pane shows the first row
            Assert.Equal(1, vm.RepairCount);
            Assert.Equal(3, vm.Profiles.Count);

            vm.ShowDrivers = true;
            Assert.Contains(vm.Rows, r => r.Info.IsDriver);
            vm.ShowDrivers = false;

            vm.Filter = ServicesViewModel.FilterThirdParty;
            Assert.All(vm.Rows, r => Assert.True(r.IsThirdParty));
            vm.Filter = ServicesViewModel.FilterProtected;
            Assert.Contains(vm.Rows, r => r.Name == FakePc.AntiCheat);
            Assert.All(vm.Rows, r => Assert.True(r.IsProtected));
            vm.Filter = ServicesViewModel.FilterFlagged;
            Assert.Contains(vm.Rows, r => r.Name == FakePc.TempWorker);
            Assert.Contains(vm.Rows, r => r.Name == "RpcSs"); // needs repair
            vm.Filter = ServicesViewModel.FilterCanChange;
            Assert.All(vm.Rows, r => Assert.True(r.CanChange));
            vm.Filter = ServicesViewModel.FilterAll;
            vm.SearchText = "contoso";
            Assert.Equal(FakePc.ThirdPartyAuto, Assert.Single(vm.Rows).Name);
            vm.SearchText = "";

            // Protected rows can't be ticked.
            var eac = vm.AllRows.Single(r => r.Name == FakePc.AntiCheat);
            eac.IsChecked = true;
            Assert.False(eac.IsChecked);

            // Privacy profile ticks DiagTrack with its recommended Disabled; repairs tick RpcSs back to Automatic.
            vm.ApplyProfileCommand.Execute(vm.Profiles.Single(p => p.State.Profile.Id == ServiceProfiles.Privacy));
            vm.TickRepairsCommand.Execute(null);
            Assert.True(vm.PreviewCommand.CanExecute(null));
            page.UpdateLayout();
            SaveScreenshot(page, Path.Combine(TestFolder.TestbedRoot, "services-page-1000.png"), 1000, 760);
        });

        Sta.RunAsync(() => vm.PreviewCommand.ExecuteAsync(null));
        Assert.Equal(new[] { "services", "services.repair" }, flow.Reviewed.Select(p => p.FeatureId).ToArray());
        var diag = flow.Reviewed[0].Items.Single(i => i.ServiceName == "DiagTrack");
        Assert.Equal(ServiceStart.Disabled, diag.ServiceStartType);
        var rpc = flow.Reviewed[1].Items.Single();
        Assert.Equal(("RpcSs", (ServiceStart?)ServiceStart.Automatic), (rpc.ServiceName!, rpc.ServiceStartType));
        Assert.All(flow.Reviewed.SelectMany(p => p.Items), i => Assert.True(host.Safety.Check(i).Allowed, i.Description));

        Sta.RunAsync(() => vm.SimulateCommand.ExecuteAsync(null));
        Assert.Equal(2, flow.Simulated.Count);
    }

    [Fact]
    public void Page_lays_out_at_1000_and_1400_pixels_wide_with_the_built_in_practice_pc()
    {
        using var folder = new TestFolder("ui");
        using var host = PcTidyHost.CreateSandbox(Path.Combine(folder.Path, "pc"), SandboxProfile.Default);
        var provider = new Provider(host, new FakeFlowHost());
        var page = Sta.Run(() => (FrameworkElement)new ServicesModule().CreatePage(provider)!);
        var vm = Sta.Run(() => (ServicesViewModel)page.DataContext);
        Sta.RunAsync(() => vm.ScanCommand.ExecuteAsync(null));
        Sta.Run(() =>
        {
            Assert.Contains(vm.Notes, n => n.Contains("Practice PC"));
            foreach (var (w, h) in new[] { (1000.0, 700.0), (1400.0, 900.0), (720.0, 640.0) })
            {
                page.Measure(new Size(w, h));
                page.Arrange(new Rect(0, 0, w, h));
                page.UpdateLayout();
                Assert.True(page.ActualWidth <= w + 0.5 && page.ActualHeight <= h + 0.5);
                AssertRowsFitTheList(page);
            }
            // The page inside a 1000 px window (the shell's navigation takes the rest), then a wide window.
            SaveScreenshot(page, Path.Combine(TestFolder.TestbedRoot, "services-page-720.png"), 720, 640);
            AssertRowsFitTheList(page);
            SaveScreenshot(page, Path.Combine(TestFolder.TestbedRoot, "services-page-1400.png"), 1400, 900);
        });
    }

    /// <summary>Every row sits inside the list (rows measured wider than the list would be cut off on narrow windows).</summary>
    private static void AssertRowsFitTheList(FrameworkElement page)
    {
        var list = (System.Windows.Controls.ListBox)page.FindName("RowsList");
        var stack = new Stack<DependencyObject>(new[] { (DependencyObject)list });
        var rows = 0;
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (d is System.Windows.Controls.ListBoxItem item)
            {
                rows++;
                var left = item.TranslatePoint(new Point(0, 0), list).X;
                Assert.True(left >= -0.5 && left + item.ActualWidth <= list.ActualWidth + 0.5, $"row at {left:0}, {item.ActualWidth:0} wide, list {list.ActualWidth:0}");
            }
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) stack.Push(VisualTreeHelper.GetChild(d, i));
        }
        Assert.True(rows > 0);
    }

    /// <summary>Renders the page to a PNG in testbed\services (fake data only) for a visual check.</summary>
    private static void SaveScreenshot(FrameworkElement page, string path, int width, int height)
    {
        page.Measure(new Size(width, height));
        page.Arrange(new Rect(0, 0, width, height));
        page.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var dc = background.RenderOpen())
            dc.DrawRectangle(Application.Current.TryFindResource("ApplicationBackgroundBrush") as Brush ?? Brushes.Black, null, new Rect(0, 0, width, height));
        bitmap.Render(background);
        bitmap.Render(page);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
