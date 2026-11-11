using System.Windows;
using System.Windows.Controls;
using PcTidy.Features.Insights.Source;
using PcTidy.Features.Insights.SystemInfo;
using Xunit;

namespace PcTidy.Features.Insights.Tests;

/// <summary>
/// System info (layout spec §4.5): six categories, links to the matching cards, and masking: no serial numbers, MAC
/// addresses, UUIDs, product keys or user names, even when a value from Windows contains one. All facts are invented.
/// </summary>
public class SystemInfoTests
{
    private const string UserName = "Quillon";
    private const string MachineName = "QUILL-DESK-7";

    /// <summary>Invented hardware, with private text planted in places Windows could put it.</summary>
    private static SystemInfoReport Build() => SystemInfoBuilder.Build(
        new InventoryFacts
        {
            Cpu = new CpuInventory("AMD Ryzen 9 9950X3D 16-Core Processor", 16, 32, 4300, 16384, 131072, "AM5"),
            Board = new BoardInventory("To Be Filled By O.E.M.", "QUILL-DESK-7 build", "Example Boards", "X999 GAMING {3F2504E0-4F89-11D3-9A0C-0305E82C3301}",
                "Example Firmware", "1.20 key ABCDE-FGHIJ-KLMNO-PQRST-UVWXY", new DateTime(2026, 3, 1)),
            Memory = new[]
            {
                new MemoryInventory("DIMM A2", 16L << 30, 6000, 6000, "Example RAM", "EX5-6000C30"),
                new MemoryInventory("DIMM B2", 16L << 30, 6000, 6000, "Example RAM", "EX5-6000C30"),
            },
            MemorySlots = 4,
            Gpus = new[] { new GpuInventory("NVIDIA GeForce RTX 9090 Example", "32.0.15.9999", new DateTime(2026, 9, 1), 16L << 30) },
            Disks = new[]
            {
                new DiskInventory("Example NVMe 2TB", 2_000_398_934_016, DiskMedia.Ssd, "NVMe"),
                new DiskInventory(@"Archive disk from C:\Users\Quillon\Documents", 4_000_787_030_016, DiskMedia.Hdd, "SATA"),
            },
            Network = new[]
            {
                new NetworkInventory("Example Wi-Fi 7 adapter 0A-1B-2C-3D-4E-5F", NetworkKind.WiFi, 2_400_000_000, true),
                new NetworkInventory("Example 2.5GbE Ethernet", NetworkKind.Ethernet, 0, false),
            },
        },
        new SecurityFacts { SecureBoot = TriState.Yes, TpmPresent = TriState.Yes, TpmVersion = "2.0", MemoryIntegrity = TriState.Yes },
        new[] { new DisplayFacts("Example monitor", 144, 240, 2560, 1440) },
        new WindowsIdentity { Build = 26200, Ubr = 7000, EditionId = "Professional" },
        cpuName: null, gpuNamesFallback: Array.Empty<string>(), privateWords: new[] { UserName, MachineName });

    [Fact]
    public void Six_categories_in_order_with_label_value_rows()
    {
        var r = Build();
        Assert.Equal(new[] { "cpu", "mainboard", "memory", "graphics", "storage", "network" }, r.Categories.Select(c => c.Id));
        Assert.Equal(new[] { "CPU", "Mainboard", "Memory", "Graphics", "Storage", "Network" }, r.Categories.Select(c => c.Title));
        Assert.All(r.Categories, c => Assert.NotEmpty(c.Rows));
        Assert.Contains("25H2", r.Windows);
        Assert.Contains(r.Find("cpu")!.Rows, x => x.Label == "Threads" && x.Value == "32");
        Assert.Contains(r.Find("memory")!.Rows, x => x.Label == "Installed" && x.Value.Contains("2 of 4 slots"));
        Assert.Contains(r.Find("storage")!.Rows, x => x.Value.Contains("NVMe SSD"));
        Assert.Contains(r.Find("graphics")!.Rows, x => x.Value.Contains("240 Hz"));
        Assert.Contains(r.Find("mainboard")!.Rows, x => x.Label == "PC maker" && x.Value == "Not set by the maker");
    }

    [Fact]
    public void Categories_link_to_the_matching_cards()
    {
        var r = Build();
        Assert.Contains(r.Find("memory")!.Links, l => l.CardId == InsightsIds.Ram);
        Assert.Contains(r.Find("graphics")!.Links, l => l.CardId == InsightsIds.ResizableBar);
        Assert.Contains(r.Find("graphics")!.Links, l => l.CardId == InsightsIds.RefreshRate);
        Assert.Contains(r.Find("graphics")!.Links, l => l.CardId == InsightsIds.NvidiaApp);
        Assert.Contains(r.Find("mainboard")!.Links, l => l.CardId == InsightsIds.Vanguard); // Secure Boot / TPM
        Assert.Contains(r.Find("cpu")!.Links, l => l.CardId == InsightsIds.DualCcdX3D);
        Assert.Contains(r.Find("cpu")!.Rows, x => x.Label == "3D V-Cache" && x.Value.Contains("two core groups"));
    }

    [Fact]
    public void No_serial_mac_uuid_product_key_or_user_name_is_ever_shown()
    {
        var text = string.Join("\n", Build().Categories.SelectMany(c => c.Rows).Select(x => x.Label + " = " + x.Value));
        Assert.DoesNotContain(UserName, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(MachineName, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("0A-1B-2C-3D-4E-5F", text);
        Assert.DoesNotContain("3F2504E0-4F89-11D3-9A0C-0305E82C3301", text);
        Assert.DoesNotContain("ABCDE-FGHIJ-KLMNO-PQRST-UVWXY", text);
        Assert.Contains(@"C:\Users\…", text); // the path stays readable, the name doesn't
        Assert.Contains(SystemInfoPrivacy.Hidden, text);
        // The real values still show.
        Assert.Contains("32.0.15.9999", text);
        Assert.Contains("EX5-6000C30", text);
    }

    [Theory]
    [InlineData("Serial number")]
    [InlineData("MAC address")]
    [InlineData("UUID")]
    [InlineData("Product key")]
    [InlineData("Asset tag")]
    [InlineData("User name")]
    [InlineData("Computer name")]
    [InlineData("IP address")]
    public void Rows_with_a_private_label_are_dropped(string label)
    {
        var rows = SystemInfoPrivacy.CleanRows(new[] { new SystemInfoRow(label, "X1Y2Z3"), new SystemInfoRow("Cores", "8") }, Array.Empty<string>());
        Assert.Equal("Cores", Assert.Single(rows).Label);
    }

    [Theory]
    [InlineData(@"D:\Users\Someone\AppData\Local\x", @"D:\Users\…\AppData\Local\x")]
    [InlineData("aa:bb:cc:dd:ee:ff", "(hidden)")]
    [InlineData("{0F1E2D3C-4B5A-6978-8796-A5B4C3D2E1F0}", "(hidden)")]
    [InlineData("33333-44444-55555-66666-77777", "(hidden)")]
    [InlineData("Driver 31.0.15.5222", "Driver 31.0.15.5222")]
    public void Values_are_masked(string value, string expected) => Assert.Equal(expected, SystemInfoPrivacy.Clean(value, Array.Empty<string>()));

    [Fact]
    public void Nothing_read_shows_a_plain_note_in_every_category()
    {
        var r = SystemInfoBuilder.Build(new InventoryFacts(), new SecurityFacts(), Array.Empty<DisplayFacts>(), new WindowsIdentity(), null, Array.Empty<string>(), Array.Empty<string>());
        Assert.Equal(6, r.Categories.Count);
        Assert.Contains(r.Find("network")!.Rows, x => x.Value.Contains("didn't report"));
        Assert.Contains(r.Find("mainboard")!.Rows, x => x.Label == "Secure Boot" && x.Value == "Unknown");
    }

    [Fact]
    public void View_and_modal_lay_out_at_1000px_with_one_close_button()
    {
        UiThread.Run(() =>
        {
            {
                var view = new SystemInfoView(Build());
                Assert.Equal(6, view.Categories.Count);
                Assert.True(view.Select("memory"));
                Assert.False(view.Select("nope"));
                var window = SystemInfoWindow.Create(null, view, out var picked);
                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(1000, 740));
                content.Arrange(new Rect(0, 0, 1000, 740));
                content.UpdateLayout();
                Assert.True(view.ActualWidth <= 920);
                Assert.True(view.ActualWidth > 600);
                Assert.Null(picked());
                Assert.Equal(WindowStyle.None, window.WindowStyle);
                Assert.True(window.AllowsTransparency);
                // One Close button belongs to the window; the others are card links inside the view.
                var windowButtons = Descendants(content).OfType<Wpf.Ui.Controls.Button>().Where(b => !IsInside(b, view)).ToList();
                Assert.Equal("Close", Assert.Single(windowButtons).Content);
                Assert.All(Descendants(view).OfType<Wpf.Ui.Controls.Button>(),
                    b => Assert.False(string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(b))));
                window.Close();
            }
        });
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    private static bool IsInside(DependencyObject element, DependencyObject container)
    {
        for (var d = LogicalTreeHelper.GetParent(element); d is not null; d = LogicalTreeHelper.GetParent(d))
            if (ReferenceEquals(d, container)) return true;
        return false;
    }
}
