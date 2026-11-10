namespace PcTidy.Catalog.Privacy;

/// <summary>One per-user DWORD switch behind a tips/ads setting (HKCU, 1 or missing = on, 0 = off).</summary>
public sealed record TipsValue(string KeyPath, string ValueName);

/// <summary>
/// One "Windows tips, suggestions and ads" setting as the user sees it, and the per-user values behind it.
/// Every value is the same switch the Settings app flips (Tweaks.md rule 2 and Community tweaks §1 item 6: per-user
/// values, never the policies that Home and Pro ignore, so nothing says "Managed by your organization").
/// </summary>
public sealed record TipsSetting(string Id, string Title, string WhereYouSeeIt, string WhatChanges, IReadOnlyList<TipsValue> Values);

/// <summary>
/// The Notification manager's tips/ads rows. Values shared with the Tweaks catalog (the ContentDeliveryManager
/// "tips, suggestions, silent installs" and "lock-screen fun facts" tweaks) use the very same key, value name,
/// type and data (0 off / 1 on), so both sections agree and the guard needs only the Tweaks allowance for them;
/// <see cref="PrivacyRegistryAllowances"/> adds allowances only for the values Tweaks doesn't list.
/// </summary>
public static class TipsCatalog
{
    public const string ContentDeliveryManager = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    public const string UserProfileEngagement = @"Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement";
    public const string Off = "0";
    public const string On = "1";

    private static TipsValue Cdm(string value) => new(ContentDeliveryManager, value);

    public static readonly IReadOnlyList<TipsSetting> All = new TipsSetting[]
    {
        new("start-suggestions", "Suggested apps in Start", "Start menu",
            "Start stops showing promoted apps and \"suggestions\". Your pinned apps and your own recent files stay.",
            new[] { Cdm("SubscribedContent-338388Enabled"), Cdm("SystemPaneSuggestionsEnabled") }),
        new("tips-popups", "Tips and tricks pop-ups", "Pop-ups and notifications",
            "Windows stops popping up tips about features you haven't tried. Nothing else about notifications changes.",
            new[] { Cdm("SubscribedContent-338389Enabled"), Cdm("SoftLandingEnabled") }),
        new("lock-screen", "Fun facts and tips on the lock screen", "Lock screen",
            "The text tips and promotions on the lock screen go away. Your lock-screen picture (or Spotlight images) stays.",
            new[] { Cdm("RotatingLockScreenOverlayEnabled"), Cdm("SubscribedContent-338387Enabled") }),
        new("settings-suggestions", "Suggested content in Settings", "Settings app",
            "The Settings app stops showing suggestions and offers. Every setting stays where it is.",
            new[] { Cdm("SubscribedContent-338393Enabled"), Cdm("SubscribedContent-353694Enabled"), Cdm("SubscribedContent-353696Enabled") }),
        new("welcome-experience", "Welcome screens after updates", "Full-screen after updates",
            "No more \"what's new\" welcome screens after Windows updates. Updates themselves aren't affected.",
            new[] { Cdm("SubscribedContent-310093Enabled") }),
        new("silent-installs", "Quietly installed suggested apps", "Start menu",
            "Windows stops installing promoted apps (often games) by itself. Apps it already installed stay: remove them in Apps if you like.",
            new[] { Cdm("SilentInstalledAppsEnabled") }),
        new("finish-setup", "\"Finish setting up your device\" screens", "Full-screen after sign-in",
            "Windows stops asking you to finish setting up (Microsoft 365, OneDrive, phone link offers). Your account isn't affected.",
            new[] { new TipsValue(UserProfileEngagement, "ScoobeSystemSettingEnabled") }),
    };

    /// <summary>Every distinct value behind the rows.</summary>
    public static IEnumerable<TipsValue> AllValues => All.SelectMany(s => s.Values).Distinct();
}
