namespace PcTidy.Catalog.Games;

/// <summary>Where a guidance card is shown.</summary>
[Flags]
public enum CardPlace
{
    None = 0,
    /// <summary>Games → Tips.</summary>
    GamesTips = 1,
    /// <summary>Games → Cleanup (the shader-cache warning).</summary>
    GamesCleanup = 2,
    /// <summary>The Game boost page.</summary>
    Boost = 4,
}

/// <summary>
/// A text-only guidance card (comparison §8.2). It explains; it never applies anything. Every card carries at least one
/// <see cref="Measurement"/> with its hardware and source, so no card is a bare "+X% FPS" claim.
/// </summary>
/// <param name="Id">Research card id, e.g. "C1".</param>
/// <param name="Points">Short plain-English steps or facts, shown as a list.</param>
public sealed record GuidanceCard(string Id, string Title, string Summary, IReadOnlyList<string> Points,
    IReadOnlyList<Measurement> Measured, CardPlace Places);

/// <summary>Cards C1, C2, C7, C9 and C12 from the comparison build list (§8.2), with the FPS research numbers.</summary>
public static class GuidanceCards
{
    public static readonly GuidanceCard CapYourFps = new("C1", "Cap your FPS",
        "A frame cap just below what your GPU can reach lowers input lag more than any driver latency mode.",
        new[]
        {
            "Use the game's own frame limiter first. Use RTSS or the GPU driver only if the game has none.",
            "Cap a little below the FPS your GPU reaches, so it is never at 99% load.",
            "With G-Sync or FreeSync (VRR): cap at your refresh rate minus 3, for example 141 on a 144 Hz screen.",
        },
        new[] { FpsMeasurements.CapBelowGpuLimit, FpsMeasurements.InGameVsExternalCap, FpsMeasurements.VrrCap },
        CardPlace.GamesTips | CardPlace.Boost);

    public static readonly GuidanceCard WhyIsMyGameSlow = new("C2", "Why is my game slow?",
        "First find out what is holding the game back: the graphics card or the processor.",
        new[]
        {
            "GPU-bound: the GPU is near 100% load. Lower resolution, shadows, anti-aliasing (MSAA) or particles, or turn on DLSS, FSR or XeSS.",
            "CPU-bound: the GPU is well below 100% and FPS barely changes when you lower graphics settings. Close background apps, cap the FPS, or lower settings that add work for the CPU (view distance, crowds, physics).",
            "Check with the overlay in your GPU's app, or Task Manager → Performance → GPU, while the game runs.",
            "In CS2, MSAA, shadows and particles cost about 10% each; anisotropic filtering costs almost nothing.",
        },
        new[] { FpsMeasurements.Cs2SettingCost, FpsMeasurements.Cs2LowerResolution },
        CardPlace.GamesTips);

    public static readonly GuidanceCard ReflexAntiLag = new("C7", "Reflex and Anti-Lag",
        "NVIDIA Reflex helps when the GPU is busy. When it isn't, it changes almost nothing.",
        new[]
        {
            "Turn Reflex On in games that have it. It helps most when the GPU is near full load.",
            "On + Boost isn't needed: it added very little over On.",
            "AMD: use Anti-Lag or Anti-Lag 2 from the current driver. Anti-Lag+ (driver 23.10.1) got CS2 players banned by VAC. If you still have that driver, update it.",
        },
        new[] { FpsMeasurements.ReflexGpuBound, FpsMeasurements.ReflexBoost, FpsMeasurements.AntiLagPlus },
        CardPlace.GamesTips | CardPlace.Boost);

    public static readonly GuidanceCard ShaderCacheWarning = new("C9", "Clearing a shader cache makes games stutter for a while",
        "Shader caches hold graphics a game has already prepared. Clear one only if it is huge or after a graphics glitch.",
        new[]
        {
            "After clearing, the game builds its shaders again. That can take 1 to 2 hours, and the game stutters until it's done.",
            "PC Tidy never ticks a shader cache for you.",
        },
        new[] { FpsMeasurements.ShaderRebuild },
        CardPlace.GamesTips | CardPlace.GamesCleanup);

    public static readonly GuidanceCard BackgroundApps = new("C12", "Does closing background apps help?",
        "A little. Closing apps frees what they were using; it doesn't make the game itself faster.",
        new[]
        {
            "The gain was a few percent, mostly in games that run at very high FPS or on PCs with little RAM.",
            "Game boost only asks the apps you picked to close and pauses OneDrive. It never touches services, priority or power settings.",
        },
        new[] { FpsMeasurements.BackgroundAppsLtt, FpsMeasurements.BackgroundAppsPcWorld },
        CardPlace.Boost);

    public static readonly IReadOnlyList<GuidanceCard> All = new[] { CapYourFps, WhyIsMyGameSlow, ReflexAntiLag, ShaderCacheWarning, BackgroundApps };

    /// <summary>The cards for one place, in display order (Game boost puts its own C12 card first).</summary>
    public static IReadOnlyList<GuidanceCard> For(CardPlace place)
    {
        var cards = All.Where(c => c.Places.HasFlag(place)).ToList();
        return place == CardPlace.Boost ? cards.OrderBy(c => c.Id == "C12" ? 0 : 1).ToList() : cards;
    }
}
