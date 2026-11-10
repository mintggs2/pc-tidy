namespace PcTidy.Catalog.Apps;

/// <summary>How PC Tidy treats a preinstalled Store app (Plan §8-4 offer / warn / never lists).</summary>
public enum PreinstalledTier
{
    /// <summary>Safe to remove for most people. Shown as removable, never ticked by default.</summary>
    Offer,
    /// <summary>Removable, but many people use it: shown with an "ask first" note.</summary>
    AskFirst,
    /// <summary>Removable, with a warning about what stops working ("needed if you game / use this").</summary>
    Warn,
    /// <summary>Never offered: Windows or other apps depend on it. Not shown as removable anywhere.</summary>
    Never,
}

/// <summary>
/// One preinstalled app family. <see cref="PackagePatterns"/> are Store package names (the part of the full name
/// before the first underscore); a trailing * means "starts with".
/// </summary>
public sealed record PreinstalledApp(string FriendlyName, PreinstalledTier Tier, string Reason, params string[] PackagePatterns)
{
    public bool Matches(string packageName) => PackagePatterns.Any(p => PreinstalledApps.PatternMatches(p, packageName));
}

/// <summary>
/// Preinstalled Store apps and what PC Tidy may do with them. Removal is always per-user (Remove-AppxPackage for the
/// current user, no admin) and re-installable from the Microsoft Store. The never list wins over every other list.
/// </summary>
public static class PreinstalledApps
{
    private const string ReinstallNote = "Removed for you only. You can get it back from the Microsoft Store.";

    private static PreinstalledApp Offer(string name, string what, params string[] packages)
        => new(name, PreinstalledTier.Offer, $"{what} {ReinstallNote}", packages);

    private static PreinstalledApp Ask(string name, string what, params string[] packages)
        => new(name, PreinstalledTier.AskFirst, $"{what} {ReinstallNote}", packages);

    private static PreinstalledApp Warn(string name, string what, params string[] packages)
        => new(name, PreinstalledTier.Warn, $"{what} {ReinstallNote}", packages);

    private static PreinstalledApp Never(string name, string why, params string[] packages)
        => new(name, PreinstalledTier.Never, why, packages);

    /// <summary>Never offered: Windows, the Store, sign-in, games or other apps break without these.</summary>
    public static readonly IReadOnlyList<PreinstalledApp> NeverList = new[]
    {
        Never("Microsoft Store", "Without the Store, apps and codecs stop getting updates.", "Microsoft.WindowsStore", "Microsoft.StorePurchaseApp", "Microsoft.Services.Store.Engagement"),
        Never("App Installer (winget)", "Windows uses it to install and update apps.", "Microsoft.DesktopAppInstaller"),
        Never("App frameworks", "Other apps are built on these and stop opening without them.",
            "Microsoft.VCLibs*", "Microsoft.UI.Xaml*", "Microsoft.NET.Native*", "Microsoft.NET.*", "Microsoft.WindowsAppRuntime*",
            "MicrosoftCorporationII.WinAppRuntime*", "Microsoft.Advertising.Xaml"),
        Never("Windows Security", "This is how you see and control your antivirus.", "Microsoft.SecHealthUI"),
        Never("Sign-in and accounts", "Windows needs these to sign you in to Microsoft accounts and work accounts.",
            "Microsoft.AAD.BrokerPlugin", "Microsoft.AccountsControl", "Microsoft.Windows.CloudExperienceHost",
            "Microsoft.CredDialogHost", "Microsoft.Windows.OOBENetworkConnectionFlow", "Microsoft.LockApp"),
        Never("Start menu and shell", "Start, the taskbar and search are built on these.",
            "Microsoft.Windows.StartMenuExperienceHost", "Microsoft.Windows.ShellExperienceHost", "Microsoft.Windows.Search",
            "MicrosoftWindows.Client.CBS", "MicrosoftWindows.Client.Core", "MicrosoftWindows.Client.FileExp",
            "Microsoft.Windows.ContentDeliveryManager", "MicrosoftWindows.UndockedDevKit", "Microsoft.Windows.SecureAssessmentBrowser"),
        Never("Media codecs", "Photos and videos stop playing in many apps without them.",
            "Microsoft.HEIFImageExtension", "Microsoft.HEVCVideoExtension*", "Microsoft.VP9VideoExtensions", "Microsoft.WebMediaExtensions",
            "Microsoft.WebpImageExtension", "Microsoft.RawImageExtension", "Microsoft.AV1VideoExtension", "Microsoft.MPEG2VideoExtension",
            "Microsoft.AVCEncoderVideoExtension", "Microsoft.DolbyAudioExtensions"),
        Never("Windows Terminal", "Windows' own command window.", "Microsoft.WindowsTerminal*"),
        Never("Notepad", "A core Windows tool.", "Microsoft.WindowsNotepad"),
        Never("Calculator", "A core Windows tool.", "Microsoft.WindowsCalculator"),
        Never("Paint", "A core Windows tool.", "Microsoft.Paint", "Microsoft.MSPaint"),
        Never("Microsoft Edge", "Windows and many apps open web content with Edge.", "Microsoft.MicrosoftEdge*", "Microsoft.Edge*"),
        Never("WebView2", "Widgets, the new Outlook, Teams and many other apps are built on it.", "Microsoft.WebView2*", "Microsoft.Win32WebViewHost"),
        Never("Gaming Services", "Game Pass games and Xbox sign-in stop working without it.", "Microsoft.GamingServices"),
        Never("Xbox sign-in", "Game Pass games and Xbox sign-in stop working without it.", "Microsoft.XboxIdentityProvider", "Microsoft.Xbox.TCUI"),
        Never("Game Bar", "Games use it for captures and Game Mode; removing it causes error pop-ups in games.", "Microsoft.XboxGamingOverlay"),
    };

    /// <summary>Offered, asked-first and warned apps (Plan §8-4).</summary>
    public static readonly IReadOnlyList<PreinstalledApp> Removable = new[]
    {
        Offer("Clipchamp", "A video editor that came with Windows.", "Clipchamp.Clipchamp"),
        Offer("Microsoft News", "Bing news feed app.", "Microsoft.BingNews"),
        Offer("Weather", "Bing weather app.", "Microsoft.BingWeather"),
        Offer("Bing Search", "Bing search app.", "Microsoft.BingSearch"),
        Offer("Get Help", "Microsoft's support chat app.", "Microsoft.GetHelp"),
        Offer("Tips", "Windows tips app.", "Microsoft.Getstarted"),
        Offer("Solitaire Collection", "Card games with ads.", "Microsoft.MicrosoftSolitaireCollection"),
        Offer("Microsoft 365 (Office) hub", "A launcher for Office web apps. Your installed Office apps aren't touched.", "Microsoft.MicrosoftOfficeHub"),
        Offer("Feedback Hub", "Sends feedback to Microsoft.", "Microsoft.WindowsFeedbackHub"),
        Offer("Maps", "Offline maps app.", "Microsoft.WindowsMaps"),
        Offer("People", "Old contacts app.", "Microsoft.People"),
        Offer("Power Automate", "Desktop automation tool.", "Microsoft.PowerAutomateDesktop"),
        Offer("Microsoft To Do", "To-do list app.", "Microsoft.Todos"),
        Offer("Movies & TV", "Old video player and store.", "Microsoft.ZuneVideo"),
        Offer("Cortana", "Microsoft's retired voice assistant.", "Microsoft.549981C3F5F10"),
        Offer("Teams (personal)", "The free personal chat version of Teams. Teams for work or school isn't touched.", "MicrosoftTeams"),
        Offer("Quick Assist", "Lets someone else control your PC to help you.", "MicrosoftCorporationII.QuickAssist"),
        Offer("Microsoft Family", "Family safety app.", "MicrosoftCorporationII.MicrosoftFamily"),
        Offer("Dev Home", "A dashboard for programmers.", "Microsoft.Windows.DevHome"),
        Offer("Journal", "Pen note-taking app.", "Microsoft.MicrosoftJournal"),
        Offer("Skype", "Microsoft's retired calling app.", "Microsoft.SkypeApp"),
        Offer("Candy Crush and other King games", "Games that came with Windows.", "king.com.*"),
        Offer("Spotify (pre-installed)", "A Spotify shortcut that came with Windows.", "SpotifyAB.SpotifyMusic"),
        Offer("Disney+ (pre-installed)", "A Disney+ shortcut that came with Windows.", "Disney.37853FC22B2CE"),
        Offer("Facebook / Instagram (pre-installed)", "A shortcut that came with Windows.", "Facebook.Facebook", "Facebook.InstagramBeta", "FACEBOOK.*"),
        Offer("TikTok (pre-installed)", "A TikTok shortcut that came with Windows.", "BytedancePte.Ltd.TikTok"),
        Offer("Prime Video (pre-installed)", "A Prime Video shortcut that came with Windows.", "AmazonVideo.PrimeVideo"),
        Offer("Netflix (pre-installed)", "A Netflix shortcut that came with Windows.", "4DF9E0F8.Netflix"),

        Ask("Sticky Notes", "Your notes sync to your Microsoft account; check you don't need any first.", "Microsoft.MicrosoftStickyNotes"),
        Ask("Copilot", "Microsoft's AI assistant app.", "Microsoft.Copilot"),
        Ask("Outlook (new)", "The new Outlook mail app. Classic Outlook from Office isn't touched.", "Microsoft.OutlookForWindows"),
        Ask("Media Player", "Windows' music and video player.", "Microsoft.ZuneMusic"),

        Warn("Xbox app", "Needed if you play Game Pass or Xbox PC games.", "Microsoft.GamingApp", "Microsoft.XboxApp"),
        Warn("Xbox overlays", "Needed if you use Xbox party chat or speech-to-text in games.", "Microsoft.XboxGameOverlay", "Microsoft.XboxSpeechToTextOverlay"),
        Warn("Phone Link", "Needed if you use your phone with this PC.", "Microsoft.YourPhone"),
        Warn("Widgets", "The Widgets board on the taskbar stops working.", "MicrosoftWindows.Client.WebExperience"),
        Warn("Photos", "Windows' picture viewer; pick another viewer first.", "Microsoft.Windows.Photos"),
        Warn("Camera", "Needed if you use the webcam app.", "Microsoft.WindowsCamera"),
        Warn("Snipping Tool", "Win + Shift + S screenshots stop working.", "Microsoft.ScreenSketch"),
        Warn("OneDrive", "Check that every online-only file is downloaded or backed up first.", "Microsoft.OneDriveSync"),
    };

    public static IEnumerable<PreinstalledApp> All => NeverList.Concat(Removable);

    /// <summary>
    /// The catalog entry for a package name, never list first (a package on it can't be offered by a looser pattern).
    /// Null = not a known preinstalled app.
    /// </summary>
    public static PreinstalledApp? Find(string? packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName)) return null;
        return NeverList.FirstOrDefault(a => a.Matches(packageName)) ?? Removable.FirstOrDefault(a => a.Matches(packageName));
    }

    /// <summary>True if PC Tidy must never offer to remove this package.</summary>
    public static bool IsNever(string? packageName) => Find(packageName)?.Tier == PreinstalledTier.Never;

    /// <summary>Case-insensitive; a trailing * means "starts with".</summary>
    public static bool PatternMatches(string pattern, string name)
        => pattern.EndsWith('*')
            ? name.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
            : name.Equals(pattern, StringComparison.OrdinalIgnoreCase);
}
