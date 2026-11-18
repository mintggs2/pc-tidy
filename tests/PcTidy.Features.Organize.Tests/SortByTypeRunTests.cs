using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Organize.Planning;
using PcTidy.Features.Organize.Rules;
using PcTidy.Hosting;
using PcTidy.TestbedGen;
using Xunit;

namespace PcTidy.Features.Organize.Tests;

/// <summary>
/// 3.3 "Sort by type" with the REAL engine on a practice PC (owner: "make sure no files are deleted or missing and …
/// make folders for categories of files"): moves only, clashes never overwrite, no empty folders, the check after the
/// run counts every file, and a failed check is put back with every file accounted for.
/// </summary>
public class SortByTypeRunTests
{
    private static readonly ExecuteOptions Real = new() { Mode = ExecutionMode.Real, ContinueWithoutRestorePoint = _ => Task.FromResult(true) };
    private static readonly DateTime Old = DateTime.UtcNow.AddDays(-2);

    private static string Put(string folder, string name, string content)
    {
        var p = Path.Combine(folder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        File.SetCreationTimeUtc(p, Old);
        File.SetLastWriteTimeUtc(p, Old);
        return p;
    }

    private static async Task<ActionPlan> PlanAsync(PcTidyHost host)
    {
        var planner = new OrganizePlanner(host.Safety, host.InUse, host.Walker, host.Settings, null, host.Locations);
        var preview = await planner.PlanAsync(new OrganizeRequest
        {
            Roots = new[] { host.Locations.Downloads },
            Settings = new OrganizeSettings { Rules = RulePresets.Find(RulePresets.ByTypeId)!.Create() },
            Culture = System.Globalization.CultureInfo.InvariantCulture,
        }, null, CancellationToken.None);
        // What the user confirms: everything ticked, Check first rows included (the strictest case for the checks).
        return PlanAssembler.Build("Organize Downloads", preview.Actions);
    }

    /// <summary>7 loose files in Downloads (6 typed, notes.dat stays) and an older photo already in Pictures: 8 files.</summary>
    private static void Sample(string dl)
    {
        Put(dl, "_clientids.gsc", "gsc");
        Put(dl, "rapidfire.gpc", "gpc");
        Put(dl, "IMG_2041.jpg", "new photo");
        Put(dl, @"Pictures\IMG_2041.jpg", "an older, different photo");   // name clash in the type folder
        Put(dl, "invoice-0451.pdf", "%PDF-1.7 invoice");
        Put(dl, "song.mp3", "ID3 song");
        Put(dl, "linux.torrent", "d8:announce");
        Put(dl, "notes.dat", "unknown type");
    }

    private static async Task InPracticePc(string name, Func<PcTidyHost, string, Task> body)
    {
        var root = Path.Combine(Sandbox.TestbedRoot, name + "-" + Guid.NewGuid().ToString("N")[..6]);
        using var host = PcTidyHost.CreateSandbox(root, SandboxProfile.Minimal);
        try
        {
            var dl = host.Locations.Downloads;
            Sample(dl);
            await body(host, dl);
        }
        finally
        {
            try { TestbedBuilder.Delete(root); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public Task Sorting_moves_only_never_overwrites_makes_no_empty_folders_counts_match_and_undo_restores_everything()
        => InPracticePc("bytype-e2e", async (host, dl) =>
        {
            var users = Path.GetDirectoryName(Path.GetDirectoryName(dl))!;
            var manifestBefore = TestbedBuilder.Manifest(users);
            var plan = await PlanAsync(host);

            // 1. Nothing is ever deleted: the plan holds moves only (no quarantine, delete, or separate folder steps).
            Assert.NotEmpty(plan.Items);
            Assert.All(plan.Items, i => Assert.Equal(ActionKind.MovePath, i.Kind));

            var checker = new OrganizeMoveCheck(host.Walker);
            Assert.True(checker.Covers(plan));
            var session = (MoveSession)checker.Before(plan, null, CancellationToken.None)!;
            Assert.Equal(8, session.FilesBefore);
            Assert.Equal(0, session.SizeOnly); // same drive: every file is proved by its identity
            var run = await host.Executor.ExecuteAsync(plan, Real, null, CancellationToken.None);
            Assert.All(run.Steps, s => Assert.Equal(StepStatus.Done, s.Status));

            // 2. Nothing goes missing: every moved file checked at its new place, same count before and after.
            var check = session.After(run, null, CancellationToken.None);
            Assert.True(check.Passed, string.Join(" ", check.Problems));
            Assert.Equal("8 files before · 8 after · 0 missing", check.Line);
            Assert.Empty(check.Notes);
            Assert.Same(check, checker.ResultFor(plan.RunId)); // what the Organize page shows

            // Clash: the existing photo is untouched and the new one arrives as "IMG_2041 (2).jpg".
            Assert.Equal("an older, different photo", File.ReadAllText(Path.Combine(dl, "Pictures", "IMG_2041.jpg")));
            Assert.Equal("new photo", File.ReadAllText(Path.Combine(dl, "Pictures", "IMG_2041 (2).jpg")));
            Assert.True(File.Exists(Path.Combine(dl, "Games", "Call of Duty", "_clientids.gsc")));
            Assert.True(File.Exists(Path.Combine(dl, "Games", "Cronus scripts", "rapidfire.gpc")));
            Assert.True(File.Exists(Path.Combine(dl, "Documents", "Invoices", "invoice-0451.pdf")));
            Assert.True(File.Exists(Path.Combine(dl, "notes.dat"))); // unknown type: left where it is

            // 3. Type folders only where a file went: no empty Fonts, Disk images, Code… folders.
            foreach (var empty in new[] { "Fonts", "Disk images", "Code", "Archives", "Program installers", "Videos" })
                Assert.False(Directory.Exists(Path.Combine(dl, empty)), empty);

            // Undo puts every file back and removes the folders it made (they're empty again): the PC is as before.
            var undo = await host.Undo.UndoRunAsync(run.RunId, null, CancellationToken.None);
            Assert.True(undo.Complete, string.Join("\n", undo.Messages));
            var diff = TestbedBuilder.Compare(manifestBefore, TestbedBuilder.Manifest(users)).Differences.ToList();
            Assert.True(diff.Count == 0, new PcTidy.TestbedGen.Manifest.ManifestDiff(diff).Format());
        });

    [Fact]
    public Task A_file_locked_during_the_run_stays_where_it_was_with_no_empty_folder_and_the_check_still_passes()
        => InPracticePc("bytype-lock", async (host, dl) =>
        {
            var plan = await PlanAsync(host);
            var session = new OrganizeMoveCheck(host.Walker).Before(plan, null, CancellationToken.None)!;

            RunRecord run;
            using (new FileStream(Path.Combine(dl, "song.mp3"), FileMode.Open, FileAccess.Read, FileShare.None)) // an app holds it
                run = await host.Executor.ExecuteAsync(plan, Real, null, CancellationToken.None);

            Assert.NotEqual(StepStatus.Done, run.Steps.Single(s => s.Item.SourcePath!.EndsWith("song.mp3", StringComparison.Ordinal)).Status);
            Assert.True(File.Exists(Path.Combine(dl, "song.mp3")));
            Assert.False(Directory.Exists(Path.Combine(dl, "Music"))); // its type folder was made and removed again
            var check = session.After(run, null, CancellationToken.None);
            Assert.True(check.Passed, string.Join(" ", check.Problems));
            Assert.Equal(8, check.FilesAfter);
        });

    [Fact]
    public Task A_damaged_copy_fails_the_check_and_the_put_back_says_exactly_where_it_is()
        => InPracticePc("bytype-changed", async (host, dl) =>
        {
            var plan = await PlanAsync(host);
            var session = new OrganizeMoveCheck(host.Walker).Before(plan, null, CancellationToken.None)!;
            var run = await host.Executor.ExecuteAsync(plan, Real, null, CancellationToken.None);

            // Forced failure: a different file of another size sits where song.mp3 arrived, with the original's date
            // (what a damaged copy looks like; an edit would have a newer date).
            var arrived = Path.Combine(dl, "Music", "song.mp3");
            File.Delete(arrived);
            File.WriteAllText(arrived, "tampered!");
            File.SetLastWriteTimeUtc(arrived, Old);
            var check = session.After(run, null, CancellationToken.None);
            Assert.False(check.Passed);
            Assert.Equal(0, check.Missing);
            Assert.Equal(1, check.Changed);
            Assert.Equal("8 files before · 8 after · 0 missing · 1 changed", check.Line);
            Assert.Contains(check.Problems, p => p.Contains("song.mp3", StringComparison.Ordinal));

            // What the shell does then: roll the whole run back with its journal, then check again.
            var report = await host.Undo.RollbackAsync(run.RunId, null, CancellationToken.None);
            var back = session.AfterPutBack(run, report, CancellationToken.None);
            Assert.True(back.PutBack);
            Assert.True(back.AllAccountedFor);
            Assert.True(File.Exists(Path.Combine(dl, "_clientids.gsc")));
            Assert.True(File.Exists(Path.Combine(dl, "rapidfire.gpc")));
            Assert.True(File.Exists(Path.Combine(dl, "invoice-0451.pdf")));
            Assert.Equal("new photo", File.ReadAllText(Path.Combine(dl, "IMG_2041.jpg")));
            Assert.Equal("an older, different photo", File.ReadAllText(Path.Combine(dl, "Pictures", "IMG_2041.jpg")));
            // Undo never moves a file that isn't the one it moved: it stays at the new place, and the check says so.
            Assert.True(File.Exists(arrived));
            Assert.False(File.Exists(Path.Combine(dl, "song.mp3")));
            Assert.Contains(back.Problems, p => p.Contains("song.mp3 is still at", StringComparison.Ordinal));
            Assert.StartsWith("5 put back · 1 left at the new place", back.Line);

            var (heading, text) = MoveCheckText.Failure(check, back, null);
            Assert.Contains("put back where it could be", heading);
            Assert.Contains("Every file is at its old place or its new one.", text);
            Assert.DoesNotContain("deleted", text);
        });

    [Fact]
    public Task A_file_edited_at_its_new_place_after_the_move_is_a_note_not_a_failure()
        => InPracticePc("bytype-edited", async (host, dl) =>
        {
            var plan = await PlanAsync(host);
            var session = new OrganizeMoveCheck(host.Walker).Before(plan, null, CancellationToken.None)!;
            var run = await host.Executor.ExecuteAsync(plan, Real, null, CancellationToken.None);
            File.WriteAllText(Path.Combine(dl, "Documents", "Invoices", "invoice-0451.pdf"), "%PDF-1.7 invoice, saved again"); // a newer date

            var check = session.After(run, null, CancellationToken.None);
            Assert.True(check.Passed, string.Join(" ", check.Problems));
            Assert.Contains(check.Notes, n => n.Contains("invoice-0451.pdf", StringComparison.Ordinal) && n.Contains("after the move", StringComparison.Ordinal));
        });

    [Fact]
    public Task A_moved_file_that_disappears_is_counted_missing_and_the_put_back_doesnt_claim_nothing_was_lost()
        => InPracticePc("bytype-gone", async (host, dl) =>
        {
            var plan = await PlanAsync(host);
            var session = new OrganizeMoveCheck(host.Walker).Before(plan, null, CancellationToken.None)!;
            var run = await host.Executor.ExecuteAsync(plan, Real, null, CancellationToken.None);

            File.Delete(Path.Combine(dl, "Torrents", "linux.torrent")); // something else removes it right after the move
            var check = session.After(run, null, CancellationToken.None);
            Assert.False(check.Passed);
            Assert.Equal(1, check.Missing);
            Assert.Equal("8 files before · 7 after · 1 missing", check.Line);

            var report = await host.Undo.RollbackAsync(run.RunId, null, CancellationToken.None);
            var back = session.AfterPutBack(run, report, CancellationToken.None);
            Assert.False(back.AllAccountedFor);
            Assert.Contains(back.Problems, p => p.Contains("linux.torrent", StringComparison.Ordinal) && p.Contains("wasn't found", StringComparison.Ordinal));
            Assert.True(File.Exists(Path.Combine(dl, "song.mp3"))); // everything else is back

            var (_, text) = MoveCheckText.Failure(check, back, null);
            Assert.DoesNotContain("deleted", text);
            Assert.Contains("couldn't find", text);
        });

    [Fact]
    public Task A_planned_file_removed_before_PC_Tidy_got_to_it_is_a_note_and_the_sort_stands()
        => InPracticePc("bytype-vanished", async (host, dl) =>
        {
            var plan = await PlanAsync(host);
            var session = new OrganizeMoveCheck(host.Walker).Before(plan, null, CancellationToken.None)!;
            File.Delete(Path.Combine(dl, "linux.torrent")); // the user deletes it while PC Tidy is noting the files
            var run = await host.Executor.ExecuteAsync(plan, Real, null, CancellationToken.None);
            Assert.Equal(StepStatus.Skipped, run.Steps.Single(s => s.Item.SourcePath!.EndsWith("linux.torrent", StringComparison.Ordinal)).Status);

            var check = session.After(run, null, CancellationToken.None);
            Assert.True(check.Passed, string.Join(" ", check.Problems));
            Assert.Equal("8 files before · 7 after · 0 missing", check.Line);
            Assert.Contains(check.Notes, n => n.Contains("linux.torrent", StringComparison.Ordinal) && n.Contains("before PC Tidy got to", StringComparison.Ordinal));
        });

    [Fact]
    public Task Files_another_app_removes_or_adds_before_the_check_are_named_but_arent_PC_Tidys_failure()
        => InPracticePc("bytype-other", async (host, dl) =>
        {
            var plan = await PlanAsync(host);
            var session = new OrganizeMoveCheck(host.Walker).Before(plan, null, CancellationToken.None)!;
            var run = await host.Executor.ExecuteAsync(plan, Real, null, CancellationToken.None);
            File.Delete(Path.Combine(dl, "notes.dat")); // not part of the sort
            Put(dl, "new-download.zip", "PK");          // arrives meanwhile

            var check = session.After(run, null, CancellationToken.None);
            Assert.True(check.Passed, string.Join(" ", check.Problems));
            Assert.Equal("8 files before · 8 after · 0 missing", check.Line);
            Assert.Contains(check.Notes, n => n.Contains("notes.dat", StringComparison.Ordinal) && n.Contains("didn't move", StringComparison.Ordinal));
            Assert.Contains(check.Notes, n => n.Contains("new-download.zip", StringComparison.Ordinal));
        });

    [Fact]
    public Task Stopping_the_check_never_fails_the_run()
        => InPracticePc("bytype-stop", async (host, dl) =>
        {
            var plan = await PlanAsync(host);
            var session = new OrganizeMoveCheck(host.Walker).Before(plan, null, CancellationToken.None)!;
            var run = await host.Executor.ExecuteAsync(plan, Real, null, CancellationToken.None);
            using var stopped = new CancellationTokenSource();
            stopped.Cancel();

            var check = session.After(run, null, stopped.Token);
            Assert.True(check.Passed);
            Assert.True(check.Stopped);
            Assert.StartsWith("Check stopped after 0 of 6 moved files", check.Line);
        });

    [Fact]
    public void A_roll_back_that_couldnt_run_is_said_plainly()
    {
        var failed = new MoveCheckResult("8 files before · 7 after · 1 missing", false, new[] { "linux.torrent isn't at its new place." }, Array.Empty<string>()) { Missing = 1 };
        var (heading, text) = MoveCheckText.Failure(failed, null, "The run is locked.");
        Assert.Contains("couldn't be put back", heading);
        Assert.Contains("PC Tidy couldn't put the run back: The run is locked.", text);
        Assert.Contains("Open History to undo this run.", text);
        Assert.DoesNotContain("deleted", text);
    }

    [Fact]
    public void A_plan_without_Organize_moves_isnt_checked()
    {
        var plan = new ActionPlan
        {
            FeatureId = "cleanup", Title = "Clean",
            Items = new[] { new PlanItem { Kind = ActionKind.QuarantinePath, FeatureId = "cleanup", Description = "x", SourcePath = @"C:\nowhere\a.tmp" } },
        };
        var check = new OrganizeMoveCheck(new ManagedFileWalker());
        Assert.False(check.Covers(plan));
        Assert.Null(check.Before(plan, null, CancellationToken.None));
    }
}
