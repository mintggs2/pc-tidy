using System.Reflection;
using PcTidy.Catalog.Insights;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Insights.Model;
using PcTidy.Features.Insights.Source;
using PcTidy.Hosting;
using Xunit;

namespace PcTidy.Features.Insights.Tests;

/// <summary>
/// v3.1 measured info cards (C3-C11, NVMe, thermal): every number carries its hardware, source and date, the read-only
/// detection picks the right PCs (X3D, Zen 4/5, Intel Arc), and nothing is offered to change. All facts are invented.
/// </summary>
public class MeasuredCardTests
{
    private const string DualX3D = "AMD Ryzen 9 7950X3D 16-Core Processor";

    private static async Task<InsightsReport> ScanAsync(TestbedFolder tb, FakeSource source, GpuVendor gpu = GpuVendor.Unknown, params string[] gpuNames)
    {
        using var host = PcTidyHost.CreateSandbox(tb.Root, SandboxProfile.Minimal);
        var ctx = host.CreateScanContext(ScanDepth.Quick);
        var system = new FakeSystem(ctx.System!, gpu, gpuNames);
        return await new InsightsScanner(_ => source, new FixedClock())
            .ScanReportAsync(new ScanContext { Locations = ctx.Locations, Safety = ctx.Safety, InUse = ctx.InUse, Settings = ctx.Settings, Walker = ctx.Walker, System = system }, null, CancellationToken.None);
    }

    private static InsightCard Card(InsightsReport r, string id) => r.Find(id) ?? throw new Xunit.Sdk.XunitException("Missing card " + id);

    private sealed class FakeSystem(ISystemInfo inner, GpuVendor gpu, IReadOnlyList<string> names) : ISystemInfo
    {
        public GpuVendor PrimaryGpu => gpu == GpuVendor.Unknown ? inner.PrimaryGpu : gpu;
        public IReadOnlyList<string> GpuNames => names.Count > 0 ? names : inner.GpuNames;
        public bool IsLaptop => inner.IsLaptop;
        public bool HasBattery => inner.HasBattery;
        public bool IsAdmin => inner.IsAdmin;
        public int WindowsBuild => inner.WindowsBuild;
        public bool RebootPending => inner.RebootPending;
        public IReadOnlyList<DriveDetails> Drives => inner.Drives;
        public IReadOnlySet<string> RunningProcesses() => inner.RunningProcesses();
    }

    /// <summary>A gaming PC where every measured card applies (invented values).</summary>
    private static FakeSource RichPc() => new()
    {
        Cpu = DualX3D,
        PowerPlan = "381b4222-f694-41f0-9685-ff5bb260df2e",
        Identity = new WindowsIdentity
        {
            Build = 22631, Ubr = 4000, ProductName = "Windows 11 Pro",
            InstalledApps = new[] { "NVIDIA App 0.0.0-test", "AMD Chipset Software" },
            OemInformation = new Dictionary<string, string> { ["Model"] = "tiny11 build" },
        },
        Security = new SecurityFacts { MemoryIntegrity = TriState.Yes, SecureBoot = TriState.Yes, TpmPresent = TriState.Yes, TpmVersion = "2.0" },
        Displays = { new DisplayFacts("Test monitor", 60, 144, 2560, 1440) },
        Memory = ReadOutcome<MemoryFacts>.Ok(new MemoryFacts(new[] { new MemoryModule("DIMM A2", 16L << 30, 6000, 6000) }, 4)),
    };

    // ------------------------------------------------------------------ every number has number, hardware, source and date

    [Fact]
    public void Every_measured_number_in_the_catalog_has_a_number_hardware_source_and_date()
    {
        var all = typeof(MeasuredNumbers).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(MeasuredNumber)).Select(f => (f.Name, (MeasuredNumber)f.GetValue(null)!)).ToList();
        Assert.True(all.Count >= 20);
        foreach (var (name, m) in all)
        {
            Assert.True(m.Number.Any(char.IsDigit), name + ": the number");
            Assert.False(string.IsNullOrWhiteSpace(m.Hardware), name + ": the hardware");
            Assert.False(string.IsNullOrWhiteSpace(m.Source), name + ": the source");
            Assert.False(string.IsNullOrWhiteSpace(m.Date), name + ": the date");
            Assert.Contains(m.Hardware, m.Where);
            Assert.Contains(m.Source, m.Where);
            // Never a general claim: no "up to X% FPS" without the measured setup.
            Assert.DoesNotContain("guaranteed", m.Number, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(nameof(MeasuredNumbers.NvidiaAppFilters), "−3 to −9%", "9800X3D + RTX 4060", "Tom's Hardware")]
    [InlineData(nameof(MeasuredNumbers.NvidiaSharpenPlus), "−10%", "RTX 5060 Ti", "PC Games Hardware")]
    [InlineData(nameof(MeasuredNumbers.VbsWorstLows), "−26%", "5800X", "ComputerBase")]
    [InlineData(nameof(MeasuredNumbers.VbsAverageAnd4K), "−2% at 4K", "13900K + RTX 4090", "Tom's Hardware")]
    [InlineData(nameof(MeasuredNumbers.Windows24H2), "+6%", "9700X + RTX 4090", "ComputerBase")]
    [InlineData(nameof(MeasuredNumbers.Windows25H2), "+1 to +5%", "9800X3D + RTX 5090", "Hardware Unboxed")]
    [InlineData(nameof(MeasuredNumbers.ProcessLasso), "+18%", "9950X3D", "NoobFeed")]
    [InlineData(nameof(MeasuredNumbers.X3DMisParked), "−12.1%", "7800X3D", "PC Games Hardware")]
    [InlineData(nameof(MeasuredNumbers.SingleStick), "−12% average, −16% 1% lows", "9700X", "TechSpot")]
    [InlineData(nameof(MeasuredNumbers.SingleStickX3D), "under 3%", "X3D", "Tom's Hardware")]
    [InlineData(nameof(MeasuredNumbers.Xmp7700X), "+17% average, +29% 1% lows", "7700X", "TechSpot")]
    [InlineData(nameof(MeasuredNumbers.XmpX3D), "0 to 4%", "14700K", "TechSpot")]
    [InlineData(nameof(MeasuredNumbers.ReBarAverage), "+2 to 4%", "Ryzen 7 5800X", "TechPowerUp")]
    [InlineData(nameof(MeasuredNumbers.ReBarPerGame), "−4.1%", "7600X + RX 7900 XT", "Club386")]
    [InlineData(nameof(MeasuredNumbers.ReBarArc), "76%", "Arc A770", "TechPowerUp")]
    [InlineData(nameof(MeasuredNumbers.AtlasOldCpu), "+20%", "Ryzen 5 2600X", "Noticias3D")]
    [InlineData(nameof(MeasuredNumbers.AtlasNewCpu), "no significant difference", "Ryzen 9 7900X", "Noticias3D")]
    [InlineData(nameof(MeasuredNumbers.Nano11), "unchanged", "7800X3D", "PC Games Hardware")]
    [InlineData(nameof(MeasuredNumbers.ForspokenLoad), "7.4 s", "SATA", "PCWorld")]
    [InlineData(nameof(MeasuredNumbers.ThermalThrottling), "25 → 60 fps", "Laptop", "blog")]
    public void Numbers_match_the_research_tables(string field, string number, string hardware, string source)
    {
        var m = (MeasuredNumber)typeof(MeasuredNumbers).GetField(field)!.GetValue(null)!;
        Assert.Contains(number, m.Number);
        Assert.Contains(hardware, m.Hardware);
        Assert.Contains(source, m.Source);
    }

    [Fact]
    public async Task Every_measured_card_shows_its_numbers_and_offers_no_change()
    {
        using var tb = new TestbedFolder();
        var r = await ScanAsync(tb, RichPc(), GpuVendor.Nvidia, "NVIDIA GeForce RTX 4070");
        var ids = new[]
        {
            InsightsIds.NvidiaApp, InsightsIds.MemoryIntegrity, InsightsIds.WindowsBuild, InsightsIds.DualCcdX3D, InsightsIds.Thermal,
            InsightsIds.RefreshRate, InsightsIds.Ram, InsightsIds.ResizableBar, InsightsIds.ModifiedWindows,
        };
        foreach (var id in ids)
        {
            var card = Card(r, id);
            Assert.True(card.IsAvailable, id);
            Assert.NotEmpty(card.Measured);
            Assert.All(card.Measured, m => Assert.False(string.IsNullOrWhiteSpace(m.Hardware) || string.IsNullOrWhiteSpace(m.Source) || string.IsNullOrWhiteSpace(m.Date)));
            Assert.Empty(card.Items); // read-only detect + guidance
        }
        // The flat scan result carries the numbers too.
        var result = InsightsScanner.ToScanResult(r, ScanDepth.Quick, TimeSpan.Zero);
        var row = result.AllItems.Single(i => i.Id == InsightsIds.NvidiaApp);
        Assert.Contains(row.Details!, kv => kv.Key == "Measured 1" && kv.Value.Contains("Tom's Hardware, Dec 2024"));
    }

    // ------------------------------------------------------------------ C3 NVIDIA App

    [Fact]
    public async Task Nvidia_app_card_follows_the_installed_app_and_only_advises()
    {
        using var tb = new TestbedFolder();
        var installed = Card(await ScanAsync(tb, RichPc(), GpuVendor.Nvidia), InsightsIds.NvidiaApp);
        Assert.Equal(InsightStatus.Info, installed.Status);
        Assert.Contains(installed.Steps, s => s.Contains("Game Filters and Photo Mode"));
        Assert.Contains("can't read it", installed.Explanation);

        var noApp = Card(await ScanAsync(tb, new FakeSource(), GpuVendor.Nvidia), InsightsIds.NvidiaApp);
        Assert.Equal(InsightStatus.Good, noApp.Status);

        Assert.False(Card(await ScanAsync(tb, new FakeSource(), GpuVendor.Amd), InsightsIds.NvidiaApp).IsAvailable);
    }

    // ------------------------------------------------------------------ C4 Memory integrity

    [Theory]
    [InlineData(TriState.Yes, InsightStatus.Good, "keeping it on")]
    [InlineData(TriState.No, InsightStatus.Info, "off")]
    public async Task Memory_integrity_is_read_only_and_kept_on(TriState state, InsightStatus status, string headline)
    {
        using var tb = new TestbedFolder();
        var card = Card(await ScanAsync(tb, new FakeSource { Security = new SecurityFacts { MemoryIntegrity = state } }), InsightsIds.MemoryIntegrity);
        Assert.Equal(status, card.Status);
        Assert.Contains(headline, card.Headline);
        Assert.Contains("never turns it off", card.Explanation);
        if (state == TriState.No) Assert.Contains("recommends turning it on", card.WhatToDo);
        Assert.Empty(card.Items);
        Assert.Equal(3, card.Measured.Count);
    }

    [Fact]
    public async Task Memory_integrity_unknown_is_not_available()
    {
        using var tb = new TestbedFolder();
        Assert.False(Card(await ScanAsync(tb, new FakeSource()), InsightsIds.MemoryIntegrity).IsAvailable);
    }

    // ------------------------------------------------------------------ C5 Windows build vs processor

    [Theory]
    [InlineData("AMD Ryzen 7 9700X 8-Core Processor", 22631, InsightStatus.Attention)]
    [InlineData("AMD Ryzen 5 7600X 6-Core Processor", 26100, InsightStatus.Good)]
    [InlineData("AMD Ryzen 7 7840HS w/ Radeon 780M Graphics", 26200, InsightStatus.Good)]
    [InlineData("AMD Ryzen 7 9700X 8-Core Processor", 19045, InsightStatus.Attention)]
    [InlineData("Intel(R) Core(TM) i7-14700K", 22631, InsightStatus.Info)]
    [InlineData("AMD Ryzen 7 5800X3D 8-Core Processor", 22631, InsightStatus.Info)]
    [InlineData(null, 26100, InsightStatus.Info)]
    public async Task Windows_build_card_reads_the_processor_family_and_build(string? cpu, int build, InsightStatus status)
    {
        using var tb = new TestbedFolder();
        var src = new FakeSource { Cpu = cpu, Identity = new WindowsIdentity { Build = build, ProductName = "Windows" } };
        var card = Card(await ScanAsync(tb, src), InsightsIds.WindowsBuild);
        Assert.Equal(status, card.Status);
        Assert.Contains(card.Facts, f => f.Label == "Windows" && f.Value.Contains(build.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Contains("Windows Update", card.WhatToDo);
        Assert.Contains(card.Links, l => l.Target == "ms-settings:windowsupdate");
        Assert.Equal(2, card.Measured.Count);
    }

    // ------------------------------------------------------------------ C6 dual-CCD X3D and the detection rules

    [Theory]
    [InlineData("AMD Ryzen 9 7950X3D 16-Core Processor", true)]
    [InlineData("AMD Ryzen 9 7900X3D 12-Core Processor", true)]
    [InlineData("AMD Ryzen 9 9950X3D 16-Core Processor", true)]
    [InlineData("AMD Ryzen 9 9900X3D 12-Core Processor", true)]
    [InlineData("AMD Ryzen 9 7945HX3D with Radeon Graphics", true)]
    [InlineData("AMD Ryzen 7 7800X3D 8-Core Processor", false)]
    [InlineData("AMD Ryzen 7 9800X3D 8-Core Processor", false)]
    [InlineData("AMD Ryzen 7 5800X3D 8-Core Processor", false)]
    [InlineData("AMD Ryzen 9 7950X 16-Core Processor", false)]
    [InlineData("Intel(R) Core(TM) i9-14900K", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Dual_ccd_x3d_is_detected_from_the_cpu_name(string? cpu, bool dual)
    {
        Assert.Equal(dual, HardwareFamilies.IsDualCcdX3D(cpu));
        if (dual) Assert.True(HardwareFamilies.IsX3D(cpu));
    }

    [Theory]
    [InlineData("AMD Ryzen 7 7800X3D 8-Core Processor", true)]
    [InlineData("AMD Ryzen 7 5700X3D 8-Core Processor", true)]
    [InlineData("AMD Ryzen 7 7700X 8-Core Processor", false)]
    [InlineData("Intel(R) Core(TM) i5-13600K", false)]
    public void X3d_is_detected_from_the_cpu_name(string cpu, bool x3d) => Assert.Equal(x3d, HardwareFamilies.IsX3D(cpu));

    [Theory]
    [InlineData("AMD Ryzen 7 9700X 8-Core Processor", CpuFamily.AmdZen5)]
    [InlineData("AMD Ryzen AI 9 HX 370 w/ Radeon 890M", CpuFamily.AmdZen5)]
    [InlineData("AMD Ryzen 9 9955HX 16-Core Processor", CpuFamily.AmdZen5)]
    [InlineData("AMD Ryzen 5 7600X 6-Core Processor", CpuFamily.AmdZen4)]
    [InlineData("AMD Ryzen 7 8700G w/ Radeon 780M Graphics", CpuFamily.AmdZen4)]
    [InlineData("AMD Ryzen 7 7840HS w/ Radeon 780M Graphics", CpuFamily.AmdZen4)]
    [InlineData("AMD Ryzen 5 PRO 7540U w/ Radeon 740M Graphics", CpuFamily.AmdZen4)]
    [InlineData("AMD Ryzen Threadripper 7980X 64-Cores", CpuFamily.AmdZen4)]
    [InlineData("AMD Ryzen 5 7520U with Radeon Graphics", CpuFamily.AmdOther)]
    [InlineData("AMD Ryzen 7 7735HS with Radeon Graphics", CpuFamily.AmdOther)]
    [InlineData("AMD Ryzen 7 5800X3D 8-Core Processor", CpuFamily.AmdOther)]
    [InlineData("Intel(R) Core(TM) i7-14700K", CpuFamily.Intel)]
    [InlineData(null, CpuFamily.Unknown)]
    public void Processor_family_is_read_from_the_brand_string(string? cpu, CpuFamily family) => Assert.Equal(family, HardwareFamilies.Classify(cpu));

    [Fact]
    public async Task Dual_ccd_x3d_card_checks_balanced_and_the_chipset_driver()
    {
        using var tb = new TestbedFolder();
        var good = Card(await ScanAsync(tb, RichPc()), InsightsIds.DualCcdX3D);
        Assert.Equal(InsightStatus.Good, good.Status);
        Assert.Contains("Game Bar", good.WhatToDo);
        Assert.Contains("chipset driver", good.WhatToDo);
        Assert.Contains("Balanced", good.WhatToDo);
        Assert.Contains("Process Lasso is an optional outside tool", good.WhatToDo);
        Assert.Contains(good.Measured, m => m == MeasuredNumbers.ProcessLasso);
        Assert.Contains(good.Measured, m => m == MeasuredNumbers.X3DMisParked);

        var src = RichPc();
        src.PowerPlan = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"; // High performance
        src.Identity = src.Identity with { InstalledApps = Array.Empty<string>() };
        var off = Card(await ScanAsync(tb, src), InsightsIds.DualCcdX3D);
        Assert.Equal(InsightStatus.Attention, off.Status);
        Assert.Contains("isn't Balanced", off.Headline);
        Assert.Contains("chipset driver wasn't found", off.Headline);
        Assert.Empty(off.Items);

        var single = Card(await ScanAsync(tb, new FakeSource { Cpu = "AMD Ryzen 7 9800X3D 8-Core Processor" }), InsightsIds.DualCcdX3D);
        Assert.False(single.IsAvailable);
    }

    // ------------------------------------------------------------------ C8: Intel Arc, memory, refresh rate

    [Theory]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", true)]
    [InlineData("Intel(R) Arc(TM) A380 Graphics", true)]
    [InlineData("Intel(R) Arc(TM) B580 Graphics", true)]
    [InlineData("Intel(R) Arc(TM) Pro A60 Graphics", true)]
    [InlineData("Intel(R) Arc(TM) Graphics", false)]          // built into Core Ultra laptops: not a card
    [InlineData("Intel(R) Arc(TM) 140V GPU (16GB)", false)]
    [InlineData("Intel(R) UHD Graphics 770", false)]
    [InlineData("NVIDIA GeForce RTX 4070", false)]
    public void Intel_arc_cards_are_told_apart_from_built_in_arc_graphics(string gpu, bool card) => Assert.Equal(card, HardwareFamilies.IsDiscreteArc(gpu));

    [Fact]
    public async Task Resizable_bar_card_quotes_the_arc_number_only_for_arc_cards()
    {
        using var tb = new TestbedFolder();
        var arc = Card(await ScanAsync(tb, new FakeSource(), GpuVendor.Intel, "Intel(R) Arc(TM) B580 Graphics"), InsightsIds.ResizableBar);
        Assert.Contains("Intel Arc", arc.Headline);
        Assert.Contains(MeasuredNumbers.ReBarArc, arc.Measured);

        var igpu = Card(await ScanAsync(tb, new FakeSource(), GpuVendor.Intel, "Intel(R) Arc(TM) Graphics"), InsightsIds.ResizableBar);
        Assert.DoesNotContain("Intel Arc", igpu.Headline);
        Assert.DoesNotContain(MeasuredNumbers.ReBarArc, igpu.Measured);
        Assert.Contains(MeasuredNumbers.ReBarPerGame, igpu.Measured);
    }

    [Fact]
    public async Task Single_stick_on_an_x3d_cpu_is_information_with_the_x3d_number()
    {
        using var tb = new TestbedFolder();
        var src = new FakeSource
        {
            Cpu = "AMD Ryzen 7 9800X3D 8-Core Processor",
            Memory = ReadOutcome<MemoryFacts>.Ok(new MemoryFacts(new[] { new MemoryModule("DIMM A2", 32L << 30, 6000, 6000) }, 4)),
        };
        var card = Card(await ScanAsync(tb, src), InsightsIds.Ram);
        Assert.Equal(InsightStatus.Info, card.Status);
        Assert.Contains("X3D", card.Explanation);
        Assert.Contains(MeasuredNumbers.SingleStickX3D, card.Measured);
        Assert.Contains(MeasuredNumbers.Xmp7700X, card.Measured);
    }

    [Fact]
    public async Task A_fast_monitor_left_at_60_hz_names_the_big_step()
    {
        using var tb = new TestbedFolder();
        var card = Card(await ScanAsync(tb, new FakeSource { Displays = { new DisplayFacts("Test monitor", 60, 165, 2560, 1440) } }), InsightsIds.RefreshRate);
        Assert.Equal(InsightStatus.Attention, card.Status);
        Assert.Contains("left at 60 Hz", card.Explanation);
        Assert.Contains("60 to 144 Hz is the big step", card.Explanation);
        Assert.Equal(MeasuredNumbers.RefreshRate, Assert.Single(card.Measured));
    }

    // ------------------------------------------------------------------ C11 debloated Windows

    [Theory]
    [InlineData("tiny11 build", "Tiny11")]
    [InlineData("nano11 by the community", "Nano11")]
    public async Task Debloated_windows_shows_the_measured_numbers(string model, string named)
    {
        using var tb = new TestbedFolder();
        var src = new FakeSource { Identity = new WindowsIdentity { Build = 26100, OemInformation = new Dictionary<string, string> { ["Model"] = model } } };
        var card = Card(await ScanAsync(tb, src), InsightsIds.ModifiedWindows);
        Assert.Equal(InsightStatus.Attention, card.Status);
        Assert.Contains(named, card.Headline);
        Assert.Equal(new[] { MeasuredNumbers.AtlasOldCpu, MeasuredNumbers.AtlasNewCpu, MeasuredNumbers.Nano11 }, card.Measured);
    }

    // ------------------------------------------------------------------ thermal

    [Fact]
    public async Task Thermal_card_is_guidance_with_the_one_laptop_number()
    {
        using var tb = new TestbedFolder();
        var card = Card(await ScanAsync(tb, new FakeSource()), InsightsIds.Thermal);
        Assert.Equal(InsightStatus.Info, card.Status);
        Assert.Contains("without installing a driver", card.Explanation);
        Assert.Contains("N=1", Assert.Single(card.Measured).Number);
        Assert.Empty(card.Items);
    }
}
