using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PcTidy.Core.Model;
using PcTidy.Features.Drivers.Scanning;
using PcTidy.Features.Drivers.Source;
using Wpf.Ui.Controls;

namespace PcTidy.Features.Drivers.Tests;

public class ModuleTests
{
    [Fact]
    public void Module_metadata()
    {
        var m = new DriversModule();
        Assert.Equal("drivers", m.Id);
        Assert.Equal("Drivers", m.Title);
        Assert.Equal(NavGroup.SpeedUp, m.Group);
        Assert.Equal("DeveloperBoard24", m.Icon);
        Assert.Equal(Release.V2, m.Release);
        var scanner = Assert.Single(m.Scanners);
        Assert.False(scanner.IncludedInOptimizeAll);
        Assert.Equal("drivers", scanner.FeatureId);
    }

    /// <summary>The icon must be a real symbol AND draw pixels with WPF-UI's icon font (an unknown glyph renders blank).</summary>
    [Fact]
    public void Module_icon_renders()
    {
        Assert.True(Enum.TryParse<SymbolRegular>(new DriversModule().Icon, out var symbol));
        int drawn = 0, blank = -1;
        var thread = new Thread(() =>
        {
            drawn = InkPixels(symbol);
            blank = InkPixels(SymbolRegular.Empty);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Equal(0, blank);
        Assert.True(drawn > 40, $"DeveloperBoard24 drew {drawn} pixels");
    }

    private static int InkPixels(SymbolRegular symbol)
    {
        if (Application.Current is null) _ = new Application(); // registers the pack:// scheme
        var icon = new SymbolIcon
        {
            Symbol = symbol,
            FontSize = 48,
            Foreground = Brushes.Black,
            // The same font WPF-UI's SymbolIcon style uses.
            FontFamily = new FontFamily(new Uri("pack://application:,,,/Wpf.Ui;component/Resources/Fonts/"), "./#FluentSystemIcons-Regular"),
        };
        icon.Measure(new Size(64, 64));
        icon.Arrange(new Rect(0, 0, 64, 64));
        icon.UpdateLayout();
        var bmp = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(icon);
        var pixels = new byte[64 * 64 * 4];
        bmp.CopyPixels(pixels, 64 * 4, 0);
        int ink = 0;
        for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] > 0) ink++;
        return ink;
    }

    [Fact]
    public void Pnputil_reader_can_only_run_enumerations()
    {
        var commands = typeof(DriverScanner).Assembly.GetType("PcTidy.Features.Drivers.Source.PnpUtilReader")!
            .GetField("Commands", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null) as System.Collections.IDictionary;
        Assert.NotNull(commands);
        foreach (string[] args in commands!.Values)
        {
            Assert.StartsWith("/enum-", args[0]);
            Assert.DoesNotContain(args, a => Regex.IsMatch(a, @"^/(add|delete|export|remove|disable|enable|restart|scan)-", RegexOptions.IgnoreCase));
            Assert.DoesNotContain(args, a => a.Equals("/force", StringComparison.OrdinalIgnoreCase) || a.Equals("/output-file", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Feature-side guard (on top of the solution-wide boundary test): only the pnputil reader may start a process,
    /// and no source line outside comments mentions a pnputil change switch.
    /// </summary>
    [Fact]
    public void Feature_source_starts_no_process_except_the_read_only_reader()
    {
        var src = Path.Combine(FindRepoRoot(), "src", "PcTidy.Features.Drivers");
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains(@"\obj\") && !f.Contains(@"\bin\")))
        {
            var code = File.ReadAllLines(file).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)).ToList();
            bool startsProcess = code.Any(l => l.Contains("Process.Start", StringComparison.Ordinal));
            Assert.True(!startsProcess || Path.GetFileName(file) == "PnpUtilReader.cs", file);
            Assert.DoesNotContain(code, l => Regex.IsMatch(l, "\"/(force|uninstall|delete-driver|remove-device|add-driver|export-driver)\"", RegexOptions.IgnoreCase));
        }
    }

    private static string FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "PcTidy.sln"))) d = d.Parent;
        return d!.FullName;
    }
}
