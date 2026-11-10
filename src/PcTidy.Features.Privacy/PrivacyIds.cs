namespace PcTidy.Features.Privacy;

/// <summary>Feature, scanner and group ids of the Privacy sweep and Notification manager.</summary>
public static class PrivacyIds
{
    /// <summary>Privacy sweep (module and its plan items).</summary>
    public const string Privacy = "privacy";
    /// <summary>Notification manager (module and its plan items).</summary>
    public const string Notifications = "notifications";

    public const string RecentGroup = "privacy.recent";
    public const string WindowsGroup = "privacy.windows";
    public const string BrowsersGroup = "privacy.browsers";

    public const string AppsGroup = "notifications.apps";
    /// <summary>Contains "tips" so an Optimize-all preset rule can pick it (see the vault page).</summary>
    public const string TipsGroup = "notifications.tips";
    public const string AiGroup = "notifications.ai";

    /// <summary>The Optimize-all preset that turns tips and ads off (Plan: "tips/ads off as part of the Privacy preset only").</summary>
    public const string PrivacyPreset = "Privacy";

    /// <summary>Test area every redirected registry root must sit under.</summary>
    public const string TestRegistryArea = @"Software\PCTidyTest";
    /// <summary>Where the simulation sandbox reads and writes (empty unless a test put values there).</summary>
    public const string SandboxRegistryRoot = @"Software\PCTidyTest\privacy\sandbox";
}
