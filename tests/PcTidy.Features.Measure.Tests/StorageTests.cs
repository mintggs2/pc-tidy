using PcTidy.Features.Measure.Compare;
using PcTidy.Features.Measure.Storage;
using Xunit;

namespace PcTidy.Features.Measure.Tests;

public class StorageTests
{
    private static SavedComparison Sample(string? runId = "run-123")
    {
        var c = new SavedComparison
        {
            Title = "Invented test",
            LinkedRunId = runId,
            Before = { Synth.Run(100, 70), Synth.Run(101, 71), Synth.Run(99, 69) },
            After = { Synth.Run(110, 77), Synth.Run(111, 78), Synth.Run(109, 76) },
            CreatedUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        };
        c.Conditions[0].SameForAllRuns = true;
        c.Conditions[0].Note = "built-in benchmark";
        c.Conditions[2].Note = "1440p, High";
        var r = c.Recompute();
        c.VerdictAtSave = r.Verdict;
        c.HeadlineAtSave = r.Headline;
        return c;
    }

    [Fact]
    public void A_comparison_round_trips_through_json_with_conditions_runs_and_the_linked_run()
    {
        using var tmp = new TempFolder();
        var store = new ComparisonStore(Path.Combine(tmp.Root, "measure"));
        var c = Sample();
        Assert.True(store.Save(c), store.LastProblem);
        Assert.True(File.Exists(store.PathFor(c.Id)));

        var back = store.Load(c.Id)!;
        Assert.Equal("Invented test", back.Title);
        Assert.Equal("run-123", back.LinkedRunId);
        Assert.Equal(c.CreatedUtc, back.CreatedUtc);
        Assert.Equal(3, back.Before.Count);
        Assert.Equal(3, back.After.Count);
        Assert.Equal(c.Before[1].Presents.AverageFps, back.Before[1].Presents.AverageFps);
        Assert.Equal(c.After[0].Presents.OnePercentLowFps, back.After[0].Presents.OnePercentLowFps);
        Assert.Equal(c.Before[0].ProcessName, back.Before[0].ProcessName);
        Assert.Equal(c.Before[0].Format, back.Before[0].Format);
        Assert.True(back.Conditions[0].SameForAllRuns);
        Assert.Equal("built-in benchmark", back.Conditions[0].Note);
        Assert.Equal("1440p, High", back.Conditions[2].Note);
        Assert.Equal(Conditions.Known.Count, back.Conditions.Count);
        Assert.Equal(Verdict.Better, back.VerdictAtSave);
        Assert.Equal(c.Recompute().Verdict, back.Recompute().Verdict);
    }

    [Fact]
    public void List_is_newest_first_filters_by_run_and_skips_damaged_files()
    {
        using var tmp = new TempFolder();
        var store = new ComparisonStore(tmp.Root);
        var a = Sample("run-a");
        var b = Sample("run-b");
        b.CreatedUtc = a.CreatedUtc.AddDays(1);
        Assert.True(store.Save(a));
        Assert.True(store.Save(b));
        File.WriteAllText(Path.Combine(tmp.Root, "broken.json"), "{ not json");

        var all = store.List();
        Assert.Equal(new[] { b.Id, a.Id }, all.Select(x => x.Id));
        Assert.Contains("couldn't be read", store.LastProblem);
        Assert.Equal(a.Id, Assert.Single(store.List("run-a")).Id);
    }

    [Fact]
    public void Saving_again_replaces_the_file_and_ids_cannot_escape_the_folder()
    {
        using var tmp = new TempFolder();
        var store = new ComparisonStore(tmp.Root);
        var c = Sample();
        c.Id = "..\\..\\evil";
        Assert.True(store.Save(c));
        Assert.Equal("evil", c.Id);
        Assert.StartsWith(tmp.Root, store.PathFor(c.Id));
        c.Title = "Renamed";
        Assert.True(store.Save(c));
        Assert.Single(Directory.GetFiles(tmp.Root, "*.json"));
        Assert.Equal("Renamed", store.Load(c.Id)!.Title);
        Assert.Empty(Directory.GetFiles(tmp.Root, "*.tmp"));
    }

    [Fact]
    public void Saved_json_has_file_names_only_and_no_frame_data()
    {
        using var tmp = new TempFolder();
        var store = new ComparisonStore(tmp.Root);
        var c = Sample();
        c.Before[0] = c.Before[0] with { FileName = Path.GetFileName(@"C:\Somewhere\run1.csv") };
        Assert.True(store.Save(c));
        var json = File.ReadAllText(store.PathFor(c.Id));
        Assert.DoesNotContain("Somewhere", json);
        Assert.DoesNotContain("PresentMs", json);
    }

    [Fact]
    public void Missing_folder_lists_nothing()
    {
        Assert.Empty(new ComparisonStore(Path.Combine(Path.GetTempPath(), "pctidy-measure-none-" + Guid.NewGuid().ToString("N"))).List());
    }
}

public class ConditionTests
{
    [Fact]
    public void Unticked_conditions_give_one_plain_warning_and_none_when_all_are_ticked()
    {
        var list = Conditions.NewList();
        Assert.Equal(Conditions.Known.Count, list.Count);
        Assert.Contains(list, c => c.Key == "battery");
        Assert.Contains(list, c => c.Key == "driver");
        var w = Assert.Single(Conditions.Warnings(list));
        Assert.Contains("Same power plan", w);
        foreach (var c in list) c.SameForAllRuns = true;
        Assert.Empty(Conditions.Warnings(list));
    }
}
