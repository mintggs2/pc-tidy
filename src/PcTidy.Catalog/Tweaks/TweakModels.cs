using PcTidy.Core.Model;

namespace PcTidy.Catalog.Tweaks;

// =====================================================================================
//  Tweak catalog models (Plan §8 "12 · Tweaks", research: 10 Research/Tweaks.md and
//  10 Research/Community tweaks.md). Pure data: no reads, no writes. The Tweaks feature
//  reads the current state of each change and turns offered tweaks into PlanItems.
// =====================================================================================

/// <summary>Groups on the Tweaks page (Plan §8: Gaming · Privacy · Debloat · UI/Explorer · Network · Power).</summary>
public enum TweakGroup { Gaming, Privacy, Debloat, UiExplorer, Network, Power }

/// <summary>Research verdict. Only Proven and Situational are ever offered; Placebo is shown greyed; Harmful is never shown.</summary>
public enum TweakVerdict { Proven, Situational, Placebo, Harmful }

public enum TweakRisk { Low, Medium, High }

[Flags]
public enum TweakFlags
{
    None = 0,
    /// <summary>Needs admin (HKLM, services, optional features, HKCU policy keys).</summary>
    Admin = 1,
    /// <summary>Takes effect after a restart.</summary>
    Restart = 2,
    /// <summary>Takes effect after signing out and in again.</summary>
    SignOut = 4,
    /// <summary>Takes effect after Explorer restarts (or a sign-out).</summary>
    RestartExplorer = 8,
    /// <summary>A policy value: Windows (or Edge) will say "Managed by your organization".</summary>
    ManagedPolicy = 16,
    /// <summary>Risky but not security-related: offered only with Expert mode on, never in presets or Optimize-all.</summary>
    ExpertOnly = 32,
    /// <summary>Takes effect the next time a game starts.</summary>
    RestartGame = 64,
    /// <summary>
    /// A protection PC Tidy never changes ("Memory integrity: keep it on"): information with a link to where Windows shows it,
    /// never a tick box. The catalog writes its title and text that way round.
    /// </summary>
    KeepOn = 128,
}

/// <summary>The three presets from Tweaks.md "Recommended presets".</summary>
public enum TweakPreset { None, SafeGaming, PrivacyBasic, CleanExplorer }

public enum FormFactor { Any, DesktopOnly, LaptopOnly }

/// <summary>CPU families some tweaks must never touch (Community tweaks §1 item 9: core parking / Game Bar steer X3D games).</summary>
[Flags]
public enum CpuKind
{
    None = 0,
    /// <summary>Ryzen 7900X3D / 7950X3D / 9900X3D / 9950X3D: two CCDs, only one with V-Cache.</summary>
    DualCcdX3D = 1,
    /// <summary>Intel 12th gen and later with P-cores and E-cores.</summary>
    Hybrid = 2,
}

/// <summary>Where a tweak makes sense. A tweak that doesn't apply is hidden (or, for <see cref="HarmfulOn"/>, treated as harmful).</summary>
public sealed record TweakApplicability
{
    public static readonly TweakApplicability Everywhere = new();
    public static readonly TweakApplicability Windows11 = new() { MinBuild = 22000 };

    /// <summary>Lowest Windows build (19045 = Win10 22H2, 22000 = Win11 21H2, 22631 = 23H2, 26100 = 24H2).</summary>
    public int MinBuild { get; init; }
    public int MaxBuild { get; init; } = int.MaxValue;
    public FormFactor FormFactor { get; init; } = FormFactor.Any;
    /// <summary>Only for these GPU vendors (null = any).</summary>
    public IReadOnlyList<GpuVendor>? GpuVendors { get; init; }
    /// <summary>On these CPUs the tweak is harmful and never shown.</summary>
    public CpuKind HarmfulOn { get; init; }
    /// <summary>Only when this registry key exists (e.g. Office installed). Hive-qualified: "HKCU\Software\Microsoft\Office\16.0".</summary>
    public string? RequiresKey { get; init; }

    public bool Matches(int build, bool isLaptop, GpuVendor gpu)
        => build >= MinBuild && build <= MaxBuild
           && (FormFactor == FormFactor.Any || (FormFactor == FormFactor.LaptopOnly) == isLaptop)
           && (GpuVendors is null || GpuVendors.Contains(gpu));
}

// ------------------------------------------------------------------ changes

/// <summary>One exact change a tweak makes (or, for placebo/harmful rows, the change the internet tells you to make).</summary>
public abstract record TweakChange
{
    /// <summary>Plain one-line description, shown in the details panel.</summary>
    public abstract string Describe();
}

/// <summary>
/// Set one registry value. <paramref name="WindowsDefault"/> null = the value is absent on a fresh Windows
/// (undo still restores the exact prior state the handler recorded, including "wasn't there").
/// Data uses the invariant format of <see cref="RegistryValueSpec.Data"/> (DWord = unsigned decimal).
/// </summary>
public sealed record RegistryValueChange(RegistryHive Hive, string KeyPath, string ValueName, RegistryKind Kind, string Data, string? WindowsDefault)
    : TweakChange
{
    public string FullKey => $@"{RegistryNames.Short(Hive)}\{KeyPath}";
    public string Label => $@"{FullKey} → {(ValueName.Length == 0 ? "(Default)" : ValueName)}";
    public override string Describe() => $"{Label} = {Show(Data)} (Windows default: {(WindowsDefault is null ? "not set" : Show(WindowsDefault))})";
    private static string Show(string d) => d.Length == 0 ? "\"\"" : d;
}

/// <summary>
/// Merge "<paramref name="Token"/>=<paramref name="Value"/>;" into a semicolon list stored in a REG_SZ
/// (DirectXUserGlobalSettings). Never overwrites the other tokens (Community tweaks §2: merge, never overwrite).
/// </summary>
public sealed record RegistryTokenChange(RegistryHive Hive, string KeyPath, string ValueName, string Token, string Value) : TweakChange
{
    public string FullKey => $@"{RegistryNames.Short(Hive)}\{KeyPath}";
    public string Label => $@"{FullKey} → {ValueName}";
    public override string Describe() => $"{Label}: add \"{Token}={Value};\" to the existing list (other entries kept)";
}

/// <summary>Set a service's start type (never Disabled: PC Tidy uses Manual so Windows can still start it on demand).</summary>
public sealed record ServiceStartChange(string ServiceName, ServiceStart Target, ServiceStart WindowsDefault) : TweakChange
{
    public override string Describe() => $"Service '{ServiceName}' → {RegistryNames.Friendly(Target)} (Windows default: {RegistryNames.Friendly(WindowsDefault)})";
}

/// <summary>Remove a Store app for the current user (reinstall from the Microsoft Store to undo).</summary>
public sealed record AppxRemoveChange(string PackageName) : TweakChange
{
    public override string Describe() => $"Remove the Store app '{PackageName}' for you (reinstall it from the Microsoft Store to get it back)";
}

/// <summary>Turn off a Windows optional feature (re-add path recorded by the handler).</summary>
public sealed record OptionalFeatureChange(string FeatureName) : TweakChange
{
    public override string Describe() => $"Turn off the Windows feature '{FeatureName}' (can be turned back on)";
}

/// <summary>Data only: a power plan the internet recommends (placebo rows).</summary>
public sealed record PowerPlanChange(Guid SchemeGuid, string Name) : TweakChange
{
    public override string Describe() => $"Switch to the '{Name}' power plan ({SchemeGuid:B})";
}

/// <summary>Data only: a command line from a guide (bcdedit, netsh, powercfg). PC Tidy never runs these as tweaks.</summary>
public sealed record CommandChange(string CommandLine) : TweakChange
{
    public override string Describe() => CommandLine;
}

/// <summary>Information only: where the user changes it themselves (Settings page, vendor app).</summary>
public sealed record GuidanceChange(string Steps, string? SettingsUri = null) : TweakChange
{
    public override string Describe() => Steps;
}

// ------------------------------------------------------------------ the tweak

/// <summary>One row of the tweak table.</summary>
public sealed record TweakDefinition
{
    /// <summary>Stable id, e.g. "ui.file-extensions". Used for presets, history and tests.</summary>
    public required string Id { get; init; }
    public required TweakGroup Group { get; init; }
    public required string Title { get; init; }
    /// <summary>What it really does, in plain English.</summary>
    public required string Effect { get; init; }
    public required TweakVerdict Verdict { get; init; }
    public TweakRisk Risk { get; init; } = TweakRisk.Low;
    public TweakFlags Flags { get; init; }
    /// <summary>Placebo/harmful: why it doesn't help. Offered rows: a caveat shown next to the row (Expert warning etc.).</summary>
    public string? Caveat { get; init; }
    public required IReadOnlyList<string> Sources { get; init; }
    public TweakApplicability Applies { get; init; } = TweakApplicability.Everywhere;
    public required IReadOnlyList<TweakChange> Changes { get; init; }
    /// <summary>How it's undone (plain English).</summary>
    public string Undo { get; init; } = "One-click undo puts back the exact value PC Tidy found, including \"wasn't set\".";
    public TweakPreset Preset { get; init; }
    /// <summary>Ticked when its preset is picked (false = opt-in inside the preset).</summary>
    public bool PresetDefault { get; init; }
    /// <summary>False for Store-app removal (reinstall to undo).</summary>
    public bool Reversible { get; init; } = true;

    public bool IsOffered => Verdict is TweakVerdict.Proven or TweakVerdict.Situational;
    public bool IsExpertOnly => Flags.HasFlag(TweakFlags.ExpertOnly);
    public bool IsManagedPolicy => Flags.HasFlag(TweakFlags.ManagedPolicy);
    /// <summary>A "keep it on" security row: information only, PC Tidy never changes it.</summary>
    public bool IsKeepOn => Flags.HasFlag(TweakFlags.KeepOn);

    /// <summary>Changes PC Tidy can actually make (registry, service, Store app, optional feature).</summary>
    public IEnumerable<TweakChange> ActionableChanges
        => Changes.Where(c => c is RegistryValueChange or RegistryTokenChange or ServiceStartChange or AppxRemoveChange or OptionalFeatureChange);

    public bool HasActions => ActionableChanges.Any();
}

/// <summary>Small naming helpers shared by the catalog and the feature.</summary>
public static class RegistryNames
{
    public static string Short(RegistryHive hive) => hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU";

    public static string Friendly(ServiceStart s) => s switch
    {
        ServiceStart.Automatic => "Automatic",
        ServiceStart.AutomaticDelayed => "Automatic (delayed start)",
        ServiceStart.Manual => "Manual",
        ServiceStart.Disabled => "Disabled",
        _ => s.ToString(),
    };

    /// <summary>Parses "HKCU\Software\X" / "HKLM\SOFTWARE\X" into hive + path.</summary>
    public static bool TrySplit(string full, out RegistryHive hive, out string keyPath)
    {
        hive = RegistryHive.CurrentUser;
        keyPath = "";
        var cut = full.IndexOf('\\');
        if (cut <= 0) return false;
        var head = full[..cut].ToUpperInvariant();
        if (head is "HKCU" or "HKEY_CURRENT_USER") hive = RegistryHive.CurrentUser;
        else if (head is "HKLM" or "HKEY_LOCAL_MACHINE") hive = RegistryHive.LocalMachine;
        else return false;
        keyPath = full[(cut + 1)..].Trim('\\');
        return keyPath.Length > 0;
    }

    /// <summary>Policy keys make Windows show "Managed by your organization".</summary>
    public static bool IsPolicyKey(string keyPath)
        => keyPath.StartsWith(@"Software\Policies\", StringComparison.OrdinalIgnoreCase)
           || keyPath.StartsWith(@"SOFTWARE\Policies\", StringComparison.OrdinalIgnoreCase);
}
