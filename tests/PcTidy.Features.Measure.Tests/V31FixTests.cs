using System.Text;
using PcTidy.Core;
using PcTidy.Features.Measure.Compare;
using PcTidy.Features.Measure.Import;
using PcTidy.Features.Measure.Stats;
using PcTidy.Features.Measure.Storage;
using PcTidy.Features.Measure.UI;
using PcTidy.Hosting;
using Xunit;

namespace PcTidy.Features.Measure.Tests;

/// <summary>v3.1 review fixes: one timing basis everywhere, bounded CapFrameX reads, observed imports, no infinite FPS, no overwritten comparison.</summary>
public class V31FixTests
{
    private static RecordingFile File(string name, float[] present, float[]? display) => new()
    {
        FileName = name,
        Format = RecordingFormat.PresentMonV1Csv,
        Processes = new[] { new ProcessCapture { ProcessName = "game.exe", PresentMs = present, DisplayMs = display } },
    };

    /// <summary>Display timing capped at 60 FPS (16.7 ms) while the game presents faster: the two bases differ a lot.</summary>
    private static RecordingFile WithDisplay(string name, double presentMs, int seed)
        => File(name, Synth.Frames(3000, presentMs, seed, jitter: 0.02, hitchChance: 0), Enumerable.Repeat(16.7f, 3000).ToArray());

    private static RecordingFile PresentsOnly(string name, double presentMs, int seed)
        => File(name, Synth.Frames(3000, presentMs, seed, jitter: 0.02, hitchChance: 0), null);

    private static (MeasureViewModel Vm, PcTidyHost Host, TempFolder Tmp) NewVm()
    {
        var tmp = new TempFolder();
        var host = PcTidyHost.CreateSandbox(tmp.Root);
        return (new MeasureViewModel(host, new ComparisonStore(Path.Combine(tmp.Root, "m"))), host, tmp);
    }

    private static double ColumnFps(IReadOnlyList<StatLine> column)
        => double.Parse(column.Single(l => l.Label == "Average FPS").Value, System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void Columns_and_run_lines_use_the_verdicts_basis_and_say_which()
    {
        var (vm, host, tmp) = NewVm();
        using (tmp) using (host)
        {
            for (int i = 0; i < 3; i++) vm.AddRun(RunSet.Before, WithDisplay($"b{i}.csv", 10, i));
            for (int i = 0; i < 2; i++) vm.AddRun(RunSet.After, WithDisplay($"a{i}.csv", 9.5, 10 + i));
            vm.AddRun(RunSet.After, PresentsOnly("a2.csv", 9.5, 12)); // one After file has no on-screen timing

            Assert.Equal(FrameTimeBasis.Presents, vm.Result.Basis);
            Assert.Equal(FrameTimeBasis.Presents, vm.Basis);
            // ~100 FPS on Present timing, not the 60 FPS on-screen numbers the old columns averaged.
            Assert.InRange(ColumnFps(vm.BeforeColumn), 95, 105);
            Assert.InRange(ColumnFps(vm.AfterColumn), 100, 110);
            Assert.Equal("Present calls", vm.BeforeColumn.Single(l => l.Label == "Timing").Value);
            Assert.All(vm.BeforeRuns.Concat(vm.AfterRuns), r => Assert.EndsWith("(Present timing)", r.StatsLine));
            Assert.Contains("Present timing", vm.BasisText);
            // The headline's means are the columns' means.
            Assert.Equal(vm.Result.Average!.BeforeMean, ColumnFps(vm.BeforeColumn), 1);

            // Without the odd file, everything switches to on-screen timing together.
            vm.AfterRuns.RemoveAt(2);
            vm.AddRun(RunSet.After, WithDisplay("a3.csv", 9.5, 13));
            Assert.Equal(FrameTimeBasis.Display, vm.Result.Basis);
            Assert.InRange(ColumnFps(vm.BeforeColumn), 59, 61);
            Assert.All(vm.AfterRuns, r => Assert.EndsWith("(on-screen timing)", r.StatsLine));
        }
    }

    [Fact]
    public void While_more_runs_are_needed_the_columns_already_use_the_same_all_have_display_rule()
    {
        var (vm, host, tmp) = NewVm();
        using (tmp) using (host)
        {
            vm.AddRun(RunSet.Before, WithDisplay("b0.csv", 10, 1));
            vm.AddRun(RunSet.After, PresentsOnly("a0.csv", 10, 2));
            Assert.Equal(Verdict.NeedMoreRuns, vm.Result.Verdict);
            Assert.Equal(FrameTimeBasis.Presents, vm.Result.Basis);
            Assert.InRange(ColumnFps(vm.BeforeColumn), 95, 105); // not the 60 FPS on-screen value
            Assert.Equal(FrameTimeBasis.Presents, Comparer.BasisFor(Array.Empty<RunSummary>()));
        }
    }

    [Fact]
    public void A_CapFrameX_file_over_the_frame_limit_is_refused_while_it_is_read()
    {
        var json = Synth.CapFrameX("game.exe", Synth.Frames(400, 10, 1), Synth.Frames(400, 10, 2));
        var r = RecordingImporter.ImportText(json, "big.json", new ImportLimits { MaxRows = 500, MinFrames = 10 });
        Assert.False(r.Ok);
        Assert.Contains("more than 500 frames", r.Problem);

        // Under the limit it still reads both runs (floats, the same numbers).
        var ok = RecordingImporter.ImportText(json, "ok.json", new ImportLimits { MaxRows = 1000, MinFrames = 10 });
        Assert.True(ok.Ok, ok.Problem);
        Assert.Equal(2, ok.File!.Processes.Count);
        Assert.Equal(400, ok.File.Processes[0].PresentMs.Length);
    }

    [Fact]
    public void CapFrameX_numbers_written_as_strings_or_NaN_are_read_or_skipped()
    {
        var frames = string.Join(",", Enumerable.Range(0, 300).Select(i => i % 50 == 0 ? "\"NaN\"" : i % 7 == 0 ? "\"9.5\"" : "10.25"));
        var json = "{\"Info\":{\"ProcessName\":\"game.exe\"},\"Runs\":[{\"CaptureData\":{\"MsBetweenPresents\":[" + frames + "]}}]}";
        var r = RecordingImporter.ImportText(json, "s.json", new ImportLimits { MinFrames = 10 });
        Assert.True(r.Ok, r.Problem);
        Assert.Equal(294, r.File!.Processes[0].PresentMs.Length);
        Assert.Equal(6, r.File.SkippedRows);
    }

    [Fact]
    public void Frame_times_that_round_to_zero_are_skipped_and_never_give_infinite_FPS()
    {
        Assert.False(FrameStats.IsUsableFrameMs(1e-50));
        Assert.False(FrameStats.IsUsableFrameMs(0.005));
        Assert.False(FrameStats.IsUsableFrameMs(double.NaN));
        Assert.False(FrameStats.IsUsableFrameMs(70_000));
        Assert.True(FrameStats.IsUsableFrameMs(0.01));
        Assert.True(FrameStats.IsUsableFrameMs(16.7));

        // Every other row is 1e-50 ms: it passes "> 0" as a double but is 0 as a float.
        var csv = "Application,MsBetweenPresents\n" + string.Concat(Enumerable.Range(0, 400).Select(i => i % 2 == 0 ? "game.exe,1e-50\n" : "game.exe,10.0\n"));
        var r = RecordingImporter.ImportText(csv, "tiny.csv", new ImportLimits { MinFrames = 10 });
        Assert.True(r.Ok, r.Problem);
        Assert.All(r.File!.Processes[0].PresentMs, f => Assert.True(f >= FrameStats.MinFrameMs));
        var stats = FrameStats.Compute(r.File.Processes[0].PresentMs);
        Assert.True(double.IsFinite(stats.AverageFps) && double.IsFinite(stats.OnePercentLowFps));

        // Even fed zeros directly, the statistics stay finite.
        var zeros = FrameStats.Compute(new float[] { 0, 0, 0 });
        Assert.Equal(0, zeros.AverageFps);
        Assert.True(double.IsFinite(FrameStats.SlowestFps(new float[] { 0, 0 }, 0.5)));
    }

    [Fact]
    public void Saving_numbers_JSON_cannot_hold_fails_with_a_message_and_writes_nothing()
    {
        using var tmp = new TempFolder();
        var store = new ComparisonStore(Path.Combine(tmp.Root, "m"));
        var bad = new SavedComparison
        {
            Title = "Broken",
            Before = new List<RunSummary> { Synth.Run(100, 80) with { Presents = new RunStats { Frames = 10, AverageFps = double.PositiveInfinity } } },
        };
        Assert.False(store.Save(bad));
        Assert.Contains("can't be stored", store.LastProblem);
        Assert.Empty(Directory.Exists(store.Folder) ? Directory.GetFiles(store.Folder) : Array.Empty<string>());
    }

    [Fact]
    public async Task An_unexpected_import_error_is_shown_for_that_file_and_the_rest_still_import()
    {
        var (vm, host, tmp) = NewVm();
        using (tmp) using (host)
        {
            var good = tmp.Write("a.csv", Synth.PresentMonV1(Synth.Rows("game.exe", Synth.Frames(500, 10, 1))));
            vm.Importer = (path, limits, ct) => path.EndsWith("boom.csv")
                ? throw new InvalidOperationException("reader bug")
                : RecordingImporter.ImportAsync(path, limits, ct);
            await vm.ImportFromPageAsync(new[] { Path.Combine(tmp.Root, "boom.csv"), good });
            Assert.False(vm.IsImporting);
            Assert.Contains("boom.csv: PC Tidy couldn't read this file (reader bug)", vm.ImportStatus);
            Assert.Contains("Added 1 run(s) to Before", vm.ImportStatus);
            Assert.Single(vm.BeforeRuns);
        }
    }

    [Fact]
    public async Task The_import_status_names_the_set_the_runs_went_to_even_if_the_target_changed_meanwhile()
    {
        var (vm, host, tmp) = NewVm();
        using (tmp) using (host)
        {
            var good = tmp.Write("a.csv", Synth.PresentMonV1(Synth.Rows("game.exe", Synth.Frames(500, 10, 1))));
            vm.TargetIndex = (int)RunSet.After;
            vm.Importer = async (path, limits, ct) =>
            {
                vm.TargetIndex = (int)RunSet.Before; // the user switches the target during a slow import
                return await RecordingImporter.ImportAsync(path, limits, ct);
            };
            await vm.ImportFilesAsync(new[] { good });
            Assert.Single(vm.AfterRuns);
            Assert.Contains("to After", vm.ImportStatus);
        }
    }

    [Fact]
    public void Check_if_this_helped_for_another_run_starts_a_new_comparison_instead_of_overwriting_the_open_one()
    {
        var (vm, host, tmp) = NewVm();
        using (tmp) using (host)
        {
            vm.CheckRun("run-1");
            for (int i = 0; i < 3; i++) vm.AddRun(RunSet.Before, WithDisplay($"b{i}.csv", 10, i));
            vm.Title = "First check";
            vm.SaveCommand.Execute(null);
            var first = Assert.Single(vm.Saved).Comparison;
            Assert.Equal(first.Id, vm.OpenComparisonId);

            vm.CheckRun("run-1"); // the same run again keeps the open comparison
            Assert.Equal(first.Id, vm.OpenComparisonId);

            vm.CheckRun("run-2");
            Assert.Null(vm.OpenComparisonId);
            Assert.Empty(vm.BeforeRuns);
            Assert.Equal("run-2", vm.LinkedRunId);
            vm.AddRun(RunSet.Before, WithDisplay("c.csv", 10, 9));
            vm.SaveCommand.Execute(null);
            Assert.Equal(2, vm.Saved.Count);
            Assert.Contains(vm.Saved, s => s.Comparison.Id == first.Id && s.Comparison.LinkedRunId == "run-1");
            Assert.Contains(vm.Saved, s => s.Comparison.LinkedRunId == "run-2");
        }
    }

    [Fact]
    public void Open_History_asks_for_the_linked_run()
    {
        var (vm, host, tmp) = NewVm();
        using (tmp) using (host)
        {
            var shell = new RecordingShell();
            host.Add<IFlowHost>(shell);
            host.Add<PcTidy.UI.Flow.IShellFlowHost>(shell);
            vm.OpenHistoryCommand.Execute(null);
            Assert.Equal(("history", null), shell.Opened[^1]);
            Assert.Equal("Open History", vm.HistoryButtonText);

            vm.CheckRun("run-9");
            vm.OpenHistoryCommand.Execute(null);
            Assert.Equal("history", shell.Opened[^1].Key);
            Assert.Equal("run-9", Assert.IsType<PcTidy.UI.Flow.HistoryOpenRequest>(shell.Opened[^1].Parameter).RunId);
            Assert.Equal("Open this run in History", vm.HistoryButtonText);
        }
    }

    private sealed class RecordingShell : PcTidy.UI.Flow.IShellFlowHost
    {
        public List<(string Key, object? Parameter)> Opened { get; } = new();
        public void NavigateTo(string pageKey) => Opened.Add((pageKey, null));
        public void NavigateTo(string pageKey, object? parameter) => Opened.Add((pageKey, parameter));
        public int QuarantineDays => 7;
        public Task<PcTidy.UI.Flow.FlowOutcome> ReviewAndRunAsync(IReadOnlyList<Core.Model.ActionPlan> plans, PcTidy.UI.Flow.FlowOptions options, CancellationToken ct) => Task.FromResult(PcTidy.UI.Flow.FlowOutcome.Cancelled);
        public Task<IReadOnlyList<Core.Model.RunRecord>> SimulateAsync(IReadOnlyList<Core.Model.ActionPlan> plans, PcTidy.UI.Flow.FlowOptions options, CancellationToken ct) => Task.FromResult<IReadOnlyList<Core.Model.RunRecord>>(Array.Empty<Core.Model.RunRecord>());
        public Task<bool> UndoRunsAsync(IReadOnlyList<Core.Model.RunRecord> runs, CancellationToken ct) => Task.FromResult(false);
        public Task<Core.Model.RunRecord?> ReviewAndRunAsync(Core.Model.ActionPlan plan, CancellationToken ct) => Task.FromResult<Core.Model.RunRecord?>(null);
        public Task<Core.Model.RunRecord?> SimulateAsync(Core.Model.ActionPlan plan, CancellationToken ct) => Task.FromResult<Core.Model.RunRecord?>(null);
        public Task<Core.Model.RunRecord?> RunApprovedAsync(Core.Model.ActionPlan plan, CancellationToken ct) => Task.FromResult<Core.Model.RunRecord?>(null);
        public void OpenInExplorer(string path) { }
        public void ShowMessage(string title, string message, bool isError = false) { }
    }
}
