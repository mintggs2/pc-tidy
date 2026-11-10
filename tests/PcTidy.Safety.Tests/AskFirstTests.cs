using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Safety.Rules;
using Xunit;

namespace PcTidy.Safety.Tests;

/// <summary>Cloud folders, user libraries and project/app folders: allowed only per folder or per feature table.</summary>
public sealed class AskFirstTests : IDisposable
{
    private readonly TestSandbox _s = new();
    public void Dispose() => _s.Dispose();

    [Fact]
    public void Cloud_folder_is_ask_first_for_every_feature_until_the_user_allows_it()
    {
        var f = _s.File(Path.Combine(_s.K.CloudRoots[0], "Notes", "plan.docx"));
        foreach (var feature in new[] { "cleanup", "organize", "spacemap", "optimizeall", "duplicates" })
        {
            var v = _s.Guard.Check(_s.Quarantine(f, feature));
            Assert.False(v.Allowed, feature);
            Assert.Equal(PathProtection.AskFirst, v.Protection);
            Assert.Contains("cloud", v.Reason, StringComparison.OrdinalIgnoreCase);
        }
        _s.Settings.AllowedAskFirstFolders.Add(Path.Combine(_s.K.CloudRoots[0], "Notes"));
        var ok = _s.Guard.Check(_s.Quarantine(f, "organize"));
        Assert.True(ok.Allowed, ok.Reason);
        Assert.Equal(PathProtection.AskFirst, ok.Protection);
    }

    [Fact]
    public void Moving_into_a_cloud_folder_is_ask_first()
    {
        var f = _s.File(Path.Combine(_s.K.Downloads, "a.jpg"));
        var v = _s.Guard.Check(_s.Move(f, Path.Combine(_s.K.CloudRoots[0], "Photos", "a.jpg")));
        Assert.False(v.Allowed);
        Assert.Equal(PathProtection.AskFirst, v.Protection);
    }

    [Fact]
    public void Desktop_redirected_into_onedrive_needs_permission_even_for_organize()
    {
        var f = _s.File(Path.Combine(_s.K.Desktop, "shot.png"));
        Assert.True(_s.Guard.IsInCloudRoot(f));
        Assert.False(_s.Guard.Check(_s.Move(f, Path.Combine(_s.K.Desktop, "Pictures", "shot.png"))).Allowed);
        _s.Settings.AllowedAskFirstFolders.Add(_s.K.Desktop);
        Assert.True(_s.Guard.Check(_s.Move(f, Path.Combine(_s.K.Desktop, "Pictures", "shot.png"))).Allowed);
    }

    [Fact]
    public void Local_user_libraries_are_organize_only()
    {
        var f = _s.File(Path.Combine(_s.K.Videos, "clip.mp4"));
        Assert.True(_s.Guard.Check(_s.Move(f, Path.Combine(_s.K.Videos, "2026", "clip.mp4"), "organize")).Allowed);
        Assert.True(_s.Guard.Check(_s.Quarantine(f, "spacemap")).Allowed);
        var cleanup = _s.Guard.Check(_s.Quarantine(f, "cleanup"));
        Assert.False(cleanup.Allowed);
        Assert.Equal(PathProtection.AskFirst, cleanup.Protection);
        Assert.Contains("Videos", cleanup.Reason);
        Assert.False(_s.Guard.Check(_s.Quarantine(f, "some-future-feature")).Allowed);
        // Downloads is not a library: cleanup may suggest old installers there.
        Assert.True(_s.Guard.Check(_s.Quarantine(_s.File(Path.Combine(_s.K.Downloads, "setup.msi")), "cleanup")).Allowed);
    }

    [Theory]
    [InlineData(".git", true)]
    [InlineData(".svn", true)]
    [InlineData(".hg", true)]
    [InlineData("node_modules", true)]
    [InlineData(".venv", true)]
    [InlineData("steamapps", true)]
    [InlineData("package.json", false)]
    [InlineData("App.sln", false)]
    [InlineData("App.csproj", false)]
    [InlineData("pyproject.toml", false)]
    [InlineData("Cargo.toml", false)]
    [InlineData("go.mod", false)]
    public void Inside_a_project_is_ask_first_but_the_project_moves_as_one_unit(string marker, bool isFolder)
    {
        var project = _s.Dir(Path.Combine(_s.K.Downloads, "MyProject"));
        if (isFolder) _s.Dir(Path.Combine(project, marker)); else _s.File(Path.Combine(project, marker));
        var inner = _s.File(Path.Combine(project, "src", "main.c"));

        var v = _s.Guard.Check(_s.Move(inner, Path.Combine(_s.K.Downloads, "main.c")));
        Assert.False(v.Allowed);
        Assert.Equal(PathProtection.AskFirst, v.Protection);
        Assert.Contains("MyProject", v.Reason);

        Assert.True(_s.Guard.Check(_s.Move(project, Path.Combine(_s.K.Downloads, "Code", "MyProject"))).Allowed);
        Assert.True(_s.Guard.IsInsideProjectOrAppFolder(inner, out var why));
        Assert.False(string.IsNullOrEmpty(why));

        _s.Settings.AllowedAskFirstFolders.Add(project);
        Assert.True(_s.Guard.Check(_s.Move(inner, Path.Combine(_s.K.Downloads, "main.c"))).Allowed);
    }

    [Fact]
    public void Exe_next_to_dll_looks_like_an_app()
    {
        var app = _s.Dir(Path.Combine(_s.K.Downloads, "PortableTool"));
        _s.File(Path.Combine(app, "tool.exe"));
        Assert.True(_s.Guard.Check(_s.Quarantine(Path.Combine(app, "readme.txt"))).Allowed); // exe alone is not enough
        _s.Guard.Projects.ClearCache();
        _s.File(Path.Combine(app, "helper.dll"));
        Assert.False(_s.Guard.Check(_s.Quarantine(Path.Combine(app, "readme.txt"))).Allowed);
    }

    [Fact]
    public void Moving_into_a_project_is_ask_first()
    {
        var project = _s.Dir(Path.Combine(_s.K.Downloads, "Repo"));
        _s.Dir(Path.Combine(project, ".git"));
        var f = _s.File(Path.Combine(_s.K.Downloads, "notes.txt"));
        Assert.False(_s.Guard.Check(_s.Move(f, Path.Combine(project, "notes.txt"))).Allowed);
    }

    [Fact]
    public void A_stray_marker_in_a_container_folder_does_not_lock_everything()
    {
        _s.File(Path.Combine(_s.K.Downloads, "package.json"));
        _s.File(Path.Combine(_s.K.UserProfile, "package.json"));
        var f = _s.File(Path.Combine(_s.K.Downloads, "photo.jpg"));
        Assert.True(_s.Guard.Check(_s.Quarantine(f)).Allowed);
    }

    [Fact]
    public void Feature_table_can_be_extended_for_later_features()
    {
        var project = _s.Dir(Path.Combine(_s.K.Downloads, "Web"));
        _s.Dir(Path.Combine(project, "node_modules"));
        _s.File(Path.Combine(project, "src", "index.js"));
        // "developer" may take a project's rebuildable folders only with build evidence (node_modules needs package.json).
        Assert.False(_s.Guard.Check(_s.Quarantine(Path.Combine(project, "node_modules"), "developer")).Allowed);
        _s.File(Path.Combine(project, "package.json"));
        Assert.True(_s.Guard.Check(_s.Quarantine(Path.Combine(project, "node_modules"), "developer")).Allowed);
        // A file or hand-made folder merely called "build"/"bin" is not build output.
        _s.File(Path.Combine(project, "bin", "setup"));
        Assert.False(_s.Guard.Check(_s.Quarantine(Path.Combine(project, "bin", "setup"), "developer")).Allowed);
        var source = Path.Combine(project, "src", "index.js");
        var item = _s.Quarantine(source, "developer");
        var policy = SafetyPolicy.Default with
        {
            FeatureAskFirst = new Dictionary<string, AskFirstCategory>(SafetyPolicy.Default.FeatureAskFirst, StringComparer.OrdinalIgnoreCase)
            {
                ["developer"] = AskFirstCategory.ProjectOrApp,
            },
        };
        Assert.False(_s.Guard.Check(item).Allowed);
        var devGuard = new SafetyGuard(_s.K, _s.Settings, policy);
        var v = devGuard.Check(item);
        Assert.True(v.Allowed, v.Reason);
        Assert.Equal(PathProtection.AskFirst, v.Protection);
    }

    [Fact]
    public void Allowed_folders_never_unlock_never_areas()
    {
        _s.Settings.AllowedAskFirstFolders.Add(_s.K.WindowsDir);
        _s.Settings.AllowedAskFirstFolders.Add(_s.K.SystemDrive);
        Assert.False(_s.Guard.Check(_s.Quarantine(Path.Combine(_s.K.WindowsDir, "System32", "x.dll"))).Allowed);
        Assert.False(_s.Guard.Check(_s.Quarantine(Path.Combine(_s.K.SteamLibraries[0], "steamapps", "a.acf"))).Allowed);
    }
}
