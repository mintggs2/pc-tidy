using PcTidy.Core.Model;

namespace PcTidy.Catalog.Speed;

/// <summary>
/// The exact registry values the Speed section's Settings cards may write, and the data they write.
/// Startup items, services and scheduled tasks use their own action kinds (StartupItemSetEnabled,
/// ServiceSetStartType, ScheduledTaskSetEnabled), so they need no entries here.
/// AllowDelete stays on so undo can put a value back to "wasn't there".
/// </summary>
public sealed class SpeedRegistryAllowances : IRegistryAllowanceSource
{
    public const string SourcePrefix = "speed.";

    public const string GameBarKey = @"Software\Microsoft\GameBar";
    public const string GameModeValue = "AutoGameModeEnabled";
    /// <summary>Game Mode on. Speed only ever turns it on (Tweaks.md: keep it on; off is placebo or worse).</summary>
    public const string GameModeOn = "1";

    public const string VisualEffectsKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects";
    public const string VisualEffectsValue = "VisualFXSetting";
    /// <summary>0 = let Windows choose what's best (the default).</summary>
    public const string VisualEffectsLetWindowsChoose = "0";
    /// <summary>2 = adjust for best performance.</summary>
    public const string VisualEffectsBestPerformance = "2";

    public const string GraphicsDriversKey = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    public const string GpuSchedulingValue = "HwSchMode";
    /// <summary>2 = hardware-accelerated GPU scheduling on. Speed only turns it back on (Tweaks.md: leave it on).</summary>
    public const string GpuSchedulingOn = "2";

    public IEnumerable<RegistryAllowance> Allowances() => new[]
    {
        new RegistryAllowance(SourcePrefix + "game-mode", RegistryHive.CurrentUser, GameBarKey, GameModeValue,
            RegistryKind.DWord, new[] { GameModeOn }),
        new RegistryAllowance(SourcePrefix + "visual-effects", RegistryHive.CurrentUser, VisualEffectsKey, VisualEffectsValue,
            RegistryKind.DWord, new[] { VisualEffectsLetWindowsChoose, VisualEffectsBestPerformance }),
        new RegistryAllowance(SourcePrefix + "gpu-scheduling", RegistryHive.LocalMachine, GraphicsDriversKey, GpuSchedulingValue,
            RegistryKind.DWord, new[] { GpuSchedulingOn }, AllowDelete: true, NeedsAdmin: true),
    };
}
