using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Apps.Inventory;
using PcTidy.Features.Apps.Scanning;
using PcTidy.Hosting;
using PcTidy.Safety;
using Xunit;

namespace PcTidy.Features.Apps.Tests;

public sealed class ScannerTests : IDisposable
{
    private readonly Sandbox _box = new(SandboxProfile.Minimal);
    private readonly FakeRegistry _reg = new();
    private readonly PcTidyHost _host;

    public ScannerTests() => _host = PcTidyHost.CreateSandbox(_box.Root, SandboxProfile.Minimal);

    public void Dispose()
    {
        _host.Dispose();
        _reg.Dispose();
        _box.Dispose();
    }

    [Fact]
    public async Task Scan_lists_preinstalled_unused_and_ghost_apps_with_nothing_ticked()
    {
        var now = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var old = _box.Folder(Path.Combine(_box.Locations.ProgramFiles, "Old Tool"), "oldtool.exe");
        var fresh = _box.Folder(Path.Combine(_box.Locations.ProgramFiles, "Fresh Tool"), "fresh.exe");
        _reg.UninstallEntry("HKLM64", "OldTool", ("DisplayName", "Old Tool"), ("InstallLocation", old), ("UninstallString", $"\"{old}\\oldtool.exe\" /uninstall"));
        _reg.UninstallEntry("HKLM64", "FreshTool", ("DisplayName", "Fresh Tool"), ("InstallLocation", fresh), ("UninstallString", $"\"{fresh}\\fresh.exe\" /uninstall"));
        _reg.UninstallEntry("HKLM64", "Ghost", ("DisplayName", "Ghost Tool"), ("InstallLocation", Path.Combine(_box.Locations.ProgramFiles, "Ghost Tool")));
        _reg.Key(@"HKCU\" + UserAssistReader.UserAssistPath + @"\{CEBFF5CD-ACE2-4F4F-9178-9926F41749EA}\Count",
            (UserAssistReader.Rot13(@"{6D809377-6AF0-444B-8957-A3773F02200E}\Old Tool\oldtool.exe"), UserAssistData.Record(4, now.AddDays(-400))),
            (UserAssistReader.Rot13(@"{6D809377-6AF0-444B-8957-A3773F02200E}\Fresh Tool\fresh.exe"), UserAssistData.Record(9, now.AddDays(-3))));
        var store = new FakeStore().Add("Clipchamp.Clipchamp", "Clipchamp").Add("Microsoft.WindowsStore", "Microsoft Store").Add("Microsoft.YourPhone", "Phone Link");

        var scanner = new AppsScanner(_ => new AppInventorySources { Locations = _box.Locations, Registry = _reg.Reader, Store = store }, () => now);
        Assert.False(scanner.IncludedInOptimizeAll);
        var result = await scanner.ScanAsync(_host.CreateScanContext(ScanDepth.Deep), null, CancellationToken.None);

        var pre = result.Groups.Single(g => g.Id == "apps.preinstalled");
        Assert.Equal(new[] { "Clipchamp", "Phone Link" }, pre.Items.Select(i => i.Title));
        Assert.All(pre.Items, i => Assert.Equal(ActionKind.AppxRemoveForUser, i.Actions.Single().Kind));

        var unused = result.Groups.Single(g => g.Id == "apps.unused");
        var item = Assert.Single(unused.Items);
        Assert.Equal("Old Tool", item.Title);
        Assert.Equal(ActionKind.RunUninstaller, item.Actions.Single().Kind);
        Assert.Equal("1.2 KB", item.CurrentState); // deep scan measured the folder

        var ghosts = result.Groups.Single(g => g.Id == "apps.orphans");
        Assert.Equal("Ghost Tool", Assert.Single(ghosts.Items).Title);
        Assert.NotNull(ghosts.Items[0].DisabledReason);

        foreach (var i in result.AllItems)
        {
            Assert.False(i.DefaultSelected);
            Assert.False(string.IsNullOrWhiteSpace(i.Reason));
            if (i.Actions.Any(a => !a.Reversible)) Assert.Equal(Badge.Permanent, i.Badge);
        }
        Assert.All(result.Groups, g => Assert.False(string.IsNullOrWhiteSpace(g.Description)));
    }

    [Fact]
    public void Module_identity()
    {
        var m = new AppsModule();
        Assert.Equal("apps", m.Id);
        Assert.Equal("Apps", m.Title);
        Assert.Equal(NavGroup.SpeedUp, m.Group);
        Assert.Equal("Apps24", m.Icon);
        Assert.Equal(Release.V2, m.Release);
        Assert.All(m.Scanners, s => Assert.False(s.IncludedInOptimizeAll));
    }

    [Theory]
    [InlineData("Apps24")]
    [InlineData("AppsList24")]
    [InlineData("Games24")]
    [InlineData("StoreMicrosoft24")]
    [InlineData("WindowApps24")]
    [InlineData("FolderOpen24")]
    [InlineData("FolderSearch24")]
    [InlineData("DeleteDismiss24")]
    [InlineData("Broom24")]
    [InlineData("ArrowClockwise24")]
    [InlineData("ArrowLeft24")]
    [InlineData("Search24")]
    public void Icons_used_by_the_page_exist(string name)
        => Assert.True(Enum.TryParse<Wpf.Ui.Controls.SymbolRegular>(name, out _), name);
}

/// <summary>Read-only checks against the real PC: nothing is changed, nothing may throw.</summary>
public sealed class RealPcSmokeTests
{
    [Fact]
    public void Inventory_of_this_pc_reads_without_throwing()
    {
        var locations = new WindowsKnownLocations(new AppSettings());
        var apps = new AppInventory(AppInventorySources.For(locations)).Load(CancellationToken.None);
        Assert.NotNull(apps);
        Assert.All(apps, a => Assert.False(string.IsNullOrWhiteSpace(a.Name)));
        Assert.Equal(apps.Count, apps.Select(a => a.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        // Every app's uninstall option can be worked out (available or with a reason).
        foreach (var a in apps)
        {
            var option = Uninstall.UninstallPlanner.For(a);
            Assert.True(option.IsAvailable || !string.IsNullOrWhiteSpace(option.BlockedReason), a.Name);
        }
    }

    [Fact]
    public async Task Apps_scan_of_this_pc_reads_without_throwing()
    {
        var settings = new AppSettings();
        var locations = new WindowsKnownLocations(settings);
        var ctx = new ScanContext
        {
            Locations = locations,
            Safety = new SafetyGuard(locations, settings),
            InUse = new NoInUse(),
            Settings = settings,
            Walker = new ManagedFileWalker(),
            Depth = ScanDepth.Quick,
        };
        var result = await new AppsScanner().ScanAsync(ctx, null, CancellationToken.None);
        Assert.All(result.AllItems, i => Assert.False(i.DefaultSelected));
    }

    private sealed class NoInUse : IInUseChecker
    {
        public Task<IReadOnlyList<InUseInfo>> CheckAsync(IReadOnlyList<string> paths, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<InUseInfo>>(Array.Empty<InUseInfo>());
        public bool IsLocked(string path) => false;
    }
}
