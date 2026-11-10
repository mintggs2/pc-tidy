using PcTidy.Catalog.Apps;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Apps.Inventory;
using PcTidy.Features.Apps.Uninstall;
using PcTidy.Hosting;
using Xunit;

namespace PcTidy.Features.Apps.Tests;

public sealed class CatalogTests
{
    [Fact]
    public void Removable_lists_never_include_a_never_list_package()
    {
        foreach (var app in PreinstalledApps.Removable)
        {
            Assert.NotEqual(PreinstalledTier.Never, app.Tier);
            foreach (var pattern in app.PackagePatterns)
            {
                // A wildcard pattern is probed with a made-up package name it would match.
                var probe = pattern.EndsWith('*') ? pattern.TrimEnd('*') + "Probe" : pattern;
                Assert.False(PreinstalledApps.IsNever(probe), $"{app.FriendlyName}: {pattern} is on the never list");
                Assert.Same(app, PreinstalledApps.Find(probe));
            }
        }
    }

    [Theory]
    [InlineData("Microsoft.WindowsStore")]
    [InlineData("Microsoft.DesktopAppInstaller")]
    [InlineData("Microsoft.VCLibs.140.00.UWPDesktop")]
    [InlineData("Microsoft.UI.Xaml.2.8")]
    [InlineData("Microsoft.WindowsAppRuntime.1.5")]
    [InlineData("Microsoft.SecHealthUI")]
    [InlineData("Microsoft.WindowsTerminal")]
    [InlineData("Microsoft.WindowsNotepad")]
    [InlineData("Microsoft.WindowsCalculator")]
    [InlineData("Microsoft.Paint")]
    [InlineData("Microsoft.MicrosoftEdge.Stable")]
    [InlineData("Microsoft.WebView2Runtime")]
    [InlineData("Microsoft.GamingServices")]
    [InlineData("Microsoft.XboxIdentityProvider")]
    [InlineData("Microsoft.HEVCVideoExtension")]
    public void Plan_never_list_packages_are_never_offered(string package)
    {
        Assert.True(PreinstalledApps.IsNever(package));
        var app = new InstalledApp
        {
            Id = "store:" + package, Name = package, Source = AppSource.Store, PackageName = package,
            PackageFullName = package + "_1.0.0.0_x64__8wekyb3d8bbwe", Preinstalled = PreinstalledApps.Find(package),
        };
        var option = UninstallPlanner.For(app);
        Assert.False(option.IsAvailable);
        Assert.False(string.IsNullOrWhiteSpace(option.BlockedReason));
    }

    [Theory]
    [InlineData("Clipchamp.Clipchamp", PreinstalledTier.Offer)]
    [InlineData("Microsoft.BingWeather", PreinstalledTier.Offer)]
    [InlineData("king.com.CandyCrushSaga", PreinstalledTier.Offer)]
    [InlineData("Microsoft.MicrosoftStickyNotes", PreinstalledTier.AskFirst)]
    [InlineData("Microsoft.YourPhone", PreinstalledTier.Warn)]
    [InlineData("Microsoft.GamingApp", PreinstalledTier.Warn)]
    public void Plan_offer_and_warn_packages_are_found(string package, PreinstalledTier tier)
        => Assert.Equal(tier, PreinstalledApps.Find(package)!.Tier);

    [Fact]
    public void Every_catalog_entry_explains_itself()
    {
        Assert.All(PreinstalledApps.All, a => Assert.False(string.IsNullOrWhiteSpace(a.Reason)));
        Assert.All(DesktopAppRules.NeverUninstall.Concat(DesktopAppRules.Warnings), r => Assert.False(string.IsNullOrWhiteSpace(r.Message)));
    }

    [Theory]
    [InlineData("Update", true)]
    [InlineData("Microsoft", true)]
    [InlineData("Microsoft Teams", true)]
    [InlineData("NVIDIA Corporation", true)]
    [InlineData("Common Files", true)]
    [InlineData("WebView2", true)]
    [InlineData("VLC", true)]
    [InlineData("Contoso Editor", false)]
    [InlineData("Audacity", false)]
    public void Generic_and_keeper_names_are_unsafe(string name, bool unsafeName)
        => Assert.Equal(unsafeName, Catalog.Apps.LeftoverNameRules.IsUnsafeName(name, out _));
}

public sealed class UninstallPlannerTests
{
    private static InstalledApp Desktop() => new()
    {
        Id = "desktop:HKLM64:Editor", Name = "Contoso Editor", Source = AppSource.Desktop, Scope = AppScope.Machine,
        UninstallString = "\"C:\\Program Files\\Contoso\\uninst.exe\" /S",
    };

    [Fact]
    public void Desktop_uninstall_runs_the_apps_own_uninstaller_after_a_restore_point()
    {
        var item = UninstallPlanner.For(Desktop()).Item!;
        Assert.Equal(ActionKind.RunUninstaller, item.Kind);
        Assert.True(item.IsSystemChange);
        Assert.False(item.Reversible);
        Assert.True(item.NeedsAdmin);
        Assert.Equal("\"C:\\Program Files\\Contoso\\uninst.exe\" /S", item.Args!["uninstallString"]);
        Assert.Equal(AppsFeature.Id, item.FeatureId);
    }

    [Fact]
    public void Msi_and_winget_apps_carry_their_ids_with_the_uninstaller_as_fallback()
    {
        var msi = UninstallPlanner.For(Desktop() with { MsiProductCode = "{11111111-2222-3333-4444-555555555555}" }).Item!;
        Assert.Equal("{11111111-2222-3333-4444-555555555555}", msi.Args!["msiProductCode"]);
        var winget = UninstallPlanner.For(Desktop() with { WingetId = "Contoso.Editor", Scope = AppScope.User }).Item!;
        Assert.Equal("Contoso.Editor", winget.Args!["wingetId"]);
        Assert.True(winget.Args.ContainsKey("uninstallString"));
        Assert.False(winget.NeedsAdmin);
    }

    /// <summary>The admin helper refuses to run winget, so an uninstall only winget can do never asks for admin rights.</summary>
    [Fact]
    public void A_machine_app_only_winget_can_remove_runs_without_admin()
    {
        var only = UninstallPlanner.For(Desktop() with { UninstallString = null, WingetId = "Contoso.Editor" }).Item!;
        Assert.Equal(ActionKind.RunUninstaller, only.Kind);
        Assert.False(only.NeedsAdmin);
        Assert.Equal("Contoso.Editor", only.Args!["wingetId"]);
        Assert.False(only.Args.ContainsKey("uninstallString"));
        Assert.Contains("winget", only.Description);
        // With its own uninstaller too, a machine-wide app still uses the admin helper (which runs that uninstaller).
        Assert.True(UninstallPlanner.For(Desktop() with { WingetId = "Contoso.Editor" }).Item!.NeedsAdmin);
    }

    [Fact]
    public void Apps_without_an_uninstaller_or_on_the_never_list_are_not_offered()
    {
        Assert.False(UninstallPlanner.For(Desktop() with { UninstallString = null }).IsAvailable);
        Assert.False(UninstallPlanner.For(Desktop() with { NoRemove = true }).IsAvailable);
        Assert.False(UninstallPlanner.For(Desktop() with { Name = "Microsoft Edge WebView2 Runtime" }).IsAvailable);
        Assert.False(UninstallPlanner.For(Desktop() with { Name = "Microsoft Edge" }).IsAvailable);
        Assert.NotNull(UninstallPlanner.For(Desktop() with { Name = "Microsoft OneDrive" }).Warning);
    }

    [Fact]
    public void Games_are_removed_through_their_launcher()
    {
        var steam = UninstallPlanner.For(new InstalledApp { Id = "steam:440", Name = "TF2", Source = AppSource.Steam, LauncherId = "440" }).Item!;
        Assert.Equal(ActionKind.OpenExternal, steam.Kind);
        Assert.Equal("steam://uninstall/440", steam.Args!["target"]);
        var epic = UninstallPlanner.For(new InstalledApp { Id = "epic:Fn", Name = "Game", Source = AppSource.Epic, LauncherId = "Fn" }).Item!;
        Assert.Equal(ActionKind.OpenExternal, epic.Kind);
        Assert.StartsWith("com.epicgames.launcher://", epic.Args!["target"]);
    }

    [Fact]
    public void Store_removal_is_per_user_by_package_full_name()
    {
        var item = UninstallPlanner.For(new InstalledApp
        {
            Id = "store:clip", Name = "Clipchamp", Source = AppSource.Store, PackageName = "Clipchamp.Clipchamp",
            PackageFullName = "Clipchamp.Clipchamp_3.0.0.0_neutral__yxz26nhyzhsrt", Preinstalled = PreinstalledApps.Find("Clipchamp.Clipchamp"),
        }).Item!;
        Assert.Equal(ActionKind.AppxRemoveForUser, item.Kind);
        Assert.Equal("Clipchamp.Clipchamp_3.0.0.0_neutral__yxz26nhyzhsrt", item.Args!["packageFullName"]);
        Assert.False(item.Reversible);
        Assert.False(item.NeedsAdmin);
        Assert.False(item.IsSystemChange);
    }

    [Fact]
    public void The_real_guard_accepts_every_kind_of_removal_the_planner_builds()
    {
        using var box = new Sandbox();
        using var host = PcTidyHost.CreateSandbox(box.Root, SandboxProfile.Minimal);
        var items = new[]
        {
            UninstallPlanner.For(Desktop()).Item!,
            UninstallPlanner.For(Desktop() with { MsiProductCode = "{11111111-2222-3333-4444-555555555555}" }).Item!,
            UninstallPlanner.For(new InstalledApp { Id = "steam:440", Name = "TF2", Source = AppSource.Steam, LauncherId = "440" }).Item!,
            UninstallPlanner.For(new InstalledApp { Id = "epic:Fn", Name = "Game", Source = AppSource.Epic, LauncherId = "Fn" }).Item!,
            UninstallPlanner.For(new InstalledApp
            {
                Id = "store:clip", Name = "Clipchamp", Source = AppSource.Store, PackageName = "Clipchamp.Clipchamp",
                PackageFullName = "Clipchamp.Clipchamp_3.0.0.0_neutral__yxz26nhyzhsrt",
            }).Item!,
        };
        foreach (var item in items)
        {
            var verdict = host.Safety.Check(item);
            Assert.True(verdict.Allowed, $"{item.Kind}: {verdict.Reason}");
        }
    }

    [Fact]
    public async Task Uninstall_simulation_changes_nothing()
    {
        using var box = new Sandbox();
        using var host = PcTidyHost.CreateSandbox(box.Root, SandboxProfile.Minimal);
        var plan = new ActionPlan { FeatureId = AppsFeature.Id, Title = "Uninstall Contoso Editor", Items = new[] { UninstallPlanner.For(Desktop()).Item! } };
        var run = await host.Executor.ExecuteAsync(plan, new ExecuteOptions { Mode = ExecutionMode.Simulate }, null, CancellationToken.None);
        Assert.Equal(RunStatus.Simulated, run.Status);
        Assert.All(run.Steps, s => Assert.NotEqual(StepStatus.Done, s.Status));
    }
}
