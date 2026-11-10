using PcTidy.Core;
using PcTidy.Core.Model;
using Xunit;

namespace PcTidy.Safety.Tests;

/// <summary>H3: every other account's profile is off limits; Public is ask-first and only for organizing.</summary>
public sealed class OtherAccountsTests : IDisposable
{
    private readonly TestSandbox _s = new();
    public void Dispose() => _s.Dispose();

    private string Users => Path.GetDirectoryName(_s.K.UserProfile)!;

    [Theory]
    [InlineData("alex", "Documents", "taxes.pdf")]
    [InlineData("alex", "Downloads", "setup.exe")]
    [InlineData("alex", "AppData", "Local", "Temp", "x.tmp")]
    [InlineData("Default", "NTUSER.DAT")]
    [InlineData("testerX", "Downloads", "a.zip")]          // shares a prefix with "tester"
    [InlineData("desktop.ini")]
    public void Other_accounts_are_never_touched_by_any_feature(params string[] below)
    {
        var f = _s.File(Path.Combine(new[] { Users }.Concat(below).ToArray()));
        foreach (var feature in new[] { "cleanup", "organize", "spacemap", "duplicates", "optimizeall" })
        {
            var v = _s.Guard.Check(_s.Quarantine(f, feature));
            Assert.False(v.Allowed, feature);
            Assert.Equal(PathProtection.Never, v.Protection);
            Assert.Contains("another account", v.Reason);
        }
        Assert.Equal(PathProtection.Never, _s.Guard.ClassifyPath(f).Protection);
        // Moving something in from your own folders is just as off limits.
        var mine = _s.File(Path.Combine(_s.K.Downloads, "mine.txt"));
        Assert.False(_s.Guard.Check(_s.Move(mine, Path.Combine(Path.GetDirectoryName(f)!, "mine.txt"))).Allowed);
        _s.Settings.AllowedAskFirstFolders.Add(Path.GetDirectoryName(f)!);
        Assert.False(_s.Guard.Check(_s.Quarantine(f, "organize")).Allowed); // a per-folder permission can't unlock it
    }

    [Fact]
    public void Carve_outs_never_reach_another_accounts_matching_folder()
    {
        var theirTemp = _s.File(Path.Combine(Users, "alex", "AppData", "Local", "Temp", "x.tmp"));
        Assert.False(_s.Guard.Check(_s.Quarantine(theirTemp, carveOut: CarveOuts.UserTemp)).Allowed);
    }

    [Fact]
    public void Public_folder_is_ask_first_for_organize_only()
    {
        var f = _s.File(Path.Combine(Users, "Public", "Documents", "shared.docx"));
        var organize = _s.Guard.Check(_s.Quarantine(f, "organize"));
        Assert.False(organize.Allowed);
        Assert.Equal(PathProtection.AskFirst, organize.Protection);
        Assert.Contains("Public", organize.Reason);
        Assert.Equal(PathProtection.AskFirst, _s.Guard.ClassifyPath(f).Protection);
        foreach (var feature in new[] { "cleanup", "spacemap", "duplicates" })
            Assert.Equal(PathProtection.Never, _s.Guard.Check(_s.Quarantine(f, feature)).Protection);

        _s.Settings.AllowedAskFirstFolders.Add(Path.Combine(Users, "Public", "Documents"));
        var allowed = _s.Guard.Check(_s.Move(f, Path.Combine(Users, "Public", "Documents", "Sorted", "shared.docx")));
        Assert.True(allowed.Allowed, allowed.Reason);
        Assert.Equal(PathProtection.AskFirst, allowed.Protection);
        Assert.False(_s.Guard.Check(_s.Quarantine(f, "cleanup")).Allowed);
        // The Public folder itself never moves.
        _s.Settings.AllowedAskFirstFolders.Add(Path.Combine(Users, "Public"));
        Assert.False(_s.Guard.Check(_s.Quarantine(Path.Combine(Users, "Public"), "organize")).Allowed);
    }

    [Fact]
    public void Your_own_profile_is_unaffected()
    {
        var f = _s.File(Path.Combine(_s.K.Downloads, "mine.zip"));
        Assert.True(_s.Guard.Check(_s.Quarantine(f)).Allowed);
        Assert.Equal(PathProtection.None, _s.Guard.ClassifyPath(f).Protection);
    }
}

/// <summary>H3: admin quarantine only inside an admin cleanup target.</summary>
public sealed class AdminQuarantineTests : IDisposable
{
    private readonly TestSandbox _s = new();
    public void Dispose() => _s.Dispose();

    private SafetyVerdict Admin(string path, string? carveOut) => _s.Guard.Check(_s.Quarantine(path, carveOut: carveOut) with { NeedsAdmin = true });

    [Fact]
    public void Admin_quarantine_needs_an_admin_carve_out_and_stays_inside_it()
    {
        var winTemp = _s.File(Path.Combine(_s.K.WindowsTemp, "setup.log"));
        Assert.True(Admin(winTemp, CarveOuts.WindowsTemp).Allowed);
        Assert.False(Admin(_s.File(Path.Combine(_s.K.Downloads, "a.zip")), null).Allowed);
        Assert.Contains("administrator", Admin(_s.File(Path.Combine(_s.K.Downloads, "b.zip")), null).Reason);
        // Outside the admin target's roots.
        Assert.False(Admin(_s.File(Path.Combine(_s.K.WindowsDir, "System32", "x.dll")), CarveOuts.WindowsTemp).Allowed);
        Assert.False(Admin(_s.File(Path.Combine(_s.K.Downloads, "c.zip")), CarveOuts.WindowsTemp).Allowed);
    }

    [Theory]
    [InlineData(CarveOuts.UserTemp)]
    [InlineData(CarveOuts.BrowserCache)]
    [InlineData(CarveOuts.OldInstallers)]
    [InlineData(CarveOuts.ShaderCache)]
    public void An_ordinary_carve_out_never_lends_itself_to_an_admin_step(string id)
    {
        var root = CarveOuts.Find(id)!.Roots(_s.K).First();
        var f = _s.File(Path.Combine(root, "x", "setup.exe"));
        Assert.True(_s.Guard.Check(_s.Quarantine(f, carveOut: id)).Allowed);
        var v = Admin(f, id);
        Assert.False(v.Allowed);
        Assert.Contains("administrator", v.Reason);
    }

    [Fact]
    public void Admin_flag_on_other_kinds_follows_their_own_rules()
    {
        var item = new PlanItem { Kind = ActionKind.EmptyRecycleBin, FeatureId = "cleanup", Description = "t", Reversible = false, NeedsAdmin = true };
        Assert.True(_s.Guard.Check(item).Allowed);
    }
}

/// <summary>L2: a carve-out only narrows. Old installers = installer files only; project/app detection stays on in ordinary places.</summary>
public sealed class CarveOutNarrowingTests : IDisposable
{
    private readonly TestSandbox _s = new();
    public void Dispose() => _s.Dispose();

    private SafetyVerdict Installer(string path) => _s.Guard.Check(_s.Quarantine(path, carveOut: CarveOuts.OldInstallers));

    [Theory]
    [InlineData("setup.msi")]
    [InlineData("App.MSIX")]
    [InlineData("tool.exe")]
    [InlineData(@"old\driver.exe")]
    public void Old_installers_takes_installer_files(string relative)
        => Assert.True(Installer(_s.File(Path.Combine(_s.K.Downloads, relative))).Allowed);

    [Theory]
    [InlineData("photo.jpg")]
    [InlineData("taxes.pdf")]
    [InlineData("setup.exe.bak")]
    [InlineData("noextension")]
    public void Old_installers_never_takes_other_files(string name)
    {
        var v = Installer(_s.File(Path.Combine(_s.K.Downloads, name)));
        Assert.False(v.Allowed);
        Assert.Contains("kind of file", v.Reason);
    }

    [Fact]
    public void Old_installers_never_takes_folders_or_odd_names()
    {
        var folder = _s.Dir(Path.Combine(_s.K.Downloads, "Installers"));
        _s.File(Path.Combine(folder, "a.msi"));
        Assert.Contains("never folders", Installer(folder).Reason);
        Assert.False(Installer(_s.Dir(Path.Combine(_s.K.Downloads, "looks-like.exe"))).Allowed);
        Assert.False(Installer(_s.OddFile(Path.Combine(_s.K.Downloads, "setup.exe."))).Allowed);
        Assert.False(Installer(_s.OddFile(Path.Combine(_s.K.Downloads, "setup.exe "))).Allowed);
    }

    [Fact]
    public void Old_installers_still_respects_projects_and_app_folders()
    {
        var app = _s.Dir(Path.Combine(_s.K.Downloads, "PortableTool"));
        var exe = _s.File(Path.Combine(app, "tool.exe"));
        _s.File(Path.Combine(app, "tool.dll"));
        var v = Installer(exe);
        Assert.False(v.Allowed);
        Assert.Equal(PathProtection.AskFirst, v.Protection);

        var repo = _s.Dir(Path.Combine(_s.K.Downloads, "my-repo"));
        _s.Dir(Path.Combine(repo, ".git"));
        Assert.False(Installer(_s.File(Path.Combine(repo, "build", "setup.exe"))).Allowed);
    }

    [Fact]
    public void Project_detection_stays_on_in_ordinary_carve_out_roots()
    {
        var proj = _s.Dir(Path.Combine(_s.K.UserTemp, "unpacked"));
        _s.File(Path.Combine(proj, "package.json"));
        Assert.False(_s.Guard.Check(_s.Quarantine(_s.File(Path.Combine(proj, "index.js")), carveOut: CarveOuts.UserTemp)).Allowed);
        // The target's own folder doesn't count: stray exe + dll at the top of Temp don't lock all of Temp.
        _s.File(Path.Combine(_s.K.UserTemp, "a.exe"));
        _s.File(Path.Combine(_s.K.UserTemp, "b.dll"));
        var junk = _s.File(Path.Combine(_s.K.UserTemp, "old.tmp"));
        Assert.True(_s.Guard.Check(_s.Quarantine(junk, carveOut: CarveOuts.UserTemp)).Allowed);
    }

    [Fact]
    public void Protected_area_carve_outs_skip_project_detection()
    {
        // Inside Windows\Temp, unpacked installers look like apps by nature; the carve-out was made for exactly them.
        var unpacked = _s.Dir(Path.Combine(_s.K.WindowsTemp, "unpacked"));
        _s.File(Path.Combine(unpacked, "setup.exe"));
        var dll = _s.File(Path.Combine(unpacked, "setup.dll"));
        Assert.True(_s.Guard.Check(_s.Quarantine(dll, carveOut: CarveOuts.WindowsTemp)).Allowed);
    }
}
