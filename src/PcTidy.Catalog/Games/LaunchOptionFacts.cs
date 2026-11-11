namespace PcTidy.Catalog.Games;

/// <summary>
/// Launch options found in guides, with the research verdict (Community tweaks T9 §F and the summary table).
/// Placebo and harmful ones are flagged when they're already set, with a one-click "remove" for games without a
/// strict anti-cheat. Proven ones are offered per engine by <see cref="EngineRecommendations"/>.
/// </summary>
public static class LaunchOptionFacts
{
    private const string PcgwApex = "https://www.pcgamingwiki.com/wiki/Apex_Legends";
    private const string PcgwDota = "https://www.pcgamingwiki.com/wiki/Dota_2";
    private const string PcgwTf2 = "https://www.pcgamingwiki.com/wiki/Team_Fortress_2";
    private const string PcgwCs2 = "https://www.pcgamingwiki.com/wiki/Counter-Strike_2";
    private const string PcgwUnity = "https://www.pcgamingwiki.com/wiki/Engine:Unity";
    public const string UnrealCli = "https://dev.epicgames.com/documentation/en-us/unreal-engine/unreal-engine-command-line-arguments-reference";
    public const string UnityPlayerCli = "https://docs.unity.com/en-us/engine/current/manual/unity-editor/command-line-arguments/player";

    private static readonly GameEngine[] AnyMainstream = { GameEngine.Unity, GameEngine.Unreal, GameEngine.Source, GameEngine.Source2, GameEngine.Godot, GameEngine.IdTech };
    private static readonly GameEngine[] Valve = { GameEngine.Source, GameEngine.Source2 };
    private static readonly GameEngine[] Unreal = { GameEngine.Unreal };

    public static readonly IReadOnlyList<LaunchOptionFact> All = new LaunchOptionFact[]
    {
        new("-high", Array.Empty<GameEngine>(), TweakVerdict.Placebo, false,
            "Raises the game's CPU priority. No measured gain; it can make voice chat, capture and Discord stutter, and in Apex it can trigger a false update prompt.",
            PcgwApex, Evidence.Verified, FpsMeasurements.Cs2High),
        new("-threads", Valve, TweakVerdict.Placebo, true,
            "An old Source 1 setting. The engine already picks the right number of threads; a wrong number can only slow it down.",
            PcgwCs2, Evidence.Unverified, FpsMeasurements.Cs2OtherOptions),
        new("-cpuCount", AnyMainstream, TweakVerdict.Placebo, true,
            "Copied from ArmA guides. This engine ignores it.", UnrealCli, Evidence.Unverified),
        new("-exThreads", AnyMainstream, TweakVerdict.Placebo, true,
            "Copied from ArmA guides. This engine ignores it.", UnrealCli, Evidence.Unverified),
        new("-maxMem", AnyMainstream, TweakVerdict.Placebo, true,
            "Copied from ArmA guides. This engine ignores it.", UnrealCli, Evidence.Unverified),
        new("-nomemrestrict", AnyMainstream, TweakVerdict.Placebo, false,
            "An old Bethesda flag. This engine ignores it.", UnrealCli, Evidence.Unverified),
        new("-USEALLAVAILABLECORES", Unreal, TweakVerdict.Placebo, false,
            "Documented by Epic, but it changes worker threads for shader compiling, not your frame rate.",
            UnrealCli, Evidence.Verified),
        new("-nomansky", Unreal, TweakVerdict.Placebo, false,
            "Not an Unreal option at all (it's missing from Epic's list). It does nothing.", UnrealCli, Evidence.Verified),
        new("-onethread", Unreal, TweakVerdict.Harmful, false,
            "A debugging switch that runs the game on one thread. It makes the game slower.", UnrealCli, Evidence.Verified),
        new("-nothreading", Unreal, TweakVerdict.Harmful, false,
            "A debugging switch that turns off multithreading. It makes the game slower.", UnrealCli, Evidence.Verified),
        new("-nojoy", Valve, TweakVerdict.Placebo, false,
            "Saves a few MB at most and stops controllers from working.", PcgwDota, Evidence.Unverified, FpsMeasurements.Cs2OtherOptions),
        new("-freq", new[] { GameEngine.Source2 }, TweakVerdict.Placebo, true,
            "Obsolete. Pick the refresh rate in the game's video settings instead.", PcgwCs2, Evidence.Unverified),
        new("-refresh", new[] { GameEngine.Source2 }, TweakVerdict.Placebo, true,
            "Obsolete. Pick the refresh rate in the game's video settings instead.", PcgwCs2, Evidence.Unverified),
        new("-heapsize", Valve, TweakVerdict.Placebo, true,
            "A Source 1 memory setting that modern builds manage themselves.", PcgwDota, Evidence.Verified),
        new("-tickrate", Valve, TweakVerdict.Placebo, true,
            "Only for servers you host yourself. Online matches use the server's tick rate.", PcgwTf2, Evidence.Unverified),
        new("-nolog", new[] { GameEngine.Unity }, TweakVerdict.Placebo, false,
            "Turns off the game's log file. Writing the log costs almost nothing, and without it crashes are harder to fix.",
            PcgwUnity, Evidence.Verified),
        new("-useperfthreads", Unreal, TweakVerdict.Placebo, false,
            "A dedicated-server option. On your own PC it does nothing.", UnrealCli, Evidence.Unverified),
        new("-NoAsyncLoadingThread", Unreal, TweakVerdict.Placebo, false,
            "A dedicated-server option. On your own PC it does nothing.", UnrealCli, Evidence.Unverified),
        new("-UseMultithreadForDS", Unreal, TweakVerdict.Placebo, false,
            "A dedicated-server option. On your own PC it does nothing.", UnrealCli, Evidence.Unverified),
    };

    /// <summary>Fact for a token on a given engine (tokens may carry "=value": "-cpuCount=8").</summary>
    public static LaunchOptionFact? Find(string token, GameEngine engine)
    {
        var name = NameOf(token);
        return All.FirstOrDefault(f => f.Token.Equals(name, StringComparison.OrdinalIgnoreCase) && f.AppliesTo(engine));
    }

    /// <summary>"-cpuCount=8" → "-cpuCount".</summary>
    public static string NameOf(string token)
    {
        var eq = token.IndexOf('=');
        return eq > 0 ? token[..eq] : token;
    }
}

/// <summary>Engine-correct recommendations (Community tweaks T9 §B–D, only items with a proven or situational verdict).</summary>
public static class EngineRecommendations
{
    public static IReadOnlyList<GameRecommendation> For(GameEngine engine) => engine switch
    {
        GameEngine.Source or GameEngine.Source2 => Valve,
        GameEngine.Unity => Unity,
        GameEngine.Unreal => Unreal,
        _ => Array.Empty<GameRecommendation>(),
    };

    private static readonly GameRecommendation[] Valve =
    {
        new()
        {
            Id = "engine.source.novid", Kind = RecommendationKind.LaunchOption, Title = "Skip the intro video (-novid)",
            Reason = "Skips the Valve logo video so you get to the menu faster. Cosmetic: no effect in matches.",
            Verdict = TweakVerdict.Proven, Evidence = Evidence.Verified, SourceUrl = "https://www.pcgamingwiki.com/wiki/Team_Fortress_2",
            AddLaunchOption = "-novid",
            // CS2 has no startup movie, so the option would do nothing there.
            NotForAppIds = new[] { "730" },
        },
    };

    private static readonly GameRecommendation[] Unity =
    {
        new()
        {
            Id = "engine.unity.exclusive", Kind = RecommendationKind.FullscreenMode, Title = "Use exclusive fullscreen (-window-mode exclusive)",
            Reason = "Makes the game take the whole screen in exclusive fullscreen instead of a borderless window. Try it if the game stutters or tears in borderless mode; remove it if alt-tab gets slow.",
            Verdict = TweakVerdict.Situational, Evidence = Evidence.Verified, SourceUrl = LaunchOptionFacts.UnityPlayerCli,
            AddLaunchOption = "-window-mode exclusive",
        },
    };

    private static readonly GameRecommendation[] Unreal =
    {
        new()
        {
            Id = "engine.unreal.vsync-off", Kind = RecommendationKind.ConfigSetting, Title = "Turn off VSync in the game's settings file",
            Reason = "VSync adds input lag. For competitive play, turn it off and cap the frame rate a little below your monitor's refresh rate instead (in the game's menu, or your GPU driver). Situational: you may see tearing.",
            Verdict = TweakVerdict.Situational, Evidence = Evidence.Verified,
            SourceUrl = "https://www.pcgamingwiki.com/wiki/Engine:Unreal_Engine_4",
            Ini = new IniSetting("GameUserSettings.ini", "/Script/Engine.GameUserSettings", "bUseVSync", "False"),
        },
        new()
        {
            Id = "engine.unreal.dx11-hdr", Kind = RecommendationKind.KnownFix, Title = "Broken HDR or crashes on DirectX 12: try -dx11",
            Reason = "Older Unreal games have broken HDR or DirectX 12 crashes; starting them with -dx11 is the usual workaround. Only if you have that problem, and only if the game ships DirectX 11.",
            Verdict = TweakVerdict.Situational, Evidence = Evidence.Verified, SourceUrl = "https://www.pcgamingwiki.com/wiki/Engine:Unreal_Engine_5",
        },
    };
}
