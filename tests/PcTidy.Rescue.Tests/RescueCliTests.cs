using PcTidy.Core.Model;

namespace PcTidy.Rescue.Tests;

[Collection("rescue")]
public class RescueCliTests
{
    private static async Task<(int Code, string Output, List<string> Asked)> Run(string[] args, bool answer = true)
    {
        var output = new StringWriter();
        var asked = new List<string>();
        var cli = new RescueCli(output, q => { asked.Add(q); return answer; });
        var code = await cli.RunAsync(RescueOptions.Parse(args));
        return (code, output.ToString(), asked);
    }

    [Fact]
    public async Task List_shows_runs_newest_first_with_hints()
    {
        using var bed = new RescueBed("cli-list");
        var run = await bed.RunAsApp(bed.QuarantineAndMovePlan());

        var (code, output, asked) = await Run(new[] { "--sandbox", bed.Root, "--list" });
        Assert.Equal(RescueCli.ExitCodes.Ok, code);
        Assert.Contains(run.RunId, output);
        Assert.Contains("Tidy Downloads", output);
        Assert.Contains("can undo", output);
        Assert.Contains("3 in quarantine", output);
        Assert.Contains("practice folder", output);
        Assert.Empty(asked);
        bed.AssertIdenticalToStartExcept(run);
    }

    [Fact]
    public async Task Undo_last_asks_first_and_no_changes_nothing()
    {
        using var bed = new RescueBed("cli-no");
        await bed.RunAsApp(bed.QuarantineAndMovePlan());

        var (code, output, asked) = await Run(new[] { "--sandbox", bed.Root, "--undo-last" }, answer: false);
        Assert.Equal(RescueCli.ExitCodes.Declined, code);
        Assert.Single(asked);
        Assert.Contains("Tidy Downloads", asked[0]);
        Assert.Contains("Nothing was changed", output);
        Assert.False(File.Exists(bed.Downloads("ChromeSetup.exe")));
    }

    [Fact]
    public async Task Undo_last_with_yes_undoes_without_asking()
    {
        using var bed = new RescueBed("cli-yes");
        await bed.RunAsApp(bed.QuarantineAndMovePlan());

        var (code, output, asked) = await Run(new[] { "--sandbox", bed.Root, "--undo-last", "--yes" });
        Assert.Equal(RescueCli.ExitCodes.Ok, code);
        Assert.Empty(asked);
        Assert.Contains("put back", output);
        bed.AssertIdenticalToStart();

        var (again, againOutput, _) = await Run(new[] { "--sandbox", bed.Root, "--undo-last", "--yes" });
        Assert.Equal(RescueCli.ExitCodes.NothingToDo, again);
        Assert.Contains("no run left to undo", againOutput);
    }

    [Fact]
    public async Task Undo_last_undoes_every_part_of_a_combined_run()
    {
        using var bed = new RescueBed("cli-combined");
        var parent = ActionPlan.NewRunId();
        var whole = bed.QuarantineAndMovePlan();
        await bed.RunAsApp(whole with { RunId = ActionPlan.NewRunId(), ParentRunId = parent, Title = "Clean", Items = whole.Items.Take(3).ToList() });
        await Task.Delay(20);
        await bed.RunAsApp(whole with { RunId = ActionPlan.NewRunId(), ParentRunId = parent, Title = "Organize", Items = whole.Items.Skip(3).ToList() });

        var (code, _, asked) = await Run(new[] { "--sandbox", bed.Root, "--undo-last" });
        Assert.Equal(RescueCli.ExitCodes.Ok, code);
        Assert.Contains("2 parts", Assert.Single(asked));
        bed.AssertIdenticalToStart();
    }

    [Fact]
    public async Task Undo_by_id_and_unknown_id()
    {
        using var bed = new RescueBed("cli-id");
        var run = await bed.RunAsApp(bed.QuarantineAndMovePlan());

        var (missing, missingOut, _) = await Run(new[] { "--sandbox", bed.Root, "--undo", "20990101-000000-abcdef", "--yes" });
        Assert.Equal(RescueCli.ExitCodes.NothingToDo, missing);
        Assert.Contains("--list", missingOut);

        var (code, _, _) = await Run(new[] { "--sandbox", bed.Root, "--undo", run.RunId, "--yes" });
        Assert.Equal(RescueCli.ExitCodes.Ok, code);
        bed.AssertIdenticalToStart();
    }

    [Fact]
    public async Task Rollback_and_restore_quarantine_from_the_command_line()
    {
        using var bed = new RescueBed("cli-rollback");
        var crashed = await bed.CrashMidRun(bed.QuarantineAndMovePlan(), finishFirst: true);
        var (undo, undoOut, _) = await Run(new[] { "--sandbox", bed.Root, "--undo", crashed, "--yes" });
        Assert.Equal(RescueCli.ExitCodes.NothingToDo, undo);
        Assert.Contains("--rollback", undoOut);

        var (code, _, _) = await Run(new[] { "--sandbox", bed.Root, "--rollback", crashed, "--yes" });
        Assert.Equal(RescueCli.ExitCodes.Ok, code);
        bed.AssertIdenticalToStart();

        var quarantined = await bed.RunAsApp(bed.QuarantineAndMovePlan(includeMoves: false));
        var (restore, _, asked) = await Run(new[] { "--sandbox", bed.Root, "--restore-quarantine", quarantined.RunId });
        Assert.Equal(RescueCli.ExitCodes.Ok, restore);
        Assert.Contains("3 item(s)", Assert.Single(asked));
        bed.AssertIdenticalToStart();
    }

    [Fact]
    public async Task Bad_command_lines_print_help_and_change_nothing()
    {
        var (code, output, _) = await Run(new[] { "--undo" });
        Assert.Equal(RescueCli.ExitCodes.Usage, code);
        Assert.Contains("needs a run id", output);
        Assert.Contains("--undo-last", output);

        var (help, helpOut, _) = await Run(new[] { "--help" });
        Assert.Equal(RescueCli.ExitCodes.Ok, help);
        Assert.Contains("--list", helpOut);
    }
}

public static class RescueBedExtensions
{
    /// <summary>After a run that wasn't undone: the run's own changes are the only differences.</summary>
    public static void AssertIdenticalToStartExcept(this RescueBed bed, RunRecord run)
    {
        var touched = run.Steps.SelectMany(s => new[] { s.Item.SourcePath, s.Item.DestinationPath }).OfType<string>()
            .Select(p => Path.GetRelativePath(bed.Root, p)).ToList();
        var unexpected = bed.DiffFromStart().Differences
            .Where(d => !touched.Any(t => d.Path.Equals(t, StringComparison.OrdinalIgnoreCase) || t.StartsWith(d.Path + "\\", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        Assert.True(unexpected.Count == 0, "Unexpected changes: " + string.Join(" | ", unexpected.Take(10)));
    }
}
