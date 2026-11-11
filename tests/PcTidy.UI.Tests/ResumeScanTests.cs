using System.Text;
using System.Windows;
using System.Windows.Threading;
using PcTidy.App.Composition;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Hosting;
using PcTidy.UI.Flow;
using PcTidy.UI.Flow.Resume;
using PcTidy.UI.Tests.Shell;
using Wpf.Ui.Controls;
using Xunit;
using Badge = PcTidy.Core.Model.Badge;

namespace PcTidy.UI.Tests;

/// <summary>
/// Resumable scans: checkpoints (round trip, atomic writes, damaged files), every staleness rule, resuming runs only
/// what is left, the checkpoint goes at the end and on Start over, the restart note, and practice mode never reads the
/// real registry. Everything runs on a practice PC in testbed\shell\ with fake scanners and invented data.
/// Not run in parallel with other tests: the page check makes a WPF Application, which is shared by the whole process.
/// </summary>
[Collection(ResumeScanTests.Collection)]
public partial class ResumeScanTests
{
    public const string Collection = "Resume scans (WPF application)";

    private const string Version = "9.9.9-test";

    // ---------------- helpers ----------------

    private sealed class FakeScanner(string id, string title) : IFeatureScanner
    {
        public int Calls;
        public IReadOnlyList<string> Files { get; set; } = Array.Empty<string>();
        /// <summary>Runs inside the scan (e.g. to press Stop part way).</summary>
        public Action? During { get; set; }
        public string FeatureId => id;
        public string Title => title;
        public Release Release => Release.V1;
        public bool IncludedInOptimizeAll => true;

        public Task<ScanResult> ScanAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            During?.Invoke();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Result(id, title, Files));
        }
    }

    private static ScanResult Result(string id, string title, IReadOnlyList<string> files) => new()
    {
        FeatureId = id, Title = title, Depth = ScanDepth.Quick, Duration = TimeSpan.FromSeconds(3),
        FinishedUtc = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        Notes = new[] { "An invented note." },
        Groups = new[]
        {
            new ScanGroup
            {
                Id = id + ".group", Title = "Invented group", Description = "For tests", Badge = Badge.Check,
                Items = files.Select((f, i) => new ScanItem
                {
                    Id = $"{id}-{i}", Title = Path.GetFileName(f), Detail = f, Reason = "An invented reason.", Badge = Badge.Safe,
                    DefaultSelected = true, SizeBytes = 100 + i, FileCount = 1, LastModifiedUtc = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    Details = new Dictionary<string, string> { ["Kind"] = "Invented" },
                    Actions = new[]
                    {
                        new PlanItem
                        {
                            Id = $"{id}-{i}-a", Kind = ActionKind.QuarantinePath, FeatureId = id, Description = "Move to quarantine",
                            SourcePath = f, ExpectedSize = 100 + i, ExpectedLastWriteUtc = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                            CarveOutId = "test.carveout", Args = new Dictionary<string, string> { ["k"] = "v" }, DependsOn = new[] { "earlier" },
                        },
                        new PlanItem
                        {
                            Id = $"{id}-{i}-r", Kind = ActionKind.RegistrySetValue, FeatureId = id, Description = "Set an invented value",
                            NeedsAdmin = true, IsSystemChange = true, Reversible = true,
                            Registry = new RegistryValueSpec { Hive = RegistryHive.CurrentUser, KeyPath = @"Software\InventedVendor\InventedApp", ValueName = "Invented", Kind = RegistryKind.DWord, Data = "1" },
                        },
                    },
                }).ToList(),
            },
        },
    };

    private sealed class TestPc : IDisposable
    {
        public TestPc(string name)
        {
            Folder = new ShellTestFolder(name);
            Host = PcTidyHost.CreateSandbox(Folder.Root);
        }

        public ShellTestFolder Folder { get; }
        public PcTidyHost Host { get; }
        public void Dispose() { Host.Dispose(); Folder.Dispose(); }
    }

    private sealed class FakeShell : IShellFlowHost
    {
        public List<ActionPlan> Reviewed { get; } = new();
        public List<string> Messages { get; } = new();
        public int QuarantineDays => 7;
        public Task<RunRecord?> ReviewAndRunAsync(ActionPlan plan, CancellationToken ct) { Reviewed.Add(plan); return Task.FromResult<RunRecord?>(null); }
        public Task<RunRecord?> SimulateAsync(ActionPlan plan, CancellationToken ct) => Task.FromResult<RunRecord?>(null);
        public Task<RunRecord?> RunApprovedAsync(ActionPlan plan, CancellationToken ct) => Task.FromResult<RunRecord?>(null);
        public void OpenInExplorer(string path) { }
        public void ShowMessage(string title, string message, bool isError = false) => Messages.Add(title + ": " + message);
        public Task<FlowOutcome> ReviewAndRunAsync(IReadOnlyList<ActionPlan> plans, FlowOptions options, CancellationToken ct) => Task.FromResult(FlowOutcome.Cancelled);
        public Task<IReadOnlyList<RunRecord>> SimulateAsync(IReadOnlyList<ActionPlan> plans, FlowOptions options, CancellationToken ct) => Task.FromResult<IReadOnlyList<RunRecord>>(Array.Empty<RunRecord>());
        public Task<bool> UndoRunsAsync(IReadOnlyList<RunRecord> runs, CancellationToken ct) => Task.FromResult(true);
        public void NavigateTo(string pageKey) { }
    }

    private sealed class Services(PcTidyHost host, ScanResume? resume, IShellFlowHost? shell = null) : IServiceProvider
    {
        public object? GetService(Type t)
            => t == typeof(ScanResume) ? resume
               : t == typeof(IFlowHost) || t == typeof(IShellFlowHost) ? shell
               : t == typeof(RunGate) ? new RunGate()
               : host.GetService(t);
    }

    private sealed class Clock(DateTime utc) : TimeProvider
    {
        public DateTime UtcNow { get; set; } = utc;
        public override DateTimeOffset GetUtcNow() => new(UtcNow);
    }

    private static readonly DateTime Start = new(2030, 6, 1, 9, 0, 0, DateTimeKind.Utc);

    private static ScanCheckpointKey Key(IKnownLocations loc, params string[] ids) => new()
    {
        Flow = ScanResume.CombinedFlow, ScannerIds = ids, Depth = ScanDepth.Quick, Preset = "Recommended",
        DataRoot = loc.DataRoot, Sandbox = loc.IsSandbox, AppVersion = Version,
    };

    private static ScanCheckpoint Checkpoint(ScanCheckpointKey key, string? build, params CheckpointSection[] sections) => new()
    {
        Key = key, Title = "Scan everything", NavigateTo = "optimize", StartedUtc = Start, UpdatedUtc = Start.AddMinutes(30),
        WindowsBuild = build, TotalSections = key.ScannerIds.Count, Sections = sections,
    };

    private static CheckpointSection Section(int index, string id, IReadOnlyList<FileSample>? samples = null) => new()
    {
        Index = index, FeatureId = id, Title = id, FinishedUtc = Start.AddMinutes(index + 1),
        Result = Result(id, id, Array.Empty<string>()), Samples = samples ?? Array.Empty<FileSample>(),
    };

    private static ScanContext Context(PcTidyHost host) => new()
    {
        Locations = host.Locations, Safety = host.Safety, InUse = host.InUse, Settings = host.Settings, Walker = host.Walker,
        Depth = ScanDepth.Quick, Preset = "Recommended",
    };

    // ---------------- checkpoint file ----------------

    [Fact]
    public void A_checkpoint_round_trips_with_its_results_and_plan_items()
    {
        using var pc = new TestPc("resume-roundtrip");
        var store = new FileScanCheckpointStore(pc.Host.Locations.DataRoot);
        var files = new[] { pc.Folder.File(@"stuff\a.tmp"), pc.Folder.File(@"stuff\b.tmp") };
        var original = Checkpoint(Key(pc.Host.Locations, "cleanup", "organize"), "10000.1", Section(0, "cleanup") with
        {
            Result = Result("cleanup", "Clean up", files),
            Samples = new[] { new FileSample(files[0], 1, Start) },
        });

        Assert.True(store.Save(original));
        Assert.StartsWith(Path.Combine(pc.Host.Locations.DataRoot, "scan-checkpoints"), store.PathFor(ScanResume.CombinedFlow));
        var back = store.Load(ScanResume.CombinedFlow)!;

        Assert.True(back.Key.SameOptions(original.Key));
        Assert.Equal(original.Key.AppVersion, back.Key.AppVersion);
        Assert.Equal("10000.1", back.WindowsBuild);
        var item = back.Sections.Single().Result!.Groups.Single().Items[1];
        var expected = original.Sections.Single().Result!.Groups.Single().Items[1];
        Assert.Equal(expected.Title, item.Title);
        Assert.Equal(expected.Details!["Kind"], item.Details!["Kind"]);
        Assert.Equal(expected.Actions.Count, item.Actions.Count);
        for (int i = 0; i < item.Actions.Count; i++)
        {
            var a = item.Actions[i];
            var e = expected.Actions[i];
            Assert.Equal(e.Id, a.Id);
            Assert.Equal(e.Kind, a.Kind);
            Assert.Equal(e.SourcePath, a.SourcePath);
            Assert.Equal(e.ExpectedSize, a.ExpectedSize);
            Assert.Equal(e.ExpectedLastWriteUtc, a.ExpectedLastWriteUtc);
            Assert.Equal(e.NeedsAdmin, a.NeedsAdmin);
            Assert.Equal(e.Registry, a.Registry);
            Assert.Equal(e.DependsOn ?? Array.Empty<string>(), a.DependsOn ?? Array.Empty<string>());
            Assert.Equal(e.Args?.GetValueOrDefault("k"), a.Args?.GetValueOrDefault("k"));
        }
        Assert.Equal(files[0], back.Sections.Single().Samples.Single().Path);

        // The header is small and holds no results; the section's results are in their own file (review M2).
        var header = File.ReadAllText(store.PathFor(ScanResume.CombinedFlow), Encoding.UTF8);
        Assert.DoesNotContain("\"Groups\"", header);
        Assert.DoesNotContain("\"ProgressText\"", header);
        var sectionFile = Assert.Single(Directory.GetFiles(store.SectionFolderFor(ScanResume.CombinedFlow)));
        Assert.Equal(back.Sections.Single().ResultFile, Path.GetFileName(sectionFile));
        Assert.Contains(Path.GetFileName(sectionFile), header);

        // The computed totals aren't saved (they would double the file).
        var json = File.ReadAllText(sectionFile, Encoding.UTF8);
        Assert.DoesNotContain("\"AllItems\"", json);
        Assert.DoesNotContain("\"TotalBytes\"", json);
        Assert.Contains("\"QuarantinePath\"", json); // enums as text, like the run journal
    }

    [Fact]
    public void Writes_are_atomic_and_a_damaged_file_is_ignored_safely()
    {
        using var pc = new TestPc("resume-atomic");
        var store = new FileScanCheckpointStore(pc.Host.Locations.DataRoot);
        var key = Key(pc.Host.Locations, "cleanup", "organize");
        Assert.True(store.Save(Checkpoint(key, "b", Section(0, "cleanup"))));
        var path = store.PathFor(ScanResume.CombinedFlow);

        // A crash mid-write leaves a temp file behind: the real file is untouched and the next save replaces it.
        File.WriteAllText(path + ".tmp", "{\"Format\":1,\"Key\":");
        Assert.Single(store.Load(ScanResume.CombinedFlow)!.Sections);
        Assert.True(store.Save(Checkpoint(key, "b", Section(0, "cleanup"), Section(1, "organize"))));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal(2, store.Load(ScanResume.CombinedFlow)!.Sections.Count);
        Assert.Single(Directory.GetFiles(store.Folder)); // nothing else was written

        // Half a file, garbage, an empty file: ignored (and removed), never a crash.
        foreach (var bad in new[] { File.ReadAllText(path)[..40], "not json at all", "" })
        {
            File.WriteAllText(path, bad);
            Assert.Null(store.Load(ScanResume.CombinedFlow));
            Assert.Empty(store.LoadAll());
            Assert.False(File.Exists(path));
        }

        // Another format or a file renamed to another flow: ignored too.
        Assert.True(store.Save(Checkpoint(key, "b", Section(0, "cleanup")) with { Format = 99 }));
        Assert.Null(store.Load(ScanResume.CombinedFlow));
        Assert.True(store.Save(Checkpoint(key, "b", Section(0, "cleanup"))));
        File.Move(path, store.PathFor("cleanup"));
        Assert.Null(store.Load("cleanup"));
    }

    [Fact]
    public void Flow_ids_can_never_leave_the_checkpoint_folder()
    {
        var store = new FileScanCheckpointStore(@"C:\Invented\Data");
        foreach (var flow in new[] { @"..\..\Windows\evil", "a/b", "con:", "..", "" })
            Assert.Equal(store.Folder, Path.GetDirectoryName(Path.GetFullPath(store.PathFor(flow))));
    }

    [Fact]
    public async Task A_section_too_big_to_keep_is_noted_and_scanned_again_on_resume()
    {
        using var pc = new TestPc("resume-big");
        var store = new FileScanCheckpointStore(pc.Host.Locations.DataRoot);
        var resume = new ScanResume(store, new SimulatedWindowsState("b"), Version, new Clock(Start));
        var small = new FakeScanner("organize", "Organize");
        var huge = new HugeScanner();
        var last = new FakeScanner("photos", "Photos");
        var scanners = new IFeatureScanner[] { small, huge, last };
        var ctx = Context(pc.Host);
        var session = resume.StartFresh(resume.KeyFor(ScanResume.CombinedFlow, scanners, ctx.Depth, ctx.Preset, null, ctx.Locations), "Scan everything", "optimize");
        await session.SectionFinishedAsync(0, small, await small.ScanAsync(ctx, null, default));
        await session.SectionFinishedAsync(1, huge, await huge.ScanAsync(ctx, null, default));

        Assert.Contains(session.Notes, n => n.Contains("Huge section", StringComparison.Ordinal));
        var cp = resume.Pending(ScanResume.CombinedFlow)!;
        Assert.True(new FileInfo(store.PathFor(ScanResume.CombinedFlow)).Length < CheckpointRules.MaxSectionBytes);
        Assert.Null(cp.Sections[1].Result);
        var decision = resume.Evaluate(cp, resume.KeyFor(ScanResume.CombinedFlow, scanners, ctx.Depth, ctx.Preset, null, ctx.Locations));
        Assert.True(decision.CanResume);
        Assert.Equal(new[] { 0 }, decision.Reuse.Keys);
        Assert.Contains(decision.Rescan, r => r.Index == 1 && r.Reason.Contains("too big", StringComparison.Ordinal));
    }

    private sealed class HugeScanner : IFeatureScanner
    {
        public string FeatureId => "duplicates";
        public string Title => "Huge section";
        public Release Release => Release.V1;
        public bool IncludedInOptimizeAll => true;
        public Task<ScanResult> ScanAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
            => Task.FromResult(new ScanResult
            {
                FeatureId = FeatureId, Title = Title,
                Groups = new[] { new ScanGroup { Id = "g", Title = "g", Items = new[] { new ScanItem { Title = "x", Reason = "Invented", Detail = new string('x', CheckpointRules.MaxSectionBytes + 10) } } } },
            });
    }

    // ---------------- staleness rules ----------------

    [Fact]
    public void Older_than_a_day_or_from_the_future_is_discarded()
    {
        var loc = new SandboxKnownLocations(@"C:\Invented\PracticePc");
        var key = Key(loc, "cleanup", "organize");
        var cp = Checkpoint(key, "b", Section(0, "cleanup"));
        Assert.True(CheckpointRules.Evaluate(cp, key, Start.AddHours(23), "b").CanResume);
        var old = CheckpointRules.Evaluate(cp, key, Start.AddHours(24).AddMinutes(1), "b");
        Assert.False(old.CanResume);
        Assert.Contains("more than a day", old.DiscardReason);
        Assert.False(CheckpointRules.Evaluate(cp, key, Start.AddHours(-1), "b").CanResume);
        Assert.Equal(TimeSpan.FromHours(24), CheckpointRules.MaxAge);
    }

    [Fact]
    public void Different_options_or_another_PC_Tidy_version_are_discarded()
    {
        var loc = new SandboxKnownLocations(@"C:\Invented\PracticePc");
        var key = Key(loc, "cleanup", "organize");
        var cp = Checkpoint(key, "b", Section(0, "cleanup"));
        var now = Start.AddHours(1);
        Assert.True(CheckpointRules.Evaluate(cp, key, now, "b").CanResume);

        var changed = new[]
        {
            key with { Depth = ScanDepth.Deep },
            key with { Preset = "Gamer" },
            key with { Roots = new[] { @"C:\Invented\Folder" } },
            key with { ScannerIds = new[] { "organize", "cleanup" } },
            key with { ScannerIds = new[] { "cleanup", "organize", "photos" } },
            key with { DataRoot = @"C:\Invented\OtherData" },
            key with { Sandbox = !key.Sandbox },
            key with { Flow = "cleanup" },
        };
        foreach (var other in changed)
        {
            var d = CheckpointRules.Evaluate(cp, other, now, "b");
            Assert.False(d.CanResume);
            Assert.Contains("options", d.DiscardReason);
        }
        var version = CheckpointRules.Evaluate(cp, key with { AppVersion = "10.0.0" }, now, "b");
        Assert.False(version.CanResume);
        Assert.Contains("PC Tidy was updated", version.DiscardReason);

        // The service applies the cheap rules when it looks: a checkpoint from another version is deleted.
        var store = new MemoryScanCheckpointStore();
        store.Save(cp);
        Assert.Null(new ScanResume(store, new SimulatedWindowsState("b"), "10.0.0", new Clock(now)).Pending(ScanResume.CombinedFlow));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void After_a_Windows_update_only_sections_about_the_users_own_files_are_kept()
    {
        var loc = new SandboxKnownLocations(@"C:\Invented\PracticePc");
        var ids = new[] { "cleanup", "organize", "tweaks", "duplicates", "services.repair", "photos", "organizer", "search" };
        var key = Key(loc, ids);
        var cp = Checkpoint(key, "10000.1", ids.Select((id, i) => Section(i, id)).ToArray());
        var now = Start.AddHours(2);

        var same = CheckpointRules.Evaluate(cp, key, now, "10000.1");
        Assert.Equal(ids.Length, same.Reuse.Count);

        foreach (var build in new[] { "10000.2", null })
        {
            var updated = CheckpointRules.Evaluate(cp, key, now, build);
            Assert.True(updated.CanResume);
            Assert.Equal(new[] { "organize", "duplicates", "photos", "search" }, updated.Reuse.Values.OrderBy(s => s.Index).Select(s => s.FeatureId));
            Assert.Equal(new[] { "cleanup", "tweaks", "services.repair", "organizer" }, updated.Rescan.Select(r => r.FeatureId));
            Assert.All(updated.Rescan, r => Assert.Equal("Windows was updated since", r.Reason));
        }

        // Nothing about the user's own files: nothing left to reuse, so it is discarded.
        var settingsOnly = Key(loc, "cleanup", "tweaks");
        var none = CheckpointRules.Evaluate(Checkpoint(settingsOnly, "10000.1", Section(0, "cleanup"), Section(1, "tweaks")), settingsOnly, now, "10000.2");
        Assert.False(none.CanResume);
    }

    [Fact]
    public void The_own_files_allow_list_matches_the_real_section_ids()
    {
        var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "organize", "duplicates", "photos", "desktoptidy", "developer", "spacemap", "drivemove", "aichats", "search" };
        Assert.Equal(own.OrderBy(x => x), CheckpointRules.OwnFilesPrefixes.OrderBy(x => x));
        var modules = FeatureCatalog.All();
        var scannerIds = modules.SelectMany(m => m.Scanners.Select(s => (Module: m.Id, Scanner: s.FeatureId))).ToList();
        // Every prefix names a real section (no typo), and every real scanner is classified by its section.
        foreach (var prefix in CheckpointRules.OwnFilesPrefixes)
            Assert.Contains(scannerIds, x => CheckpointRules.IsAboutOwnFiles(x.Scanner) && x.Module.Equals(prefix, StringComparison.OrdinalIgnoreCase));
        foreach (var (module, scanner) in scannerIds)
            Assert.True(own.Contains(module) == CheckpointRules.IsAboutOwnFiles(scanner), $"{module}/{scanner}");
        foreach (var settings in new[] { "cleanup", "cleanup.hidden", "tweaks", "tweaks.badtweaks", "services", "services.repair", "drivers", "pccare", "privacy", "notifications", "speed", "speed.startup", "apps", "games", "insights" })
            Assert.False(CheckpointRules.IsAboutOwnFiles(settings), settings);
    }

    [Fact]
    public void A_section_whose_folders_changed_more_than_a_fifth_is_scanned_again()
    {
        using var folder = new ShellTestFolder("resume-files");
        var files = Enumerable.Range(0, 10).Select(i => folder.File($@"photos\p{i}.jpg", new string('x', 10 + i))).ToList();
        var samples = files.Select(f => new FileSample(f, new FileInfo(f).Length, new FileInfo(f).LastWriteTimeUtc)).ToList();
        var loc = new SandboxKnownLocations(@"C:\Invented\PracticePc");
        var key = Key(loc, "photos", "organize");
        var cp = Checkpoint(key, "b", Section(0, "photos", samples));
        var now = Start.AddHours(1);
        Assert.True(CheckpointRules.Evaluate(cp, key, now, "b").Reuse.ContainsKey(0));

        // 2 of 10 changed (20%): still reused.
        File.AppendAllText(files[0], "more");
        File.Delete(files[1]);
        Assert.True(CheckpointRules.Evaluate(cp, key, now, "b").Reuse.ContainsKey(0));

        // 3 of 10 (30%): that section is scanned again; here nothing else is left, so the checkpoint goes.
        File.SetLastWriteTimeUtc(files[2], new DateTime(2031, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var d = CheckpointRules.Evaluate(cp, key, now, "b");
        Assert.False(d.CanResume);
        Assert.Contains("files changed", d.DiscardReason);

        // Sampling reads only files that exist, and at most 200.
        var many = Result("photos", "Photos", Enumerable.Range(0, 450).Select(i => i < 10 ? files[i % 10] : Path.Combine(folder.Root, $"gone{i}.jpg")).ToList());
        var sampled = CheckpointRules.Sample(many);
        Assert.True(sampled.Count <= CheckpointRules.SampleSize);
        Assert.All(sampled, s => Assert.True(File.Exists(s.Path)));
    }

    // ---------------- resume runs only what is left; the checkpoint goes at the end ----------------

    [Fact]
    public async Task Resuming_runs_only_the_sections_that_had_not_finished()
    {
        using var pc = new TestPc("resume-run");
        var store = new FileScanCheckpointStore(pc.Host.Locations.DataRoot);
        var clock = new Clock(Start);
        var resume = new ScanResume(store, new SimulatedWindowsState("b"), Version, clock);
        var a = new FakeScanner("cleanup", "Clean up") { Files = new[] { pc.Folder.File(@"t\a.tmp") } };
        var b = new FakeScanner("organize", "Organize") { Files = new[] { pc.Folder.File(@"t\b.txt") } };
        var c = new FakeScanner("photos", "Photos");
        var scanners = new IFeatureScanner[] { a, b, c };
        var ctx = Context(pc.Host);

        // First run: Stop is pressed while the third section scans.
        using (var cts = new CancellationTokenSource())
        {
            c.During = cts.Cancel;
            var first = ResumeFlow.Start(resume, ScanResume.CombinedFlow, "Scan everything", "optimize", scanners, ctx, null);
            var outcomes = await ScanRunner.RunAsync(scanners, ctx, (_, _, _) => { }, cts.Token, first.Session);
            Assert.True(outcomes.Last().Cancelled);
        }
        var saved = resume.Pending(ScanResume.CombinedFlow)!;
        Assert.Equal(2, saved.Sections.Count);
        Assert.True(saved.StoppedByUser);
        Assert.Equal("Your last scan stopped after 2 of 3 sections (" + ResumeText.Time(saved.LastProgressUtc) + ").", ResumeText.Stopped(saved));

        // Next launch: resume. Only the third scanner runs; the first two come back with when they were found.
        c.During = null;
        clock.UtcNow = Start.AddHours(3);
        var again = ResumeFlow.Start(resume, ScanResume.CombinedFlow, "Scan everything", "optimize", scanners, ctx, saved);
        Assert.NotNull(again.Session);
        Assert.Equal(new[] { 0, 1 }, again.Session!.Reused.Keys.OrderBy(k => k));
        var resumed = await ScanRunner.RunAsync(scanners, ctx, (_, _, _) => { }, CancellationToken.None, again.Session);
        Assert.Equal((1, 1, 2), (a.Calls, b.Calls, c.Calls));
        Assert.NotNull(resumed[0].FoundAtUtc);
        Assert.NotNull(resumed[1].FoundAtUtc);
        Assert.Null(resumed[2].FoundAtUtc);
        Assert.Equal(a.Files[0], resumed[0].Result!.AllItems.Single().Actions[0].SourcePath);
        Assert.StartsWith("Found ", again.FoundTexts()[0]); // "Found at 09:01" today, "Found on 1 Jun at 09:01" on another day
        Assert.EndsWith("before you stopped the scan", again.FoundTexts()[0]);

        // The whole scan finished: the checkpoint is deleted.
        Assert.Null(store.Load(ScanResume.CombinedFlow));
        Assert.False(Directory.Exists(store.Folder) && Directory.EnumerateFiles(store.Folder).Any());
        var notes = ResumeFlow.PreviewMessages(resumed, again).ToList();
        Assert.Contains(notes, x => x.StartsWith("Resumed your last scan: 2 sections come from it and 1 was scanned now.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_crash_part_way_keeps_the_finished_sections_and_a_fresh_scan_or_Start_over_replaces_them()
    {
        using var pc = new TestPc("resume-crash");
        var store = new MemoryScanCheckpointStore();
        var resume = new ScanResume(store, new SimulatedWindowsState("b"), Version, new Clock(Start));
        var a = new FakeScanner("organize", "Organize");
        var b = new FakeScanner("photos", "Photos");
        var scanners = new IFeatureScanner[] { a, b };
        var ctx = Context(pc.Host);

        // "Crash": the first section finished and was saved; the process never got further.
        var session = ResumeFlow.Start(resume, ScanResume.CombinedFlow, "Scan everything", "optimize", scanners, ctx, null).Session!;
        await session.SectionFinishedAsync(0, a, await a.ScanAsync(ctx, null, default));
        var cp = resume.Pending(ScanResume.CombinedFlow)!;
        Assert.False(cp.StoppedByUser);
        Assert.EndsWith("before PC Tidy closed", ResumeText.FoundAt(cp.Sections[0].FinishedUtc, cp.StoppedByUser));

        // A fresh scan (not a resume) replaces it, and finishing deletes it.
        var fresh = ResumeFlow.Start(resume, ScanResume.CombinedFlow, "Scan everything", "optimize", scanners, ctx, null);
        Assert.Equal(0, store.Count);
        await ScanRunner.RunAsync(scanners, ctx, (_, _, _) => { }, CancellationToken.None, fresh.Session);
        Assert.Equal(0, store.Count);

        // Start over throws it away.
        store.Save(cp);
        resume.Discard(ScanResume.CombinedFlow);
        Assert.Null(resume.Pending(ScanResume.CombinedFlow));

        // A checkpoint that can't be used any more: scanned again from the start, and the preview says why.
        store.Save(cp);
        var stale = ResumeFlow.Start(new ScanResume(store, new SimulatedWindowsState("b"), Version, new Clock(Start.AddDays(2))), ScanResume.CombinedFlow,
            "Scan everything", "optimize", scanners, ctx, cp);
        Assert.Empty(stale.Session!.Reused);
        Assert.Contains("more than a day old", stale.DiscardNote);
    }

    // ---------------- practice mode and demo never read the real registry ----------------

    [Fact]
    public void Practice_PC_and_demo_never_read_the_real_registry()
    {
        using var pc = new TestPc("resume-sandbox");
        int before = RegistryWindowsStateReader.Reads;

        var practice = ScanResumeSetup.Create(isDemo: false, sandboxArg: true, pc.Host.Locations, pc.Host.System);
        Assert.IsType<SimulatedWindowsState>(practice.Windows);
        Assert.IsType<FileScanCheckpointStore>(practice.Store);
        Assert.StartsWith(pc.Host.Locations.DataRoot, ((FileScanCheckpointStore)practice.Store).Folder);
        practice.RestartPending();
        practice.WindowsBuild();

        // Even without --sandbox, a practice PC's locations are enough.
        var bySandboxLocations = ScanResumeSetup.Create(isDemo: false, sandboxArg: false, pc.Host.Locations, pc.Host.System);
        Assert.IsType<SimulatedWindowsState>(bySandboxLocations.Windows);
        bySandboxLocations.RestartPending();

        var demo = ScanResumeSetup.Create(isDemo: true, sandboxArg: false, pc.Host.Locations, pc.Host.System);
        Assert.IsType<MemoryScanCheckpointStore>(demo.Store);
        Assert.IsType<SimulatedWindowsState>(demo.Windows);
        demo.RestartPending();

        Assert.Equal(before, RegistryWindowsStateReader.Reads);
        // The practice PC's simulated facts (Sim\sim.json defaults) are what it reports.
        Assert.False(practice.RestartPending());
        Assert.Equal(pc.Host.System.WindowsBuild + ".0", practice.WindowsBuild());
    }

    // ---------------- the pages: resume offer, the restart note, Start over ----------------

    /// <summary>One STA thread for every page check: WPF's Application (and its resources) belongs to the thread that made it.</summary>
    [Fact]
    public void The_flow_pages_offer_resume_show_the_restart_note_only_when_asked_and_start_over()
    {
        using var pc = new TestPc("resume-pages");
        RunSta(() =>
        {
            CombinedPage(pc);
            StandardPage(pc);
            ResumeIsEvaluatedOffTheUiThread(pc);
            TheCheckpointIsDiscardedAfterARun(pc);
        });
    }

    private static void CombinedPage(TestPc pc)
    {
        int registryReads = RegistryWindowsStateReader.Reads;
        var windows = new SimulatedWindowsState("b", restartPending: false);
        var store = new MemoryScanCheckpointStore();
        var resume = new ScanResume(store, windows, Version);
        var shell = new FakeShell();
        var a = new FakeScanner("organize", "Organize");
        var b = new FakeScanner("photos", "Photos");
        var sections = new[] { new OptimizeSection(a, "Organize", SymbolRegular.Folder24), new OptimizeSection(b, "Photos", SymbolRegular.Image24) };
        var page = new CombinedFlowPage(new Services(pc.Host, resume, shell), () => sections);
        Loaded(page);
        Assert.False(page.ShowsRestartWarning);
        Assert.False(page.ShowsResumeOffer);

        // The note shows only when the (fake) reader says Windows Update is waiting to restart.
        windows.Pending = true;
        Loaded(page);
        Assert.True(page.ShowsRestartWarning);
        var note = Find<InfoBar>(page).Single(x => x.Message == ResumeText.RestartMessage);
        Assert.Equal(InfoBarSeverity.Informational, note.Severity);
        Click(page, "Open Windows Update in Windows Settings");
        var open = shell.Reviewed.Single().Items.Single();
        Assert.Equal(ActionKind.OpenExternal, open.Kind);
        Assert.Equal("ms-settings:windowsupdate", open.Args!["target"]);
        windows.Pending = false;

        // A part-way scan (first section done) is offered, and Resume scans only the second section.
        var ctx = ScanRunner.CreateContext(new Services(pc.Host, resume, shell), ScanDepth.Quick, page.Preset);
        var session = resume.StartFresh(resume.KeyFor(ScanResume.CombinedFlow, new IFeatureScanner[] { a, b }, ScanDepth.Quick, page.Preset, null, ctx.Locations), "Scan everything", "optimize");
        Pump(session.SectionFinishedAsync(0, a, Result("organize", "Organize", Array.Empty<string>())));
        Loaded(page);
        Assert.True(page.ShowsResumeOffer);
        Assert.Contains(Find<InfoBar>(page), x => x.Title == "Your last scan stopped after 1 of 2 sections (" + ResumeText.Time(page.PendingCheckpoint!.LastProgressUtc) + ").");
        Pump(page.ResumeAsync());
        Assert.Equal(FlowState.Preview, page.State);
        Assert.Equal((0, 1), (a.Calls, b.Calls));
        Assert.StartsWith("Found at ", page.ResultsList.Model!.Sections.Single(s => s.FeatureId == "organize").Note);
        Assert.Null(page.ResultsList.Model!.Sections.Single(s => s.FeatureId == "photos").Note);
        Assert.Equal(0, store.Count); // finished: deleted

        // Start over: the saved scan is thrown away and everything runs again.
        session = resume.StartFresh(resume.KeyFor(ScanResume.CombinedFlow, new IFeatureScanner[] { a, b }, ScanDepth.Quick, page.Preset, null, ctx.Locations), "Scan everything", "optimize");
        Pump(session.SectionFinishedAsync(0, a, Result("organize", "Organize", Array.Empty<string>())));
        Pump(page.StartOverAsync());
        Assert.Equal((1, 2), (a.Calls, b.Calls));
        Assert.Equal(0, store.Count);
        Assert.Equal(registryReads, RegistryWindowsStateReader.Reads);
        Assert.Empty(shell.Messages);
    }

    private static void StandardPage(TestPc pc)
    {
        var windows = new SimulatedWindowsState("b", restartPending: true);
        var resume = new ScanResume(new MemoryScanCheckpointStore(), windows, Version);
        var shell = new FakeShell();
        var scanners = new IFeatureScanner[] { new FakeScanner("cleanup", "Temp files"), new FakeScanner("cleanup.hidden", "Hidden leftovers") };
        var page = new StandardFlowPage(new Services(pc.Host, resume, shell), "Clean up", "Invented summary", SymbolRegular.Broom24, scanners) { FlowId = "cleanup" };

        // Quick scans are short: no note. Deep scans are long: the note shows when a restart is waiting.
        page.Depth = ScanDepth.Quick;
        Loaded(page);
        Assert.False(page.ShowsRestartWarning);
        page.Depth = ScanDepth.Deep;
        Loaded(page);
        Assert.True(page.ShowsRestartWarning);
        windows.Pending = false;
        Loaded(page);
        Assert.False(page.ShowsRestartWarning);

        // A finished scan leaves no checkpoint; nothing is offered.
        Pump(page.StartScanAsync());
        Assert.Equal(FlowState.Preview, page.State);
        Assert.Null(page.PendingCheckpoint);
    }

    // ---------------- WPF helpers ----------------

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try { EnsureAppResources(); action(); } catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException("UI failed: " + error);
    }

    private static void EnsureAppResources()
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (app.Resources.Contains("TidyCard")) return;
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Dark });
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/PcTidy.UI;component/Themes/TidyDark.xaml") });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/PcTidy.UI;component/Themes/Tidy.xaml") });
    }

    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private static void Loaded(FrameworkElement page)
    {
        page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        page.Measure(new Size(1000 - 280, 760));
        page.Arrange(new Rect(0, 0, 1000 - 280, 760));
        page.UpdateLayout();
        // The resume offer and the restart note are read off the UI thread: wait for them.
        if (page is StandardFlowPage standard) Pump(standard.FlowNotesTask);
        else if (page is CombinedFlowPage combined) Pump(combined.FlowNotesTask);
    }

    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T t) yield return t;
            foreach (var inner in Find<T>(child)) yield return inner;
        }
    }

    private static void Click(DependencyObject root, string automationName)
    {
        var button = Find<System.Windows.Controls.Button>(root).First(b => System.Windows.Automation.AutomationProperties.GetName(b) == automationName);
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
    }
}

[CollectionDefinition(ResumeScanTests.Collection, DisableParallelization = true)]
public sealed class ResumeScanCollection { }
