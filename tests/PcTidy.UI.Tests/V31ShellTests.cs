using System.Runtime.ExceptionServices;
using PcTidy.App.Pages;
using PcTidy.App.Pages.Now;
using PcTidy.App.Shell;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.UI.Flow;
using Xunit;

namespace PcTidy.UI.Tests;

/// <summary>v3.1 shell and shared UI: rail More and counters, progress states, restore points, tour, Measure link, one task at a time.</summary>
public class V31ShellTests
{
    // ---------------- fakes ----------------

    private sealed class FakeModule(string id, NavGroup group, int order = 0, string? title = null) : IFeatureModule
    {
        public string Id => id;
        public string Title => title ?? id;
        public string Summary => "";
        public NavGroup Group => group;
        public string Icon => "Box24";
        public int Order => order;
        public Release Release => Release.V1;
        public IReadOnlyList<IFeatureScanner> Scanners => Array.Empty<IFeatureScanner>();
        public object? CreatePage(IServiceProvider services) => null;
    }

    private sealed class BadgeModule(string id, Func<CancellationToken, Task<string?>> badge) : IFeatureModule, INavBadgeSource
    {
        public string Id => id;
        public string Title => id;
        public string Summary => "";
        public NavGroup Group => NavGroup.SpeedUp;
        public string Icon => "Box24";
        public int Order => 0;
        public Release Release => Release.V1;
        public IReadOnlyList<IFeatureScanner> Scanners => Array.Empty<IFeatureScanner>();
        public object? CreatePage(IServiceProvider services) => null;
        public Task<string?> GetNavBadgeAsync(CancellationToken ct) => badge(ct);
    }

    private static PlanItem Item(string feature = "tweaks", bool reversible = true, Dictionary<string, string>? args = null) => new()
    {
        Kind = ActionKind.RegistrySetValue, FeatureId = feature, Description = "Change a setting", Reversible = reversible, Args = args,
    };

    private static RunRecord Run(string feature, StepStatus step = StepStatus.Done, RunStatus status = RunStatus.Completed, ExecutionMode mode = ExecutionMode.Real,
        DateTime? created = null, string? parent = null, bool reversible = true, PlanItem? item = null) => new()
    {
        RunId = Guid.NewGuid().ToString("N"), ParentRunId = parent, FeatureId = feature, Title = feature, Mode = mode, Status = status,
        CreatedUtc = created ?? DateTime.UtcNow,
        Steps = new[] { new StepRecord { Index = 0, Item = item ?? Item(feature, reversible), Status = step } },
    };

    /// <summary>Runs WPF code on its own STA thread (controls need one).</summary>
    private static void Sta(Action action)
    {
        ExceptionDispatchInfo? error = null;
        var t = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        error?.Throw();
    }

    // ---------------- rail: groups and "More" ----------------

    [Fact]
    public void Rarely_used_sections_go_under_More_at_the_end_and_the_groups_keep_the_rest()
    {
        var modules = new IFeatureModule[]
        {
            new FakeModule("cleanup", NavGroup.CleanUp), new FakeModule("developer", NavGroup.CleanUp),
            new FakeModule("speed", NavGroup.SpeedUp), new FakeModule("tweaks", NavGroup.SpeedUp),
            new FakeModule("photos", NavGroup.Organize), new FakeModule("desktoptidy", NavGroup.Organize), new FakeModule("drivemove", NavGroup.Organize), new FakeModule("aichats", NavGroup.Organize),
            new FakeModule("assistant", NavGroup.You), new FakeModule("learned", NavGroup.You),
        };
        var rows = RailLayout.Build(modules);

        Assert.Equal(RailEntryKind.More, rows[^1].Kind);
        Assert.Equal(new[] { "developer", "photos", "desktoptidy", "drivemove", "aichats", "assistant", "learned" }, rows[^1].MoreChildren.Select(m => m.Id));
        Assert.Equal(new[] { "CLEAN UP", "SPEED UP" }, rows.Where(r => r.Kind == RailEntryKind.Header).Select(r => r.Text)); // ORGANIZE and YOU are empty now: no header
        Assert.Equal(new[] { "cleanup", "speed", "tweaks" }, rows.Where(r => r.Kind == RailEntryKind.Module).Select(r => r.Module!.Id));
        Assert.Single(rows, r => r.Kind == RailEntryKind.More);
    }

    [Fact]
    public void The_real_rail_starts_at_about_16_section_rows()
    {
        var modules = PcTidy.App.Composition.FeatureCatalog.All().OrderBy(m => m.Group).ThenBy(m => m.Order).ToList();
        var rows = RailLayout.Build(modules);
        int sectionRows = rows.Count(r => r.Kind is RailEntryKind.Module or RailEntryKind.More);
        Assert.InRange(sectionRows, 14, 16); // about 16 (layout spec §4.2), so "More" is on screen at 1024×700
        Assert.Equal(RailLayout.MoreIds.Count, rows.Single(r => r.Kind == RailEntryKind.More).MoreChildren.Count); // every More id is a real module
        Assert.True(RailLayout.VisibleRows(rows, moreOpen: true) - RailLayout.VisibleRows(rows, moreOpen: false) == RailLayout.MoreIds.Count);
    }

    [Fact]
    public void More_opens_by_itself_for_a_section_inside_it_and_its_state_is_remembered()
    {
        Assert.True(RailLayout.ShouldOpenMore(remembered: false, currentModuleId: "photos"));
        Assert.True(RailLayout.ShouldOpenMore(remembered: true, currentModuleId: "cleanup"));
        Assert.False(RailLayout.ShouldOpenMore(remembered: false, currentModuleId: "cleanup"));
        Assert.False(RailLayout.ShouldOpenMore(remembered: false, currentModuleId: null));

        var settings = new AppSettings();
        Assert.False(ShellState.Get(settings).RailMoreOpen); // closed by default
        ShellState.Update(settings, s => s with { RailMoreOpen = true });
        var json = System.Text.Json.JsonSerializer.Serialize(settings, AppSettings.Json);
        var back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json, AppSettings.Json)!;
        Assert.True(ShellState.Get(back).RailMoreOpen);
    }

    [Fact]
    public void Shell_screen_state_is_left_out_of_settings_exports()
    {
        var settings = new AppSettings();
        ShellState.Update(settings, s => s with { RailMoreOpen = true, HistoryViewedUtc = DateTime.UtcNow });
        ShellPrefs.Set(settings, ShellPrefs.Get(settings) with { ShowNowCard = true });
        var (json, _) = PcTidy.App.Portability.SettingsPackage.ExportJson(settings, @"C:\Users\someone");
        Assert.DoesNotContain(ShellState.Key, json);
        Assert.Contains("ShowNowCard", json); // a real preference travels
    }

    // ---------------- rail counters ----------------

    [Fact]
    public async Task Counters_come_from_each_source_and_a_failure_a_timeout_or_nothing_shows_nothing()
    {
        var modules = new IFeatureModule[]
        {
            new BadgeModule("tweaks", _ => Task.FromResult<string?>("7 of 31 on")),
            new BadgeModule("services", _ => throw new InvalidOperationException("can't read")),
            new BadgeModule("slow", async ct => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return "late"; }),
            new BadgeModule("blank", _ => Task.FromResult<string?>("   ")),
            new BadgeModule("new", _ => Task.FromResult<string?>("NEW")),
            new BadgeModule("long", _ => Task.FromResult<string?>("a very long counter text that never fits the rail")),
            new FakeModule("plain", NavGroup.CleanUp),
        };
        var started = DateTime.UtcNow;
        var badges = await new NavBadgeFetcher(TimeSpan.FromMilliseconds(200)).FetchAsync(modules, CancellationToken.None);

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10)); // the slow one was cut off
        Assert.Equal("7 of 31 on", badges["tweaks"]);
        Assert.Null(badges["services"]);
        Assert.Null(badges["slow"]);
        Assert.Null(badges["blank"]);
        Assert.Null(badges["new"]); // never a "NEW" badge
        Assert.True(badges["long"]!.Length <= NavBadgeFetcher.MaxLength);
        Assert.False(badges.ContainsKey("plain"));
    }

    [Fact]
    public async Task A_source_that_blocks_never_holds_up_the_caller()
    {
        using var release = new ManualResetEventSlim();
        var modules = new IFeatureModule[]
        {
            new BadgeModule("blocking", _ => { release.Wait(TimeSpan.FromSeconds(5)); return Task.FromResult<string?>("3 changed"); }),
        };
        var fetch = new NavBadgeFetcher(TimeSpan.FromSeconds(10)).FetchAsync(modules, CancellationToken.None);
        Assert.False(fetch.IsCompleted); // returned at once: the source runs on the thread pool
        release.Set();
        var badges = await fetch;
        Assert.Equal("3 changed", badges["blocking"]);
    }

    [Fact]
    public void History_counts_undoable_runs_since_it_was_last_opened()
    {
        var seen = DateTime.UtcNow.AddHours(-1);
        var parent = "parent-1";
        var runs = new[]
        {
            Run("cleanup", created: seen.AddMinutes(10)),                                         // counts
            Run("tweaks", created: seen.AddMinutes(20), parent: parent),                           // one Optimize run …
            Run("speed", created: seen.AddMinutes(20), parent: parent),                            // … counted once
            Run("cleanup", created: seen.AddMinutes(-10)),                                         // before: seen already
            Run("cleanup", created: seen.AddMinutes(30), mode: ExecutionMode.Simulate, status: RunStatus.Simulated, step: StepStatus.Simulated),
            Run("cleanup", created: seen.AddMinutes(40), status: RunStatus.Undone, step: StepStatus.Undone),
            Run("cleanup", created: seen.AddMinutes(50), reversible: false),                      // nothing to undo
        };
        Assert.Equal(2, HistoryBadge.Count(runs, seen));
        Assert.Equal(0, HistoryBadge.Count(runs, null));
        Assert.Equal("2", HistoryBadge.Text(2));
        Assert.Null(HistoryBadge.Text(0));
    }

    // ---------------- progress overlay ----------------

    [Fact]
    public void Sections_move_from_waiting_to_running_to_done_skipped_or_needs_restart()
    {
        var sections = new ProgressSections(new[] { "Clean", "Tweaks", "Organize", "Speed" });
        int changes = 0;
        sections.Changed += () => changes++;
        Assert.All(sections.Items, s => Assert.Equal(SectionRunState.Waiting, s.State));

        sections.Start(0);
        Assert.Equal(SectionRunState.Running, sections.Items[0].State);
        sections.Finish(0, Run("cleanup"), needsRestart: false);
        Assert.Equal(SectionRunState.Done, sections.Items[0].State);

        sections.Start(1);
        var restartItem = Item(args: new Dictionary<string, string> { ["needsRestart"] = "true" });
        var tweaks = Run("tweaks", item: restartItem);
        sections.Finish(1, tweaks, ProgressSections.NeedsRestart(tweaks, null));
        Assert.Equal(SectionRunState.NeedsRestart, sections.Items[1].State);

        sections.Start(2);
        sections.Finish(2, Run("organize", step: StepStatus.Skipped), needsRestart: false);
        Assert.Equal(SectionRunState.Skipped, sections.Items[2].State); // nothing done

        sections.StopRemaining(); // "Stop after this step"
        Assert.Equal(SectionRunState.Skipped, sections.Items[3].State);

        sections.Start(0); // a finished section never starts again
        Assert.Equal(SectionRunState.Done, sections.Items[0].State);
        Assert.Equal(7, changes);
        Assert.Equal("Needs restart", ProgressSections.Label(SectionRunState.NeedsRestart));
        Assert.Equal("Everything done so far is already saved and can be undone.", ProgressSections.SavedLine);
    }

    [Fact]
    public void Needs_restart_comes_from_the_preview_ids_or_the_item_itself_and_only_for_done_steps()
    {
        var item = Item();
        var done = Run("tweaks", item: item);
        Assert.True(ProgressSections.NeedsRestart(done, new HashSet<string> { item.Id }));
        Assert.False(ProgressSections.NeedsRestart(done, new HashSet<string>()));
        var skipped = Run("tweaks", step: StepStatus.Skipped, item: item);
        Assert.False(ProgressSections.NeedsRestart(skipped, new HashSet<string> { item.Id }));
    }

    // ---------------- History › Restore points ----------------

    private static readonly DateTime T0 = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Restore_points_are_newest_first_and_the_newest_never_offers_delete()
    {
        var rows = RestorePointsModel.Build(new[]
        {
            new RestorePointFact(7, "Older", T0, RestorePointsModel.ApplicationInstall),
            new RestorePointFact(9, "Newest", T0.AddDays(3), RestorePointsModel.ModifySettings),
            new RestorePointFact(8, "Middle", T0.AddDays(1), RestorePointsModel.DeviceDriverInstall),
        });
        Assert.Equal(new[] { "Newest", "Middle", "Older" }, rows.Select(r => r.Name));
        Assert.True(rows[0].IsNewest);
        Assert.False(rows[0].CanDelete);
        Assert.Null(RestorePointsModel.DeletePlan(rows[0]));
        Assert.All(rows.Skip(1), r => Assert.True(r.CanDelete));

        var plan = RestorePointsModel.DeletePlan(rows[1])!;
        var item = Assert.Single(plan.Items);
        Assert.Equal(ActionKind.RestorePointDelete, item.Kind);
        Assert.Equal("8", item.Args!["sequenceNumber"]);
        Assert.False(item.Reversible);
        Assert.True(item.NeedsAdmin);
        Assert.Equal("Made before a driver was installed", rows[1].Description);
    }

    [Fact]
    public void A_single_restore_point_is_the_newest_and_cant_be_deleted()
    {
        var row = Assert.Single(RestorePointsModel.Build(new[] { new RestorePointFact(3, "Only", T0, 0) }));
        Assert.False(row.CanDelete);
        Assert.Null(RestorePointsModel.DeletePlan(row));
    }

    [Fact]
    public void Colour_tags_stay_on_their_point_and_unknown_tags_are_ignored()
    {
        var fact = new RestorePointFact(5, "Point", T0, 0);
        var key = RestorePointsModel.TagKey(fact);
        var tags = RestorePointsModel.WithTag(new Dictionary<string, string>(), key, "Blue");
        Assert.Equal("Blue", RestorePointsModel.Build(new[] { fact }, tags)[0].Tag);
        Assert.Null(RestorePointsModel.Build(new[] { fact }, RestorePointsModel.WithTag(tags, key, null))[0].Tag);
        Assert.Null(RestorePointsModel.Build(new[] { fact }, new Dictionary<string, string> { [key] = "Glitter" })[0].Tag);
        // A reused sequence number with another date is another point.
        Assert.Null(RestorePointsModel.Build(new[] { fact with { CreatedUtc = T0.AddDays(9) } }, tags)[0].Tag);
    }

    // ---------------- spotlight tour ----------------

    [Fact]
    public void The_tour_steps_forward_and_back_and_closes_once()
    {
        var tour = new TourModel();
        Assert.InRange(tour.Count, 6, 8);
        Assert.Equal("Step 1 of " + tour.Count, tour.StepText);
        Assert.False(tour.CanGoBack);
        tour.Back();
        Assert.Equal(0, tour.Index); // no step before the first

        tour.Next();
        Assert.Equal(1, tour.Index);
        Assert.True(tour.CanGoBack);
        tour.Back();
        Assert.Equal(0, tour.Index);

        int closed = 0;
        tour.Closed += () => closed++;
        for (int i = 0; i < tour.Count - 1; i++) tour.Next();
        Assert.True(tour.IsLast);
        Assert.Equal("Done", tour.NextText);
        tour.Next(); // Done closes
        Assert.False(tour.IsOpen);
        tour.Close();
        tour.Next();
        Assert.Equal(1, closed);
        Assert.Equal(tour.Count - 1, tour.Index);
    }

    [Fact]
    public void The_tour_covers_scan_everything_the_groups_history_quarantine_settings_and_simulate()
    {
        var targets = TourModel.DefaultSteps.Select(s => s.Target).ToList();
        Assert.Contains("home.scan", targets);
        Assert.Contains("rail.groups", targets);
        Assert.Contains("rail.history", targets);
        Assert.Contains("rail.quarantine", targets);
        Assert.Contains("rail.settings", targets);
        Assert.Contains(TourModel.DefaultSteps, s => s.Title.Contains("Simulate"));
    }

    [Fact]
    public void The_tour_card_stays_inside_a_1024_by_700_window()
    {
        var window = new System.Windows.Size(1024, 700);
        var card = new System.Windows.Size(340, 180);
        foreach (var target in new System.Windows.Rect?[] { new(8, 600, 220, 28), new(760, 180, 200, 48), new(8, 80, 230, 500), null })
        {
            var p = TourOverlay.CardPosition(target, card, window);
            Assert.InRange(p.X, 0, window.Width - card.Width);
            Assert.InRange(p.Y, 0, window.Height - card.Height);
            if (target is { } r) Assert.False(new System.Windows.Rect(p, card).IntersectsWith(r), "the card never covers what it points at");
        }
    }

    // ---------------- "Check if this helped" ----------------

    [Fact]
    public void Check_if_this_helped_shows_for_tweaks_games_speed_and_services_runs_when_Measure_exists()
    {
        foreach (var feature in new[] { "tweaks", "games", "speed", "services" })
        {
            var run = Run(feature);
            var request = RunCheck.For(new[] { run }, measureAvailable: true);
            Assert.NotNull(request);
            Assert.Equal(run.RunId, request!.RunId);
        }
        Assert.Null(RunCheck.For(new[] { Run("cleanup") }, true));       // not a performance section
        Assert.Null(RunCheck.For(new[] { Run("tweaks") }, false));       // no Measure in this build
        Assert.Null(RunCheck.For(new[] { Run("tweaks", StepStatus.Undone, RunStatus.Undone) }, true));
        Assert.Null(RunCheck.For(new[] { Run("tweaks", StepStatus.Simulated, RunStatus.Simulated, ExecutionMode.Simulate) }, true));
        Assert.Null(RunCheck.For(new[] { Run("tweaks", StepStatus.Skipped) }, true)); // nothing was done
        Assert.Null(RunCheck.For(Array.Empty<RunRecord>(), true));
    }

    [Fact]
    public void An_optimize_run_hands_Measure_its_tweaks_section()
    {
        var clean = Run("cleanup", parent: "p");
        var tweaks = Run("tweaks", parent: "p");
        var request = RunCheck.For(new[] { clean, tweaks }, true)!;
        Assert.Equal(tweaks.RunId, request.RunId);
        Assert.Equal("p", request.ParentRunId);
        Assert.Equal(RunCheck.MeasureModuleId, "measure");
    }

    private sealed class FakeLauncher(bool available) : IRunCheckLauncher
    {
        public bool IsAvailable => available;
        public List<RunCheckRequest> Opened { get; } = new();
        public void Open(RunCheckRequest request) => Opened.Add(request);
    }

    [Fact]
    public void The_Done_card_hides_the_link_without_Measure_and_after_undo() => Sta(() =>
    {
        var card = new DoneCard { Runs = new[] { Run("speed") } };
        Assert.Null(card.CheckRequest); // no launcher
        card.RunCheckLauncher = new FakeLauncher(available: false);
        Assert.Null(card.CheckRequest);
        card.RunCheckLauncher = new FakeLauncher(available: true);
        Assert.NotNull(card.CheckRequest);
        card.MarkUndone();
        Assert.Null(card.CheckRequest);
    });

    [Fact]
    public void Parameters_reach_pages_through_the_navigation_hook()
    {
        var nav = new FakeNavigation();
        nav.OpenWith("measure", new RunCheckRequest("run-1", "tweaks", "Tweaks"));
        Assert.Equal(("measure", (object?)new RunCheckRequest("run-1", "tweaks", "Tweaks")), nav.Last);
        INavigationService plain = new PlainNavigation();
        plain.OpenWith("measure", "x"); // a shell without the hook still opens the section
        Assert.Equal("measure", ((PlainNavigation)plain).Opened);
    }

    private sealed class FakeNavigation : INavigationService, IParameterNavigation
    {
        public (string, object?) Last { get; private set; }
        public void Open(string moduleId, IReadOnlyCollection<string>? preselectItemIds = null, string? groupId = null) => Last = (moduleId, null);
        public void OpenWithParameter(string moduleId, object? parameter) => Last = (moduleId, parameter);
    }

    private sealed class PlainNavigation : INavigationService
    {
        public string? Opened { get; private set; }
        public void Open(string moduleId, IReadOnlyCollection<string>? preselectItemIds = null, string? groupId = null) => Opened = moduleId;
    }

    // ---------------- one long task at a time ----------------

    [Fact]
    public void The_gate_is_busy_while_any_task_runs_and_nested_tasks_are_counted()
    {
        var gate = new RunGate();
        int flips = 0;
        gate.Changed += (_, _) => flips++;
        Assert.True(gate.Allows(true));
        var a = gate.Begin("Clean");
        var b = gate.Begin("Undo");
        Assert.True(gate.IsBusy);
        Assert.False(gate.Allows(true));
        Assert.Equal("Available when Undo finishes", gate.Reason); // the latest task still going is named
        Assert.Equal("Available when the current task finishes", RunGate.BusyReason);
        a.Dispose();
        a.Dispose(); // twice is harmless
        Assert.True(gate.IsBusy);
        b.Dispose();
        Assert.False(gate.IsBusy);
        Assert.Null(gate.Reason);
        Assert.False(gate.Allows(false));
        Assert.Equal(2, flips); // busy once, idle once
    }

    [Fact]
    public void Gated_buttons_are_disabled_with_the_reason_and_get_the_pages_latest_wish_back() => Sta(() =>
    {
        var gate = new RunGate();
        var run = new System.Windows.Controls.Button { IsEnabled = true };
        var simulate = new System.Windows.Controls.Button { IsEnabled = false };
        using var h1 = RunGateButtons.Gate(run, gate);
        using var h2 = RunGateButtons.Gate(simulate, gate);

        var task = gate.Begin("Clean");
        Assert.False(run.IsEnabled);
        Assert.Equal("Available when Clean finishes", run.ToolTip); // the task is named
        Assert.Equal("Available when Clean finishes", System.Windows.Automation.AutomationProperties.GetHelpText(run));
        simulate.IsEnabled = true; // the page ticked something meanwhile
        Assert.False(simulate.IsEnabled);

        task.Dispose();
        Assert.True(run.IsEnabled);
        Assert.Null(run.ToolTip);
        Assert.True(simulate.IsEnabled); // the page's latest wish
    });

    // ---------------- confirm dialog ----------------

    [Fact]
    public void The_confirm_dialog_lists_every_item_and_puts_Back_before_Confirm() => Sta(() =>
    {
        var items = Enumerable.Range(1, 40).Select(i => new PlanItem
        {
            Kind = ActionKind.QuarantinePath, FeatureId = "cleanup", Description = $"Temp file {i}", SourcePath = $@"C:\t\{i}.tmp", ExpectedSize = 1024 * i,
        }).Append(new PlanItem { Kind = ActionKind.QuarantinePath, FeatureId = "cleanup", Description = "Empty the Recycle Bin", Reversible = false }).ToArray();
        var model = new ConfirmModel(new[] { new ActionPlan { FeatureId = "cleanup", Title = "Clean", Items = items } }, null, 7);

        var lines = ConfirmDialogContent.AffectedLines(model);
        Assert.Equal(41, lines.Count);
        Assert.StartsWith("Temp file 1 · ", lines[0]);
        Assert.EndsWith("can't be undone", lines[^1]);

        var content = new ConfirmDialogContent(model, suggestSimulation: false);
        Assert.NotNull(content.AffectedList);
        Assert.Equal(41, content.AffectedList!.Items.Count);
        Assert.True(content.AffectedList.MaxHeight < 200); // scrolls instead of growing

        var footer = content.AddFooter("Clean 820 KB", Wpf.Ui.Controls.SymbolRegular.Broom24, canRun: true, Wpf.Ui.Controls.ContentDialogButton.Close, _ => { });
        Assert.Equal(new[] { "Simulate first", "Back", "Clean 820 KB" }, footer.InOrder.Select(b => (string)b.Content));
        Assert.True(System.Windows.Input.KeyboardNavigation.GetTabIndex(footer.Back) < System.Windows.Input.KeyboardNavigation.GetTabIndex(footer.Primary));
    });

    // ---------------- Now card ----------------

    [Fact]
    public void The_Now_card_samples_only_when_on_open_shown_and_not_minimised()
    {
        Assert.True(NowPolicy.ShouldSample(enabled: true, collapsed: false, homeVisible: true, minimized: false));
        Assert.False(NowPolicy.ShouldSample(false, false, true, false)); // off by default in Settings
        Assert.False(NowPolicy.ShouldSample(true, true, true, false));   // folded
        Assert.False(NowPolicy.ShouldSample(true, false, false, false)); // Home not on screen
        Assert.False(NowPolicy.ShouldSample(true, false, true, true));   // minimised
        Assert.False(new ShellPrefs().ShowNowCard);
        Assert.Equal("Live, not recorded", NowPolicy.LiveLabel);
    }

    [Fact]
    public void The_Now_line_keeps_the_last_60_seconds_and_hides_GPU_when_unreadable()
    {
        var history = new NowHistory();
        for (int i = 0; i < 75; i++) history.Add(new NowSample(i, null, 40));
        Assert.Equal(60, history.Cpu.Count);
        Assert.Equal(15, history.Cpu[0]);
        Assert.False(history.HasGpu);
        history.Add(new NowSample(150, 30, 40)); // clamped
        Assert.Equal(100, history.Cpu[^1]);
        Assert.True(history.HasGpu);
    }

    private sealed class FailingSource : INowSource
    {
        public NowSample Read() => throw new InvalidOperationException("counter gone");
    }

    [Fact]
    public void A_failed_reading_shows_nothing_new_and_demo_numbers_are_invented()
    {
        var sampler = new NowSampler(new FailingSource());
        int seen = 0;
        sampler.Sampled += _ => seen++;
        sampler.SampleOnce();
        Assert.Equal(0, seen);

        var demo = new NowSampler(new DemoNowSource());
        NowSample? got = null;
        demo.Sampled += s => got = s;
        demo.SampleOnce();
        Assert.NotNull(got);
        Assert.InRange(got!.Cpu, 0, 100);
        Assert.NotNull(got.Gpu);
        Assert.False(demo.IsRunning);
        demo.Start();
        Assert.True(demo.IsRunning);
        demo.Stop();
        Assert.False(demo.IsRunning);
    }
}
