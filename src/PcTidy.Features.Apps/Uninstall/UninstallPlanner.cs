using PcTidy.Catalog.Apps;
using PcTidy.Core.Model;
using PcTidy.Features.Apps.Inventory;

namespace PcTidy.Features.Apps.Uninstall;

/// <summary>How an app would be removed, or why it can't be.</summary>
/// <param name="Item">The plan item, or null when removal isn't offered.</param>
/// <param name="BlockedReason">Plain-English reason removal isn't offered (null when it is).</param>
/// <param name="Warning">Shown before the user confirms (OneDrive files, shared runtimes, "needed if you game"...).</param>
/// <param name="ButtonText">"Uninstall", "Remove", "Open Steam to uninstall"...</param>
public sealed record UninstallOption(PlanItem? Item, string? BlockedReason, string? Warning, string ButtonText)
{
    public bool IsAvailable => Item is not null;
}

/// <summary>
/// Builds the one plan item that removes an app (Plan §8-4):
/// desktop apps run their own uninstaller (MSI via its ProductCode, winget when winget installed it),
/// Store apps are removed for the current user, and games go through their launcher. Nothing is deleted by PC Tidy.
/// </summary>
public static class UninstallPlanner
{
    /// <summary>Opens Epic's library: the launcher has no "uninstall this game" link, so the user picks Uninstall there.</summary>
    public const string EpicLibraryUrl = "com.epicgames.launcher://store/library";

    public static UninstallOption For(InstalledApp app) => app.Source switch
    {
        AppSource.Steam => Steam(app),
        AppSource.Epic => Epic(app),
        AppSource.Store => Store(app),
        _ => Desktop(app),
    };

    private static UninstallOption Desktop(InstalledApp app)
    {
        if (DesktopAppRules.FindNever(app.Name) is { } never) return Blocked(never.Message);
        if (app.NoRemove) return Blocked("This app says it can't be uninstalled on its own (it's part of another app).");
        var warning = DesktopAppRules.FindWarning(app.Name)?.Message;
        if (app.IsOrphan)
            warning = Join(warning, $"{app.OrphanReason} Its uninstaller may not run; if it doesn't, use \"Find leftovers\" instead.");

        // No size goes on the item: PC Tidy can't know what an uninstaller removes, so it promises no total.
        var args = new Dictionary<string, string> { ["displayName"] = app.Name };
        if (app.InstallLocation is { } location) args["installLocation"] = location;
        // The handler tries the MSI code, then winget (silent, logged), then the app's own uninstall command.
        if (app.WingetId is { } winget) args["wingetId"] = winget;
        if (app.MsiProductCode is { } code) args["msiProductCode"] = code;
        if (app.UninstallString is { } command) args["uninstallString"] = command;
        bool ownUninstaller = app.MsiProductCode is not null || app.UninstallString is not null;
        if (!ownUninstaller && app.WingetId is null)
            return Blocked("This app didn't tell Windows how to uninstall it. Use the app's own settings, or \"Find leftovers\" if it's already gone.");

        var item = new PlanItem
        {
            Kind = ActionKind.RunUninstaller,
            FeatureId = AppsFeature.Id,
            Description = ownUninstaller ? $"Uninstall {app.Name} with its own uninstaller" : $"Uninstall {app.Name} with winget",
            // Uninstallers change the system (files, services, drivers): a restore point is made first.
            IsSystemChange = true,
            Reversible = false,
            // PC Tidy's admin helper never runs winget (it asks for admin rights itself, per installer), so an uninstall
            // that only winget can do runs without admin rights.
            NeedsAdmin = app.Scope == AppScope.Machine && ownUninstaller,
            Args = args,
        };
        return new UninstallOption(item, null, warning, "Uninstall");
    }

    private static UninstallOption Store(InstalledApp app)
    {
        if (app.PackageFullName is null) return Blocked("Windows didn't give this app's package name, so it can't be removed from here.");
        if (app.Preinstalled is { Tier: PreinstalledTier.Never } never) return Blocked(never.Reason);
        if (PreinstalledApps.IsNever(app.PackageName)) return Blocked("Windows or other apps depend on this, so PC Tidy never removes it.");
        var warning = app.Preinstalled?.Tier is PreinstalledTier.Warn or PreinstalledTier.AskFirst ? app.Preinstalled.Reason : null;
        var item = new PlanItem
        {
            Kind = ActionKind.AppxRemoveForUser,
            FeatureId = AppsFeature.Id,
            Description = $"Remove {app.Name} for you (you can get it back from the Microsoft Store)",
            // Per-user removal: no admin, no restore point. PC Tidy can't put it back itself, so it's marked permanent.
            Reversible = false,
            Args = new Dictionary<string, string> { ["packageFullName"] = app.PackageFullName, ["displayName"] = app.Name },
        };
        return new UninstallOption(item, null, warning, "Remove");
    }

    private static UninstallOption Steam(InstalledApp app)
    {
        if (app.LauncherId is null) return Blocked("Steam didn't give this game's id. Uninstall it from your Steam library.");
        return new UninstallOption(OpenLauncher($"steam://uninstall/{app.LauncherId}",
                $"Open Steam to uninstall {app.Name} (Steam asks you to confirm)"),
            null, "Steam removes the game and asks you to confirm. Cloud saves stay in your Steam account.", "Uninstall in Steam");
    }

    private static UninstallOption Epic(InstalledApp app)
        => new(OpenLauncher(EpicLibraryUrl, $"Open the Epic Games Launcher to uninstall {app.Name}"),
            null, $"In the Epic Games Launcher, open Library, then {app.Name} → ⋯ → Uninstall.", "Uninstall in Epic");

    /// <summary>A launcher screen is opened; the launcher does the removal and asks the user itself.</summary>
    private static PlanItem OpenLauncher(string target, string description) => new()
    {
        Kind = ActionKind.OpenExternal,
        FeatureId = AppsFeature.Id,
        Description = description,
        Args = new Dictionary<string, string> { ["target"] = target },
    };

    private static UninstallOption Blocked(string why) => new(null, why, null, "Uninstall");

    private static string? Join(string? a, string? b) => a is null ? b : b is null ? a : a + " " + b;
}
