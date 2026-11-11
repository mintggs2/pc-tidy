using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.UI.Flow;
using PcTidy.UI.Flow.Resume;
using PcTidy.UI.Tests.Shell;
using Wpf.Ui.Controls;
using Xunit;
using Badge = PcTidy.Core.Model.Badge;

namespace PcTidy.UI.Tests;

/// <summary>
/// 3.1.1 review fixes for resumable scans: M2 (a section is measured while it is written and given up at the cap; one
/// file per section plus a small header; pages read off the UI thread), L2 (slow or offline samples count as changed),
/// L3 (folders are sampled), L4 (the original scan time), L5 (deep checks on read), the checkpoint goes after a run,
/// and resumed items still pass the engine's SafetyGuard and changed-since-scan checks. Practice PC in testbed\shell\ only.
/// </summary>
public partial class ResumeScanTests
{
    private static ScanResult FileResult(string id, IReadOnlyList<string> files, bool folders = false) => new()
    {
        FeatureId = id, Title = id,
        Groups = new[]
        {
            new ScanGroup
            {
                Id = id + ".g", Title = "Invented group",
                Items = files.Select((f, i) => new ScanItem
                {
                    Id = $"{id}-{i}", Title = Path.GetFileName(f), Detail = f, Reason = "Invented.", Badge = Badge.Safe, DefaultSelected = true,
                    Actions = new[]
                    {
                        new PlanItem
                        {
                            Id = $"{id}-{i}-a", Kind = ActionKind.QuarantinePath, FeatureId = id, Description = "Move to quarantine", SourcePath = f,
                            IsDirectory = folders,
                            ExpectedSize = folders ? 0 : new FileInfo(f).Length,
                            ExpectedLastWriteUtc = folders ? new DirectoryInfo(f).LastWriteTimeUtc : new FileInfo(f).LastWriteTimeUtc,
                        },
                    },
                }).ToList(),
            },
        },
    };

    // ---------------- M2: memory and the per-section files ----------------

    [Fact]
    public void A_huge_section_is_given_up_at_the_cap_without_building_it_in_memory()
    {
        using var pc = new TestPc("resume-cap");
        var store = new FileScanCheckpointStore(pc.Host.Locations.DataRoot);
        // About 60 MB of JSON if it were all serialized.
        var items = Enumerable.Range(0, 300_000).Select(i => new ScanItem { Id = "i" + i, Title = "Invented item " + i, Detail = @"C:\Invented\Folder\file" + i + ".tmp", Reason = "Invented reason text." }).ToList();
        var huge = new ScanResult { FeatureId = "duplicates", Title = "Huge", Groups = new[] { new ScanGroup { Id = "g", Title = "g", Items = items } } };

        long before = GC.GetAllocatedBytesForCurrentThread();
        var write = store.SaveSection(ScanResume.CombinedFlow, 0, huge, CheckpointRules.MaxSectionBytes);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(write.TooBig);
        Assert.Null(write.FileName);
        Assert.True(allocated < 24L << 20, $"Saving allocated {allocated / (1 << 20)} MB");
        // Nothing half-written is left.
        Assert.True(!Directory.Exists(store.SectionFolderFor(ScanResume.CombinedFlow)) || !Directory.EnumerateFiles(store.SectionFolderFor(ScanResume.CombinedFlow)).Any());

        // The memory store (demo) gives up the same way.
        Assert.True(new MemoryScanCheckpointStore().SaveSection(ScanResume.CombinedFlow, 0, huge, CheckpointRules.MaxSectionBytes).TooBig);

        // The counting writer counts exactly what a normal serializer writes.
        var small = Result("organize", "Organize", Array.Empty<string>());
        Assert.Equal(ScanCheckpointJson.Serialize(small).LongLength, ScanCheckpointJson.SerializeCapped(small, null, long.MaxValue));
        Assert.Throws<CheckpointTooBigException>(() => ScanCheckpointJson.SerializeCapped(small, null, 10));
    }

    [Fact]
    public async Task Each_section_has_its_own_file_and_only_the_small_header_is_rewritten()
    {
        using var pc = new TestPc("resume-files2");
        var store = new FileScanCheckpointStore(pc.Host.Locations.DataRoot);
        var resume = new ScanResume(store, new SimulatedWindowsState("b"), Version, new Clock(Start));
        var scanners = new IFeatureScanner[] { new FakeScanner("organize", "Organize"), new FakeScanner("photos", "Photos"), new FakeScanner("search", "Search") };
        var ctx = Context(pc.Host);
        var session = resume.StartFresh(resume.KeyFor(ScanResume.CombinedFlow, scanners, ctx.Depth, ctx.Preset, null, ctx.Locations), "Scan everything", "optimize");
        var sections = store.SectionFolderFor(ScanResume.CombinedFlow);

        await session.SectionFinishedAsync(0, scanners[0], await scanners[0].ScanAsync(ctx, null, default));
        var first = Assert.Single(Directory.GetFiles(sections));
        var firstWritten = File.GetLastWriteTimeUtc(first);
        await session.SectionFinishedAsync(1, scanners[1], await scanners[1].ScanAsync(ctx, null, default));

        // The first section's file wasn't written again; the second got its own.
        Assert.Equal(2, Directory.GetFiles(sections).Length);
        Assert.Equal(firstWritten, File.GetLastWriteTimeUtc(first));
        Assert.True(new FileInfo(store.PathFor(ScanResume.CombinedFlow)).Length < 16 * 1024);

        // A crash after a section file was written but before the header: the old header still holds, the stray file goes at the next save.
        Assert.True(store.SaveSection(ScanResume.CombinedFlow, 2, Result("search", "Search", Array.Empty<string>()), CheckpointRules.MaxSectionBytes).Saved);
        Assert.Equal(2, store.Load(ScanResume.CombinedFlow)!.Sections.Count);
        File.WriteAllText(Path.Combine(sections, "7-0000aaaa.json.tmp"), "half");
        session.Stopped();
        Assert.Equal(2, Directory.GetFiles(sections).Length);

        // Home's card and the offers read only the header (no results in it).
        var header = store.LoadHeader(ScanResume.CombinedFlow)!;
        Assert.All(header.Sections, s => { Assert.Null(s.Result); Assert.NotNull(s.ResultFile); });
        Assert.True(header.NeedsResults);
        Assert.All(store.Load(ScanResume.CombinedFlow)!.Sections, s => Assert.NotNull(s.Result));

        // Start over removes the header and every section file.
        resume.Discard(ScanResume.CombinedFlow);
        Assert.False(Directory.Exists(sections));
        Assert.False(File.Exists(store.PathFor(ScanResume.CombinedFlow)));
    }

    // ---------------- L5: tampered but well-formed checkpoints ----------------

    [Theory]
    [InlineData("sections-null-entry")]
    [InlineData("scanner-ids-null")]
    [InlineData("scanner-ids-null-entry")]
    [InlineData("sections-null")]
    [InlineData("section-title-null")]
    [InlineData("samples-null-entry")]
    [InlineData("index-out-of-range")]
    [InlineData("result-file-escapes")]
    public void A_tampered_header_is_ignored_and_removed_and_never_crashes(string damage)
    {
        using var pc = new TestPc("resume-tamper");
        var store = new FileScanCheckpointStore(pc.Host.Locations.DataRoot);
        var key = Key(pc.Host.Locations, "organize", "photos");
        Assert.True(store.Save(Checkpoint(key, "b", Section(0, "organize", new[] { new FileSample(@"C:\Invented\a.jpg", 1, Start) }))));
        var path = store.PathFor(ScanResume.CombinedFlow);
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var section = json["Sections"]![0]!.AsObject();
        switch (damage)
        {
            case "sections-null-entry": json["Sections"]!.AsArray().Add(null); break;
            case "scanner-ids-null": json["Key"]!["ScannerIds"] = null; break;
            case "scanner-ids-null-entry": json["Key"]!["ScannerIds"]!.AsArray().Add(null); break;
            case "sections-null": json["Sections"] = null; break;
            case "section-title-null": section["Title"] = null; break;
            case "samples-null-entry": section["Samples"]!.AsArray().Add(null); break;
            case "index-out-of-range": section["Index"] = 5; break;
            case "result-file-escapes": section["ResultFile"] = @"..\..\settings.json"; break;
        }
        File.WriteAllText(path, json.ToJsonString());

        Assert.Null(store.LoadHeader(ScanResume.CombinedFlow));
        Assert.False(File.Exists(path)); // removed, so it can't come back on every page load
        Assert.Empty(store.LoadAll());
        var resume = new ScanResume(store, new SimulatedWindowsState("b"), Version, new Clock(Start));
        Assert.Null(resume.Pending(ScanResume.CombinedFlow));
        Assert.Empty(resume.PendingAll());
    }

    [Theory]
    [InlineData("group-null")]
    [InlineData("items-null")]
    [InlineData("item-null")]
    [InlineData("action-null")]
    [InlineData("groups-null")]
    public void A_tampered_section_file_makes_the_whole_checkpoint_ignored(string damage)
    {
        using var pc = new TestPc("resume-tamper2");
        var store = new FileScanCheckpointStore(pc.Host.Locations.DataRoot);
        var key = Key(pc.Host.Locations, "organize", "photos");
        var files = new[] { pc.Folder.File(@"p\a.jpg") };
        Assert.True(store.Save(Checkpoint(key, "b", Section(0, "organize") with { Result = Result("organize", "Organize", files) })));
        var sectionFile = Directory.GetFiles(store.SectionFolderFor(ScanResume.CombinedFlow)).Single();
        var json = JsonNode.Parse(File.ReadAllText(sectionFile))!.AsObject();
        var group = json["Groups"]![0]!.AsObject();
        switch (damage)
        {
            case "group-null": json["Groups"]!.AsArray().Add(null); break;
            case "items-null": group["Items"] = null; break;
            case "item-null": group["Items"]!.AsArray().Add(null); break;
            case "action-null": group["Items"]![0]!["Actions"]!.AsArray().Add(null); break;
            case "groups-null": json["Groups"] = null; break;
        }
        File.WriteAllText(sectionFile, json.ToJsonString());

        Assert.NotNull(store.LoadHeader(ScanResume.CombinedFlow)); // the header alone is fine…
        Assert.Null(store.Load(ScanResume.CombinedFlow)); // …but its results aren't, so all of it goes
        Assert.False(File.Exists(store.PathFor(ScanResume.CombinedFlow)));
        Assert.False(Directory.Exists(store.SectionFolderFor(ScanResume.CombinedFlow)));
    }

    [Fact]
    public void A_format_1_checkpoint_from_before_is_ignored()
    {
        using var pc = new TestPc("resume-format1");
        var store = new FileScanCheckpointStore(pc.Host.Locations.DataRoot);
        Assert.True(store.Save(Checkpoint(Key(pc.Host.Locations, "organize", "photos"), "b", Section(0, "organize")) with { Format = 1 }));
        Assert.Null(store.LoadHeader(ScanResume.CombinedFlow));
    }

    [Fact]
    public void The_stopped_text_never_throws_on_odd_sections()
    {
        var cp = Checkpoint(Key(new SandboxKnownLocations(@"C:\Invented\PracticePc"), "organize", "photos"), "b") with { Sections = new CheckpointSection[] { null! } };
        Assert.Equal(cp.StartedUtc, cp.LastProgressUtc);
    }

    // ---------------- L2: slow or offline samples ----------------

    [Fact]
    public void Samples_that_take_too_long_or_sit_on_a_network_path_count_as_changed()
    {
        var samples = Enumerable.Range(0, 10).Select(i => new FileSample($@"C:\Invented\f{i}.txt", 1, Start)).ToList();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(CheckpointRules.ChangedALot(samples, _ => { Thread.Sleep(2000); return true; }, TimeSpan.FromMilliseconds(200)));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1.5));
        Assert.False(CheckpointRules.ChangedALot(samples, _ => true, TimeSpan.FromSeconds(5)));
        Assert.True(CheckpointRules.ChangedALot(samples, _ => throw new IOException("gone"), TimeSpan.FromSeconds(5)));

        // A share or a \\?\ path is never looked at (it could block on a disconnected server).
        foreach (var p in new[] { @"\\invented-server\share\a.txt", @"\\?\C:\Invented\a.txt", "//invented-server/share/a.txt", "relative.txt" })
        {
            Assert.False(CheckpointRules.OnLocalFixedDrive(p), p);
            Assert.False(CheckpointRules.StillSame(new FileSample(p, 1, Start)), p);
        }
    }

    /// <summary>Page check (runs on the one STA thread of the page test): the pages evaluate a resume on a pool thread.</summary>
    private static void ResumeIsEvaluatedOffTheUiThread(TestPc pc)
    {
        var store = new MemoryScanCheckpointStore();
        int? sampleThread = null;
        var resume = new ScanResume(store, new SimulatedWindowsState("b"), Version)
        {
            StillSame = _ => { sampleThread = Environment.CurrentManagedThreadId; return true; },
        };
        var a = new FakeScanner("organize", "Organize") { Files = new[] { pc.Folder.File(@"o\a.txt") } };
        var b = new FakeScanner("photos", "Photos");
        var page = new CombinedFlowPage(new Services(pc.Host, resume, new FakeShell()), () => new[]
        {
            new OptimizeSection(a, "Organize", SymbolRegular.Folder24), new OptimizeSection(b, "Photos", SymbolRegular.Image24),
        });
        var ctx = ScanRunner.CreateContext(new Services(pc.Host, resume), ScanDepth.Quick, page.Preset);
        var session = resume.StartFresh(resume.KeyFor(ScanResume.CombinedFlow, new IFeatureScanner[] { a, b }, ScanDepth.Quick, page.Preset, null, ctx.Locations), "Scan everything", "optimize");
        Pump(session.SectionFinishedAsync(0, a, Result("organize", "Organize", a.Files)));
        Loaded(page);
        Assert.True(page.ShowsResumeOffer);
        Pump(page.ResumeAsync());
        Assert.Equal(FlowState.Preview, page.State);
        Assert.Equal((0, 1), (a.Calls, b.Calls));
        Assert.NotNull(sampleThread);
        Assert.NotEqual(Environment.CurrentManagedThreadId, sampleThread);
    }

    // ---------------- L3: folders are sampled ----------------

    [Fact]
    public void A_section_of_only_folders_is_sampled_and_checked_too()
    {
        using var folder = new ShellTestFolder("resume-folders");
        var dirs = Enumerable.Range(0, 4).Select(i => { var d = Path.Combine(folder.Root, $"cache{i}"); Directory.CreateDirectory(d); File.WriteAllText(Path.Combine(d, "x"), "x"); return d; }).ToList();
        var samples = CheckpointRules.Sample(FileResult("developer", dirs, folders: true));
        Assert.Equal(4, samples.Count);
        Assert.All(samples, s => Assert.True(s.IsFolder));
        Assert.False(CheckpointRules.ChangedALot(samples, CheckpointRules.StillSame));

        // Something new in two of the four folders (50%): changed a lot.
        foreach (var d in dirs.Take(2)) Directory.SetLastWriteTimeUtc(d, new DateTime(2031, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.True(CheckpointRules.ChangedALot(samples, CheckpointRules.StillSame));
        Directory.Delete(dirs[3], recursive: true);
        Assert.False(CheckpointRules.StillSame(samples.Single(s => s.Path == dirs[3])));
    }

    // ---------------- L4: the original scan time ----------------

    [Fact]
    public void A_resumed_preview_says_when_it_was_really_scanned()
    {
        var now = new DateTime(2030, 6, 2, 10, 0, 0, DateTimeKind.Local);
        var yesterday = now.AddDays(-1).AddHours(-2).ToUniversalTime();
        var earlier = now.AddHours(-1).ToUniversalTime();
        Assert.StartsWith("Scanned yesterday at ", ResumeText.ScannedAt(new DateTime?[] { null, earlier, yesterday }, now));
        Assert.Equal("Scanned at " + earlier.ToLocalTime().ToString("t"), ResumeText.ScannedAt(new DateTime?[] { earlier, null }, now));
        Assert.Equal("Scanned at " + now.ToString("t"), ResumeText.ScannedAt(new DateTime?[] { null, null }, now));
        Assert.StartsWith("Scanned on ", ResumeText.ScannedAt(new DateTime?[] { now.AddDays(-3).ToUniversalTime() }, now));
    }

    // ---------------- the checkpoint goes after a run ----------------

    private sealed class RanShell : IShellFlowHost
    {
        public int Runs;
        public int QuarantineDays => 7;
        public Task<RunRecord?> ReviewAndRunAsync(ActionPlan plan, CancellationToken ct) => Task.FromResult<RunRecord?>(null);
        public Task<RunRecord?> SimulateAsync(ActionPlan plan, CancellationToken ct) => Task.FromResult<RunRecord?>(null);
        public Task<RunRecord?> RunApprovedAsync(ActionPlan plan, CancellationToken ct) => Task.FromResult<RunRecord?>(null);
        public void OpenInExplorer(string path) { }
        public void ShowMessage(string title, string message, bool isError = false) { }
        public Task<FlowOutcome> ReviewAndRunAsync(IReadOnlyList<ActionPlan> plans, FlowOptions options, CancellationToken ct)
        {
            Runs++;
            var records = plans.Select(p => new RunRecord
            {
                RunId = p.RunId, FeatureId = p.FeatureId, Title = p.Title, Status = RunStatus.Completed, FinishedUtc = DateTime.UtcNow,
                Steps = p.Items.Select((i, n) => new StepRecord { Index = n, Item = i, Status = StepStatus.Done }).ToList(),
            }).ToList();
            return Task.FromResult(new FlowOutcome(FlowOutcomeKind.Ran, records));
        }
        public Task<IReadOnlyList<RunRecord>> SimulateAsync(IReadOnlyList<ActionPlan> plans, FlowOptions options, CancellationToken ct) => Task.FromResult<IReadOnlyList<RunRecord>>(Array.Empty<RunRecord>());
        public Task<bool> UndoRunsAsync(IReadOnlyList<RunRecord> runs, CancellationToken ct) => Task.FromResult(true);
        public void NavigateTo(string pageKey) { }
    }

    /// <summary>Page check (runs on the one STA thread of the page test): a run makes the saved scan go.</summary>
    private static void TheCheckpointIsDiscardedAfterARun(TestPc pc)
    {
        var store = new MemoryScanCheckpointStore();
        var resume = new ScanResume(store, new SimulatedWindowsState("b"), Version);
        var shell = new RanShell();
        var a = new FakeScanner("cleanup", "Temp files") { Files = new[] { pc.Folder.File(@"t\a.tmp") } };
        var b = new FakeScanner("cleanup.hidden", "Hidden leftovers");
        var page = new StandardFlowPage(new Services(pc.Host, resume, shell), "Clean up", "Invented summary", SymbolRegular.Broom24, new IFeatureScanner[] { a, b }) { FlowId = "cleanup" };
        Loaded(page);
        // Stop is pressed while the second section scans: the first is kept for resuming, the preview shows it.
        b.During = () => page.Dispatcher.Invoke(() => Click(page, "Stop scanning"));
        Pump(page.StartScanAsync());
        Assert.Equal(FlowState.Preview, page.State);
        Assert.Equal(1, store.Count);
        Assert.True(store.LoadHeader("cleanup")!.StoppedByUser);

        // Running what was found makes the saved scan out of date: it goes.
        Pump(page.RunAsync());
        Assert.Equal(1, shell.Runs);
        Assert.Equal(0, store.Count);
        Assert.Equal(0, store.SectionCount);
    }

    // ---------------- resumed items still pass the engine's checks ----------------

    /// <summary>
    /// A checkpoint is only a starting point: when its items run, the real Executor (the practice PC's engine) still
    /// asks SafetyGuard about each one and checks it is the same file as scanned. Here the saved results were tampered
    /// with (one item now points into Windows) and another file changed after the scan: neither is touched.
    /// </summary>
    [Fact]
    public async Task Resumed_items_still_go_through_SafetyGuard_and_the_changed_since_scan_check()
    {
        using var pc = new TestPc("resume-guard");
        var loc = pc.Host.Locations;
        var store = new FileScanCheckpointStore(loc.DataRoot);
        var resume = new ScanResume(store, new SimulatedWindowsState("b"), Version, new Clock(DateTime.UtcNow));
        var downloads = Path.Combine(loc.Downloads, "Invented old stuff");
        var files = Enumerable.Range(0, 8).Select(i => WriteFile(Path.Combine(downloads, $"old{i}.tmp"), "invented " + i)).ToList();
        var system = WriteFile(Path.Combine(loc.WindowsDir, "System32", "invented.dll"), "not really a system file");

        var scanner = new FakeResultScanner("cleanup", FileResult("cleanup", files));
        var other = new FakeScanner("cleanup.hidden", "Hidden leftovers");
        var scanners = new IFeatureScanner[] { scanner, other };
        var ctx = Context(pc.Host);
        var session = resume.StartFresh(resume.KeyFor("cleanup", scanners, ctx.Depth, ctx.Preset, null, loc), "Clean up", "cleanup");
        await session.SectionFinishedAsync(0, scanner, scanner.Fixed);

        // Tampered: item 7 now points at a file inside Windows. Changed: file 6 got bigger after the scan.
        var sectionFile = Directory.GetFiles(store.SectionFolderFor("cleanup")).Single();
        var json = JsonNode.Parse(File.ReadAllText(sectionFile))!;
        json["Groups"]![0]!["Items"]![7]!["Actions"]![0]!["SourcePath"] = system;
        File.WriteAllText(sectionFile, json.ToJsonString());
        File.AppendAllText(files[6], " and more");

        var pending = resume.Pending("cleanup")!;
        var start = ResumeFlow.Start(resume, "cleanup", "Clean up", "cleanup", scanners, ctx, pending);
        Assert.True(start.Decision!.CanResume); // 1 of 8 samples changed: the section is reused as saved
        var items = start.Session!.Reused[0].Result!.AllItems.SelectMany(i => i.Actions).ToList();
        Assert.Contains(items, i => i.SourcePath == system);

        var plan = new ActionPlan { FeatureId = "cleanup", Title = "Clean up", Items = items };
        var record = await pc.Host.Executor.ExecuteAsync(plan, new ExecuteOptions { Mode = ExecutionMode.Real, ContinueWithoutRestorePoint = _ => Task.FromResult(true) }, null, CancellationToken.None);

        StepRecord StepFor(string path) => record.Steps.Single(s => s.Item.SourcePath == path);
        Assert.NotEqual(StepStatus.Done, StepFor(system).Status); // SafetyGuard said no
        Assert.True(File.Exists(system));
        Assert.Equal(StepStatus.Skipped, StepFor(files[6]).Status);
        Assert.Contains("changed since the scan", StepFor(files[6]).Message);
        Assert.True(File.Exists(files[6]));
        Assert.Equal(StepStatus.Done, StepFor(files[0]).Status); // an untouched item still runs
        Assert.False(File.Exists(files[0]));
    }

    private static string WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }

    private sealed class FakeResultScanner(string id, ScanResult result) : IFeatureScanner
    {
        public ScanResult Fixed => result;
        public string FeatureId => id;
        public string Title => id;
        public Release Release => Release.V1;
        public bool IncludedInOptimizeAll => true;
        public Task<ScanResult> ScanAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct) => Task.FromResult(result);
    }
}
