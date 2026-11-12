using System.Windows;
using PcTidy.Features.Services.Scanning;
using PcTidy.Features.Services.Sources;
using PcTidy.Features.Services.UI;
using PcTidy.Hosting;

namespace PcTidy.Features.Services.Tests;

/// <summary>
/// v3.1 Services additions: the rail counter ("N changed from Windows default"), the summary line when many Windows
/// services are disabled at once, and the "Why there are no service presets" card. Fake services only.
/// </summary>
public class V31ServicesTests
{
    private const string Win = Svc.Windows;

    /// <summary>A throwing source: the counter must say nothing rather than guess.</summary>
    private sealed class BrokenSource : IServiceSource
    {
        public IReadOnlyList<ServiceRecord> ReadAll(ICollection<string> problems, CancellationToken ct) => throw new UnauthorizedAccessException("no");
    }

    /// <summary>Catalog entries (exact names) that Windows never ships Disabled, for building fake Windows services.</summary>
    private static IReadOnlyList<ServiceEntry> DefaultOnEntries(int count)
        => ServiceCatalog.All.Where(e => !e.IsTemplate && e.Name is not ("RpcSs" or "DiagTrack") // FakePc's own catalog names
                                         && e.WindowsDefaults.Count > 0 && !e.WindowsDefaults.Contains(ServiceStart.Disabled)
                                         && !e.WindowsDefaults.Contains(ServiceStart.Boot) && !e.WindowsDefaults.Contains(ServiceStart.System))
            .Take(count).ToList();

    private static int Raw(ServiceStart s) => s switch { ServiceStart.Automatic or ServiceStart.AutomaticDelayed => 2, ServiceStart.Manual => 3, _ => 4 };

    private static ServiceRecord WindowsService(ServiceEntry e, int start)
        => Svc.Shared(e.Name, start, $@"{Win}\System32\{e.Name.ToLowerInvariant()}.dll") with
        {
            DelayedAutostart = e.WindowsDefaults[0] == ServiceStart.AutomaticDelayed,
        };

    [Fact]
    public void Counter_counts_only_windows_services_with_a_catalog_default_that_start_differently()
    {
        var entries = DefaultOnEntries(3);
        var records = new List<ServiceRecord>
        {
            WindowsService(entries[0], Raw(entries[0].WindowsDefaults[0])),   // at its default: not counted
            WindowsService(entries[1], 4),                                     // disabled: counted
            WindowsService(entries[2], 4) with { ServiceType = 0xE0 },         // a per-user copy: skipped
            Svc.Own("PcTidyTestWinSvc", 4, $@"{Win}\System32\pctidytestwin.exe"),       // no catalog entry: skipped
            Svc.Own("EasyAntiCheat", 4, @"C:\Program Files (x86)\EasyAntiCheat\EasyAntiCheat.exe"), // not a Windows service
            new ServiceRecord { Name = "pctidytestdrv", ServiceType = 0x1, Start = 4, ImagePath = @"\SystemRoot\System32\drivers\pctidytestdrv.sys" },
        };
        var changed = ServiceDefaultsCounter.ChangedFromDefault(records, Win);
        Assert.Equal(entries[1].Name, Assert.Single(changed).Record.Name);
        Assert.Equal("1 changed", ServiceDefaultsCounter.BadgeText(changed.Count));
        Assert.Null(ServiceDefaultsCounter.BadgeText(0));
    }

    [Theory]
    [InlineData(@"%SystemRoot%\system32\svchost.exe -k netsvcs -p", @"%SystemRoot%\System32\x.dll", true)]
    [InlineData(@"C:\Windows\System32\svchost.exe -k LocalService", @"C:\Program Files\Contoso\x.dll", false)]
    [InlineData(@"""C:\Program Files\Contoso\svc.exe"" /run", null, false)]
    [InlineData(@"\SystemRoot\System32\lsass.exe", null, true)]
    [InlineData(@"System32\spoolsv.exe", null, true)]
    [InlineData(@"C:\WindowsApps\x.exe", null, false)]
    public void A_windows_service_is_one_whose_program_or_dll_is_under_the_windows_folder(string imagePath, string? dll, bool expected)
        => Assert.Equal(expected, ServiceDefaultsCounter.IsWindowsService(new ServiceRecord { Name = "x", ImagePath = imagePath, ServiceDll = dll }, Win));

    [Fact]
    public async Task The_module_is_a_nav_badge_source_that_reads_only_the_service_list()
    {
        var entries = DefaultOnEntries(2);
        var world = new FakeServiceWorld().Add(WindowsService(entries[0], 4)).Add(WindowsService(entries[1], 4));
        var module = new ServicesModule(_ => world.Environment(), () => world.Environment(), Win);
        Assert.IsAssignableFrom<INavBadgeSource>(module);
        Assert.Equal("2 changed", await module.GetNavBadgeAsync(CancellationToken.None));
        Assert.Empty(world.StartWrites); // read-only

        var clean = new FakeServiceWorld().Add(WindowsService(entries[0], Raw(entries[0].WindowsDefaults[0])));
        Assert.Null(await new ServicesModule(_ => clean.Environment(), () => clean.Environment(), Win).GetNavBadgeAsync(CancellationToken.None));

        // Unreadable, or a module built for tests/practice without a badge source: nothing, never a guess or the real PC.
        var broken = new ServicesEnvironment { Services = new BrokenSource(), Files = new FixedFileFacts(Array.Empty<FileFacts>()), Usage = world };
        Assert.Null(await new ServicesModule(_ => broken, () => broken, Win).GetNavBadgeAsync(CancellationToken.None));
        Assert.Null(await new ServicesModule(_ => world.Environment()).GetNavBadgeAsync(CancellationToken.None));
    }

    [Fact]
    public void Mass_disabling_gives_one_summary_line_from_five_services()
    {
        var entries = DefaultOnEntries(5);
        Assert.Equal(5, entries.Count);
        var four = ServiceDefaultsCounter.ChangedFromDefault(entries.Take(4).Select(e => WindowsService(e, 4)), Win);
        Assert.Null(ServiceDefaultsCounter.MassDisabledText(ServiceDefaultsCounter.DisabledAgainstDefault(four)));
        var five = ServiceDefaultsCounter.ChangedFromDefault(entries.Select(e => WindowsService(e, 4)), Win);
        var text = ServiceDefaultsCounter.MassDisabledText(ServiceDefaultsCounter.DisabledAgainstDefault(five));
        Assert.NotNull(text);
        Assert.StartsWith("5 Windows services are switched off", text);
        Assert.Contains("bad-services repair", text);
        // Manual where Windows has Automatic is a change, but not "switched off".
        var manual = ServiceDefaultsCounter.ChangedFromDefault(entries.Where(e => !e.WindowsDefaults.Contains(ServiceStart.Manual)).Select(e => WindowsService(e, 3)), Win);
        Assert.Equal(0, ServiceDefaultsCounter.DisabledAgainstDefault(manual));
    }

    [Fact]
    public void The_no_presets_card_has_the_three_reasons_in_plain_words_without_naming_the_other_tool()
    {
        Assert.Equal("Why there are no service presets", ServicePresetFacts.Title);
        Assert.Equal(3, ServicePresetFacts.Facts.Count);
        var all = ServicePresetFacts.Summary + string.Join(" ", ServicePresetFacts.Facts.Select(f => f.Topic + f.Text + f.Source));
        Assert.DoesNotContain("Extreme", all, StringComparison.OrdinalIgnoreCase); // no other product's preset names
        Assert.Contains("Black Viper", all);
        Assert.Contains("No boot-time, memory or FPS numbers", all);
        Assert.Contains("Xbox", all);
        Assert.Contains("sign-in", all);
        Assert.Contains("dozens of Windows features", all);
        Assert.Contains("other tuning tools", all);
        Assert.All(ServicePresetFacts.Facts, f => Assert.False(string.IsNullOrWhiteSpace(f.Source)));
    }

    private sealed class Provider(PcTidyHost host, IFlowHost flow) : IServiceProvider
    {
        public object? GetService(Type t) => t == typeof(IFlowHost) ? flow : host.GetService(t);
    }

    [Fact]
    public void Page_shows_the_summary_line_and_the_presets_card_and_points_at_the_repair()
    {
        using var folder = new TestFolder("v31");
        var entries = DefaultOnEntries(5);
        using var host = new FakeServicesHost(Path.Combine(folder.Path, "pc"), l =>
        {
            var world = FakePc.Build(l);
            foreach (var e in entries.Where(e => world.Query(e.Name) is null))
                world.Add(Svc.Shared(e.Name, 4, Path.Combine(l.WindowsDir, "System32", e.Name.ToLowerInvariant() + ".dll")),
                    FixedFileFacts.Microsoft(Path.Combine(l.WindowsDir, "System32", e.Name.ToLowerInvariant() + ".dll")));
            return world;
        });
        var module = new ServicesModule(_ => host.World.Environment());
        var provider = new Provider(host.Sandbox, new FakeFlowHost());
        var (page, vm) = Sta.Run(() =>
        {
            var p = (FrameworkElement)module.CreatePage(provider)!;
            p.Measure(new Size(1000, 760));
            p.Arrange(new Rect(0, 0, 1000, 760));
            return (p, (ServicesViewModel)p.DataContext);
        });
        Assert.False(vm.HasMassDisabled);
        Sta.RunAsync(() => vm.ScanCommand.ExecuteAsync(null));
        Sta.Run(() =>
        {
            Assert.True(vm.HasMassDisabled, "five Windows services are disabled against their default");
            Assert.Contains("Windows services are switched off", vm.MassDisabledText);
            Assert.Equal(ServicePresetFacts.Title, vm.PresetCardTitle);
            Assert.Equal(3, vm.PresetFacts.Count);
            var card = (System.Windows.Controls.Expander)page.FindName("PresetCard");
            Assert.False(card.IsExpanded); // closed until opened: the list keeps its room
            Assert.False(string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(card)));
            vm.ShowRepairsCommand.Execute(null);
            Assert.Equal(ServicesViewModel.FilterFlagged, vm.Filter);
            Assert.DoesNotContain(vm.AllRows, r => r.IsChecked); // pointing at the repair ticks nothing
            page.Measure(new Size(1000, 760));
            page.Arrange(new Rect(0, 0, 1000, 760));
            page.UpdateLayout();
            Assert.True(page.ActualWidth <= 1000.5);
        });
    }
}
