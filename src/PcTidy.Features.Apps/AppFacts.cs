using System.Globalization;
using PcTidy.Core;
using PcTidy.Features.Apps.Inventory;

namespace PcTidy.Features.Apps;

/// <summary>Plain-English facts about an app, shared by the scanner rows and the Apps page so both say the same thing.</summary>
public static class AppFacts
{
    public static string SourceText(InstalledApp app) => app.Source switch
    {
        AppSource.Store => app.Preinstalled is null ? "Microsoft Store app" : "Came with Windows (Store app)",
        AppSource.Steam => "Steam game",
        AppSource.Epic => "Epic Games game",
        _ => app.Scope == AppScope.User ? "Desktop app (just for you)" : "Desktop app (everyone on this PC)",
    };

    /// <summary>"3 days ago", "in March 2025", or "unknown". Never guessed.</summary>
    public static string LastUsedText(InstalledApp app, DateTime utcNow)
    {
        if (app.LastUsedUtc is not { } last) return "unknown";
        var days = (utcNow - last).TotalDays;
        if (days < 1) return "today";
        if (days < 2) return "yesterday";
        if (days < 31) return $"{(int)days} days ago";
        if (days < 365) return $"{(int)(days / 30.4)} months ago";
        return "in " + last.ToLocalTime().ToString("MMMM yyyy", CultureInfo.CurrentCulture);
    }

    public static string InstallDateText(InstalledApp app)
    {
        if (app.InstallDate is not { } d) return "unknown";
        var text = d.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
        return app.InstallDateIsApproximate ? "about " + text : text;
    }

    /// <summary>Rows for the details panel. Unknown values say "unknown" instead of being left out or guessed.</summary>
    public static IReadOnlyDictionary<string, string> Details(InstalledApp app, long? measuredBytes, DateTime utcNow)
    {
        var d = new Dictionary<string, string>
        {
            ["Type"] = SourceText(app),
            ["Publisher"] = app.Publisher ?? "unknown",
            ["Version"] = app.Version ?? "unknown",
            ["Installed"] = InstallDateText(app),
            ["Last used"] = LastUsedText(app, utcNow) + (app.LastUsedSource switch
            {
                LastUsedSource.UserAssist => app.RunCount is > 0 ? $" (opened {app.RunCount} times, from Windows' launch history)" : " (from Windows' launch history)",
                LastUsedSource.Steam => " (from Steam)",
                _ => "",
            }),
            ["Size it reports"] = app.ReportedSizeBytes is { } r ? Sizes.Format(r) : "unknown",
            ["Size measured"] = measuredBytes is { } m ? Sizes.Format(m) : "not measured",
            ["Location"] = app.InstallLocation ?? "unknown",
        };
        if (app.ManagedBy is { } managed) d["Managed by"] = managed[("Managed by ".Length)..];
        if (app.MsiProductCode is { } code) d["Windows Installer code"] = code;
        if (app.WingetId is { } winget) d["winget id"] = winget;
        if (app.RegistryKey is { } key) d["Registry entry"] = key;
        if (app.PackageFullName is { } pkg) d["Package"] = pkg;
        if (app.IsOrphan) d["Problem"] = app.OrphanReason ?? "Its files are gone.";
        return d;
    }
}
