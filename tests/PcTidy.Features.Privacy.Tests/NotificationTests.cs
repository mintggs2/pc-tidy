using PcTidy.Catalog;
using PcTidy.Catalog.Privacy;
using PcTidy.Features.Privacy.Common;
using PcTidy.Features.Privacy.Notifications;

namespace PcTidy.Features.Privacy.Tests;

/// <summary>Per-app notification switches and Windows tips/ads, read from a test registry area, run and undone for real there.</summary>
public class NotificationTests
{
    private const string Settings = NotificationApps.SettingsKey;

    private static void PutApps(TestRegistry reg)
    {
        reg.CreateKey($@"{Settings}\Contoso.Chat_abc123!App");                       // never toggled: on
        reg.SetDword($@"{Settings}\com.fabrikam.Player", "Enabled", 0);               // turned off
        reg.SetDword($@"{Settings}\Windows.Defender.SecurityCenter", "Enabled", 1);   // security: stays on
        reg.SetString($@"{Settings}\Odd.App", "Enabled", "yes");                      // unusual type: left alone
        reg.CreateKey($@"{Settings}\Windows.SystemToast.Suggested");
        reg.SetDword($@"{Settings}\Windows.SystemToast.Suggested", "LastNotificationAddedTime", 5);
    }

    [Fact]
    public void Enumeration_reads_every_app_switch_from_the_injected_root()
    {
        using var reg = new TestRegistry();
        PutApps(reg);

        var apps = AppNotificationsScanner.Read(reg.Registry).ToDictionary(a => a.AppId);

        Assert.Equal(5, apps.Count);
        Assert.True(apps["Contoso.Chat_abc123!App"].Enabled);
        Assert.False(apps["com.fabrikam.Player"].Enabled);
        Assert.Null(apps["Odd.App"].Enabled);
        Assert.True(apps["Windows.Defender.SecurityCenter"].Protected);
        Assert.Equal("Suggestions from Windows", apps["Windows.SystemToast.Suggested"].Name);
        Assert.Equal("Chat", apps["Contoso.Chat_abc123!App"].Name);
        Assert.Equal("Fabrikam", apps["com.fabrikam.Player"].Name);
        Assert.All(apps.Values, a => Assert.StartsWith(reg.Root + "\\", a.PhysicalKey));
    }

    [Fact]
    public async Task Rows_flip_each_app_and_never_offer_security_alerts_or_unusual_values()
    {
        using var reg = new TestRegistry();
        PutApps(reg);
        using var pc = TestPc.HandMade("notif-rows");

        var result = await new AppNotificationsScanner(_ => reg.Environment()).ScanAsync(pc.Context(), null, CancellationToken.None);
        var rows = result.AllItems.ToDictionary(i => i.Detail!);

        Assert.All(rows.Values, i => { Assert.False(i.DefaultSelected); Assert.False(string.IsNullOrWhiteSpace(i.Reason)); });
        var on = rows["Contoso.Chat_abc123!App"].Actions.Single();
        Assert.Equal(ActionKind.RegistrySetValue, on.Kind);
        Assert.Equal("0", on.Registry!.Data);
        Assert.Equal(reg.Key($@"{Settings}\Contoso.Chat_abc123!App"), on.Registry.KeyPath);
        Assert.Equal(RegistryKind.DWord, on.Registry.Kind);
        Assert.Equal("1", rows["com.fabrikam.Player"].Actions.Single().Registry!.Data);
        Assert.NotNull(rows["Windows.Defender.SecurityCenter"].DisabledReason);
        Assert.Empty(rows["Windows.Defender.SecurityCenter"].Actions);
        Assert.NotNull(rows["Odd.App"].DisabledReason);
        Assert.Empty(rows["Odd.App"].Actions);
        Assert.All(rows.Values.SelectMany(i => i.Actions), a => Assert.True(pc.Host.Safety.Check(a).Allowed));
    }

    [Fact]
    public void Dynamic_allowances_are_exactly_Enabled_0_or_1_under_each_current_app_and_never_security_alerts()
    {
        using var reg = new TestRegistry();
        PutApps(reg);

        var allowances = new NotificationAllowances(reg.Key(Settings)).Allowances().ToList();

        Assert.Equal(new[] { "com.fabrikam.Player", "Contoso.Chat_abc123!App", "Odd.App", "Windows.SystemToast.Suggested" },
            allowances.Select(a => a.KeyPath.Split('\\')[^1]).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        Assert.All(allowances, a =>
        {
            Assert.Equal(RegistryHive.CurrentUser, a.Hive);
            Assert.Equal(NotificationAllowances.Source, a.Source);
            Assert.StartsWith(reg.Key(Settings) + "\\", a.KeyPath);
            Assert.Equal(2, a.KeyPath.Split('\\').Length - reg.Key(Settings).Split('\\').Length + 1);
            Assert.Equal("Enabled", a.ValueName);
            Assert.Equal(RegistryKind.DWord, a.Kind);
            Assert.Equal(new[] { "0", "1" }, a.AllowedData);
            Assert.False(a.AllowDelete);
            Assert.False(a.NeedsAdmin);
        });
        // A key that doesn't exist gives no allowances (never an error).
        Assert.Empty(new NotificationAllowances(reg.Key(@"Software\Nothing\Here")).Allowances());
    }

    [Fact]
    public void The_real_catalog_allows_current_apps_only_with_0_or_1_and_refuses_anything_else()
    {
        var guard = new PcTidy.Safety.SafetyGuard(new SandboxKnownLocations(Area.NewFolder("guard")), new AppSettings());
        PlanItem Set(string app, string data, ActionKind kind = ActionKind.RegistrySetValue) => new()
        {
            Kind = kind, FeatureId = PrivacyIds.Notifications, Description = "test",
            Registry = new RegistryValueSpec
            {
                Hive = RegistryHive.CurrentUser, KeyPath = NotificationApps.AppKey(Settings, app), ValueName = "Enabled",
                Kind = RegistryKind.DWord, Data = data,
            },
        };

        // Read-only look at this PC's real list: every non-security app present when the catalog loaded is allowed.
        var real = NotificationApps.ReadAppIds().Where(id => !NotificationApps.IsProtected(id)).ToList();
        foreach (var app in real.Take(5))
        {
            Assert.True(guard.Check(Set(app, "0")).Allowed, app);
            Assert.True(guard.Check(Set(app, "1")).Allowed, app);
            Assert.False(guard.Check(Set(app, "2")).Allowed, app);
            Assert.False(guard.Check(Set(app, "", ActionKind.RegistryDeleteValue)).Allowed, app);
        }
        Assert.False(guard.Check(Set("PcTidy.Test.NotAnApp." + Guid.NewGuid().ToString("N"), "0")).Allowed);
        Assert.False(guard.Check(Set("Windows.Defender.SecurityCenter", "0")).Allowed);
        Assert.Null(RegistryCatalog.Find(RegistryHive.CurrentUser, Settings, "Enabled"));
    }

    [Fact]
    public void Tips_values_are_allowed_exactly_once_with_the_same_data_as_Tweaks()
    {
        var all = RegistryCatalog.All;
        foreach (var v in TipsCatalog.AllValues)
        {
            var matches = all.Where(a => a.Hive == RegistryHive.CurrentUser
                && string.Equals(a.KeyPath, v.KeyPath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.ValueName, v.ValueName, StringComparison.OrdinalIgnoreCase)).ToList();
            var a = Assert.Single(matches);
            Assert.Equal(RegistryKind.DWord, a.Kind);
            Assert.Contains("0", a.AllowedData);
            Assert.Contains("1", a.AllowedData);
            Assert.False(a.NeedsAdmin);
        }
        var shared = PrivacyRegistryAllowances.SharedWithTweaks();
        Assert.Contains(shared, v => v.ValueName == "SubscribedContent-338388Enabled");
        Assert.All(new PrivacyRegistryAllowances().Allowances(), a =>
        {
            Assert.DoesNotContain(shared, v => v.ValueName == a.ValueName && v.KeyPath == a.KeyPath);
            Assert.Equal(new[] { "0", "1" }, a.AllowedData);
            Assert.False(a.AllowDelete);
        });
        Assert.Contains(new PrivacyRegistryAllowances().Allowances(), a => a.ValueName == "ScoobeSystemSettingEnabled");
    }

    [Fact]
    public async Task Tips_on_their_own_page_are_offered_unticked_and_in_optimize_all_only_for_the_privacy_preset()
    {
        using var reg = new TestRegistry();
        reg.SetDword(TipsCatalog.ContentDeliveryManager, "RotatingLockScreenOverlayEnabled", 0);
        reg.SetDword(TipsCatalog.ContentDeliveryManager, "SubscribedContent-338387Enabled", 0);
        using var pc = TestPc.HandMade("tips");
        var scanner = new TipsScanner(_ => reg.Environment());
        Assert.True(scanner.IncludedInOptimizeAll);

        var page = (await scanner.ScanAsync(pc.Context(), null, CancellationToken.None)).AllItems.ToList();
        Assert.Equal(TipsCatalog.All.Count, page.Count);
        Assert.All(page, i => Assert.False(i.DefaultSelected));
        var lockScreen = page.Single(i => i.Title.Contains("lock screen", StringComparison.OrdinalIgnoreCase));
        Assert.StartsWith("Turn back on", lockScreen.Title);
        Assert.Equal(Badge.Check, lockScreen.Badge);
        Assert.All(lockScreen.Actions, a => Assert.Equal("1", a.Registry!.Data));
        Assert.All(page.Where(i => i.Title.StartsWith("Turn off")), i =>
        {
            Assert.Equal(Badge.Safe, i.Badge);
            Assert.All(i.Actions, a => Assert.Equal("0", a.Registry!.Data));
        });

        Assert.Empty((await scanner.ScanAsync(pc.Context("Safe"), null, CancellationToken.None)).AllItems);
        Assert.Empty((await scanner.ScanAsync(pc.Context("Recommended"), null, CancellationToken.None)).AllItems);

        var preset = (await scanner.ScanAsync(pc.Context(PrivacyIds.PrivacyPreset), null, CancellationToken.None)).ToList();
        var shared = PrivacyRegistryAllowances.SharedWithTweaks().Select(v => v.ValueName).ToHashSet();
        Assert.NotEmpty(preset);
        Assert.All(preset, i =>
        {
            Assert.True(i.DefaultSelected);
            Assert.Equal(Badge.Safe, i.Badge);
            Assert.True(PcTidy.UI.Flow.OptimizePresets.IsTickableByDefault(i));
            Assert.All(i.Actions, a => { Assert.Equal("0", a.Registry!.Data); Assert.DoesNotContain(a.Registry.ValueName!, shared); });
        });
    }

    [Fact]
    public async Task Turning_switches_off_and_on_then_undo_leaves_the_registry_exactly_as_it_was()
    {
        using var reg = new TestRegistry();
        PutApps(reg);
        reg.SetDword(TipsCatalog.ContentDeliveryManager, "SystemPaneSuggestionsEnabled", 1);
        var before = reg.Snapshot();
        using var pc = TestPc.HandMade("notif-e2e");
        var module = new NotificationsModule(_ => reg.Environment());

        var apps = (await module.Apps.ScanAsync(pc.Context(), null, CancellationToken.None)).AllItems.Where(i => i.Actions.Count > 0).ToList();
        var tips = (await module.Tips.ScanAsync(pc.Context(), null, CancellationToken.None)).AllItems.ToList();
        var picked = apps.Concat(tips.Where(t => t.Title.Contains("Start", StringComparison.Ordinal) || t.Title.Contains("Finish", StringComparison.OrdinalIgnoreCase))).ToList();
        var plan = PlanBuilder.FromSelection(PrivacyIds.Notifications, "Notifications", picked);
        Assert.True(plan.Items.Count >= 5);
        Assert.All(plan.Items, i => Assert.True(pc.Host.Safety.Check(i).Allowed, i.Description));

        var run = await pc.Host.Executor.ExecuteAsync(plan, TestPc.Real, null, CancellationToken.None);
        Assert.True(run.DoneCount == plan.Items.Count, Runs.Problems(run));
        Assert.Equal(new DwordRead(true, 0), reg.Registry.ReadDword($@"{Settings}\Contoso.Chat_abc123!App", "Enabled"));
        Assert.Equal(new DwordRead(true, 1), reg.Registry.ReadDword($@"{Settings}\com.fabrikam.Player", "Enabled"));
        Assert.Equal(new DwordRead(true, 0), reg.Registry.ReadDword(TipsCatalog.ContentDeliveryManager, "SystemPaneSuggestionsEnabled"));
        Assert.Equal(new DwordRead(true, 0), reg.Registry.ReadDword(TipsCatalog.UserProfileEngagement, "ScoobeSystemSettingEnabled"));
        Assert.NotEqual(before, reg.Snapshot());

        var undo = await pc.Host.Undo.UndoRunAsync(run.RunId, null, CancellationToken.None);
        Assert.True(undo.Complete, string.Join("\n", undo.Messages));
        Assert.Equal(before, reg.Snapshot());
    }

    [Fact]
    public void Copilot_plus_needs_both_an_NPU_and_the_Recall_feature()
    {
        Assert.False(AiFeatureFacts.None.IsCopilotPlus);
        Assert.False(new AiFeatureFacts(null, HasNpu: true, RecallState.NotPresent).IsCopilotPlus);
        Assert.False(new AiFeatureFacts(null, HasNpu: false, RecallState.Disabled).IsCopilotPlus);
        Assert.True(new AiFeatureFacts(null, HasNpu: true, RecallState.Enabled).IsCopilotPlus);

        var normal = AiFeaturesScanner.RecallRow(AiFeatureFacts.None);
        Assert.Equal("Not on this PC", normal.CurrentState);
        Assert.NotNull(normal.DisabledReason);
        Assert.Empty(normal.Actions);

        var plus = AiFeaturesScanner.RecallRow(new AiFeatureFacts(null, true, RecallState.Enabled));
        var off = plus.Actions.Single();
        Assert.Equal(ActionKind.OptionalFeatureRemove, off.Kind);
        Assert.Equal("Recall", off.Args!["name"]);
        Assert.True(off.NeedsAdmin && off.IsSystemChange && off.Reversible);
        Assert.False(plus.DefaultSelected);
    }

    [Fact]
    public void This_normal_pc_is_not_detected_as_copilot_plus()
    {
        // Read-only: PackageManager and WMI only.
        var facts = new WindowsAiFeatureProbe().Read(CancellationToken.None);
        if (!facts.HasNpu) Assert.False(facts.IsCopilotPlus);
        Assert.Equal(facts.HasNpu && facts.Recall != RecallState.NotPresent, facts.IsCopilotPlus);
        // The machine these tests are written on has no NPU.
        Assert.False(facts.IsCopilotPlus, "Detected a Copilot+ PC: if this machine really has an NPU and Recall, update this test.");
    }

    [Fact]
    public void Copilot_removal_is_permanent_unticked_and_allowed_by_the_guard()
    {
        var guard = new PcTidy.Safety.SafetyGuard(new SandboxKnownLocations(Area.NewFolder("guard")), new AppSettings());
        var row = AiFeaturesScanner.CopilotRow(new AiFeatureFacts("Microsoft.Copilot_1.25.1.0_x64__8wekyb3d8bbwe", false, RecallState.NotPresent));
        Assert.Equal(Badge.Permanent, row.Badge);
        Assert.False(row.DefaultSelected);
        var a = row.Actions.Single();
        Assert.Equal(ActionKind.AppxRemoveForUser, a.Kind);
        Assert.False(a.Reversible);
        Assert.True(guard.Check(a).Allowed);
        Assert.NotNull(AiFeaturesScanner.CopilotRow(AiFeatureFacts.None).DisabledReason);
    }

    [Fact]
    public void Modules_are_wired_as_the_plan_says()
    {
        var privacy = new PrivacyModule();
        Assert.Equal(("privacy", "Privacy sweep", NavGroup.You, "ShieldKeyhole24", Release.V3), (privacy.Id, privacy.Title, privacy.Group, privacy.Icon, privacy.Release));
        Assert.All(privacy.Scanners, s => Assert.False(s.IncludedInOptimizeAll));
        var notifications = new NotificationsModule();
        Assert.Equal(("notifications", "Notifications", NavGroup.You, "Alert24", Release.V3), (notifications.Id, notifications.Title, notifications.Group, notifications.Icon, notifications.Release));
        Assert.Equal(new[] { true, false, false }, notifications.Scanners.Select(s => s.IncludedInOptimizeAll));
        Assert.All(notifications.Scanners.Concat(privacy.Scanners), s => Assert.Equal(Release.V3, s.Release));
        Assert.Contains(PrivacyModule.Shortcuts, s => s.SettingsTarget == "ms-settings:clipboard");
        Assert.Contains(PrivacyModule.Shortcuts, s => s.SettingsTarget == "ms-settings:privacy-activityhistory");
        Assert.Contains(PrivacyModule.Shortcuts, s => s.Navigate == NavigationRequest.CleanPrivacyExtras);
    }

    [Theory]
    [InlineData("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", "Windows Calculator")]
    [InlineData("com.squirrel.Discord.Discord", "Discord")]
    [InlineData("c:.apps.tool.9.2.0.mytool.exe", "Mytool")]
    [InlineData("Logi.GHUB.Systray", "GHUB")]
    [InlineData("com.Roblox.Player", "Roblox")]
    public void App_ids_get_readable_names(string id, string expected) => Assert.Equal(expected, AppNames.Guess(id));
}

internal static class ScanResultExtensions
{
    public static List<ScanItem> ToList(this ScanResult r) => r.AllItems.ToList();
}
