using PcTidy.Core;
using PcTidy.Core.Model;
using Xunit;

namespace PcTidy.Safety.Tests;

/// <summary>Cleanup carve-outs: strictly inside their roots (or their single files), nothing else.</summary>
public sealed class CarveOutTests : IDisposable
{
    private readonly TestSandbox _s = new();
    public void Dispose() => _s.Dispose();

    public static IEnumerable<object[]> AllCarveOuts => CarveOuts.All.Select(c => new object[] { c.Id });

    // Windows-tool targets (Recycle Bin, DISM, Delivery Optimization, Windows.old, Spotify info) have no file roots.
    private static readonly HashSet<string> ToolOnly = new()
    {
        CarveOuts.Spotify, CarveOuts.RecycleBin, CarveOuts.DeliveryOptimization, CarveOuts.WindowsOld, CarveOuts.ComponentStore,
    };

    [Theory]
    [MemberData(nameof(AllCarveOuts))]
    public void Every_carve_out_allows_children_of_its_roots_but_not_the_roots(string id)
    {
        var carveOut = CarveOuts.Find(id)!;
        var roots = carveOut.Roots(_s.K).ToList();
        if (ToolOnly.Contains(id)) { Assert.Empty(roots); return; }
        Assert.NotEmpty(roots);
        var filesOnly = carveOut.AllowedExtensions is { Count: > 0 };
        foreach (var root in roots)
        {
            var name = filesOnly ? "setup" + carveOut.AllowedExtensions!.First() : "junk.tmp";
            var child = _s.File(Path.Combine(root, "old", name));
            var allowed = _s.Guard.Check(_s.Quarantine(child, carveOut: id));
            Assert.True(allowed.Allowed, $"{id}: {child}: {allowed.Reason}");
            // Extension-limited targets (old installers) never take whole folders.
            Assert.Equal(!filesOnly, _s.Guard.Check(_s.Quarantine(Path.GetDirectoryName(child)!, carveOut: id)).Allowed);

            var itself = _s.Guard.Check(_s.Quarantine(root, carveOut: id));
            Assert.False(itself.Allowed);
            Assert.Contains("folder itself", itself.Reason);
        }
    }

    [Theory]
    [MemberData(nameof(AllCarveOuts))]
    public void Every_carve_out_rejects_paths_outside_its_roots(string id)
    {
        var outside = new[]
        {
            Path.Combine(_s.K.WindowsDir, "System32", "kernel32.dll"),
            Path.Combine(_s.K.ProgramFiles, "App", "a.dll"),
            Path.Combine(_s.K.Downloads, "file.txt"),
        };
        foreach (var p in outside)
        {
            if (id == CarveOuts.OldInstallers && p.StartsWith(_s.K.Downloads)) continue; // Downloads is its root
            var v = _s.Guard.Check(_s.Quarantine(p, carveOut: id));
            Assert.False(v.Allowed, $"{id} must not reach {p}");
            Assert.Contains("outside", v.Reason);
        }
    }

    [Theory]
    [InlineData(@"Default\Login Data")]
    [InlineData(@"Default\Local Storage\leveldb\000003.log")]
    [InlineData(@"Default\IndexedDB\https_x\a.blob")]
    [InlineData(@"Local State")]
    [InlineData(@"Profile 1\Bookmarks")]
    [InlineData(@"Default\Network\Cookies")]
    [InlineData(@"Default\History")]
    public void Browser_cache_carve_out_never_reaches_logins_or_saved_data(string relative)
    {
        var userData = CarveOuts.ChromiumUserDataDirs(_s.K).First();
        var f = _s.File(Path.Combine(userData, relative));
        Assert.False(_s.Guard.Check(_s.Quarantine(f, carveOut: CarveOuts.BrowserCache)).Allowed, relative);
        var cache = _s.File(Path.Combine(userData, "Default", "Cache", "Cache_Data", "f_000001"));
        Assert.True(_s.Guard.Check(_s.Quarantine(cache, carveOut: CarveOuts.BrowserCache)).Allowed);
    }

    [Fact]
    public void Privacy_carve_out_reaches_cookies_and_history_but_never_logins()
    {
        var userData = CarveOuts.ChromiumUserDataDirs(_s.K).First();
        Assert.True(_s.Guard.Check(_s.Quarantine(_s.File(Path.Combine(userData, "Default", "Network", "Cookies")), carveOut: CarveOuts.BrowserPrivacy)).Allowed);
        Assert.True(_s.Guard.Check(_s.Quarantine(_s.File(Path.Combine(userData, "Default", "History")), carveOut: CarveOuts.BrowserPrivacy)).Allowed);
        Assert.False(_s.Guard.Check(_s.Quarantine(_s.File(Path.Combine(userData, "Default", "Login Data")), carveOut: CarveOuts.BrowserPrivacy)).Allowed);
        Assert.False(_s.Guard.Check(_s.Quarantine(_s.File(Path.Combine(userData, "Local State")), carveOut: CarveOuts.BrowserPrivacy)).Allowed);
    }

    [Fact]
    public void Carve_out_id_on_a_tool_item_grants_nothing_but_does_not_block()
    {
        var item = new PlanItem
        {
            Kind = ActionKind.EmptyRecycleBin, FeatureId = "cleanup", Description = "Empty",
            Reversible = false, CarveOutId = CarveOuts.RecycleBin,
        };
        Assert.True(_s.Guard.Check(item).Allowed);
    }

    [Fact]
    public void Windows_temp_needs_its_carve_out()
    {
        var f = _s.File(Path.Combine(_s.K.WindowsTemp, "setup.log"));
        Assert.False(_s.Guard.Check(_s.Quarantine(f)).Allowed);
        Assert.True(_s.Guard.Check(_s.Quarantine(f, carveOut: CarveOuts.WindowsTemp)).Allowed);
        Assert.False(_s.Guard.ClassifyPath(f).Allowed); // ClassifyPath never considers carve-outs
    }

    [Fact]
    public void Carve_out_cannot_climb_out_with_dot_dot()
    {
        var sneaky = Path.Combine(_s.K.WindowsTemp, "..", "System32", "config", "SAM");
        var v = _s.Guard.Check(_s.Quarantine(sneaky, carveOut: CarveOuts.WindowsTemp));
        Assert.False(v.Allowed);
    }

    [Fact]
    public void Memory_dmp_single_file_is_allowed_only_for_crash_dumps()
    {
        var dmp = _s.File(Path.Combine(_s.K.WindowsDir, "MEMORY.DMP"));
        Assert.True(_s.Guard.Check(_s.Quarantine(dmp, carveOut: CarveOuts.CrashDumps)).Allowed);
        Assert.False(_s.Guard.Check(_s.Quarantine(dmp, carveOut: CarveOuts.WindowsTemp)).Allowed);
        Assert.False(_s.Guard.Check(_s.Quarantine(dmp)).Allowed);
        Assert.False(_s.Guard.Check(_s.Quarantine(Path.Combine(_s.K.WindowsDir, "OTHER.DMP"), carveOut: CarveOuts.CrashDumps)).Allowed);
    }

    [Fact]
    public void Steam_cache_carve_out_opens_only_its_folders_never_games()
    {
        var cache = _s.File(Path.Combine(_s.K.SteamRoot!, "appcache", "httpcache", "ab", "cd"));
        Assert.True(_s.Guard.Check(_s.Quarantine(cache, carveOut: CarveOuts.SteamCache)).Allowed);
        var game = Path.Combine(_s.K.SteamRoot!, "steamapps", "common", "Game", "x.pak");
        Assert.False(_s.Guard.Check(_s.Quarantine(game, carveOut: CarveOuts.SteamCache)).Allowed);
        var config = Path.Combine(_s.K.SteamRoot!, "config", "config.vdf");
        Assert.False(_s.Guard.Check(_s.Quarantine(config, carveOut: CarveOuts.SteamCache)).Allowed);
    }

    [Fact]
    public void Unknown_carve_out_ids_are_blocked()
    {
        var f = _s.File(Path.Combine(_s.K.WindowsTemp, "a.tmp"));
        foreach (var id in new[] { "cleanup.everything", "", "CLEANUP.WINDOWS-TEMP ", CarveOuts.UserTemp })
        {
            var v = _s.Guard.Check(_s.Quarantine(f, carveOut: id));
            Assert.False(v.Allowed, id);
        }
    }

    [Fact]
    public void Carve_outs_only_work_for_cleanup_and_only_for_quarantine()
    {
        var f = _s.File(Path.Combine(_s.K.WindowsTemp, "a.tmp"));
        Assert.False(_s.Guard.Check(_s.Quarantine(f, feature: "organize", carveOut: CarveOuts.WindowsTemp)).Allowed);
        var move = _s.Move(f, Path.Combine(_s.K.Downloads, "a.tmp"), feature: "cleanup") with { CarveOutId = CarveOuts.WindowsTemp };
        Assert.False(_s.Guard.Check(move).Allowed);
    }

    [Fact]
    public void Junction_inside_a_carve_out_is_still_blocked()
    {
        var target = _s.Dir(Path.Combine(_s.K.Downloads, "precious"));
        _s.File(Path.Combine(target, "photo.jpg"));
        var link = _s.Junction(Path.Combine(_s.K.WindowsTemp, "trap"), target);
        Assert.False(_s.Guard.Check(_s.Quarantine(link, carveOut: CarveOuts.WindowsTemp)).Allowed);
        Assert.False(_s.Guard.Check(_s.Quarantine(Path.Combine(link, "photo.jpg"), carveOut: CarveOuts.WindowsTemp)).Allowed);
    }
}
