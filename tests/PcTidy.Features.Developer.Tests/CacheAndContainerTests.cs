using PcTidy.Core.Model;
using PcTidy.Features.Developer.Caches;
using PcTidy.Features.Developer.Common;
using PcTidy.Features.Developer.Containers;
using PcTidy.Features.Developer.Environment;
using Xunit;

namespace PcTidy.Features.Developer.Tests;

/// <summary>Package caches (tool detection with fake folders), Docker's disk-usage JSON and WSL disks.</summary>
public class CacheAndContainerTests
{
    // ---------------- package caches ----------------

    [Fact]
    public async Task Only_caches_of_tools_on_this_PC_are_offered_through_the_tools_own_command()
    {
        using var s = new DevSandbox("caches");
        var nodeDir = Path.Combine(s.K.ProgramFiles, "nodejs");
        s.Put(Path.Combine(nodeDir, "npm.cmd"), "@echo off");                         // npm in its usual install folder
        s.Put(Path.Combine(s.K.LocalAppData, "npm-cache", "_cacache", "a"), new byte[1000]);
        var pipCache = Path.Combine(s.Root, "D", "pip-cache");                       // moved by PIP_CACHE_DIR
        s.Put(Path.Combine(pipCache, "http", "b"), new byte[300]);
        s.Environment.Variables["PIP_CACHE_DIR"] = pipCache;
        var scripts = Path.Combine(s.K.LocalAppData, "Programs", "Python", "Scripts");
        s.Put(Path.Combine(scripts, "pip.exe"), "MZ");
        s.Environment.Path.Add(scripts);                                             // pip on PATH
        s.Put(Path.Combine(s.K.LocalAppData, "Yarn", "Cache", "v6", "c"), new byte[200]); // Yarn's cache, but no Yarn

        var r = await new PackageCacheScanner().ScanAsync(s.Context(), null, CancellationToken.None);
        var items = Assert.Single(r.Groups, g => g.Id == PackageCacheScanner.CachesGroupId).Items;

        var npm = Assert.Single(items, i => i.Id == "dev-cache-npm");
        var run = Assert.Single(npm.Actions);
        Assert.Equal(ActionKind.RunSystemTool, run.Kind);
        Assert.Equal(ToolIds.DevCacheClean, run.ToolId);
        Assert.Equal("npm", run.Args!["tool"]);
        Assert.False(run.Reversible);
        Assert.Equal(Badge.Permanent, npm.Badge);
        Assert.False(npm.DefaultSelected);
        Assert.Equal(0, run.ExpectedSize);       // npm verify decides what goes: nothing is promised
        Assert.Equal(0, npm.SizeBytes);
        Assert.Contains("1000 B", npm.CurrentState);

        var pip = Assert.Single(items, i => i.Id == "dev-cache-pip");
        Assert.Equal(300, Assert.Single(pip.Actions).ExpectedSize); // pip cache purge empties it
        Assert.Equal(300, pip.SizeBytes);
        Assert.Equal(pipCache, pip.Detail, ignoreCase: true);

        var yarn = Assert.Single(items, i => i.Id == "dev-cache-yarn");
        Assert.Empty(yarn.Actions);
        Assert.Contains("wasn't found", yarn.DisabledReason);

        Assert.DoesNotContain(items, i => i.Id is "dev-cache-pnpm" or "dev-cache-gradle" or "dev-cache-nuget-http");
        Assert.All(items, i => Assert.False(string.IsNullOrWhiteSpace(i.Reason)));
    }

    [Fact]
    public async Task Nuget_global_packages_only_in_expert_mode_as_a_quarantine_move()
    {
        using var s = new DevSandbox("nuget");
        s.Put(Path.Combine(s.K.UserProfile, ".nuget", "packages", "newtonsoft.json", "13.0.1", "x.nupkg"), new byte[50]);
        var r = await new PackageCacheScanner().ScanAsync(s.Context(), null, CancellationToken.None);
        var item = Assert.Single(Assert.Single(r.Groups, g => g.Id == PackageCacheScanner.NuGetPackagesGroupId).Items);
        Assert.Empty(item.Actions);
        Assert.Contains("Expert mode", item.DisabledReason);

        s.Host.Settings.ExpertMode = true;
        r = await new PackageCacheScanner().ScanAsync(s.Context(), null, CancellationToken.None);
        item = Assert.Single(Assert.Single(r.Groups, g => g.Id == PackageCacheScanner.NuGetPackagesGroupId).Items);
        var move = Assert.Single(item.Actions);
        Assert.Equal(ActionKind.QuarantinePath, move.Kind);
        Assert.True(move.Reversible);
        Assert.Contains("devenv", move.RequiresClosedApps!);
        Assert.False(item.DefaultSelected);
        Assert.Equal(Badge.Check, item.Badge);
    }

    [Fact]
    public async Task No_tools_means_a_friendly_note_and_not_available()
    {
        using var s = new DevSandbox("no-tools", PcTidy.Core.SandboxProfile.Minimal);
        var ctx = s.Context();
        Assert.False(DeveloperAvailability.HasPackageTools(ctx));
        Assert.False(DeveloperAvailability.HasDockerOrWsl(ctx));
        var r = await new PackageCacheScanner().ScanAsync(ctx, null, CancellationToken.None);
        Assert.Empty(r.Groups);
        Assert.Contains(r.Notes, n => n.Contains("No package managers"));
        var d = await new DockerWslScanner().ScanAsync(ctx, null, CancellationToken.None);
        Assert.Empty(d.Groups);
        Assert.Contains(d.Notes, n => n.Contains("Neither Docker nor WSL"));
    }

    // ---------------- Docker ----------------

    internal const string DockerFixture = """
        {
          "LayersSize": 1092588,
          "Images": [
            { "Id": "sha256:aaa", "RepoTags": ["web:latest"], "Size": 500000, "SharedSize": 0, "Containers": 1 },
            { "Id": "sha256:bbb", "RepoTags": ["<none>:<none>"], "Size": 300000, "SharedSize": 100000, "Containers": 0 },
            { "Id": "sha256:ccc", "RepoTags": null, "Size": 200000, "SharedSize": -1, "Containers": 0 },
            { "Id": "sha256:ddd", "RepoTags": ["old:1"], "Size": 900000, "SharedSize": 0, "Containers": 0 }
          ],
          "Containers": [
            { "Id": "e90b", "Names": ["/web"], "State": "running", "SizeRw": 1000, "SizeRootFs": 501000 },
            { "Id": "f11c", "Names": ["/old-job"], "State": "exited", "SizeRw": 4000 },
            { "Id": "a22d", "Names": ["/made"], "State": "created", "SizeRw": 0 }
          ],
          "Volumes": [
            { "Name": "pgdata", "UsageData": { "Size": 7000000, "RefCount": 1 } },
            { "Name": "scratch", "UsageData": { "Size": -1, "RefCount": 0 } }
          ],
          "BuildCache": [
            { "ID": "c1", "Type": "regular", "Size": 60000, "InUse": false, "Shared": false },
            { "ID": "c2", "Type": "regular", "Size": 5000, "InUse": true, "Shared": false },
            { "ID": "c3", "Type": "regular", "Size": 7000, "InUse": false, "Shared": true }
          ]
        }
        """;

    [Fact]
    public void Docker_disk_usage_is_parsed_with_exact_bytes()
    {
        var u = DockerDiskUsage.Parse(DockerFixture)!;
        Assert.Equal(4, u.Images.Count);
        Assert.Equal(new[] { "sha256:bbb", "sha256:ccc" }, u.DanglingImages.Select(i => i.Id));
        Assert.Equal(200000 + 200000, u.DanglingImagesBytes);      // shared layers of bbb aren't freed
        Assert.Equal(new[] { "old-job", "made" }, u.StoppedContainers.Select(c => c.Name));
        Assert.Equal(4000, u.StoppedContainersBytes);
        Assert.Equal(60000, u.UnusedBuildCacheBytes);
        Assert.Equal(new[] { "pgdata", "scratch" }, u.Volumes.Select(v => v.Name));
        Assert.Equal(-1, u.Volumes[1].Size);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    public void Docker_parser_rejects_anything_unexpected(string? json) => Assert.Null(DockerDiskUsage.Parse(json));

    [Fact]
    public void Docker_parser_tolerates_missing_parts() => Assert.Empty(DockerDiskUsage.Parse("{\"Images\":[{\"Id\":\"x\"}]}")!.Volumes);

    [Fact]
    public async Task Docker_prunes_are_offered_and_volumes_are_never()
    {
        using var s = new DevSandbox("docker");
        var bin = Path.Combine(s.K.ProgramFiles, "Docker", "Docker", "resources", "bin");
        s.Put(Path.Combine(bin, "docker.exe"), "MZ");
        s.Environment.DockerJson = DockerFixture;

        var r = await new DockerWslScanner().ScanAsync(s.Context(), null, CancellationToken.None);
        var docker = Assert.Single(r.Groups, g => g.Id == DockerWslScanner.DockerGroupId);
        Assert.Equal(new[] { "images", "containers", "builder" }, docker.Items.Select(i => Assert.Single(i.Actions).Args!["what"]));
        Assert.All(docker.Items, i =>
        {
            Assert.Equal(Badge.Permanent, i.Badge);
            Assert.False(i.DefaultSelected);
            Assert.All(i.Actions, a => Assert.False(a.Reversible));
            Assert.Equal(i.SizeBytes, i.Actions.Sum(a => a.ExpectedSize));
        });
        Assert.Equal(400000, docker.Items[0].SizeBytes);
        var volumes = Assert.Single(r.Groups, g => g.Id == DockerWslScanner.VolumesGroupId);
        Assert.All(volumes.Items, v => { Assert.Empty(v.Actions); Assert.NotNull(v.DisabledReason); });
        Assert.Equal("pgdata", volumes.Items[0].Title);

        s.Environment.DockerJson = null; // Docker not running
        r = await new DockerWslScanner().ScanAsync(s.Context(), null, CancellationToken.None);
        Assert.DoesNotContain(r.Groups, g => g.Id == DockerWslScanner.DockerGroupId);
        Assert.Contains(r.Notes, n => n.Contains("Docker isn't running"));
    }

    // ---------------- WSL ----------------

    [Fact]
    public async Task Wsl_disks_get_stop_then_sparse_and_dockers_disk_gets_guidance_only()
    {
        using var s = new DevSandbox("wsl");
        var ubuntu = Path.Combine(s.K.LocalAppData, "Packages", "Ubuntu", "LocalState");
        s.Put(Path.Combine(ubuntu, "ext4.vhdx"), new byte[4096]);
        var odd = Path.Combine(s.K.LocalAppData, "wsl", "odd");
        s.Put(Path.Combine(odd, "ext4.vhdx"), new byte[10]);
        s.Put(Path.Combine(s.K.LocalAppData, "Docker", "wsl", "disk", "docker_data.vhdx"), new byte[2048]);
        s.Environment.Distros.Add(new WslDistro("Ubuntu", ubuntu, "ext4.vhdx", 2));
        s.Environment.Distros.Add(new WslDistro("My Distro;rm", odd, "ext4.vhdx", 2));
        s.Environment.Distros.Add(new WslDistro("Legacy", Path.Combine(s.K.LocalAppData, "legacy"), "ext4.vhdx", 1));
        s.Environment.Distros.Add(new WslDistro("docker-desktop", Path.Combine(s.K.LocalAppData, "Docker", "wsl", "main"), "ext4.vhdx", 2));

        var r = await new DockerWslScanner().ScanAsync(s.Context(), null, CancellationToken.None);
        var wsl = Assert.Single(r.Groups, g => g.Id == DockerWslScanner.WslGroupId);
        Assert.Equal(2, wsl.Items.Count); // WSL 1 has no disk; docker-desktop isn't yours to shrink
        var item = Assert.Single(wsl.Items, i => i.Title.StartsWith("Ubuntu"));
        Assert.Equal(new[] { ToolIds.WslShutdown, ToolIds.WslSetSparse }, item.Actions.Select(a => a.ToolId));
        Assert.Equal("Ubuntu", item.Actions[1].Args!["distro"]);
        Assert.All(item.Actions, a => Assert.False(a.Reversible));
        Assert.Contains("wsl --export Ubuntu", item.Reason);
        Assert.Contains("close", item.Reason);
        Assert.Equal(Badge.Permanent, item.Badge);
        Assert.False(item.DefaultSelected);
        Assert.Equal("4 KB", item.CurrentState);
        var bad = Assert.Single(wsl.Items, i => i.Title.StartsWith("My Distro"));
        Assert.Empty(bad.Actions);
        Assert.NotNull(bad.DisabledReason);

        var disk = Assert.Single(r.Groups, g => g.Id == DockerWslScanner.DockerDiskGroupId);
        var dd = Assert.Single(disk.Items);
        Assert.Empty(dd.Actions);
        Assert.Contains("Docker Desktop", dd.DisabledReason);
    }

    [Fact]
    public void Several_wsl_rows_stop_wsl_only_once()
    {
        var a = Plans.Tool(ToolIds.WslShutdown, "stop");
        var b = Plans.Tool(ToolIds.WslSetSparse, "x", new Dictionary<string, string> { ["distro"] = "A" });
        var c = Plans.Tool(ToolIds.WslShutdown, "stop");
        var d = Plans.Tool(ToolIds.WslSetSparse, "y", new Dictionary<string, string> { ["distro"] = "B" });
        Assert.Equal(new[] { a, b, d }, Plans.DropRepeatedShutdowns(new[] { a, b, c, d }));
    }

    /// <summary>DEV-1: without the WSL package (no 'wsl --manage'), WSL would be stopped and then the sparse step would fail.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("1.2.5")]
    public async Task Wsl_without_manage_support_is_shown_but_never_stopped_for_nothing(string? version)
    {
        using var s = new DevSandbox("wsl-old");
        var ubuntu = Path.Combine(s.K.LocalAppData, "Packages", "Ubuntu", "LocalState");
        s.Put(Path.Combine(ubuntu, "ext4.vhdx"), new byte[4096]);
        s.Environment.Distros.Add(new WslDistro("Ubuntu", ubuntu, "ext4.vhdx", 2));
        s.Environment.WslVersion = version is null ? null : Version.Parse(version);

        var r = await new DockerWslScanner().ScanAsync(s.Context(), null, CancellationToken.None);
        var item = Assert.Single(Assert.Single(r.Groups, g => g.Id == DockerWslScanner.WslGroupId).Items);
        Assert.Empty(item.Actions);
        Assert.Equal(DockerWslScanner.WslTooOld, item.DisabledReason);
        Assert.Contains("wsl --update", item.DisabledReason);
    }
}
