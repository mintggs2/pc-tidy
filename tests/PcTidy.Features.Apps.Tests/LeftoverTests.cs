using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Apps.Inventory;
using PcTidy.Features.Apps.Leftovers;
using PcTidy.Hosting;
using PcTidy.TestbedGen;
using Xunit;

namespace PcTidy.Features.Apps.Tests;

/// <summary>Leftover matching on a sandbox PC, checked by the REAL SafetyGuard (PcTidyHost.CreateSandbox).</summary>
public sealed class LeftoverTests : IDisposable
{
    private readonly Sandbox _box = new(SandboxProfile.Minimal);
    private readonly FakeRegistry _reg = new();
    private readonly PcTidyHost _host;

    public LeftoverTests() => _host = PcTidyHost.CreateSandbox(_box.Root, SandboxProfile.Minimal);

    public void Dispose()
    {
        _host.Dispose();
        _reg.Dispose();
        _box.Dispose();
    }

    private SandboxKnownLocations K => _box.Locations;

    private LeftoverScanner Scanner() => new(K, _host.Safety, _reg.Reader, new ManagedFileWalker());

    private InstalledApp Editor() => new()
    {
        Id = "desktop:HKLM64:ContosoEditor",
        Name = "Contoso Editor 2.1",
        Publisher = "Contoso Software, Inc.",
        Source = AppSource.Desktop,
        InstallLocation = Path.Combine(K.ProgramFiles, "Contoso Software", "Contoso Editor"),
    };

    private IReadOnlyList<LeftoverFinding> Scan(InstalledApp removed, params InstalledApp[] others)
        => Scanner().Scan(removed, others, CancellationToken.None);

    [Fact]
    public void Exact_name_matches_are_found_and_rated()
    {
        var publisherFolder = _box.Folder(Path.Combine(K.LocalAppData, "Contoso Software", "Contoso Editor"), "cache.bin", "logs\\a.log");
        var roaming = _box.Folder(Path.Combine(K.RoamingAppData, "Contoso Editor"), "settings.json");
        var oldInstall = _box.Folder(Editor().InstallLocation!, "left.dll");
        _box.Folder(Path.Combine(K.RoamingAppData, "Contoso Editor Backup"), "x.bin"); // similar, not exact: never matched

        var findings = Scan(Editor());
        var folders = findings.Where(f => f.Kind == LeftoverKind.Folder).ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(3, folders.Count);

        var best = folders[publisherFolder];
        Assert.Equal(LeftoverConfidence.VeryGood, best.Confidence);
        Assert.Null(best.NotRemovableReason);
        Assert.True(best.PreSelected);
        Assert.Equal(2 * 1234L, best.SizeBytes);

        var good = folders[roaming];
        Assert.Equal(LeftoverConfidence.Good, good.Confidence);
        Assert.False(good.PreSelected); // only very good matches are ticked

        // Its old install folder is a very good match, but the real guard keeps Program Files untouched.
        var old = folders[oldInstall];
        Assert.Equal(LeftoverConfidence.VeryGood, old.Confidence);
        Assert.NotNull(old.NotRemovableReason);
        Assert.False(old.PreSelected);

        Assert.DoesNotContain(findings, f => f.Path.EndsWith("Backup", StringComparison.OrdinalIgnoreCase));
        Assert.All(findings, f => Assert.Equal(ActionKind.QuarantinePath, f.Action!.Kind));
    }

    [Theory]
    [InlineData("Update", "Update")]
    [InlineData("Microsoft", "Microsoft")]
    [InlineData("Microsoft Helper", "Microsoft Helper")]
    [InlineData("Go", "Go")]
    [InlineData("Common Files", "Common Files")]
    public void Generic_short_and_keeper_names_are_never_matched(string appName, string folderName)
    {
        _box.Folder(Path.Combine(K.LocalAppData, folderName));
        _box.Folder(Path.Combine(K.RoamingAppData, folderName));
        var app = new InstalledApp
        {
            Id = "desktop:x", Name = appName, Source = AppSource.Desktop,
            InstallLocation = Path.Combine(K.ProgramFiles, folderName),
        };
        Assert.Empty(Scan(app));
    }

    [Fact]
    public void A_shared_publisher_folder_is_never_offered()
    {
        // App "Fabrikam" by "Fabrikam Ltd": its name equals the publisher, and Fabrikam Studio (still installed) lives inside.
        var shared = _box.Folder(Path.Combine(K.LocalAppData, "Fabrikam"), "shared.bin");
        _box.Folder(Path.Combine(shared, "Fabrikam Studio"), "studio.bin");
        var removed = new InstalledApp { Id = "desktop:fab", Name = "Fabrikam", Publisher = "Fabrikam Ltd", Source = AppSource.Desktop };
        var studio = new InstalledApp { Id = "desktop:studio", Name = "Fabrikam Studio", Publisher = "Fabrikam Ltd", Source = AppSource.Desktop };

        var f = Assert.Single(Scan(removed, studio));
        Assert.Equal(LeftoverConfidence.Bad, f.Confidence);
        Assert.Equal("Other apps from Fabrikam share this folder.", f.NotRemovableReason);
        Assert.False(f.PreSelected);
    }

    [Fact]
    public void A_folder_another_app_still_uses_is_never_offered()
    {
        var folder = _box.Folder(Path.Combine(K.RoamingAppData, "Contoso Editor"), "x.bin");
        var helper = new InstalledApp
        {
            Id = "desktop:helper", Name = "Contoso Plugin Host", Source = AppSource.Desktop,
            InstallLocation = Path.Combine(folder, "plugins"),
        };
        var f = Assert.Single(Scan(Editor(), helper));
        Assert.Equal(LeftoverConfidence.Bad, f.Confidence);
        Assert.Equal("Contoso Plugin Host still uses this folder.", f.NotRemovableReason);
    }

    [Fact]
    public void A_name_another_installed_app_answers_to_is_never_matched()
    {
        _box.Folder(Path.Combine(K.RoamingAppData, "Contoso Editor"), "x.bin");
        // A newer version side by side cleans to the same name.
        var newer = new InstalledApp { Id = "desktop:new", Name = "Contoso Editor 3.0", Source = AppSource.Desktop };
        Assert.DoesNotContain(Scan(Editor(), newer), f => f.Kind == LeftoverKind.Folder);
    }

    [Fact]
    public void Folders_that_look_like_your_own_files_are_never_pre_ticked()
    {
        var folder = _box.Folder(Path.Combine(K.LocalAppData, "Contoso Software", "Contoso Editor"), "Saves\\slot1.sav");
        var f = Scan(Editor()).Single(x => x.Path.Equals(folder, StringComparison.OrdinalIgnoreCase));
        Assert.True(f.MayHoldYourFiles);
        Assert.Equal(LeftoverConfidence.Questionable, f.Confidence);
        Assert.False(f.PreSelected);
    }

    [Fact]
    public void Registry_leftovers_are_exact_publisher_app_keys_and_shown_for_information_only()
    {
        _reg.Key(@"HKCU\Software\Contoso Software\Contoso Editor", ("Theme", "dark"));
        _reg.Key(@"HKCU\Software\Contoso Editor", ("Theme", "dark")); // Software\<App> alone: never matched
        var keys = Scan(Editor()).Where(f => f.Kind == LeftoverKind.RegistryKey).ToList();
        var key = Assert.Single(keys);
        Assert.Equal(@"HKCU\Software\Contoso Software\Contoso Editor", key.Path);
        Assert.Equal(ActionKind.RegistryDeleteKey, key.Action!.Kind);
        Assert.Equal(@"Software\Contoso Software\Contoso Editor", key.Action.Registry!.KeyPath);
        // The guard's registry allow-list is static, so the row is shown with Remove disabled.
        Assert.StartsWith("Shown so you know.", key.NotRemovableReason);
        var row = LeftoverItems.ToScanItem(key, "Contoso Editor");
        Assert.NotNull(row.DisabledReason);
        Assert.False(row.DefaultSelected);
    }

    [Fact]
    public void Scan_rows_follow_the_badge_rules()
    {
        _box.Folder(Path.Combine(K.LocalAppData, "Contoso Software", "Contoso Editor"));
        _box.Folder(Path.Combine(K.RoamingAppData, "Contoso Editor"));
        var groups = LeftoverItems.ToGroups(Scan(Editor()), "Contoso Editor");
        foreach (var item in groups.SelectMany(g => g.Items))
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Reason));
            if (item.Badge != Badge.Safe) Assert.False(item.DefaultSelected);
            if (item.DefaultSelected) Assert.Null(item.DisabledReason);
        }
        Assert.All(groups, g => Assert.False(string.IsNullOrWhiteSpace(g.Description)));
    }

    [Fact]
    public async Task Removing_leftovers_quarantines_them_and_undo_puts_everything_back()
    {
        var folder = _box.Folder(Path.Combine(K.LocalAppData, "Contoso Software", "Contoso Editor"), "cache.bin", "deep\\more.bin");
        var area = K.LocalAppData;
        var before = TestbedBuilder.Manifest(area);

        var items = LeftoverItems.ToGroups(Scan(Editor()), "Contoso Editor").SelectMany(g => g.Items).Where(i => i.DefaultSelected).ToList();
        var plan = PlanBuilder.FromSelection(AppsFeature.Id, "Leftovers of Contoso Editor", items);
        Assert.Single(plan.Items);
        Assert.False(plan.RequiresRestorePoint);

        var run = await _host.Executor.ExecuteAsync(plan, new ExecuteOptions { Mode = ExecutionMode.Real }, null, CancellationToken.None);
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.False(Directory.Exists(folder));

        var undo = await _host.Undo.UndoRunAsync(run.RunId, null, CancellationToken.None);
        Assert.True(undo.Complete, string.Join("; ", undo.Messages));
        Assert.True(TestbedBuilder.Compare(before, TestbedBuilder.Manifest(area)).Identical);
    }
}
