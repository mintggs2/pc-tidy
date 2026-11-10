using PcTidy.Core.Model;
using PcTidy.UI.Flow;
using Xunit;

namespace PcTidy.UI.Tests;

/// <summary>The numbers on the bottom bar, confirm dialog, simulation and Done card come from one classification.</summary>
public class CountingTests
{
    private static PlanItem Q(long size, bool admin = false, string? src = @"C:\Users\tester\AppData\Local\Temp\a.tmp") => new()
    { Kind = ActionKind.QuarantinePath, FeatureId = "cleanup", Description = "temp", ExpectedSize = size, NeedsAdmin = admin, SourcePath = src };
    private static PlanItem Move() => new() { Kind = ActionKind.MovePath, FeatureId = "organize", Description = "move" };
    private static PlanItem Folder() => new() { Kind = ActionKind.CreateDirectory, FeatureId = "organize", Description = "folder", DestinationPath = @"C:\x\Pictures" };
    private static PlanItem Startup() => new() { Kind = ActionKind.StartupItemSetEnabled, FeatureId = "speed", Description = "startup", Enable = false };
    private static PlanItem Tweak(bool system = false) => new() { Kind = ActionKind.RegistrySetValue, FeatureId = "tweaks", Description = "tweak", IsSystemChange = system };
    private static PlanItem Bin(long size) => new() { Kind = ActionKind.EmptyRecycleBin, FeatureId = "cleanup", Description = "bin", ExpectedSize = size, Reversible = false };

    private static ActionPlan Plan(params PlanItem[] items) => new() { FeatureId = "x", Title = "X", Items = items };

    [Fact]
    public void Startup_and_settings_are_separate_and_folders_are_not_counted()
    {
        var c = PlanCounts.Of(new[] { Q(100), Move(), Folder(), Startup(), Tweak(), Tweak() });
        Assert.Equal(1, c.Quarantine);
        Assert.Equal(1, c.Moves);
        Assert.Equal(1, c.Startup);
        Assert.Equal(2, c.Settings);
        Assert.Equal(5, c.Total);
    }

    [Fact]
    public void Confirm_lists_startup_and_settings_as_their_own_lines_with_the_same_counts()
    {
        var model = new ConfirmModel(new[] { Plan(Q(5), Startup(), Tweak(), Tweak()) }, null, 7);
        Assert.Contains(model.Groups, g => g.Kind == ConfirmModel.KindStartup && g.Title.Contains("1 startup item"));
        Assert.Contains(model.Groups, g => g.Kind == ConfirmModel.KindSettings && g.Title.Contains("2 settings"));
        Assert.Equal(PlanCounts.Of(model.Plans.SelectMany(p => p.Items)), model.Counts);
    }

    [Fact]
    public void Primary_button_names_the_action()
    {
        Assert.Equal("Clean 6 KB", PlanFacts.PrimaryAction(new[] { Q(3 * 1024), Q(3 * 1024) }));
        Assert.Equal("Move 2 files", PlanFacts.PrimaryAction(new[] { Move(), Move(), Folder() }));
        Assert.Equal("Apply 3 changes", PlanFacts.PrimaryAction(new[] { Startup(), Tweak(), Tweak() }));
        Assert.Equal("Apply all 3 changes", PlanFacts.PrimaryAction(new[] { Q(1), Move(), Tweak() }));
    }

    [Fact]
    public void Confirm_button_drops_permanent_bytes_until_acknowledged()
    {
        var model = new ConfirmModel(new[] { Plan(Q(1024), Bin(4096)) }, null, 7);
        Assert.True(model.HasPermanent);
        Assert.Equal("Clean 1 KB", model.PrimaryText(false));
        Assert.Equal("Clean 5 KB", model.PrimaryText(true));
    }

    [Fact]
    public void Restore_point_line_whenever_any_item_is_a_system_change()
    {
        Assert.True(new ConfirmModel(new[] { Plan(Q(1), Tweak(system: true)) }, null, 7).NeedsRestorePoint);
        Assert.False(new ConfirmModel(new[] { Plan(Q(1), Tweak()) }, null, 7).NeedsRestorePoint);
    }

    private static RunRecord Run(params StepRecord[] steps) => new() { RunId = "r", FeatureId = "cleanup", Title = "Clean", Steps = steps, Status = RunStatus.Completed };
    private static StepRecord Done(PlanItem i, int n, long bytes = 0) => new() { Index = n, Item = i, Status = StepStatus.Done, BytesAffected = bytes };
    private static StepRecord Skipped(PlanItem i, int n) => new() { Index = n, Item = i, Status = StepStatus.Skipped, Message = "In use" };

    [Fact]
    public void Same_drive_quarantine_is_not_claimed_as_freed()
    {
        var ctx = new RunSummaryContext(@"C:\Users\tester\AppData\Local\PC Tidy\Quarantine", 7);
        var s = new RunSummary(new[] { Run(Done(Q(2048), 0, 2048), Skipped(Q(1024), 1)) }, ctx);
        Assert.Equal(0, s.BytesFreed);
        Assert.Equal(2048, s.BytesHeld);
        Assert.StartsWith("2 KB moved to quarantine", s.Headline);
        Assert.NotNull(s.SpaceNote);
        Assert.Equal("1 of 2", RunSummary.OfPlanned(s.Quarantined, s.Planned.Quarantine));
        Assert.Equal(1, s.Skipped);
    }

    [Fact]
    public void Other_drive_quarantine_and_permanent_deletes_count_as_freed_using_measured_bytes()
    {
        var ctx = new RunSummaryContext(@"D:\PC Tidy\Quarantine", 7);
        // Expected 9 KB but the engine measured 2 KB: only the measured bytes are claimed.
        var s = new RunSummary(new[] { Run(Done(Q(9 * 1024), 0, 2048), Done(Bin(100), 1, 1024)) }, ctx);
        Assert.Equal(3072, s.BytesFreed);
        Assert.Equal("3 KB freed", s.Headline);
        Assert.Null(s.SpaceNote);
    }

    private static StepRecord SkippedBecause(PlanItem i, int n, string why) => new() { Index = n, Item = i, Status = StepStatus.Skipped, Message = why };

    [Fact]
    public void Freed_well_below_the_estimate_says_how_much_and_why()
    {
        const long MB = 1024 * 1024;
        var ctx = new RunSummaryContext(@"D:\PC Tidy\Quarantine", 7);
        var s = new RunSummary(new[] { Run(
            Done(Q(400 * MB), 0, 400 * MB),
            SkippedBecause(Q(150 * MB), 1, "It's in use (open in Chrome), so it was left alone."),
            SkippedBecause(Q(40 * MB), 2, "It changed since the scan, so it was left alone."),
            SkippedBecause(Q(20 * MB), 3, "It's no longer there.")) }, ctx);
        Assert.Equal(610 * MB, s.EstimatedBytes);
        Assert.True(s.FellShort);
        var line = s.ShortfallLine!;
        Assert.StartsWith("Freed 400 MB of about 610 MB.", line);
        Assert.Contains("In use or locked: 150 MB", line);
        Assert.Contains("changed since the scan: 40 MB", line);
        Assert.Contains("already gone: 20 MB", line);
    }

    [Fact]
    public void Close_to_the_estimate_says_nothing_extra()
    {
        const long MB = 1024 * 1024;
        var ctx = new RunSummaryContext(@"D:\PC Tidy\Quarantine", 7);
        // 2 % and 10 MB short: within both limits.
        var s = new RunSummary(new[] { Run(Done(Q(500 * MB), 0, 490 * MB)) }, ctx);
        Assert.False(s.FellShort);
        Assert.Null(s.ShortfallLine);
        // 60 MB short of 5 GB is only 1.2 %, but more than 50 MB: shown.
        var big = new RunSummary(new[] { Run(Done(Q(5000 * MB), 0, 4940 * MB)) }, ctx);
        Assert.True(big.FellShort);
        Assert.Contains("Smaller than when scanned", big.ShortfallLine);
    }

    [Fact]
    public void History_never_says_no_restore_point_needed_after_a_system_change()
    {
        var s = new RunSummary(new[] { Run(Done(Tweak(system: true), 0)) });
        Assert.DoesNotContain("needed", s.RestorePointLine);
        var plain = new RunSummary(new[] { Run(Done(Q(1), 0, 1)) });
        Assert.Contains("No restore point needed", plain.RestorePointLine);
    }

    [Fact]
    public void Settings_changes_count_as_changes_not_files()
    {
        var s = new RunSummary(new[] { Run(Done(Startup(), 0), Done(Tweak(), 1), Done(Folder(), 2)) });
        Assert.Equal(1, s.StartupChanged);
        Assert.Equal(1, s.SettingsChanged);
        Assert.Equal("2 changes made", s.Headline);
    }

    private static ScanItem Item(Badge badge, PlanItem? action, string? disabled = null) => new()
    { Title = "t", Reason = "r", Badge = badge, Actions = action is null ? Array.Empty<PlanItem>() : new[] { action }, DisabledReason = disabled };

    [Fact]
    public void Page_pill_follows_the_rows_not_the_module()
    {
        Assert.Equal("All Safe", FlowCopy.ResultPill(new[] { Item(Badge.Safe, Tweak()), Item(Badge.Check, null, "placebo") }, ModuleRisk.Check)!.Value.Text);
        Assert.Equal("Check each item", FlowCopy.ResultPill(new[] { Item(Badge.Check, Tweak()) }, ModuleRisk.Safe)!.Value.Text);
        Assert.Null(FlowCopy.ResultPill(new[] { Item(Badge.Check, null, "placebo") }, null));
    }

    [Fact]
    public void Preview_note_describes_what_this_scan_offers()
    {
        Assert.Contains("settings keep a copy", FlowCopy.PreviewNote(new[] { Item(Badge.Safe, Tweak()) }, 7));
        Assert.DoesNotContain("quarantine", FlowCopy.PreviewNote(new[] { Item(Badge.Safe, Tweak()) }, 7));
        Assert.Contains("quarantine for 7 days", FlowCopy.PreviewNote(new[] { Item(Badge.Safe, Q(1)) }, 7));
        Assert.Contains("for information", FlowCopy.PreviewNote(new[] { Item(Badge.Check, null) }, 7));
    }
}
