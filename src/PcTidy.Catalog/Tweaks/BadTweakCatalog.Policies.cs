using PcTidy.Core.Model;
using static PcTidy.Catalog.Tweaks.TweakSources;

namespace PcTidy.Catalog.Tweaks;

public static partial class BadTweakCatalog
{
    /// <summary>
    /// Policy values that make Windows say "Managed by your organization", listed one by one (Plan §8).
    /// Each can be removed to bring back the normal Settings switch. Some are PC Tidy's own optional tweaks:
    /// they're listed too, so the user can see why the banner appears.
    /// </summary>
    public sealed record KnownPolicy(string Id, RegistryHive Hive, string KeyPath, string ValueName, string WhatItDoes, string Source);

    public static readonly IReadOnlyList<KnownPolicy> KnownPolicies = new[]
    {
        P("telemetry-level", @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", "Fixes the diagnostic data level; on Home and Pro, 0 counts as 1.", DiagnosticData),
        P("feedback-notifications", @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "DoNotShowFeedbackNotifications", "Hides feedback requests.", DiagnosticData),
        P("onesettings", @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "DisableOneSettingsDownloads", "Stops Windows downloading its remote settings (and Microsoft's fixes for bad diagnostics).", DiagnosticData),
        P("consumer-features", @"SOFTWARE\Policies\Microsoft\Windows\CloudContent", "DisableWindowsConsumerFeatures", "Meant to stop suggested apps; only works on Enterprise and Education.", ExperienceCsp),
        P("soft-landing", @"SOFTWARE\Policies\Microsoft\Windows\CloudContent", "DisableSoftLanding", "Meant to stop Windows tips; only works on Enterprise and Education.", ExperienceCsp),
        U("spotlight", @"Software\Policies\Microsoft\Windows\CloudContent", "DisableWindowsSpotlightFeatures", "Meant to turn off Spotlight; ignored on Home and Pro.", ExperienceCsp),
        U("tailored-policy", @"Software\Policies\Microsoft\Windows\CloudContent", "DisableTailoredExperiencesWithDiagnosticData", "Turns off tailored experiences (the Settings switch does the same).", ExperienceCsp),
        P("advertising-id-policy", @"SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo", "DisabledByGroupPolicy", "Forces the advertising ID off (the Settings switch does the same).", PrivacyCsp),
        P("activity-feed", @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed", "Turns off activity history.", PrivacyCsp),
        P("publish-activities", @"SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", "Turns off activity history.", PrivacyCsp),
        P("upload-activities", @"SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities", "Stops activity history uploads.", PrivacyCsp),
        P("clipboard-history-policy", @"SOFTWARE\Policies\Microsoft\Windows\System", "AllowClipboardHistory", "Blocks clipboard history (Win+V).", PrivacyCsp),
        P("cloud-clipboard-policy", @"SOFTWARE\Policies\Microsoft\Windows\System", "AllowCrossDeviceClipboard", "Blocks clipboard sync between devices.", PrivacyCsp),
        P("cortana", @"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowCortana", "Turns off Cortana (already removed from current Windows).", SearchCsp),
        P("web-search", @"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "DisableWebSearch", "Old web-search switch; current Windows ignores it.", SearchCsp),
        P("connected-search", @"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "ConnectedSearchUseWeb", "Turns off web results; only works on Enterprise and Education.", SearchCsp),
        P("cloud-search", @"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowCloudSearch", "Hides OneDrive and work results in search.", SearchCsp),
        P("search-highlights", @"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "EnableDynamicContentInWSB", "Turns off search highlights.", SearchCsp),
        U("web-results-start", @"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", "No web results in Start search (PC Tidy's Clean Explorer tweak uses this).", SearchCsp),
        P("game-dvr-policy", @"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR", "Blocks Game Bar recording.", GameMode),
        P("exclude-drivers", @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "ExcludeWUDriversInQualityUpdate", "Windows Update doesn't install drivers.", UpdateCsp),
        P("delivery-optimization-mode", @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", "Controls whether update pieces are shared with other PCs.", DeliveryOptimization),
        P("ceip", @"SOFTWARE\Policies\Microsoft\SQMClient\Windows", "CEIPEnable", "Turns off the Customer Experience Improvement Program.", ManageConnections),
        P("widgets-policy", @"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", "Turns off Widgets.", ExperienceCsp),
        U("copilot-policy", @"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", "Deprecated Copilot switch; does nothing to the current Copilot app.", WindowsAiCsp),
        P("recall-policy", @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "AllowRecallEnablement", "Removes Recall (Copilot+ PCs only).", WindowsAiCsp),
        P("ai-data-analysis", @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", "Stops Recall saving snapshots (Copilot+ PCs only).", WindowsAiCsp),
        P("onedrive-policy", @"SOFTWARE\Policies\Microsoft\Windows\OneDrive", "DisableFileSyncNGSC", "Stops OneDrive from syncing files, which can break folder backup.", ManageConnections),
        P("start-recommended", @"SOFTWARE\Policies\Microsoft\Windows\Explorer", "HideRecommendedSection", "Hides Start's Recommended section (not always honoured on Pro).", ExperienceCsp),
    };

    private static KnownPolicy P(string id, string key, string value, string what, string source) => new(id, RegistryHive.LocalMachine, key, value, what, source);
    private static KnownPolicy U(string id, string key, string value, string what, string source) => new(id, RegistryHive.CurrentUser, key, value, what, source);

    /// <summary>
    /// Policy keys the finder looks through for values it doesn't know (listed for information only). The Defender policy
    /// key isn't here: the Defender finding lists every value under it and offers to remove each one.
    /// </summary>
    public static readonly IReadOnlyList<(RegistryHive Hive, string KeyPath)> WatchedPolicyKeys = new[]
    {
        (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection"),
        (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\CloudContent"),
        (RegistryHive.CurrentUser, @"Software\Policies\Microsoft\Windows\CloudContent"),
        (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Windows Search"),
        (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System"),
        (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate"),
        (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU"),
        (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\WindowsStore"),
        (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Explorer"),
        (RegistryHive.CurrentUser, @"Software\Policies\Microsoft\Windows\Explorer"),
        (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI"),
    };

    public const string EdgePolicyKey = @"SOFTWARE\Policies\Microsoft\Edge";

    private static IEnumerable<BadTweakCheck> Policies()
    {
        foreach (var p in KnownPolicies)
        {
            yield return new BadTweakCheck
            {
                Id = "policy." + p.Id, Area = BadTweakArea.Policies, Severity = BadTweakSeverity.Low,
                Title = $"Policy: {p.ValueName}",
                WhyBad = p.WhatItDoes + " Because it's a policy, Settings says \"Managed by your organization\" and the switch is greyed out.",
                WindowsDefault = "Not set.",
                Flags = p.Hive == RegistryHive.LocalMachine ? BadTweakFlags.Admin : BadTweakFlags.None,
                Sources = Src(p.Source),
                Note = "Only remove it if you didn't set it on purpose. On a work or school PC, ask your IT team first.",
                Signals = new[] { Sig(new RegistryProbe(p.Hive, p.KeyPath, p.ValueName, ValueTest.Present, Array.Empty<string>()), Remove) },
            };
        }
        yield return new BadTweakCheck
        {
            Id = "policy.edge", Area = BadTweakArea.Policies, Severity = BadTweakSeverity.Low,
            Title = "Microsoft Edge policies are set",
            WhyBad = "Any Edge policy makes Edge say \"Your browser is managed by your organization\". Edge's own settings do the same jobs without that.",
            WindowsDefault = "Not set.", Flags = BadTweakFlags.Admin, Sources = Src(ManageConnections),
            Signals = new[] { Custom(CustomProbeKind.EdgePolicies, new GuidanceFix("Open edge://policy to see them. Remove them with the tool that set them, or in Registry Editor under HKLM\\SOFTWARE\\Policies\\Microsoft\\Edge.")) },
        };
        yield return new BadTweakCheck
        {
            Id = "policy.other", Area = BadTweakArea.Policies, Severity = BadTweakSeverity.Low,
            Title = "Other Windows policies are set",
            WhyBad = "These policies make Settings say \"Managed by your organization\". PC Tidy doesn't know them well enough to remove them for you.",
            WindowsDefault = "Not set.", Sources = Src(ManageConnections),
            Signals = new[] { Custom(CustomProbeKind.OtherPolicies, new GuidanceFix("Remove them with the tool that set them, or in Registry Editor (as admin) under the key shown.")) },
        };
    }
}
