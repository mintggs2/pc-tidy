using PcTidy.Core;
using PcTidy.Features.Learning.Advisor;
using PcTidy.Features.Learning.Learners;
using PcTidy.Features.Learning.Model;
using PcTidy.Features.Learning.Sources;
using PcTidy.Features.Learning.Store;
using PcTidy.Features.Organize;
using D = PcTidy.Features.Learning.Model.LearningDecision;

namespace PcTidy.Features.Learning.Tests;

public class AdvisorTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
    private readonly TestFolder _box = new("advisor");
    private readonly TestClock _clock = new(T0);
    private readonly SandboxKnownLocations _locations;
    private readonly AppSettings _settings;
    private readonly FakeDrives _drives = new();
    private readonly FakeApps _apps = new();

    public AdvisorTests()
    {
        _locations = new SandboxKnownLocations(_box.Root, SandboxProfile.Default);
        _settings = new AppSettings { DataRoot = _locations.DataRoot };
    }

    public void Dispose() => _box.Dispose();

    private LearningAdvisor NewAdvisor(Func<string, bool>? folderExists = null) => new(
        LearningStore.Open(_locations.DataRoot, _clock), _settings, _locations, journal: null,
        new LearningSources { Drives = _drives, UserAssist = _apps, FolderExists = folderExists ?? (_ => true) },
        _clock, id => id == "cleanup" ? "Clean" : null);

    [Fact]
    public void Three_skips_untick_a_group_and_the_page_explains_it()
    {
        var a = NewAdvisor();
        for (int i = 0; i < 3; i++)
        {
            Assert.False(a.IsUntickedByDefault("cleanup", "crash-dumps"));
            a.RecordDecision("cleanup", "crash-dumps", D.Reject, groupTitle: "Crash reports", example: $"dump{i}.dmp");
            _clock.Advance(TimeSpan.FromDays(1));
        }
        Assert.True(a.IsUntickedByDefault("cleanup", "crash-dumps"));
        Assert.Contains("3 of the last 3", a.SkipAdvice("cleanup", "crash-dumps")!.Reason);

        var card = Assert.Single(a.LearnedItems(), i => i.Kind == LearnedKind.Skip);
        Assert.Equal("Crash reports starts unticked", card.Title);
        Assert.Equal(T0, card.SinceUtc);
        Assert.Equal(3, card.Examples.Count);
        Assert.Contains("In Clean", card.Effect);

        // Turn off: kept, but no longer unticks.
        a.SetOff(card, true);
        Assert.False(a.IsUntickedByDefault("cleanup", "crash-dumps"));
        Assert.True(Assert.Single(a.LearnedItems(), i => i.Kind == LearnedKind.Skip).Off);
        a.SetOff(card, false);
        Assert.True(a.IsUntickedByDefault("cleanup", "crash-dumps"));

        // Reset forgets the choices.
        a.ResetSkip("cleanup", "crash-dumps");
        Assert.False(a.IsUntickedByDefault("cleanup", "crash-dumps"));
        Assert.Null(a.SkipAdvice("cleanup", "crash-dumps"));
    }

    [Fact]
    public void Undo_of_an_accepted_run_counts_double()
    {
        var a = NewAdvisor();
        a.RecordDecision("cleanup", "temp", D.Reject);
        a.RecordDecision("cleanup", "temp", D.Accept, runId: "run-1");
        Assert.False(a.IsUntickedByDefault("cleanup", "temp"));
        a.RecordUndo("run-1");
        // newest first: undo (2 skips), accept → only 2 of 3
        Assert.False(a.IsUntickedByDefault("cleanup", "temp"));
        a.RecordDecision("cleanup", "temp", D.Reject);
        Assert.True(a.IsUntickedByDefault("cleanup", "temp"));
    }

    [Fact]
    public void Paused_or_switched_off_learning_records_nothing_but_still_applies()
    {
        var a = NewAdvisor();
        for (int i = 0; i < 3; i++) a.RecordDecision("cleanup", "g", D.Reject);
        a.SetPaused(true);
        Assert.True(a.IsPaused);
        a.RecordDecision("cleanup", "g", D.Accept);
        a.RecordFolderVisit(Path.Combine(_locations.Downloads, "x"));
        Assert.True(a.IsUntickedByDefault("cleanup", "g"));
        Assert.Empty(a.FrequentFolders());
        a.SetPaused(false);

        _settings.LearningEnabled = false;
        Assert.True(a.IsPaused);
        Assert.True(a.IsOffInSettings);
        a.RecordDecision("cleanup", "g", D.Accept);
        _settings.LearningEnabled = true;
        Assert.True(a.IsUntickedByDefault("cleanup", "g"));
        Assert.Equal(3, a.Store.Decisions().Count);
    }

    [Fact]
    public void Folders_are_private_fade_and_can_be_deleted()
    {
        var a = NewAdvisor();
        var videos = Path.Combine(_locations.Videos, "Clips");
        var cloud = Path.Combine(_locations.CloudRoots[0], "Secret");
        a.RecordFolderVisit(videos);
        a.RecordFolderVisit(videos);
        a.RecordFolderVisit(cloud); // inside OneDrive: not remembered
        a.RecordFolderVisit(Path.Combine(_locations.DataRoot, "Quarantine")); // PC Tidy's own folder: not remembered
        var f = Assert.Single(a.FrequentFolders());
        Assert.Equal(videos, f.Folder);
        Assert.Equal(2, f.Visits);

        _settings.AllowedAskFirstFolders.Add(cloud);
        var allowed = NewAdvisor();
        allowed.RecordFolderVisit(cloud);
        Assert.Equal(2, allowed.FrequentFolders().Count);

        var card = Assert.Single(allowed.LearnedItems(), i => i.Kind == LearnedKind.Folder && i.Title == "Clips");
        allowed.Delete(card);
        Assert.Equal(cloud, Assert.Single(allowed.FrequentFolders()).Folder);
    }

    [Fact]
    public void Folders_that_no_longer_exist_are_not_offered()
    {
        var a = NewAdvisor(folderExists: f => !f.EndsWith("Gone"));
        a.RecordFolderVisit(Path.Combine(_locations.Videos, "Gone"));
        Assert.Empty(a.FrequentFolders());
    }

    [Fact]
    public async Task Drive_forecast_appears_after_fourteen_daily_readings()
    {
        var a = NewAdvisor();
        const long GB = 1L << 30;
        for (int day = 0; day < 14; day++)
        {
            _drives.Free = 100 * GB - day * 2 * GB;
            await a.ObserveAsync(CancellationToken.None);
            // a second reading the same day replaces the first
            await a.ObserveAsync(CancellationToken.None);
            var f = Assert.Single(a.DriveForecasts());
            Assert.Equal(day + 1, f.Samples);
            if (day < 13) Assert.Null(f.DaysUntilFull);
            _clock.Advance(TimeSpan.FromDays(1));
        }
        var forecast = Assert.Single(a.DriveForecasts());
        Assert.Equal(37, forecast.DaysUntilFull); // 74 GB left, 2 GB a day
        Assert.Contains(a.LearnedItems(), i => i.Kind == LearnedKind.Drive && i.Reason.Contains("full in about 37 days"));
    }

    [Fact]
    public async Task Unused_apps_wait_for_thirty_days_of_watching()
    {
        var a = NewAdvisor();
        _apps.Usage = new List<AppUsage>
        {
            new(@"C:\Program Files\Paint3D\paint.exe", "paint", AppSource.UserAssist, 4, T0.AddDays(-120)),
            new(@"C:\Program Files\Editor\editor.exe", "editor", AppSource.UserAssist, 10, T0.AddDays(-1)),
        };
        await a.ObserveAsync(CancellationToken.None);
        _clock.Advance(TimeSpan.FromDays(29));
        await a.ObserveAsync(CancellationToken.None);
        Assert.Empty(a.UnusedApps());
        Assert.Equal(1, a.UnusedAppsReport().DaysUntilReady);

        // The editor keeps being used (its count goes up); paint doesn't.
        _clock.Advance(TimeSpan.FromDays(2));
        _apps.Usage[1] = _apps.Usage[1] with { RunCount = 15, LastRunUtc = _clock.UtcNow.AddHours(-1) };
        await a.ObserveAsync(CancellationToken.None);
        var app = Assert.Single(a.UnusedApps());
        Assert.Equal("paint", app.Name);
        Assert.Contains("hasn't seen it opened or running since", app.Reason);

        var card = Assert.Single(a.LearnedItems(), i => i.Kind == LearnedKind.App);
        a.SetOff(card, true);
        Assert.Empty(a.UnusedApps());
    }

    private sealed class FakeRunning : IRunningAppsSource
    {
        public HashSet<string> Paths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyCollection<string> RunningProgramPaths() => Paths;
    }

    private sealed class FakeStartup : IStartupAppsSource
    {
        public HashSet<string> Paths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);
        public (IReadOnlyCollection<string>, IReadOnlyCollection<string>) StartupPrograms() => (Paths, Names);
    }

    [Fact]
    public async Task Apps_that_run_every_day_or_start_with_Windows_are_never_called_unused()
    {
        var running = new FakeRunning();
        var startup = new FakeStartup();
        var a = new LearningAdvisor(LearningStore.Open(_locations.DataRoot, _clock), _settings, _locations, journal: null,
            new LearningSources { UserAssist = _apps, Running = running, Startup = startup }, _clock);
        // All three were opened from Start once, long ago; Windows' launch record never changes after that.
        const string chat = @"C:\Users\tester\AppData\Local\Chat\chat.exe", music = @"C:\Users\tester\AppData\Roaming\Music\music.exe", paint = @"C:\Program Files\Paint3D\paint.exe";
        _apps.Usage = new List<AppUsage>
        {
            new(chat, "chat", AppSource.UserAssist, 1, T0.AddDays(-200)),
            new(music, "music", AppSource.UserAssist, 1, T0.AddDays(-200)),
            new(paint, "paint", AppSource.UserAssist, 1, T0.AddDays(-200)),
        };
        await a.ObserveAsync(CancellationToken.None);
        startup.Paths.Add(music); // started at sign-in through the Run key
        for (int day = 0; day < 35; day++)
        {
            _clock.Advance(TimeSpan.FromDays(1));
            running.Paths.Clear();
            running.Paths.Add(chat); // reopened from its tray icon every day
            a.SampleIdle();
        }
        await a.ObserveAsync(CancellationToken.None);

        var unused = Assert.Single(a.UnusedApps());
        Assert.Equal("paint", unused.Name);
        Assert.Contains("opened or running", unused.Reason);
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\Chat\\chat.exe\" --tray", @"C:\Program Files\Chat\chat.exe")]
    [InlineData(@"C:\Program Files\VPN\vpn.exe /minimized", @"C:\Program Files\VPN\vpn.exe")]
    [InlineData("rundll32 something", null)]
    public void Run_commands_give_the_program_they_start(string command, string? program)
        => Assert.Equal(program, WindowsStartupApps.ProgramOf(command));

    [Fact]
    public void Installed_apps_never_launched_are_watched_too()
    {
        var a = NewAdvisor();
        _apps.Usage = new List<AppUsage> { new(@"C:\Program Files\Tool\bin\tool.exe", "tool", AppSource.UserAssist, 2, T0.AddDays(-2)) };
        a.ObserveInstalledApps(new[]
        {
            new WatchedApp("app:tool", "Tool", @"C:\Program Files\Tool"),
            new WatchedApp("app:never", "Never Opened", @"C:\Program Files\Never"),
        });
        a.ObserveAsync(CancellationToken.None).GetAwaiter().GetResult();
        _clock.Advance(TimeSpan.FromDays(40));
        a.ObserveAsync(CancellationToken.None).GetAwaiter().GetResult();
        var never = Assert.Single(a.UnusedApps(), x => x.Key == "app:never");
        Assert.Contains("40 days it has been watching", never.Reason);
        Assert.Contains(a.UnusedApps(), x => x.Key == "app:tool"); // last launch 42 days ago
    }

    [Fact]
    public void Rule_suggestion_can_be_edited_turned_off_made_a_rule_and_deleted()
    {
        var a = NewAdvisor();
        var dl = _locations.Downloads;
        var dest = Path.Combine(_box.Root, "D", "3D");
        var store = a.Store;
        foreach (var m in Moves.Daily("blend", dl, dest, 5)) store.AddMinedRun(new RunMark(m.RunId, m.Utc, new[] { @"C:\" }), new[] { m }, Array.Empty<FolderVisit>());

        var rule = Assert.Single(a.SuggestedRules());
        Assert.Equal(dest, rule.Destination);
        var card = Assert.Single(a.LearnedItems(), i => i.Kind == LearnedKind.Rule);
        Assert.Contains("5 moves on 5 days", card.Counts);

        a.EditRule(card, "My 3D files", Path.Combine(_box.Root, "D", "Models"));
        Assert.Equal(("My 3D files", Path.Combine(_box.Root, "D", "Models")), (a.SuggestedRules()[0].Name, a.SuggestedRules()[0].Destination));

        a.SetOff(card, true);
        Assert.Empty(a.SuggestedRules());
        a.SetOff(card, false);

        var hand = a.MakeRule(card, new OrganizeSettingsRuleSink(_settings));
        Assert.True(hand.Added);
        var organize = OrganizeSettings.Load(_settings);
        Assert.Equal("My 3D files", organize.Rules[0].Name);
        Assert.False(organize.Rules[0].RunAutomatically);
        Assert.Empty(a.SuggestedRules()); // it's a rule now
        Assert.DoesNotContain(a.LearnedItems(), i => i.Kind == LearnedKind.Rule);
        // Handing the same rule over again doesn't add a copy.
        Assert.False(new OrganizeSettingsRuleSink(_settings).Add(rule with { Name = "My 3D files", Destination = Path.Combine(_box.Root, "D", "Models") }).Added);

        // Delete forgets the evidence.
        var b = NewAdvisor();
        foreach (var m in Moves.Daily("stl", dl, dest, 5, idStart: 50)) b.Store.AddMinedRun(new RunMark(m.RunId, m.Utc, new[] { @"C:\" }), new[] { m }, Array.Empty<FolderVisit>());
        var stl = Assert.Single(b.LearnedItems(), i => i.Kind == LearnedKind.Rule && i.Title.Contains(".stl"));
        b.Delete(stl);
        Assert.DoesNotContain(b.SuggestedRules(), r => r.Value == "stl");
        Assert.DoesNotContain(b.Store.Moves(), m => m.SourcePath.EndsWith(".stl"));
    }

    [Fact]
    public void Forget_everything_empties_the_page()
    {
        var a = NewAdvisor();
        for (int i = 0; i < 3; i++) a.RecordDecision("cleanup", "g", D.Reject);
        a.RecordFolderVisit(_locations.Videos);
        Assert.NotEmpty(a.LearnedItems());
        int changed = 0;
        a.Changed += (_, _) => changed++;
        a.ForgetEverything();
        Assert.Empty(a.LearnedItems());
        Assert.False(a.IsUntickedByDefault("cleanup", "g"));
        Assert.True(changed > 0);
    }

    [Fact]
    public void A_damaged_file_never_breaks_the_callers()
    {
        var path = LearningStore.PathFor(_locations.DataRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "garbage garbage garbage garbage garbage garbage garbage garbage garbage garbage");
        var a = NewAdvisor();
        Assert.NotNull(a.Problem);
        Assert.False(a.IsUntickedByDefault("cleanup", "g"));
        a.RecordDecision("cleanup", "g", D.Reject);
        Assert.Empty(a.SuggestedRules());
        Assert.Empty(a.LearnedItems());
        a.ForgetEverything();
        Assert.Null(a.Problem);
        a.RecordDecision("cleanup", "g", D.Reject);
        Assert.Single(a.Store.Decisions());
    }

    [Fact]
    public void Quiet_hours_learn_from_idle_samples()
    {
        var signals = new FixedIdle { Idle = true };
        var a = new LearningAdvisor(LearningStore.Open(_locations.DataRoot, _clock), _settings, _locations, null,
            new LearningSources { Idle = signals }, _clock);
        var at3 = new DateTime(2026, 9, 1, 3, 0, 0, DateTimeKind.Local).ToUniversalTime();
        for (int i = 0; i < 5; i++)
        {
            _clock.UtcNow = at3.AddDays(i);
            a.SampleIdle();
        }
        Assert.Equal(new[] { 3 }, a.UsualQuietHours().Hours);
        Assert.Contains(a.LearnedItems(), i => i.Kind == LearnedKind.QuietHours);
    }

    private sealed class FakeDrives : IDriveSpaceSource
    {
        public long Free { get; set; } = 100L << 30;
        public IReadOnlyList<FreeSpaceSample> Read(DateTime nowUtc) => new[] { new FreeSpaceSample(@"C:\", nowUtc, 500L << 30, Free) };
    }

    private sealed class FakeApps : IAppUsageSource
    {
        public List<AppUsage> Usage { get; set; } = new();
        public IReadOnlyList<AppUsage> Read(CancellationToken ct) => Usage.ToList();
    }

    private sealed class FixedIdle : IIdleSignals
    {
        public bool Idle { get; set; }
        public uint TickCount => 10_000_000;
        public uint? LastInputTick => Idle ? 0u : 9_999_000u;
        public UserNotificationState NotificationState => UserNotificationState.AcceptsNotifications;
        public bool? OnBattery => false;
    }
}
