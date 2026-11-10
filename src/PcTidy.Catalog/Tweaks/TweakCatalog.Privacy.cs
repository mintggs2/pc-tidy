using PcTidy.Core.Model;
using static PcTidy.Catalog.Tweaks.TweakSources;

namespace PcTidy.Catalog.Tweaks;

public static partial class TweakCatalog
{
    /// <summary>Copilot's Store package name (Community tweaks T6 §38: name unverified, so the row only shows when it's installed).</summary>
    public const string CopilotPackage = "Microsoft.Copilot";

    private static IEnumerable<TweakDefinition> Privacy()
    {
        // Community tweaks §1 item 6: per-user Settings values on Home/Pro, never the policies Pro ignores.
        yield return new TweakDefinition
        {
            Id = "privacy.advertising-id", Group = TweakGroup.Privacy, Title = "Turn off the advertising ID",
            Effect = "Apps can no longer read an ID that lets ad networks follow you across apps. Same as Settings → Privacy → General.",
            Verdict = TweakVerdict.Proven, Sources = Src(PrivacySettings, PrivacyCsp),
            Changes = Do(Cu(@"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0, 1)),
            Preset = TweakPreset.PrivacyBasic, PresetDefault = true,
        };
        yield return new TweakDefinition
        {
            Id = "privacy.tailored-experiences", Group = TweakGroup.Privacy, Title = "Turn off tailored experiences",
            Effect = "Microsoft stops using your diagnostic data to pick tips and offers for you. You may still see tips, just less personal ones.",
            Verdict = TweakVerdict.Proven, Sources = Src(PrivacySettings, ExperienceCsp),
            Changes = Do(Cu(@"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0, 1)),
            Preset = TweakPreset.PrivacyBasic, PresetDefault = true,
        };
        yield return new TweakDefinition
        {
            Id = "privacy.tips-suggestions", Group = TweakGroup.Privacy, Title = "Turn off tips, suggestions and silent app installs",
            Effect = "Stops suggested apps in Start, \"tips\" pop-ups, Settings suggestions and Windows quietly installing promoted apps. Uses your own Settings switches, so nothing says \"managed\".",
            Verdict = TweakVerdict.Proven, Sources = Src(ExperienceCsp, Win11DebloatRevert),
            Changes = Do(
                Cu(Cdm, "SubscribedContent-338388Enabled", 0, 1), Cu(Cdm, "SubscribedContent-338389Enabled", 0, 1),
                Cu(Cdm, "SubscribedContent-353694Enabled", 0, 1), Cu(Cdm, "SubscribedContent-353696Enabled", 0, 1),
                Cu(Cdm, "SubscribedContent-310093Enabled", 0, 1), Cu(Cdm, "SystemPaneSuggestionsEnabled", 0, 1),
                Cu(Cdm, "SilentInstalledAppsEnabled", 0, 1), Cu(Cdm, "SoftLandingEnabled", 0, 1)),
            Preset = TweakPreset.PrivacyBasic, PresetDefault = true,
        };
        yield return new TweakDefinition
        {
            Id = "privacy.lock-screen-fun-facts", Group = TweakGroup.Privacy, Title = "Turn off lock-screen \"fun facts\" and ads",
            Effect = "Removes the tips and promotions drawn over the lock screen. Your lock-screen picture (or Spotlight images) stays.",
            Verdict = TweakVerdict.Proven, Sources = Src(ExperienceCsp),
            Changes = Do(Cu(Cdm, "RotatingLockScreenOverlayEnabled", 0, 1), Cu(Cdm, "SubscribedContent-338387Enabled", 0, 1)),
            Preset = TweakPreset.PrivacyBasic, PresetDefault = true,
        };
        // Community tweaks §9.6: the registry values guides use for these two (RotatingLockScreenEnabled,
        // ShowSyncProviderNotifications) aren't documented by Microsoft, so PC Tidy points to the switch instead of writing them.
        yield return new TweakDefinition
        {
            Id = "privacy.lock-screen-spotlight", Group = TweakGroup.Privacy, Title = "Lock screen: your own picture instead of Spotlight",
            Effect = "Windows Spotlight changes the lock-screen picture from the internet and can show promotions with it. Picking \"Picture\" or \"Slideshow\" stops that.",
            Verdict = TweakVerdict.Situational, Sources = Src(ExperienceCsp),
            Caveat = "Only if you don't like Spotlight: the pictures themselves are harmless.",
            Changes = Do(new GuidanceChange("Settings → Personalization → Lock screen → Personalize your lock screen: choose Picture or Slideshow.", "ms-settings:lockscreen")),
            Undo = "Nothing to undo: PC Tidy only opens the page.",
        };
        yield return new TweakDefinition
        {
            Id = "privacy.explorer-sync-ads", Group = TweakGroup.Privacy, Title = "File Explorer: hide OneDrive and Microsoft 365 offers",
            Effect = "File Explorer sometimes shows a banner advertising OneDrive or Microsoft 365. One tick box in Folder Options turns those banners off; your files and OneDrive keep working.",
            Verdict = TweakVerdict.Proven, Sources = Src(FileExplorerHelp),
            Changes = Do(new GuidanceChange("File Explorer → ··· → Options → View tab: untick \"Show sync provider notifications\", then OK.")),
            Undo = "Nothing to undo: PC Tidy changes nothing here.",
        };
        yield return new TweakDefinition
        {
            Id = "privacy.optional-diagnostics-off", Group = TweakGroup.Privacy, Title = "Send only required diagnostic data",
            Effect = "Lowers diagnostic data from \"Optional\" to \"Required\", the same as the switch in Settings → Privacy → Diagnostics & feedback. Windows Update keeps working normally.",
            Verdict = TweakVerdict.Proven, Flags = TweakFlags.Admin, Sources = Src(DiagnosticData),
            Changes = Do(Lm(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection", "AllowTelemetry", 1, 3)),
            Preset = TweakPreset.PrivacyBasic, PresetDefault = true,
        };
        yield return new TweakDefinition
        {
            Id = "privacy.feedback-never", Group = TweakGroup.Privacy, Title = "Never ask for feedback",
            Effect = "Windows stops popping up \"How likely are you to recommend…\" surveys. Same as Feedback frequency: Never.",
            Verdict = TweakVerdict.Proven, Sources = Src(ManageConnections),
            Changes = Do(Cu(@"Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0, null)),
            Preset = TweakPreset.PrivacyBasic, PresetDefault = true,
        };
        yield return new TweakDefinition
        {
            Id = "privacy.inking-typing", Group = TweakGroup.Privacy, Title = "Turn off inking & typing personalization",
            Effect = "Windows stops building a personal word list from what you type and write to improve suggestions.",
            Verdict = TweakVerdict.Proven, Sources = Src(PrivacySettings),
            Changes = Do(Cu(@"Software\Microsoft\Input\TIPC", "Enabled", 0, 1)),
            Preset = TweakPreset.PrivacyBasic, PresetDefault = true,
        };
        yield return new TweakDefinition
        {
            Id = "privacy.copilot-app", Group = TweakGroup.Privacy, Title = "Remove the Copilot app",
            Effect = "Uninstalls the Copilot app for your account. Nothing else in Windows depends on it.",
            Verdict = TweakVerdict.Proven, Sources = Src(Copilot, WindowsAiCsp),
            Caveat = "To get it back, install Copilot from the Microsoft Store.",
            Changes = Do(new AppxRemoveChange(CopilotPackage)),
            Undo = "Reinstall Copilot from the Microsoft Store (PC Tidy can't put a removed app back by itself).",
            Reversible = false,
            Preset = TweakPreset.PrivacyBasic, PresetDefault = false,
        };
        yield return new TweakDefinition
        {
            Id = "privacy.ceip-off", Group = TweakGroup.Privacy, Title = "Turn off the Customer Experience Improvement Program",
            Effect = "Stops the old CEIP usage reports.",
            Verdict = TweakVerdict.Proven, Flags = TweakFlags.Admin | TweakFlags.ManagedPolicy, Sources = Src(ManageConnections),
            Changes = Do(Lm(@"SOFTWARE\Policies\Microsoft\SQMClient\Windows", "CEIPEnable", 0, null)),
            Preset = TweakPreset.PrivacyBasic, PresetDefault = false,
        };
        yield return new TweakDefinition
        {
            Id = "privacy.web-results-in-start", Group = TweakGroup.Privacy, Title = "No web results in Start search",
            Effect = "Start search only shows things on this PC instead of Bing results.",
            Verdict = TweakVerdict.Proven, Flags = TweakFlags.ManagedPolicy | TweakFlags.Admin | TweakFlags.RestartExplorer, Sources = Src(SearchCsp),
            Changes = Do(Cu(@"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1, null)),
            Preset = TweakPreset.CleanExplorer, PresetDefault = true,
        };
        yield return new TweakDefinition
        {
            Id = "privacy.activity-history", Group = TweakGroup.Privacy, Title = "Turn off activity history",
            Effect = "Windows stops recording and uploading which apps and files you use. Low value since Timeline was removed, but it still feeds \"resume\" cards.",
            Verdict = TweakVerdict.Proven, Flags = TweakFlags.Admin | TweakFlags.ManagedPolicy | TweakFlags.SignOut, Sources = Src(PrivacyCsp),
            Changes = Do(
                Lm(@"SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed", 0, null),
                Lm(@"SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0, null),
                Lm(@"SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities", 0, null)),
        };
        yield return new TweakDefinition
        {
            Id = "privacy.clipboard-history-off", Group = TweakGroup.Privacy, Title = "Turn off clipboard history",
            Effect = "Win+V stops keeping a list of what you copied (passwords copied from a password manager can end up there).",
            Verdict = TweakVerdict.Proven, Sources = Src(PrivacyCsp),
            Caveat = "It's off until you first press Win+V, so on many PCs this is already done.",
            Changes = Do(Cu(@"Software\Microsoft\Clipboard", "EnableClipboardHistory", 0, 0)),
        };
        yield return new TweakDefinition
        {
            Id = "privacy.office-telemetry", Group = TweakGroup.Privacy, Title = "Office: send less telemetry",
            Effect = "Office sends only basic telemetry. Connected features (co-authoring, online templates) keep working.",
            Verdict = TweakVerdict.Proven, Flags = TweakFlags.ManagedPolicy | TweakFlags.Admin, Sources = Src(ManageConnections),
            Applies = new TweakApplicability { RequiresKey = @"HKCU\Software\Microsoft\Office\16.0" },
            Changes = Do(Cu(@"Software\Policies\Microsoft\office\common\clienttelemetry", "SendTelemetry", 3, null)),
        };
        yield return new TweakDefinition
        {
            Id = "privacy.diagtrack-manual", Group = TweakGroup.Privacy, Title = "Telemetry service: don't start automatically",
            Effect = "Sets the Connected User Experiences and Telemetry service (DiagTrack) to Manual.",
            Verdict = TweakVerdict.Situational, Risk = TweakRisk.Medium, Flags = TweakFlags.ExpertOnly | TweakFlags.Admin | TweakFlags.Restart, Sources = Src(IotServices, DiagnosticData),
            Caveat = "Breaks Feedback Hub, Windows Insider and the Diagnostic Data Viewer; feature updates may turn it back on. \"Required\" diagnostic data is the supported way.",
            Changes = Do(new ServiceStartChange("DiagTrack", ServiceStart.Manual, ServiceStart.Automatic)),
        };
        yield return new TweakDefinition
        {
            Id = "privacy.location", Group = TweakGroup.Privacy, Title = "Location services",
            Effect = "Turning location off breaks automatic time zone, weather and Find my device. Decide in Settings.",
            Verdict = TweakVerdict.Situational, Sources = Src(PrivacySettings),
            Changes = Do(new GuidanceChange("Settings → Privacy & security → Location.", "ms-settings:privacy-location")),
            Undo = "Nothing to undo: PC Tidy only opens the page.",
        };
        yield return new TweakDefinition
        {
            Id = "privacy.find-my-device", Group = TweakGroup.Privacy, Title = "Find my device",
            Effect = "Little use on a desktop; useful on a laptop. Decide in Settings.",
            Verdict = TweakVerdict.Situational, Sources = Src(PrivacySettings),
            Changes = Do(new GuidanceChange("Settings → Privacy & security → Find my device.", "ms-settings:findmydevice")),
            Undo = "Nothing to undo: PC Tidy only opens the page.",
        };
        yield return new TweakDefinition
        {
            Id = "privacy.edge-settings", Group = TweakGroup.Privacy, Title = "Microsoft Edge privacy",
            Effect = "Edge policies make Edge say \"Managed by your organization\". Edge's own Privacy settings do the same job without that.",
            Verdict = TweakVerdict.Situational, Sources = Src(ManageConnections),
            Changes = Do(new GuidanceChange("Edge → Settings → Privacy, search, and services.")),
            Undo = "Nothing to undo: PC Tidy changes nothing here.",
        };

        // ---------------- placebo ----------------
        yield return Placebo("privacy.allow-telemetry-0", TweakGroup.Privacy, "AllowTelemetry = 0 policy",
            "On Home and Pro Windows treats 0 as 1 (Required), and it adds \"Managed by your organization\". \"Send only required diagnostic data\" above does the same.",
            Src(DiagnosticData), Lm(@"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0, null));
        yield return Placebo("privacy.recall", TweakGroup.Privacy, "Recall policies",
            "Recall only exists on Copilot+ PCs, so these policies do nothing on most PCs.",
            Src(WindowsAiCsp), Lm(@"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "AllowRecallEnablement", 0, null));
        yield return Placebo("privacy.turn-off-copilot-policy", TweakGroup.Privacy, "TurnOffWindowsCopilot policy",
            "Microsoft deprecated it; it does nothing to the current Copilot app. Remove the app instead.",
            Src(WindowsAiCsp), Cu(@"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1, null));

        // ---------------- harmful ----------------
        yield return Harmful("privacy.office-disconnected", "Office disconnectedstate = 2", "Kills every connected Office feature (co-authoring, online templates, OneDrive saving).",
            Src(ManageConnections), Cu(@"Software\Policies\Microsoft\office\16.0\common\privacy", "disconnectedstate", 2, null));
        yield return Harmful("privacy.hosts-telemetry-block", "Block Microsoft in the hosts file", "Blocking the wrong host breaks Windows Update, Store, activation and sign-in.",
            Src(Endpoints), new CommandChange("0.0.0.0 <Microsoft host> lines in the hosts file"));
    }
}
