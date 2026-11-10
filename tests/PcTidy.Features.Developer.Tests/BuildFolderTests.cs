using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Developer.Common;
using PcTidy.Features.Developer.Projects;
using Xunit;

namespace PcTidy.Features.Developer.Tests;

/// <summary>Which folders are offered (marker matrix), when (age rules) and when not (skip rules), with the real SafetyGuard.</summary>
public class BuildFolderTests
{
    private static Task<ScanResult> Scan(DevSandbox s, IReadOnlyList<string>? roots = null)
        => new BuildFoldersScanner().ScanAsync(s.Context(roots), null, CancellationToken.None);

    private static ScanItem? Offered(ScanResult r, string path)
        => r.Groups.Where(g => g.Id != BuildFoldersScanner.SkippedGroupId).SelectMany(g => g.Items)
            .FirstOrDefault(i => string.Equals(i.Detail, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// folder name · files next to it (in the project folder) · files inside it · expected: "Safe", "Check" or "none".
    /// </summary>
    [Theory]
    [InlineData("node_modules", "package.json", "lodash/index.js", "Safe")]
    [InlineData("node_modules", "", "lodash/index.js", "none")]
    [InlineData("obj", "App.csproj", "project.assets.json", "Safe")]
    [InlineData("obj", "App.fsproj", "project.assets.json", "Safe")]
    [InlineData("obj", "App.csproj", "something.txt", "Check")]
    [InlineData("obj", "", "project.assets.json", "none")]
    [InlineData("bin", "App.vbproj", "Debug/App.dll", "Safe")]
    [InlineData("bin", "App.csproj", "tool.exe", "Check")]
    [InlineData("bin", "", "Debug/App.dll", "none")]
    [InlineData(".vs", "App.sln", "App/v17/.suo", "Check")]
    [InlineData(".vs", "App.slnx", "App/v17/.suo", "Check")]
    [InlineData(".vs", "", "App/v17/.suo", "none")]
    [InlineData("target", "Cargo.toml", "CACHEDIR.TAG", "Safe")]
    [InlineData("target", "Cargo.toml", "debug/app.exe", "Check")]
    [InlineData("target", "pom.xml", "CACHEDIR.TAG", "none")]
    [InlineData("__pycache__", "", "main.cpython-312.pyc", "Safe")]
    [InlineData("__pycache__", "", "notes.py", "none")]
    [InlineData(".venv", "requirements.txt", "pyvenv.cfg", "Check")]
    [InlineData(".venv", "", "Lib/site.py", "none")]
    [InlineData("build", "build.gradle", "classes/A.class", "Safe")]
    [InlineData("build", "build.gradle.kts", "classes/A.class", "Safe")]
    [InlineData("build", "CMakeLists.txt", "CMakeCache.txt", "Safe")]
    [InlineData("build", "CMakeLists.txt", "app.exe", "Check")]
    [InlineData("build", "package.json", "icon.ico", "Check")]
    [InlineData("build", "", "output.bin", "none")]
    public async Task Rebuildable_folders_are_offered_only_next_to_their_marker(string folder, string sibling, string inside, string expected)
    {
        using var s = new DevSandbox("markers");
        var project = s.Project("app");
        var host = Path.Combine(project, "src");
        if (sibling.Length > 0) s.Put(Path.Combine(host, sibling), "x");
        else s.Put(Path.Combine(host, "code.txt"), "x");
        var candidate = Path.Combine(host, folder);
        s.Put(Path.Combine(candidate, inside.Replace('/', '\\')), "built");

        var result = await Scan(s);
        var item = Offered(result, candidate);
        if (expected == "none")
        {
            Assert.Null(item);
            return;
        }
        Assert.NotNull(item);
        Assert.Equal(expected, item!.Badge.ToString());
        Assert.Null(item.DisabledReason);
        Assert.Equal(expected == "Safe", item.DefaultSelected);
        var action = Assert.Single(item.Actions);
        Assert.Equal(ActionKind.QuarantinePath, action.Kind);
        Assert.Equal("developer", action.FeatureId);
        Assert.Equal(candidate, action.SourcePath, ignoreCase: true);
        Assert.True(action.IsDirectory);
        Assert.Equal(5, action.ExpectedSize); // "built" = 5 bytes, measured exactly
        Assert.Equal(item.SizeBytes, action.ExpectedSize);
        Assert.False(string.IsNullOrWhiteSpace(item.Reason));
    }

    [Fact]
    public async Task Venv_without_a_dependency_list_says_so()
    {
        using var s = new DevSandbox("venv");
        var p = s.Project("tool");
        s.Put(Path.Combine(p, ".venv", "pyvenv.cfg"), "home = x");
        var item = Offered(await Scan(s), Path.Combine(p, ".venv"));
        Assert.NotNull(item);
        Assert.Equal(Badge.Check, item!.Badge);
        Assert.False(item.DefaultSelected);
        Assert.Contains("no requirements or lock file", item.Reason);
    }

    [Fact]
    public async Task Git_folder_and_source_are_never_offered_and_the_walk_never_enters_git()
    {
        using var s = new DevSandbox("git");
        var p = s.Project("web");
        s.Put(Path.Combine(p, "package.json"), "{}");
        s.Put(Path.Combine(p, ".git", "build", "x"), "x"); // a folder called build inside .git: never
        s.Put(Path.Combine(p, ".git", "node_modules", "x"), "x");
        s.Put(Path.Combine(p, "node_modules", "a", "a.js"), "x");
        var r = await Scan(s);
        var items = r.Groups.SelectMany(g => g.Items).Where(i => i.Actions.Count > 0).ToList();
        var only = Assert.Single(items);
        Assert.Equal(Path.Combine(p, "node_modules"), only.Detail, ignoreCase: true);
    }

    // ---------------- age rules ----------------

    [Theory]
    [InlineData(200, null, null, true)]     // nothing recent
    [InlineData(90, null, null, true)]      // exactly at the limit counts as idle
    [InlineData(89, null, null, false)]     // git activity inside 90 days
    [InlineData(200, 10.0, null, false)]    // a source file edited 10 days ago
    [InlineData(200, null, 5.0, false)]     // fetched 5 days ago (.git\FETCH_HEAD)
    public async Task Projects_with_recent_activity_are_left_alone(int gitDays, double? sourceDays, double? fetchDays, bool offered)
    {
        using var s = new DevSandbox("age");
        var p = s.Project("app", gitDays);
        s.Put(Path.Combine(p, "package.json"), "{}", s.Now.AddDays(-gitDays));
        s.Put(Path.Combine(p, "node_modules", "x", "x.js"), "x", s.Now.AddDays(-1)); // build folders don't count as your activity
        if (sourceDays is { } sd) s.Put(Path.Combine(p, "src", "index.js"), "x", s.Now.AddDays(-sd));
        if (fetchDays is { } fd) s.Put(Path.Combine(p, ".git", "FETCH_HEAD"), "x", s.Now.AddDays(-fd));

        var r = await Scan(s);
        var item = Offered(r, Path.Combine(p, "node_modules"));
        Assert.Equal(offered, item is not null);
        if (!offered)
        {
            var skipped = Assert.Single(r.Groups, g => g.Id == BuildFoldersScanner.SkippedGroupId).Items.Single();
            Assert.NotNull(skipped.DisabledReason);
            Assert.Empty(skipped.Actions);
            Assert.Equal(0, skipped.SizeBytes); // not offered = not counted
        }
    }

    [Fact]
    public async Task Idle_days_follow_the_option()
    {
        using var s = new DevSandbox("idle-option");
        var p = s.Project("app", 40);
        s.Put(Path.Combine(p, "package.json"), "{}", s.Now.AddDays(-40));
        s.Put(Path.Combine(p, "node_modules", "x.js"), "x");
        Assert.Null(Offered(await Scan(s), Path.Combine(p, "node_modules")));
        s.ExtraOptions[DeveloperOptions.IdleDays] = 30;
        Assert.NotNull(Offered(await Scan(s), Path.Combine(p, "node_modules")));
    }

    // ---------------- skip rules ----------------

    [Fact]
    public async Task Open_files_leave_the_whole_project_alone_with_the_app_named()
    {
        using var s = new DevSandbox("open");
        var p = s.Project("app");
        s.Put(Path.Combine(p, "package.json"), "{}");
        s.Put(Path.Combine(p, "node_modules", "x.js"), "x");
        s.Put(Path.Combine(p, "App.csproj"), "<Project/>");
        s.Put(Path.Combine(p, "obj", "project.assets.json"), "{}");
        s.InUse = new FakeInUse("Visual Studio Code", Path.Combine(p, "node_modules"));

        var r = await Scan(s);
        Assert.Null(Offered(r, Path.Combine(p, "obj")));
        var skipped = Assert.Single(Assert.Single(r.Groups, g => g.Id == BuildFoldersScanner.SkippedGroupId).Items);
        Assert.Contains("Visual Studio Code", skipped.DisabledReason);
    }

    [Fact]
    public async Task A_really_locked_file_is_found_by_the_real_in_use_checker()
    {
        using var s = new DevSandbox("locked");
        var p = s.Project("app");
        s.Put(Path.Combine(p, "package.json"), "{}");
        var locked = s.Put(Path.Combine(p, "node_modules", "x.js"), "x");
        using (TestbedGen.TestbedBuilder.LockFile(locked))
        {
            var r = await Scan(s);
            Assert.Null(Offered(r, Path.Combine(p, "node_modules")));
            Assert.Contains(r.Groups, g => g.Id == BuildFoldersScanner.SkippedGroupId);
        }
        Assert.NotNull(Offered(await Scan(s), Path.Combine(p, "node_modules")));
    }

    [Fact]
    public async Task Cloud_folders_are_ask_first_with_a_warning()
    {
        using var s = new DevSandbox("cloud"); // Default profile: OneDrive at <profile>\OneDrive
        var p = s.Project("synced", parent: Path.Combine(s.K.CloudRoots[0], "Code"));
        s.Put(Path.Combine(p, "package.json"), "{}");
        s.Put(Path.Combine(p, "node_modules", "x.js"), "x");

        var item = Offered(await Scan(s), Path.Combine(p, "node_modules"));
        Assert.NotNull(item);
        Assert.Empty(item!.Actions);
        Assert.Contains("cloud", item.DisabledReason);
        Assert.Contains("also removes it from the cloud", item.Reason);
        Assert.Equal(0, item.SizeBytes);

        s.Host.Settings.AllowedAskFirstFolders.Add(p); // the user allowed this folder
        item = Offered(await Scan(s), Path.Combine(p, "node_modules"));
        Assert.NotNull(item);
        Assert.Null(item!.DisabledReason);
        Assert.Equal(Badge.Check, item.Badge); // never Safe in the cloud
        Assert.False(item.DefaultSelected);
        Assert.Single(item.Actions);
    }

    [Fact]
    public async Task Junctions_are_never_followed()
    {
        using var s = new DevSandbox("junction");
        var elsewhere = Path.Combine(s.Root, "D", "elsewhere");
        s.Put(Path.Combine(elsewhere, "big.bin"), new byte[10_000]);
        var p = s.Project("linked");
        s.Put(Path.Combine(p, "package.json"), "{}");
        var p2 = s.Project("pnpm");
        s.Put(Path.Combine(p2, "package.json"), "{}");
        s.Put(Path.Combine(p2, "node_modules", ".pnpm", "a.js"), "abc");
        if (!DevSandbox.TryJunction(Path.Combine(p, "node_modules"), elsewhere) || !DevSandbox.TryJunction(Path.Combine(p2, "node_modules", "lodash"), elsewhere))
            return; // this PC can't make junctions: nothing to prove

        var r = await Scan(s, new[] { s.Repos });
        Assert.Null(Offered(r, Path.Combine(p, "node_modules")));               // node_modules itself a link: skipped
        var pnpm = Offered(r, Path.Combine(p2, "node_modules"));
        Assert.NotNull(pnpm);
        Assert.Equal(3, pnpm!.SizeBytes);                                         // the link's target isn't counted
        Assert.Contains("link", pnpm.Reason);
        Assert.True(File.Exists(Path.Combine(elsewhere, "big.bin")));
    }

    [Fact]
    public async Task Stray_package_json_in_the_user_folder_doesnt_make_it_a_project()
    {
        using var s = new DevSandbox("container");
        s.Put(Path.Combine(s.K.UserProfile, "package.json"), "{}");
        s.Put(Path.Combine(s.K.UserProfile, "node_modules", "x.js"), "x");
        var p = s.Project("real");
        s.Put(Path.Combine(p, "package.json"), "{}");
        s.Put(Path.Combine(p, "node_modules", "y.js"), "y");
        var r = await Scan(s);
        Assert.Null(Offered(r, Path.Combine(s.K.UserProfile, "node_modules")));
        Assert.NotNull(Offered(r, Path.Combine(p, "node_modules")));
    }

    [Fact]
    public async Task Hidden_tool_folders_and_AppData_are_not_searched()
    {
        using var s = new DevSandbox("hidden-tools");
        var ext = Path.Combine(s.K.UserProfile, ".vscode", "extensions", "ms-python", "package.json");
        s.Put(ext, "{}");
        s.Put(Path.Combine(Path.GetDirectoryName(ext)!, "node_modules", "x.js"), "x");
        var cache = Path.Combine(s.K.LocalAppData, "npm-cache", "_npx", "abc", "package.json");
        s.Put(cache, "{}");
        s.Put(Path.Combine(Path.GetDirectoryName(cache)!, "node_modules", "x.js"), "x");
        var r = await Scan(s);
        Assert.DoesNotContain(r.Groups.SelectMany(g => g.Items), i => i.Actions.Count > 0);
        Assert.Contains(r.Notes, n => n.Contains("No code projects"));
    }

    [Fact]
    public void Default_roots_are_the_profile_and_the_other_drives()
    {
        var k = new SandboxKnownLocations(Path.Combine(DevSandbox.TestbedRoot, "roots-unused"));
        var roots = DevContext.DefaultRoots(k);
        Assert.Equal(new[] { k.UserProfile, k.FixedDrives[1] }, roots);
        var single = DevContext.DefaultRoots(new SandboxKnownLocations(Path.Combine(DevSandbox.TestbedRoot, "roots-unused"), SandboxProfile.Minimal));
        Assert.Equal(new[] { k.UserProfile }, single);
        Assert.Equal(new[] { @"C:\a" }, ProjectFinder.DistinctRoots(new[] { @"C:\a", @"C:\a\b", @"c:\A\" }));
    }
}
