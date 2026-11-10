namespace PcTidy.Catalog.Games;

/// <summary>
/// One file-layout clue for an engine. Paths are relative to the game's install folder; "*" is a wildcard inside
/// one name segment, "**" matches any one folder. Only folder listings are read, never file contents.
/// </summary>
/// <param name="Weight">How sure the clue is. The engine with the highest total wins; ties stay Unknown.</param>
public sealed record EngineSignature(GameEngine Engine, string RelativePattern, int Weight, string Why);

/// <summary>Engine detection rules (Community tweaks T9; layouts as shipped by each engine's packager).</summary>
public static class EngineSignatures
{
    public static readonly IReadOnlyList<EngineSignature> All = new EngineSignature[]
    {
        // Unity players ship UnityPlayer.dll next to the exe (2017.2+), and a <Name>_Data folder with globalgamemanagers.
        new(GameEngine.Unity, "UnityPlayer.dll", 10, "UnityPlayer.dll next to the game"),
        new(GameEngine.Unity, @"*_Data\globalgamemanagers", 8, "a Unity data folder"),
        new(GameEngine.Unity, @"*_Data\Managed\UnityEngine.dll", 8, "Unity's managed code"),

        // Unreal packaged games: <Project>\Binaries\Win64\<Project>-Win64-Shipping.exe, Engine\Binaries, Content\Paks.
        new(GameEngine.Unreal, @"**\Binaries\Win64\*-Win64-Shipping.exe", 10, "an Unreal \"-Win64-Shipping\" game exe"),
        new(GameEngine.Unreal, @"Engine\Binaries", 8, "Unreal's Engine\\Binaries folder"),
        new(GameEngine.Unreal, @"**\Content\Paks", 6, "Unreal .pak content folders"),
        new(GameEngine.Unreal, @"Content\Paks", 6, "Unreal .pak content folders"),

        // Source 2: game\bin\win64\engine2.dll. Source 1: bin\engine.dll and hl2.exe.
        new(GameEngine.Source2, @"game\bin\win64\engine2.dll", 12, "Source 2's engine2.dll"),
        new(GameEngine.Source, @"bin\engine.dll", 10, "Source's bin\\engine.dll"),
        new(GameEngine.Source, "hl2.exe", 6, "hl2.exe"),
        new(GameEngine.Source, @"*\gameinfo.txt", 4, "a Source gameinfo.txt"),

        // id Tech 6/7 ships its data in base\*.resources.
        new(GameEngine.IdTech, @"base\*.resources", 8, "id Tech .resources packs"),

        // Godot exports a .pck next to the exe.
        new(GameEngine.Godot, "*.pck", 6, "a Godot .pck file"),
    };
}

/// <summary>Files and folders anti-cheats install inside a game folder. Any match = strict mode (information only).</summary>
public static class AntiCheatSignatures
{
    public static readonly IReadOnlyList<(AntiCheat AntiCheat, string RelativePattern)> All = new (AntiCheat, string)[]
    {
        (AntiCheat.EasyAntiCheat, "EasyAntiCheat"),
        (AntiCheat.EasyAntiCheat, "EasyAntiCheat_EOS_Setup.exe"),
        (AntiCheat.EasyAntiCheat, "EasyAntiCheat_Setup.exe"),
        (AntiCheat.EasyAntiCheat, "start_protected_game.exe"),
        (AntiCheat.EasyAntiCheat, @"**\EasyAntiCheat"),
        (AntiCheat.BattlEye, "BattlEye"),
        (AntiCheat.BattlEye, @"**\BattlEye"),
        (AntiCheat.BattlEye, "BEService*.exe"),
        (AntiCheat.EaJavelin, "EAAntiCheat.Installer.exe"),
        (AntiCheat.EaJavelin, "EAAntiCheat.GameServiceLauncher.exe"),
        (AntiCheat.GameGuard, "GameGuard"),
        (AntiCheat.GameGuard, @"**\GameGuard"),
        (AntiCheat.OtherKernel, "anticheat"),
    };
}

/// <summary>Executables in game folders that are never "the game" (crash reporters, installers, anti-cheat bootstrappers).</summary>
public static class HelperExecutables
{
    public static readonly IReadOnlyList<string> Patterns = new[]
    {
        "UnityCrashHandler*", "CrashReportClient*", "crashpad_handler*", "*CrashReporter*", "*crash_reporter*",
        "unins*", "*setup*", "*installer*", "vc_redist*", "vcredist*", "dxsetup*", "DXSETUP*", "UE4PrereqSetup*", "UEPrereqSetup*",
        "EasyAntiCheat*", "BEService*", "BattlEye*", "dotnet*", "*redist*", "*launcherpatcher*",
    };
}
