namespace PcTidy.Catalog.Apps;

/// <summary>A rule for desktop apps (Uninstall registry entries), matched against the display name.</summary>
/// <param name="NamePattern">Display name; a trailing * means "starts with". Case-insensitive.</param>
/// <param name="Message">Plain-English line shown with the app.</param>
public sealed record DesktopAppRule(string NamePattern, string Message)
{
    public bool Matches(string displayName) => PreinstalledApps.PatternMatches(NamePattern, displayName.Trim());
}

/// <summary>
/// Desktop apps PC Tidy never offers to uninstall (Plan §8-4 never list, Research §4 "never copy"),
/// and apps that get a warning before their uninstaller runs.
/// </summary>
public static class DesktopAppRules
{
    public static readonly IReadOnlyList<DesktopAppRule> NeverUninstall = new DesktopAppRule[]
    {
        new("Microsoft Edge", "Windows and many apps open web content with Edge, so PC Tidy never removes it. In the European Economic Area, Windows lets you uninstall Edge yourself in Settings > Apps; that way keeps WebView2, which other apps need."),
        new("Microsoft Edge Update", "Keeps Edge and WebView2 secure, so PC Tidy never removes it."),
        new("Microsoft Edge WebView2*", "Widgets, the new Outlook, Teams and many other apps are built on WebView2."),
        new("Windows App Runtime*", "Other apps are built on this and stop opening without it."),
        new("Microsoft Windows Desktop Runtime*", "Other apps are built on this and stop opening without it."),
        new("Microsoft .NET Runtime*", "Other apps are built on this and stop opening without it."),
        new("Microsoft ASP.NET Core*", "Other apps are built on this and stop opening without it."),
        new("Microsoft Update Health Tools", "Windows Update uses it to keep this PC healthy."),
        new("Gaming Services", "Game Pass games and Xbox sign-in stop working without it."),
        new("Microsoft GameInput", "Games use it to read controllers."),
        new("Windows PC Health Check", "Windows installs and updates it itself."),
        new("Microsoft Intune Management Extension", "Your organization manages this PC with it."),
    };

    public static readonly IReadOnlyList<DesktopAppRule> Warnings = new DesktopAppRule[]
    {
        new("Microsoft OneDrive*", "Check that your online-only files are downloaded or backed up first: they stay in the cloud, not on this PC."),
        new("Microsoft Visual C++*", "Other apps may need this runtime. Only remove it if you know nothing uses it."),
        new("Microsoft .NET*", "Other apps may need this runtime. Only remove it if you know nothing uses it."),
        new("Microsoft DirectX*", "Games may need this. Only remove it if you know nothing uses it."),
        new("Java*", "Other apps may need Java. Only remove it if you know nothing uses it."),
        new("NVIDIA*", "This is part of your graphics driver. Use the Drivers section or NVIDIA's own tools instead."),
        new("AMD Software*", "This is part of your graphics driver. Use the Drivers section or AMD's own tools instead."),
        new("Intel(R)*", "This may be part of a driver. Removing it can stop a device working."),
        new("Realtek*", "This is probably a sound or network driver. Removing it can stop a device working."),
        new("Steam", "Every Steam game goes with it. Uninstall the games you don't want from Steam first."),
        new("Epic Games Launcher", "Epic games can't start without it."),
    };

    public static DesktopAppRule? FindNever(string? displayName)
        => string.IsNullOrWhiteSpace(displayName) ? null : NeverUninstall.FirstOrDefault(r => r.Matches(displayName));

    public static DesktopAppRule? FindWarning(string? displayName)
        => string.IsNullOrWhiteSpace(displayName) ? null : Warnings.FirstOrDefault(r => r.Matches(displayName));
}
