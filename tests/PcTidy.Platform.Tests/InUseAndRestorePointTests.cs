using PcTidy.Core;
using PcTidy.Core.Model;
using Xunit;

namespace PcTidy.Platform.Tests;

public class RestartManagerInUseCheckerTests
{
    private readonly RestartManagerInUseChecker _checker = new();

    [Fact]
    public async Task Open_file_is_in_use_closed_file_is_not()
    {
        using var sandbox = new TestSandbox();
        var held = Path.Combine(sandbox.Root, "held.txt");
        var free = Path.Combine(sandbox.Root, "free é 名.txt");
        File.WriteAllText(held, "x");
        File.WriteAllText(free, "y");

        using (new FileStream(held, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.True(_checker.IsLocked(held));
            var results = await _checker.CheckAsync(new[] { held, free }, default);

            Assert.True(results[0].InUse);
            Assert.NotEmpty(results[0].Apps); // Restart Manager names this test process
            Assert.False(results[1].InUse);
        }

        Assert.False(_checker.IsLocked(held));
        Assert.False((await _checker.CheckAsync(new[] { held }, default))[0].InUse);
    }

    [Fact]
    public async Task Folder_is_in_use_when_a_file_inside_is_and_batches_over_200_files_work()
    {
        using var sandbox = new TestSandbox();
        var busy = Directory.CreateDirectory(Path.Combine(sandbox.Root, "busy", "deep")).FullName;
        var quiet = Directory.CreateDirectory(Path.Combine(sandbox.Root, "quiet")).FullName;
        var many = new List<string>();
        for (var i = 0; i < 230; i++)
        {
            var f = Path.Combine(quiet, $"f{i}.txt");
            File.WriteAllText(f, "z");
            many.Add(f);
        }
        var held = Path.Combine(busy, "log.txt");
        File.WriteAllText(held, "x");

        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var paths = new List<string>(many) { Path.Combine(sandbox.Root, "busy"), quiet };
            var results = await _checker.CheckAsync(paths, default);

            Assert.Equal(paths.Count, results.Count);
            Assert.All(results.Take(230), r => Assert.False(r.InUse));
            Assert.True(results[230].InUse);
            Assert.False(results[231].InUse);
        }
    }

    [Fact]
    public async Task Missing_paths_directories_and_odd_names_are_not_locked()
    {
        using var sandbox = new TestSandbox();
        var missing = Path.Combine(sandbox.Root, "nope.txt");

        Assert.False(_checker.IsLocked(missing));
        Assert.False(_checker.IsLocked(sandbox.Root));
        Assert.False(_checker.IsLocked("CON"));
        var results = await _checker.CheckAsync(new[] { missing }, default);
        Assert.False(results.Single().InUse);
    }

    [Fact]
    public void Long_paths_work()
    {
        using var sandbox = new TestSandbox();
        var dir = sandbox.Root;
        while (dir.Length < 300) dir = Path.Combine(dir, new string('d', 40));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "long.txt");
        File.WriteAllText(file, "x");

        Assert.False(_checker.IsLocked(file));
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.True(_checker.IsLocked(file));
    }
}

/// <summary>Restore points: the sandbox path, the read-only listing, and the decision logic against a fake (no WMI writes).</summary>
public class RestorePointServiceTests
{
    private sealed class FakeBackend : IRestorePointBackend
    {
        public List<RestorePointEntry> Points { get; } = new();
        public SystemProtectionState Protection { get; set; } = SystemProtectionState.On;
        public bool CreateActuallyMakesOne { get; set; } = true;
        public int CreateCalls { get; private set; }
        public bool TouchesRealSystem => false;
        public SystemProtectionState GetProtection() => Protection;
        public RestorePointListing List() => new(Points.ToList(), null);
        public uint Create(string description)
        {
            CreateCalls++;
            if (CreateActuallyMakesOne) Points.Add(new RestorePointEntry((uint)(Points.Count + 100), description, DateTime.UtcNow, 12));
            return 0; // Windows says "success" either way
        }
    }

    private static RestorePointService Service(IKnownLocations loc, FakeBackend backend, bool elevated = true)
        => new(loc, backend, () => elevated, () => DateTime.UtcNow, TimeSpan.Zero);

    [Fact]
    public async Task Sandbox_fakes_a_created_point_without_calling_wmi()
    {
        using var sandbox = new TestSandbox();
        var info = await new RestorePointService(sandbox.Locations).EnsureAsync("Before PC Tidy tweaks", default);

        Assert.True(info.Created);
        Assert.Null(info.Problem);
        Assert.Contains("(sandbox)", info.Description);
    }

    [Fact]
    public async Task Listing_is_read_only_and_never_throws()
    {
        using var sandbox = new TestSandbox();
        var listing = await new RestorePointService(sandbox.Locations).ListAsync(default);

        Assert.NotNull(listing.Points);
        if (listing.Problem is null) Assert.All(listing.Points, p => Assert.True(p.CreatedUtc > DateTime.MinValue));
    }

    [Fact]
    public async Task Not_elevated_returns_needs_admin()
    {
        using var sandbox = new TestSandbox();
        var backend = new FakeBackend();
        var info = await Service(sandbox.Locations, backend, elevated: false).EnsureAsync("x", default);

        Assert.False(info.Created);
        Assert.Contains("admin", info.Problem);
        Assert.Equal(0, backend.CreateCalls);
    }

    [Fact]
    public async Task Point_from_the_last_24_hours_is_reused()
    {
        using var sandbox = new TestSandbox();
        var backend = new FakeBackend();
        backend.Points.Add(new RestorePointEntry(5, "Windows Update", DateTime.UtcNow.AddHours(-3), 0));

        var info = await Service(sandbox.Locations, backend).EnsureAsync("x", default);

        Assert.True(info.Reused);
        Assert.Equal("Windows Update", info.Description);
        Assert.Equal(0, backend.CreateCalls);
    }

    [Fact]
    public async Task New_point_is_verified_by_listing_again()
    {
        using var sandbox = new TestSandbox();
        var backend = new FakeBackend();
        backend.Points.Add(new RestorePointEntry(5, "Old", DateTime.UtcNow.AddDays(-3), 0));

        var info = await Service(sandbox.Locations, backend).EnsureAsync("Before PC Tidy", default);

        Assert.True(info.Created);
        Assert.Equal("Before PC Tidy", info.Description);
        Assert.Equal(1, backend.CreateCalls);
    }

    [Fact]
    public async Task Success_code_without_a_new_point_is_reported_as_a_problem()
    {
        using var sandbox = new TestSandbox();
        var backend = new FakeBackend { CreateActuallyMakesOne = false };

        var info = await Service(sandbox.Locations, backend).EnsureAsync("x", default);

        Assert.False(info.Created);
        Assert.False(info.Reused);
        Assert.Contains("none appeared", info.Problem);
    }

    [Theory]
    [InlineData(SystemProtectionState.Off)]
    [InlineData(SystemProtectionState.OffByPolicy)]
    public async Task System_protection_off_is_a_problem_and_nothing_is_attempted(SystemProtectionState state)
    {
        using var sandbox = new TestSandbox();
        var backend = new FakeBackend { Protection = state };

        var info = await Service(sandbox.Locations, backend).EnsureAsync("x", default);

        Assert.NotNull(info.Problem);
        Assert.Equal(0, backend.CreateCalls);
    }

    [Fact]
    public async Task Long_descriptions_are_trimmed()
    {
        using var sandbox = new TestSandbox();
        var backend = new FakeBackend();
        var info = await Service(sandbox.Locations, backend).EnsureAsync(new string('a', 300), default);
        Assert.Equal(RestorePointService.MaxDescriptionLength, info.Description!.Length);
    }
}
