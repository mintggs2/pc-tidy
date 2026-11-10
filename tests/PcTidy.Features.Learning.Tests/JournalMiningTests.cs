using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Learning.Advisor;
using PcTidy.Features.Learning.Learners;
using PcTidy.Features.Learning.Model;
using PcTidy.Features.Learning.Store;
using PcTidy.Features.Organize;
using PcTidy.Features.Organize.Rules;
using PcTidy.Hosting;

namespace PcTidy.Features.Learning.Tests;

/// <summary>
/// Mining PC Tidy's own journal after a REAL sandbox run (real SafetyGuard, Executor, journal and Undo from
/// <see cref="PcTidyHost.CreateSandbox"/>), all inside testbed\learning\.
/// </summary>
public class JournalMiningTests : IDisposable
{
    private readonly TestFolder _box = new("journal");
    private readonly PcTidyHost _host;
    private readonly SandboxKnownLocations _k;

    public JournalMiningTests()
    {
        _host = PcTidyHost.CreateSandbox(_box.Root, SandboxProfile.Default);
        _k = (SandboxKnownLocations)_host.Locations;
    }

    public void Dispose()
    {
        _host.Dispose();
        _box.Dispose();
    }

    private string Put(string folder, string name, string content = "model data")
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static PlanItem Move(string source, string destFolder, Dictionary<string, string>? args = null) => new()
    {
        Kind = ActionKind.MovePath,
        FeatureId = "organize",
        Description = $"Move '{Path.GetFileName(source)}' to {destFolder}",
        SourcePath = source,
        DestinationPath = Path.Combine(destFolder, Path.GetFileName(source)),
        ExpectedSize = new FileInfo(source).Length,
        ExpectedLastWriteUtc = File.GetLastWriteTimeUtc(source),
        Args = args,
    };

    private async Task<RunRecord> RunAsync(IEnumerable<PlanItem> items, ExecutionMode mode = ExecutionMode.Real)
    {
        var plan = new ActionPlan { FeatureId = "organize", Title = "Organize", Items = items.ToList() };
        return await _host.Executor.ExecuteAsync(plan, new ExecuteOptions { Mode = mode }, null, CancellationToken.None);
    }

    [Fact]
    public async Task A_real_run_is_mined_once_and_its_undo_is_forgotten()
    {
        var clock = new TestClock(DateTime.UtcNow.AddMinutes(-5));
        var advisor = new LearningAdvisor(LearningStore.Open(_host.Settings.DataRoot, clock), _host.Settings, _host.Locations, _host.Journal,
            new LearningSources(), clock);

        var downloads = _k.Downloads;
        var threeD = Path.Combine(_box.Root, "D", "3D");
        var files = Enumerable.Range(1, 5).Select(i => Put(downloads, $"chair{i}.blend")).ToList();
        // One of them came from a website (Mark of the Web), read back after the move.
        File.WriteAllText(files[0] + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://www.blendswap.com/files/chair1.blend\r\n");
        // A move made by an existing Organize rule is not a new habit.
        var byRule = Put(downloads, "table.blend");

        var run = await RunAsync(files.Select(f => Move(f, threeD))
            .Append(Move(byRule, threeD, new() { [OrganizeLedger.RuleIdArg] = "rule-1" })));
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.True(File.Exists(Path.Combine(threeD, "chair1.blend")));

        // A simulation changes nothing and teaches nothing.
        var sim = Put(downloads, "lamp.blend");
        var simRun = await RunAsync(new[] { Move(sim, threeD) }, ExecutionMode.Simulate);

        await advisor.ObserveAsync(CancellationToken.None);
        var moves = advisor.Store.Moves();
        Assert.Equal(5, moves.Count);
        Assert.All(moves, m => Assert.StartsWith(threeD, m.FinalPath, StringComparison.OrdinalIgnoreCase));
        Assert.All(moves, m => Assert.Equal(run.RunId, m.RunId));
        Assert.Equal("blendswap.com", moves.Single(m => m.FinalPath.EndsWith("chair1.blend")).Host);
        Assert.DoesNotContain(moves, m => m.SourcePath.EndsWith("table.blend"));
        Assert.False(advisor.Store.IsRunMined(simRun.RunId));
        Assert.True(advisor.Store.IsRunMined(run.RunId));
        Assert.Contains(advisor.Store.FolderVisits(), v => v.Folder.Equals(threeD, StringComparison.OrdinalIgnoreCase) && v.Source == VisitSource.Move);
        var mark = advisor.Store.RunMarks().Single(r => r.RunId == run.RunId);
        Assert.Contains(PathUtil.DriveOf(threeD)!, mark.Drives, StringComparer.OrdinalIgnoreCase);

        // Mined once: a second pass adds nothing.
        await advisor.ObserveAsync(CancellationToken.None);
        Assert.Equal(5, advisor.Store.Moves().Count);

        // All five moves were on one day: not a habit yet. One more on another day makes it one.
        Assert.Empty(advisor.SuggestedRules());
        var earlier = Moves.Of("stool.blend", downloads, threeD, "older-run", DateTime.UtcNow.AddDays(-3));
        advisor.Store.AddMinedRun(new RunMark("older-run", earlier.Utc, new[] { @"C:\" }), new[] { earlier }, Array.Empty<FolderVisit>());
        var suggestion = Assert.Single(advisor.SuggestedRules());
        Assert.Equal(MoveFeature.Extension, suggestion.Feature);
        Assert.Equal(PathUtil.Normalize(threeD), suggestion.Destination, ignoreCase: true);
        Assert.Equal(PathUtil.Normalize(downloads), suggestion.SourceFolder, ignoreCase: true);

        // The rule it makes passes Organize's own checks with the real SafetyGuard.
        var rule = suggestion.CreateRule();
        var problems = RuleValidator.Validate(rule, new[] { downloads }, _host.Safety, _host.Settings);
        Assert.DoesNotContain(problems, p => p.Blocking);

        // Undo puts the files back; learning forgets those moves.
        var report = await _host.Undo.UndoRunAsync(run.RunId, null, CancellationToken.None);
        Assert.True(report.Complete);
        Assert.True(File.Exists(files[0]));
        advisor.RecordUndo(run.RunId);
        Assert.Equal("older-run", Assert.Single(advisor.Store.Moves()).RunId);
        Assert.Empty(advisor.SuggestedRules());
    }

    [Fact]
    public async Task Runs_from_before_learning_started_are_never_mined()
    {
        var file = Put(_k.Downloads, "old1.blend");
        var run = await RunAsync(new[] { Move(file, Path.Combine(_box.Root, "D", "3D")) });
        var clock = new TestClock(DateTime.UtcNow.AddMinutes(5)); // learning started after the run (e.g. after "Forget everything")
        var advisor = new LearningAdvisor(LearningStore.Open(_host.Settings.DataRoot, clock), _host.Settings, _host.Locations, _host.Journal,
            new LearningSources(), clock);
        await advisor.ObserveAsync(CancellationToken.None);
        Assert.False(advisor.Store.IsRunMined(run.RunId));
        Assert.Empty(advisor.Store.Moves());
    }

    [Fact]
    public async Task Moves_inside_OneDrive_are_not_learned_unless_allowed()
    {
        var cloudFile = Put(Path.Combine(_k.CloudRoots[0], "Inbox"), "notes1.txt");
        var run = await RunAsync(new[] { Move(cloudFile, Path.Combine(_box.Root, "D", "Notes")) });
        var clock = new TestClock(DateTime.UtcNow.AddMinutes(-5));
        var advisor = new LearningAdvisor(LearningStore.Open(_host.Settings.DataRoot, clock), _host.Settings, _host.Locations, _host.Journal,
            new LearningSources(), clock);
        await advisor.ObserveAsync(CancellationToken.None);
        if (run.Status == RunStatus.Completed) Assert.True(advisor.Store.IsRunMined(run.RunId));
        Assert.Empty(advisor.Store.Moves());
    }
}
