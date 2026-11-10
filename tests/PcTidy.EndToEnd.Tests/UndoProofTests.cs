using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Cleanup;
using PcTidy.Features.Organize;
using PcTidy.Features.Organize.Planning;
using PcTidy.Features.SpaceMap;
using PcTidy.TestbedGen;
using PcTidy.TestbedGen.Manifest;
using Xunit;
using Xunit.Abstractions;

namespace PcTidy.EndToEnd.Tests;

/// <summary>
/// The owner's acceptance test: run every v1 feature on a generated messy PC, check nothing
/// precious was touched, then undo and prove the tree is exactly as it was.
/// </summary>
[Collection("e2e")] // sequential: each test generates a few thousand files
public class UndoProofTests(ITestOutputHelper log)
{
    private static IEnumerable<ScanItem> Ticked(ScanResult r) => r.AllItems.Where(i => i.DefaultSelected && i.DisabledReason is null);

    private static IEnumerable<ScanItem> EverythingReversible(ScanResult r)
        => r.AllItems.Where(i => i.DisabledReason is null && i.Actions.Count > 0 && i.Actions.All(a => a.Reversible));

    private void Report(string what, RunRecord run)
    {
        log.WriteLine($"{what}: {run.Status}, done {run.DoneCount}, skipped {run.SkippedCount}, failed {run.FailedCount}, freed {Sizes.Format(run.BytesFreed)}");
        foreach (var s in run.Steps.Where(s => s.Status is StepStatus.Failed).Take(20)) log.WriteLine($"  FAILED: {s.Item.Description}: {s.Message}");
    }

    private void AssertNoViolations(IReadOnlyList<ManifestDifference> v)
    {
        foreach (var d in v.Take(40)) log.WriteLine($"VIOLATION {d.Change}: {d.Path} ({d.Before} -> {d.After})");
        Assert.True(v.Count == 0, $"{v.Count} protected file(s) changed: " + string.Join(" | ", v.Take(10).Select(d => $"{d.Change}: {d.Path}")));
    }

    private void Describe(ScanResult scan)
    {
        foreach (var g in scan.Groups)
            log.WriteLine($"group {g.Id} '{g.Title}' {g.Badge}: {g.Items.Count} items, {g.Items.Count(i => i.DefaultSelected)} ticked, " +
                          $"actions {string.Join(",", g.Items.SelectMany(i => i.Actions).GroupBy(a => a.Kind).Select(k => $"{k.Key}x{k.Count()}"))}");
        foreach (var n in scan.Notes) log.WriteLine("note: " + n);
    }

    private static void AssertIdentical(ManifestDiff diff)
        => Assert.True(diff.Identical, "Undo did not restore the tree exactly:\n" + diff.Format(60));

    [Fact]
    public async Task Cleanup_default_selection_frees_space_touches_no_canary_and_undo_restores_everything()
    {
        using var e = new E2E("cleanup-default");
        var scan = await new CleanupScanner().ScanAsync(e.Context(), null, CancellationToken.None);
        var plan = PlanBuilder.FromSelection("cleanup", "Clean", Ticked(scan));
        Assert.NotEmpty(plan.Items);

        var run = await e.Run(plan);
        Report("cleanup", run);
        Assert.Equal(0, run.FailedCount);
        Assert.True(run.DoneCount > 0);
        Assert.True(run.BytesFreed > 0);
        AssertNoViolations(e.CanaryViolations(CanaryTier.Default)); // default run keeps even cookies/history

        var undo = await e.Undo(run.RunId);
        Assert.Equal(0, undo.Failed);
        AssertIdentical(e.DiffFromStart());
    }

    [Fact]
    public async Task Cleanup_everything_reversible_incl_privacy_extras_never_touches_logins_and_undoes_exactly()
    {
        using var e = new E2E("cleanup-all");
        var scan = await new CleanupScanner().ScanAsync(e.Context(ScanDepth.Deep), null, CancellationToken.None);
        var plan = PlanBuilder.FromSelection("cleanup", "Clean all", EverythingReversible(scan));

        var run = await e.Run(plan);
        Report("cleanup-all", run);
        Assert.Equal(0, run.FailedCount);
        AssertNoViolations(e.CanaryViolations(CanaryTier.Never));

        var undo = await e.Undo(run.RunId);
        Assert.Equal(0, undo.Failed);
        AssertIdentical(e.DiffFromStart());
    }

    [Fact]
    public async Task Organize_downloads_moves_files_into_categories_and_undo_puts_every_file_back()
    {
        using var e = new E2E("organize");
        var scan = await new OrganizeScanner().ScanAsync(e.Context(roots: new[] { e.Host.Locations.Downloads }), null, CancellationToken.None);
        Describe(scan);
        var selected = scan.AllItems.Where(i => i.DisabledReason is null && i.Actions.Count > 0 && (i.DefaultSelected || i.Badge == Badge.Safe)).ToList();
        var plan = PlanBuilder.FromSelection("organize", "Organize Downloads", selected);
        Assert.Contains(plan.Items, i => i.Kind == ActionKind.MovePath);

        var run = await e.Run(plan);
        Report("organize", run);
        Assert.Equal(0, run.FailedCount);
        Assert.True(run.Steps.Count(s => s.Status == StepStatus.Done && s.Item.Kind == ActionKind.MovePath) > 0);
        AssertNoViolations(e.CanaryViolations(CanaryTier.Default)); // shortcuts/desktop.ini stay put

        var undo = await e.Undo(run.RunId);
        Assert.Equal(0, undo.Failed);
        AssertIdentical(e.DiffFromStart());
    }

    [Fact]
    public async Task Space_map_big_and_old_quarantine_and_undo_restores_exactly()
    {
        using var e = new E2E("spacemap", SandboxProfile.Default);
        var scan = await new SpaceMapScanner().ScanAsync(e.Context(ScanDepth.Deep), null, CancellationToken.None);
        var quarantine = scan.AllItems
            .Where(i => i.DisabledReason is null)
            .Select(i => i with { Actions = i.Actions.Where(a => a.Kind == ActionKind.QuarantinePath).ToList() })
            .Where(i => i.Actions.Count > 0).ToList();
        if (quarantine.Count == 0) { log.WriteLine("no big-and-old files found in this testbed"); return; }

        var run = await e.Run(PlanBuilder.FromSelection("spacemap", "Big and old", quarantine));
        Report("spacemap", run);
        Assert.Equal(0, run.FailedCount);
        AssertNoViolations(e.CanaryViolations(CanaryTier.Never));

        var undo = await e.Undo(run.RunId);
        Assert.Equal(0, undo.Failed);
        AssertIdentical(e.DiffFromStart());
    }

    [Fact]
    public async Task Optimize_everything_style_combined_run_undoes_as_a_whole()
    {
        using var e = new E2E("optimize-all");
        var parent = ActionPlan.NewRunId();
        var ctx = e.Context();
        var runs = new List<RunRecord>();
        var cleanup = await new CleanupScanner().ScanAsync(ctx, null, CancellationToken.None);
        runs.Add(await e.Run(PlanBuilder.FromSelection("cleanup", "Clean", Ticked(cleanup), parent)));
        var organize = await new OrganizeScanner().ScanAsync(e.Context(roots: new[] { e.Host.Locations.Downloads }), null, CancellationToken.None);
        runs.Add(await e.Run(PlanBuilder.FromSelection("organize", "Organize", organize.AllItems.Where(i => i.DefaultSelected), parent)));
        foreach (var r in runs) { Report(r.FeatureId, r); Assert.Equal(0, r.FailedCount); }
        Assert.All(runs, r => Assert.Equal(parent, r.ParentRunId));

        // Undo the whole thing: newest part first.
        foreach (var r in Enumerable.Reverse(runs)) Assert.Equal(0, (await e.Undo(r.RunId)).Failed);
        AssertIdentical(e.DiffFromStart());
    }

    [Fact]
    public async Task Simulation_mode_changes_nothing_at_all()
    {
        using var e = new E2E("simulate");
        var cleanup = await new CleanupScanner().ScanAsync(e.Context(ScanDepth.Deep), null, CancellationToken.None);
        var organize = await new OrganizeScanner().ScanAsync(e.Context(roots: new[] { e.Host.Locations.Downloads }), null, CancellationToken.None);
        var all = cleanup.AllItems.Concat(organize.AllItems).Where(i => i.DisabledReason is null && i.Actions.Count > 0);
        var run = await e.Run(PlanBuilder.FromSelection("optimizeall", "Simulate everything", all), ExecutionMode.Simulate);
        Assert.Equal(RunStatus.Simulated, run.Status);
        Assert.Contains(run.Steps, s => s.Status == StepStatus.Simulated);
        AssertIdentical(e.DiffFromStart());
    }

    [Fact]
    public async Task Minimal_pc_one_drive_no_games_no_onedrive_works_without_errors_and_undoes_exactly()
    {
        using var e = new E2E("minimal", SandboxProfile.Minimal);
        var cleanup = await new CleanupScanner().ScanAsync(e.Context(), null, CancellationToken.None);
        var organize = await new OrganizeScanner().ScanAsync(e.Context(roots: new[] { e.Host.Locations.Downloads, e.Host.Locations.Desktop }), null, CancellationToken.None);
        var space = await new SpaceMapScanner().ScanAsync(e.Context(), null, CancellationToken.None);
        Assert.NotNull(space);

        var run1 = await e.Run(PlanBuilder.FromSelection("cleanup", "Clean", Ticked(cleanup)));
        var run2 = await e.Run(PlanBuilder.FromSelection("organize", "Organize", organize.AllItems.Where(i => i.DefaultSelected)));
        Report("minimal cleanup", run1); Report("minimal organize", run2);
        Assert.Equal(0, run1.FailedCount + run2.FailedCount);
        AssertNoViolations(e.CanaryViolations(CanaryTier.Default));

        Assert.Equal(0, (await e.Undo(run2.RunId)).Failed);
        Assert.Equal(0, (await e.Undo(run1.RunId)).Failed);
        AssertIdentical(e.DiffFromStart());
    }

    [Fact]
    public async Task A_file_in_use_is_skipped_with_the_reason_never_failed()
    {
        using var e = new E2E("in-use");
        var scan = await new CleanupScanner().ScanAsync(e.Context(), null, CancellationToken.None);
        var plan = PlanBuilder.FromSelection("cleanup", "Clean", Ticked(scan));
        var victim = plan.Items.First(i => i.Kind == ActionKind.QuarantinePath && !i.IsDirectory && i.SourcePath is not null && File.Exists(i.SourcePath)).SourcePath!;
        RunRecord run;
        using (TestbedBuilder.LockFile(victim)) run = await e.Run(plan);
        Report("in-use", run);
        Assert.Equal(0, run.FailedCount);
        Assert.True(File.Exists(victim));
        var step = run.Steps.First(s => s.Item.SourcePath == victim);
        Assert.Equal(StepStatus.Skipped, step.Status);
        Assert.Equal(0, (await e.Undo(run.RunId)).Failed);
        AssertIdentical(e.DiffFromStart());
    }

    [Fact]
    public async Task Windows_tool_items_only_simulate_in_the_sandbox()
    {
        using var e = new E2E("tools");
        var scan = await new CleanupScanner().ScanAsync(e.Context(ScanDepth.Deep), null, CancellationToken.None);
        var tools = scan.AllItems.Where(i => i.Actions.Any(a => a.Kind is ActionKind.RunSystemTool or ActionKind.EmptyRecycleBin)).ToList();
        if (tools.Count == 0) return;
        var run = await e.Run(PlanBuilder.FromSelection("cleanup", "Tools", tools));
        Report("tools", run);
        Assert.Equal(0, run.FailedCount);
        Assert.DoesNotContain(run.Steps, s => s.Status == StepStatus.Done && s.Item.Kind == ActionKind.RunSystemTool && !(s.Message ?? "").Contains("sandbox", StringComparison.OrdinalIgnoreCase) && !(s.Message ?? "").Contains("simulat", StringComparison.OrdinalIgnoreCase));
        AssertIdentical(e.DiffFromStart() is var d && d.Differences.All(x => x.Path.Contains("$Recycle.Bin")) ? new ManifestDiff(Array.Empty<ManifestDifference>()) : d);
    }
}

[CollectionDefinition("e2e", DisableParallelization = true)]
public class E2ECollection { }
