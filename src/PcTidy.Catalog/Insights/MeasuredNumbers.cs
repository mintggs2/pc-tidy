namespace PcTidy.Catalog.Insights;

/// <summary>
/// One measured result, copied from the research ("FPS boost tweaks - do they work" §4 and its appendix pages F1-F6):
/// the number, the hardware it was measured on, who measured it and when. A card shows it as "measured on other PCs",
/// never as a promise for this one. "date not stated" means the research didn't record a date for that source.
/// </summary>
public sealed record MeasuredNumber(string Number, string Hardware, string Source, string Date)
{
    /// <summary>"Hardware · Source, Date" (the line under the number).</summary>
    public string Where => $"{Hardware} · {Source}, {Date}";
}

/// <summary>Every measured number the Insights cards quote (data only; v3.1 build list C3-C11 and the WORKS rows marked detect + info).</summary>
public static class MeasuredNumbers
{
    public const string NoDate = "date not stated";

    // ---- C3: NVIDIA App overlay (F6 "NVIDIA App overlay", F4 "Image Sharpening and Game Filters")
    public static readonly MeasuredNumber NvidiaAppFilters = new(
        "−3 to −9% average FPS in 4 of 5 games with the NVIDIA App's default overlay (Game Filters and Photo Mode on)",
        "Ryzen 7 9800X3D + RTX 4060, Windows 11 24H2, 3 runs per game", "Tom's Hardware", "Dec 2024");
    public static readonly MeasuredNumber NvidiaSharpenPlus = new(
        "The Sharpen+ filter: about −10% (−8 FPS) in Forza Horizon 6",
        "Ryzen 7 9800X3D + RTX 5060 Ti, 1440p", "PC Games Hardware", "2026");

    // ---- C4: Memory integrity / VBS (F2 "Memory Integrity / VBS / HVCI off")
    public static readonly MeasuredNumber VbsCpuBound = new(
        "Memory integrity on: −6% average, −8% 1% lows over 14 games at 720p (held back by the processor)",
        "Ryzen 7 5800X + RX 7900 XTX, Windows 11 22H2", "ComputerBase", "Mar 2023");
    public static readonly MeasuredNumber VbsWorstLows = new(
        "Worst game: −26% on 1% lows (Diablo II: Resurrected, 720p)",
        "Ryzen 7 5800X + RX 7900 XTX, Windows 11 22H2", "ComputerBase", "Mar 2023");
    public static readonly MeasuredNumber VbsAverageAnd4K = new(
        "About −5% average over 15 games; about −2% at 4K",
        "Core i9-13900K + RTX 4090, Windows 11", "Tom's Hardware (via PC Games Hardware)", NoDate);

    // ---- C5: Windows build vs processor (F2 "Windows 10 vs Windows 11 vs 24H2 / 25H2")
    public static readonly MeasuredNumber Windows24H2 = new(
        "Windows 11 24H2: about +6% average vs 23H2 over 7 games at 720p",
        "Ryzen 7 9700X + RTX 4090, 3 runs per game", "ComputerBase", "Sep 2024");
    public static readonly MeasuredNumber Windows25H2 = new(
        "Windows 11 25H2: about +1 to +5% vs Windows 10 22H2 over 14 games",
        "Ryzen 7 9800X3D + RTX 5090", "Hardware Unboxed (via PC Games Hardware and Notebookcheck)", "Jan 2026");

    // ---- C6: dual-CCD X3D (F6 "Process Lasso", F1 core parking)
    public static readonly MeasuredNumber ProcessLasso = new(
        "Process Lasso instead of AMD's chipset-driver core parking: +18% average, +10% 1% lows (Forza Horizon 5, 1080p); about the same when the graphics card was the limit",
        "Ryzen 9 9950X3D + RTX 5080 (one outlet)", "NoobFeed", NoDate);
    public static readonly MeasuredNumber X3DMisParked = new(
        "Cores parked wrongly after a processor swap without a clean Windows install: −12.1% (Cyberpunk 2077, 720p, 215 → 189 fps)",
        "Ryzen 7 7800X3D (swapped in for a 7950X3D)", "PC Games Hardware", NoDate);

    // ---- C8: refresh rate, memory, Resizable BAR, XMP/EXPO (F5, F4)
    public static readonly MeasuredNumber RefreshRate = new(
        "60 → 144 Hz: input-to-screen latency fell in every game tested (the big step); 144 → 360 Hz gained much less",
        "PCs measured with NVIDIA's Reflex Latency Analyzer", "PCWorld", NoDate);
    public static readonly MeasuredNumber SingleStick = new(
        "One memory stick instead of two: −12% average, −16% 1% lows over 13 games at 1080p",
        "Ryzen 7 9700X, DDR5-6000, 1 × 16 GB vs 2 × 8 GB", "TechSpot (via NoobFeed)", "Dec 2025");
    public static readonly MeasuredNumber SingleStickX3D = new(
        "X3D processors lost under 3% on one stick",
        "Ryzen X3D processors with DDR5", "Tom's Hardware", NoDate);
    public static readonly MeasuredNumber Xmp7700X = new(
        "Memory profile (XMP/EXPO) on: +17% average, +29% 1% lows (Hogwarts Legacy)",
        "Ryzen 7 7700X", "TechSpot (snippet)", NoDate);
    public static readonly MeasuredNumber XmpX3D = new(
        "Memory profile (XMP/EXPO) on: about 0 to 4% over 13 games",
        "Ryzen 7 7800X3D and Core i7-14700K", "TechSpot (via NoobFeed)", NoDate);
    public static readonly MeasuredNumber ReBarAverage = new(
        "Resizable BAR on: +2 to 4% average in games the driver supports (22 games)",
        "RTX 3060 Ti to RTX 3090 + Ryzen 7 5800X", "TechPowerUp", "2021");
    public static readonly MeasuredNumber ReBarPerGame = new(
        "Per game from −4.1% (Batman: Arkham City) to +17.5% (Atomic Heart)",
        "Ryzen 5 7600X + RX 7900 XT, 1440p", "Club386", "8 Jun 2026");
    public static readonly MeasuredNumber ReBarArc = new(
        "Resizable BAR off: 77% of the Resizable-BAR-on speed at 1080p, 76% at 1440p",
        "Intel Arc A770", "TechPowerUp", NoDate);

    // ---- C11: debloated Windows (F2 "Debloated Windows", F6 "Tiny11 / Nano11")
    public static readonly MeasuredNumber AtlasOldCpu = new(
        "AtlasOS vs Windows 11: +20% average (Cyberpunk 2077, 1080p Ultra, 119 vs 99 fps)",
        "Ryzen 5 2600X + RX 9060 XT", "NJ Tech (via Noticias3D)", NoDate);
    public static readonly MeasuredNumber AtlasNewCpu = new(
        "AtlasOS vs Windows 11: no significant difference, some small losses",
        "Ryzen 9 7900X + RX 9060 XT", "NJ Tech (via Noticias3D)", NoDate);
    public static readonly MeasuredNumber Nano11 = new(
        "Nano11 vs Windows 11: average FPS unchanged in several games at 1080p",
        "Ryzen 7 7800X3D + RX 9070", "Ancient Gameplays (via PC Games Hardware)", "11 May 2025");

    // ---- WORKS rows marked detect + info (F5)
    public static readonly MeasuredNumber ForspokenLoad = new(
        "Forspoken scene load: 7.4 s from a SATA SSD vs 1.9 s from an NVMe SSD",
        "Samsung 860 EVO (SATA) vs an NVMe SSD", "PCWorld", NoDate);
    public static readonly MeasuredNumber HddLoad = new(
        "A hard disk loaded Forspoken more than 10 times slower than NVMe",
        "Hard disk vs PCIe 3.0, 4.0 and 5.0 NVMe SSDs", "Notebookcheck (secondary)", NoDate);
    public static readonly MeasuredNumber ThermalThrottling = new(
        "Fans cleaned, new paste and pads: 25 → 60 fps in Unigine Heaven, 98 °C → 60 °C (one laptop, N=1)",
        "Laptop with RTX 3070 Ti + 12th-gen Core i9", "Developer blog", NoDate);
}
