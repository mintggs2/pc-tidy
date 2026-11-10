using PcTidy.Catalog.Drivers;
using PcTidy.Core.Model;
using PcTidy.Features.Drivers.Analysis;
using PcTidy.Features.Drivers.Model;
using PcTidy.Features.Drivers.Scanning;
using PcTidy.Platform;

namespace PcTidy.Features.Drivers.Tests;

public class VendorAndBoardTests
{
    [Theory]
    [InlineData("NVIDIA App 9.8.7.6", "nvidia-app")]
    [InlineData("NVIDIA GeForce Experience 3.27", "nvidia-app")]
    [InlineData("AMD Software: Adrenalin Edition", "amd-software")]
    [InlineData("Intel® Driver & Support Assistant", "intel-dsa")]
    [InlineData("Logitech G HUB", "logitech-ghub")]
    [InlineData("SteelSeries GG 99.2.0", "steelseries-gg")]
    [InlineData("Razer Synapse", "razer-synapse")]
    [InlineData("iCUE", "corsair-icue")]
    [InlineData("Corsair iCUE5 Software", "corsair-icue")]
    [InlineData("AMD Chipset Software", null)]
    [InlineData("NVIDIA App driver settings", null)]
    [InlineData("Example Text Editor", null)]
    public void Tools_are_recognised_from_their_uninstall_names(string displayName, string? toolId)
        => Assert.Equal(toolId, VendorTools.FindByUninstallName(displayName)?.Id);

    [Fact]
    public void Every_official_page_is_https_and_passes_the_open_handler()
    {
        foreach (var url in VendorTools.All.Select(t => t.OfficialUrl)
                     .Concat(BoardVendors.All.Select(b => b.UrlTemplate.Replace("{model}", "EXAMPLE%20X100%20PRO%20%28MS-0000%29", StringComparison.Ordinal))))
        {
            Assert.StartsWith("https://", url);
            Assert.True(OpenExternalHandler.TryValidate(url, out var kind, out var problem), url + ": " + problem);
            Assert.Equal(ExternalTargetKind.WebPage, kind);
        }
    }

    [Fact]
    public async Task Installed_tools_and_hardware_without_a_tool_are_suggested()
    {
        var inv = await Fixtures.InventoryAsync();
        var tools = VendorAdvisor.Tools(inv.Apps, inv.Devices, inv.WmiDrivers);

        Assert.Contains(tools, t => t.Tool.Id == "nvidia-app" && t.Installed);
        Assert.Contains(tools, t => t.Tool.Id == "razer-synapse" && t.Installed);
        Assert.Contains(tools, t => t.Tool.Id == "logitech-ghub" && !t.Installed   // Logitech hardware present, no G HUB
                                    && t.MatchedDevice == "Logitech Example Wireless Receiver");
        Assert.DoesNotContain(tools, t => t.Tool.Id == "amd-software");          // AMD non-graphics device only, no AMD graphics
        Assert.Contains(inv.Devices, d => d.IsPresent && d.Manufacturer == "AMD" && d.ClassName != "Display");
        Assert.Single(tools, t => t.Tool.Id == "nvidia-app");                  // two NVIDIA entries, one row
    }

    [Theory]
    [InlineData("Gigabyte Technology Co., Ltd.", "EXAMPLE G-200 WIFI", "https://www.gigabyte.com/Search?kw=EXAMPLE%20G-200%20WIFI", true)]
    [InlineData("ASUSTeK COMPUTER INC.", "EXAMPLE PRIME A-300", "https://www.asus.com/support/download-center/", true)]
    [InlineData("Micro-Star International Co., Ltd.", "EXAMPLE X100 PRO (MS-0000)", "https://www.msi.com/search/EXAMPLE%20X100%20PRO%20%28MS-0000%29", true)]
    [InlineData("Dell Inc.", "0XYZ12", "https://www.dell.com/support/home/", true)]
    [InlineData("Contoso Boards", "CB-1", "https://www.bing.com/search?q=Contoso%20Boards%20CB-1%20support%20drivers", false)]
    public void Board_support_links(string maker, string model, string url, bool makerSite)
    {
        var link = BoardVendors.SupportLinkFor(maker, model)!;
        Assert.Equal(url, link.Url);
        Assert.Equal(makerSite, link.IsMakerSite);
        Assert.True(OpenExternalHandler.TryValidate(link.Url, out _, out _));
    }

    [Theory]
    [InlineData("To be filled by O.E.M.", "Default string")]
    [InlineData("", "")]
    [InlineData(null, "Base Board Product Name")]
    public void Placeholder_boards_give_no_link(string? maker, string? model) => Assert.Null(BoardVendors.SupportLinkFor(maker, model));

    [Fact]
    public async Task Updates_tab_has_board_page_realtek_note_and_settings_link()
    {
        var pages = (await Fixtures.ReportAsync()).Result.Group(DriverTabs.Updates, "official-pages")!.Items;
        var board = pages.Single(i => i.Title.Contains("MSI", StringComparison.Ordinal));
        Assert.Contains("Realtek", board.Reason);
        Assert.Contains("revision", board.Reason);
        Assert.Equal(ActionKind.OpenExternal, Assert.Single(board.Actions).Kind);
        var settings = pages.Single(i => i.Title.StartsWith("Optional updates", StringComparison.Ordinal));
        Assert.Equal("ms-settings:windowsupdate-optionalupdates", settings.Actions[0].Args!["target"]);
        Assert.True(OpenExternalHandler.TryValidate(settings.Actions[0].Args!["target"], out _, out _));
    }

    [Fact]
    public async Task No_windows_update_access_gives_a_friendly_note_not_an_error()
    {
        var report = await Fixtures.ReportAsync(tweak: inv => inv with { Updates = DriverUpdateSearch.Unavailable("Windows Update couldn't be reached. Check your internet connection and try again.") });
        Assert.Null(report.Result.Group(DriverTabs.Updates, "windows-update"));
        Assert.Contains("internet", report.TabNotes[DriverTabs.Updates]);
        Assert.NotNull(report.Result.Group(DriverTabs.Updates, "official-pages")); // the official pages still work
    }

    [Theory]
    [InlineData(unchecked((int)0x8024402C), "internet")]
    [InlineData(unchecked((int)0x80070422), "service is turned off")]
    [InlineData(unchecked((int)0x8024002E), "managed by your organization")]
    [InlineData(unchecked((int)0x80004005), "Settings")]
    public void Windows_update_errors_read_plainly(int hresult, string contains)
        => Assert.Contains(contains, Source.WindowsDriverSource.WindowsUpdateProblem(hresult));
}
