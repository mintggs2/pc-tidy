using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using PcTidy.Catalog.Tweaks;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Insights.Source;
using PcTidy.Features.Insights.SystemInfo;
using PcTidy.Features.Measure.Import;
using PcTidy.Features.Tweaks.Reading;
using PcTidy.UI.Flow;
using CoreHive = PcTidy.Core.Model.RegistryHive;

namespace PcTidy.App.Composition.Demo;

/// <summary>
/// The v3.1 pages in demo mode (Tweaks, Measure, Games › Optimize, PC care's Lag check and Fixes, System info), fed with
/// invented data only: a made-up registry for Tweaks, generated frame times for Measure, made-up hardware for System info.
/// Nothing is read from this PC's settings or hardware, and nothing is written anywhere.
/// </summary>
public static class DemoV31
{
    /// <summary>Where the demo's Tweaks plans would point (a test area; the demo executor runs nothing anyway).</summary>
    public const string RegistryRoot = @"Software\PCTidyTest\tweaks\demo";

    /// <summary>How many offered tweaks the invented registry has switched on.</summary>
    public const int TweaksOn = 4;

    // ---------------- Tweaks ----------------

    private static readonly Lazy<DemoRegistryReader> Registry = new(() => DemoRegistryReader.WithTweaksOn(TweaksOn));

    /// <summary>The readers the demo Tweaks page uses: the invented registry, fixed probes, the demo's own (empty) folders.</summary>
    public static TweakEnvironment TweakEnvironmentFor(IKnownLocations locations) => new()
    {
        Registry = Registry.Value,
        Probe = FixedSystemProbe.Unknown,
        Boot = new FixedBootConfigReader(BootConfigSnapshot.Empty),
        Apps = new FixedAppxInventory(),
        Tasks = new FixedTaskReader(),
        HostsPath = TweakEnvironment.HostsFileIn(locations.WindowsDir),
        LocalAppData = locations.LocalAppData,
        WindowsDir = locations.WindowsDir,
        WindowsBuild = 26100,
        IsLaptop = false,
        Gpu = GpuVendor.Unknown,
        IsRealPc = false,
    };

    /// <summary>The real Tweaks section on the invented registry (its page, its rail counter).</summary>
    public static PcTidy.Features.Tweaks.TweaksModule TweaksModule()
    {
        var locations = new SandboxKnownLocations(DemoComposition.DemoRoot);
        return new PcTidy.Features.Tweaks.TweaksModule(ctx => TweakEnvironmentFor(ctx.Locations), () => TweakEnvironmentFor(locations));
    }

    // ---------------- Measure ----------------

    /// <summary>The demo's Measure section: saved comparisons would go to the demo's own temp folder (none are saved in screenshots).</summary>
    public static PcTidy.Features.Measure.MeasureModule MeasureModule()
        => new(Path.Combine(DemoComposition.DemoRoot, "Data", "measure"));

    /// <summary>
    /// An invented frame-time recording: <paramref name="frames"/> frames around <paramref name="frameMs"/> with a little
    /// noise and a rare hitch, from a made-up game. The same seed gives the same run.
    /// </summary>
    public static RecordingFile Recording(string fileName, double frameMs, int seed, int frames = 7200)
    {
        var random = new Random(seed);
        var present = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            // Sum of three uniforms: a soft bell around the frame time, about ±6 %.
            double noise = (random.NextDouble() + random.NextDouble() + random.NextDouble() - 1.5) * 0.08;
            double ms = frameMs * (1 + noise);
            if (i % 400 == 200) ms += 7; // the same small hitch at the same spot of the scene, every run
            present[i] = (float)Math.Max(1, ms);
        }
        return new RecordingFile
        {
            FileName = fileName,
            Format = RecordingFormat.PresentMonV1Csv,
            Processes = new[]
            {
                new ProcessCapture { ProcessName = "SampleGame.exe", PresentMs = present, DisplayMs = present.ToArray() },
            },
        };
    }

    /// <summary>Fills a Measure page with an invented A/B comparison: three runs before, three after (all made up).</summary>
    public static void FillComparison(PcTidy.Features.Measure.UI.MeasureViewModel vm)
    {
        for (int i = 0; i < 3; i++) vm.AddRun(PcTidy.Features.Measure.UI.RunSet.Before, Recording($"sample-before-{i + 1}.csv", 9.0 + i * 0.03, 100 + i));
        for (int i = 0; i < 3; i++) vm.AddRun(PcTidy.Features.Measure.UI.RunSet.After, Recording($"sample-after-{i + 1}.csv", 8.55 + i * 0.03, 200 + i));
        vm.Title = "Sample game: after the Tweaks run";
        foreach (var c in vm.Conditions.Take(3)) c.Same = true;
    }

    // ---------------- System info ----------------

    /// <summary>An invented PC for the System info view (no name, serial or address of any real PC).</summary>
    public static SystemInfoReport SystemInfoReport()
    {
        const long GB = 1L << 30;
        var inventory = new InventoryFacts
        {
            Cpu = new CpuInventory("Sample 8-core processor", 8, 16, 4700, 8192, 32768, "Socket S1"),
            Board = new BoardInventory("Sample PC maker", "Sample Tower 1", "Sample board maker", "Board X1", "Sample firmware", "1.20", new DateTime(2025, 3, 14)),
            Memory = new[]
            {
                new MemoryInventory("Slot A2", 16 * GB, 6000, 6000, "Sample memory", "SM-16G-6000"),
                new MemoryInventory("Slot B2", 16 * GB, 6000, 6000, "Sample memory", "SM-16G-6000"),
            },
            MemorySlots = 4,
            Gpus = new[] { new GpuInventory("Sample graphics card 12 GB", "31.0.15.1000", new DateTime(2026, 5, 2), 12 * GB) },
            Disks = new[]
            {
                new DiskInventory("Sample NVMe SSD 1 TB", 1000L * 1000 * 1000 * 1000, DiskMedia.Ssd, "NVMe"),
                new DiskInventory("Sample hard disk 2 TB", 2000L * 1000 * 1000 * 1000, DiskMedia.Hdd, "SATA"),
            },
            Network = new[]
            {
                new NetworkInventory("Sample 2.5 GbE adapter", NetworkKind.Ethernet, 2_500_000_000, true),
                new NetworkInventory("Sample Wi-Fi adapter", NetworkKind.WiFi, 0, false),
            },
        };
        var security = new SecurityFacts { SecureBoot = TriState.Yes, TpmPresent = TriState.Yes, TpmVersion = "2.0", MemoryIntegrity = TriState.Yes };
        var displays = new[] { new DisplayFacts("Sample monitor", 144, 165, 2560, 1440) };
        var windows = new WindowsIdentity { Build = 26100, Ubr = 1000, EditionId = "Professional" };
        return SystemInfoBuilder.Build(inventory, security, displays, windows, null, Array.Empty<string>(), Array.Empty<string>());
    }

    /// <summary>
    /// The System info view showing the invented PC. Insights builds a view from a ready report only internally (for its tests),
    /// so it is reached by reflection; if that ever fails, the normal view is used, which in demo mode reads the practice PC's
    /// (empty) data, never this PC.
    /// </summary>
    public static UserControl SystemInfoView(IServiceProvider services)
    {
        var type = typeof(PcTidy.Features.Insights.SystemInfoView);
        try
        {
            var ctor = type.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(SystemInfoReport) }, null);
            if (ctor?.Invoke(new object[] { SystemInfoReport() }) is UserControl view) return view;
        }
        catch (Exception ex) when (ex is TargetInvocationException or MemberAccessException or NotSupportedException) { /* fall back below */ }
        return new PcTidy.Features.Insights.SystemInfoView(services);
    }
}

/// <summary>Demo mode's System info: the invented PC in a plain modal with one Close button. Nothing is read or changed.</summary>
public sealed class DemoSystemInfoOpener : ISystemInfoOpener
{
    /// <summary>The window on screen (screenshots capture and close it).</summary>
    public static Window? Current { get; private set; }

    public void Show(Window owner, IServiceProvider services)
    {
        var window = Create(owner, services);
        Current = window;
        try { window.ShowDialog(); }
        finally { Current = null; }
    }

    /// <summary>The modal (not shown): the view, and Close (Esc works too).</summary>
    public static Window Create(Window? owner, IServiceProvider services)
    {
        var close = new Wpf.Ui.Controls.Button
        {
            Content = "Close", Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, MinWidth = 120, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0), IsDefault = true, IsCancel = true,
        };
        System.Windows.Automation.AutomationProperties.SetName(close, "Close System info");
        var grid = new Grid { Margin = new Thickness(24) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(DemoV31.SystemInfoView(services));
        Grid.SetRow(close, 1);
        grid.Children.Add(close);
        var frame = new Border { BorderThickness = new Thickness(1), Child = grid };
        frame.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        frame.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        var window = new Window
        {
            Title = "System info", Content = frame, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            Width = Math.Max(640, (owner?.ActualWidth ?? 1024) - 120), Height = Math.Max(480, (owner?.ActualHeight ?? 700) - 120),
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner, Owner = owner,
        };
        close.Click += (_, _) => window.Close();
        return window;
    }
}

/// <summary>
/// An invented registry for the demo's Tweaks page: Windows defaults everywhere (nothing set), except <see cref="On"/> offered
/// tweaks that are switched on. Logical keys; reads only, from memory.
/// </summary>
public sealed class DemoRegistryReader : IRegistryReader
{
    private readonly Dictionary<(CoreHive Hive, string Key), Dictionary<string, RegValue>> _keys = new();

    public RegistryRedirect Redirect { get; } = RegistryRedirect.ToTestRoot(DemoV31.RegistryRoot);

    /// <summary>The tweaks switched on in this invented registry.</summary>
    public IReadOnlyList<string> On { get; private set; } = Array.Empty<string>();

    /// <summary>Switches on the first <paramref name="count"/> offered, everyday tweaks that are plain registry values.</summary>
    public static DemoRegistryReader WithTweaksOn(int count)
    {
        var reader = new DemoRegistryReader();
        var picks = TweakCatalog.All
            .Where(t => t.IsOffered && !t.IsExpertOnly && !t.IsManagedPolicy && t.Applies == TweakApplicability.Everywhere
                        && t.ActionableChanges.Any() && t.ActionableChanges.All(c => c is RegistryValueChange))
            .GroupBy(t => t.Group).Select(g => g.First()) // one per group, so the page shows variety
            .Take(count).ToList();
        foreach (var t in picks)
            foreach (var c in t.ActionableChanges.OfType<RegistryValueChange>())
                reader.Set(c.Hive, c.KeyPath, c.ValueName, c.Kind, c.Data);
        reader.On = picks.Select(t => t.Id).ToList();
        return reader;
    }

    public void Set(CoreHive hive, string keyPath, string valueName, RegistryKind kind, string data)
    {
        var key = (hive, Norm(keyPath));
        if (!_keys.TryGetValue(key, out var values)) _keys[key] = values = new Dictionary<string, RegValue>(StringComparer.OrdinalIgnoreCase);
        values[valueName] = new RegValue(kind, data);
    }

    private static string Norm(string keyPath) => keyPath.Trim('\\').ToLowerInvariant();

    public bool KeyExists(CoreHive hive, string keyPath)
    {
        var k = Norm(keyPath);
        return _keys.Keys.Any(x => x.Hive == hive && (x.Key == k || x.Key.StartsWith(k + "\\", StringComparison.Ordinal)));
    }

    public RegValue? Read(CoreHive hive, string keyPath, string valueName)
        => _keys.TryGetValue((hive, Norm(keyPath)), out var values) && values.TryGetValue(valueName, out var v) ? v : null;

    public IReadOnlyList<string> ValueNames(CoreHive hive, string keyPath)
        => _keys.TryGetValue((hive, Norm(keyPath)), out var values) ? values.Keys.ToList() : Array.Empty<string>();

    public IReadOnlyList<string> SubKeyNames(CoreHive hive, string keyPath)
    {
        var k = Norm(keyPath) + "\\";
        return _keys.Keys.Where(x => x.Hive == hive && x.Key.StartsWith(k, StringComparison.Ordinal))
                    .Select(x => x.Key[k.Length..].Split('\\')[0]).Distinct().ToList();
    }
}
