using PcTidy.Catalog.Apps;
using PcTidy.Core.Model;

namespace PcTidy.Features.Apps.Inventory;

/// <summary>Where an app came from (decides how it is uninstalled).</summary>
public enum AppSource
{
    /// <summary>A classic desktop app from the Uninstall registry lists.</summary>
    Desktop,
    /// <summary>A Microsoft Store (MSIX/AppX) app for the current user.</summary>
    Store,
    /// <summary>A Steam game: removed through Steam only.</summary>
    Steam,
    /// <summary>An Epic Games game: removed through the Epic Games Launcher only.</summary>
    Epic,
}

/// <summary>Installed for everyone on the PC (machine) or only for the current user.</summary>
public enum AppScope { Machine, User }

/// <summary>Where the "last used" date came from.</summary>
public enum LastUsedSource { Unknown, UserAssist, Steam }

/// <summary>
/// One installed app as PC Tidy shows it. Everything is read-only inventory data; nothing here changes the PC.
/// Unknown values stay null ("unknown" on screen) rather than being guessed.
/// </summary>
public sealed record InstalledApp
{
    /// <summary>Stable id: "desktop:HKLM64:{key}", "store:{family}", "steam:{appid}", "epic:{appName}".</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required AppSource Source { get; init; }
    public AppScope Scope { get; init; } = AppScope.Machine;
    public string? Publisher { get; init; }
    public string? Version { get; init; }
    public string? InstallLocation { get; init; }
    /// <summary>Size the app reported itself (Uninstall EstimatedSize, Steam SizeOnDisk, Epic InstallSize).</summary>
    public long? ReportedSizeBytes { get; init; }
    public DateTime? InstallDate { get; init; }
    /// <summary>True when <see cref="InstallDate"/> is the registry key's last-change time, not a recorded install date.</summary>
    public bool InstallDateIsApproximate { get; init; }
    public DateTime? LastUsedUtc { get; init; }
    public int? RunCount { get; init; }
    public LastUsedSource LastUsedSource { get; init; }

    // ---- desktop apps (Uninstall registry) ----
    public string? UninstallString { get; init; }
    public string? QuietUninstallString { get; init; }
    /// <summary>"{GUID}" for Windows Installer (MSI) apps.</summary>
    public string? MsiProductCode { get; init; }
    /// <summary>winget package id when the entry says winget installed it.</summary>
    public string? WingetId { get; init; }
    public string? DisplayIcon { get; init; }
    /// <summary>"HKLM\SOFTWARE\...\Uninstall\Key" (32-bit entries show WOW6432Node).</summary>
    public string? RegistryKey { get; init; }
    public RegistryHive? RegistryHive { get; init; }
    /// <summary>64-bit registry view (false = the 32-bit WOW6432Node list).</summary>
    public bool RegistryView64 { get; init; } = true;
    /// <summary>The entry says it can't be removed (NoRemove = 1).</summary>
    public bool NoRemove { get; init; }

    // ---- Store apps ----
    public string? PackageFullName { get; init; }
    public string? PackageFamilyName { get; init; }
    /// <summary>Package name (the part of the full name before the first underscore).</summary>
    public string? PackageName { get; init; }
    /// <summary>Preinstalled-app catalog entry (offer / warn / never), if this Store app is on it.</summary>
    public PreinstalledApp? Preinstalled { get; init; }

    // ---- games ----
    /// <summary>Steam app id or Epic app name.</summary>
    public string? LauncherId { get; init; }

    /// <summary>The entry's files are gone (InstallLocation or the uninstaller is missing).</summary>
    public bool IsOrphan { get; init; }
    public string? OrphanReason { get; init; }

    public bool IsGame => Source is AppSource.Steam or AppSource.Epic;

    /// <summary>"Managed by Steam" / "Managed by Epic" tag for games.</summary>
    public string? ManagedBy => Source switch
    {
        AppSource.Steam => "Managed by Steam",
        AppSource.Epic => "Managed by Epic",
        _ => null,
    };
}
