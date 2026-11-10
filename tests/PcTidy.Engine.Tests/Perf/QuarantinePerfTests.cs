using System.Diagnostics;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Engine.IO;
using PcTidy.Hosting;
using Xunit;
using Xunit.Abstractions;

namespace PcTidy.Engine.Tests;

/// <summary>Timing tests run on their own, after the others, so parallel tests don't distort the numbers.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PerfCollection
{
    public const string Name = "Perf (runs alone)";
}

/// <summary>
/// Speed with the real composition root (real SafetyGuard, Restart Manager, journal and quarantine) in sandbox mode,
/// on the drive that holds the testbed. A cleanup-sized run: 2,000 small temp files quarantined, then undone.
/// Prints per-phase timings so regressions are easy to see.
/// </summary>
[Collection(PerfCollection.Name)]
public class QuarantinePerfTests
{
    private const int FileCount = 2000;
    private readonly ITestOutputHelper _out;

    public QuarantinePerfTests(ITestOutputHelper output) => _out = output;

    [Fact]
    [Trait("Category", "Perf")]
    public async Task Quarantining_2000_small_files_and_undoing_them_takes_seconds_not_minutes()
    {
        var root = Path.Combine(TestSandbox.TestbedRootPath, "perf", Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using var host = PcTidyHost.CreateSandbox(root, SandboxProfile.Minimal);
            var temp = host.Locations.UserTemp;
            var items = new List<PlanItem>(FileCount);
            var make = Stopwatch.StartNew();
            for (int i = 0; i < FileCount; i++)
            {
                var path = Path.Combine(temp, $"batch{i / 200:00}", $"tmp{i:0000}.tmp");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, new byte[200 + i % 3000]);
                var info = new FileInfo(path);
                items.Add(new PlanItem
                {
                    Kind = ActionKind.QuarantinePath, FeatureId = "cleanup", Description = "temp file",
                    SourcePath = path, CarveOutId = CarveOuts.UserTemp,
                    ExpectedSize = info.Length, ExpectedLastWriteUtc = info.LastWriteTimeUtc,
                });
            }
            _out.WriteLine($"made {FileCount} files in {make.ElapsedMilliseconds} ms on {Path.GetPathRoot(root)}");

            var plan = new ActionPlan { FeatureId = "cleanup", Title = "perf", Items = items };
            var timings = new EngineTimings();
            ((Executor)host.Executor).Timings = timings;

            var run = Stopwatch.StartNew();
            var record = await host.Executor.ExecuteAsync(plan, new ExecuteOptions { Mode = ExecutionMode.Real }, null, default);
            run.Stop();
            Assert.Equal(FileCount, record.DoneCount);
            _out.WriteLine($"execute: {run.ElapsedMilliseconds} ms");
            _out.WriteLine(timings.Report());

            timings.Reset();
            var undo = Stopwatch.StartNew();
            var report = await host.Undo.UndoRunAsync(plan.RunId, null, default);
            undo.Stop();
            Assert.Equal(FileCount, report.Restored);
            _out.WriteLine($"undo: {undo.ElapsedMilliseconds} ms");
            _out.WriteLine(timings.Report());

            Assert.True(run.Elapsed < TimeSpan.FromSeconds(30), $"Quarantining {FileCount} files took {run.Elapsed.TotalSeconds:0.0} s.");
            Assert.True(undo.Elapsed < TimeSpan.FromSeconds(30), $"Undoing {FileCount} files took {undo.Elapsed.TotalSeconds:0.0} s.");
        }
        finally
        {
            if (Directory.Exists(root)) FsOps.DeleteTree(root);
        }
    }
}
