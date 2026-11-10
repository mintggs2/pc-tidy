using PcTidy.Features.Duplicates.Keep;
using PcTidy.Features.Duplicates.Model;
using PcTidy.Features.Duplicates.Scanning;

namespace PcTidy.Features.Duplicates.Tests;

/// <summary>The Plan §8.3 keep rule, step by step, on hand-made copies.</summary>
public sealed class KeepRuleTests
{
    private static readonly DateTime Old = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime New = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static DuplicateCopy C(string path, LocationClass loc, DateTime? write = null, bool reference = false, string? blocked = null)
        => new()
        {
            Path = path, Size = 1000, LastWriteUtc = write ?? New, Location = loc,
            LocationLabel = loc switch { LocationClass.Library => "your Pictures folder", LocationClass.DownloadsTempOrBackup => "Downloads", _ => "a folder" },
            InReferenceFolder = reference, HasCopyName = ScanScope.HasCopyName(path), RemovalBlockedReason = blocked,
        };

    private static DuplicateGroup Decide(params DuplicateCopy[] copies) => KeepRules.Decide("g", "HASH", 1000, copies);

    [Fact]
    public void Step1_reference_folder_copy_is_kept_even_if_everything_else_prefers_another()
    {
        var g = Decide(C(@"C:\P\a.jpg", LocationClass.Library, Old), C(@"E:\Archive\a (1).jpg", LocationClass.DownloadsTempOrBackup, New, reference: true));
        Assert.Equal(@"E:\Archive\a (1).jpg", g.KeptPath);
        Assert.Contains("reference folder", g.KeepReason);
        Assert.Empty(g.AutoTicked);
    }

    [Fact]
    public void Step1_protected_copy_is_kept()
    {
        var g = Decide(C(@"C:\Other\a.jpg", LocationClass.Other, Old), C(@"C:\Cloud\a.jpg", LocationClass.Other, New, blocked: "asks first"));
        Assert.Equal(@"C:\Cloud\a.jpg", g.KeptPath);
        Assert.Contains("without your OK", g.KeepReason);
    }

    [Fact]
    public void Step2_library_beats_downloads_and_the_downloads_copy_is_the_only_tick()
    {
        var g = Decide(C(@"C:\DL\a.jpg", LocationClass.DownloadsTempOrBackup, Old), C(@"C:\Pics\a (1).jpg", LocationClass.Library, New));
        Assert.Equal(@"C:\Pics\a (1).jpg", g.KeptPath);
        Assert.Equal(new[] { @"C:\DL\a.jpg" }, g.AutoTicked);
        Assert.Contains("Pictures", g.KeepReason);
    }

    [Fact]
    public void Step2_other_beats_downloads_but_nothing_is_ticked_without_a_library_copy()
    {
        var g = Decide(C(@"C:\DL\a.jpg", LocationClass.DownloadsTempOrBackup, Old), C(@"D:\Photos\a.jpg", LocationClass.Other, New));
        Assert.Equal(@"D:\Photos\a.jpg", g.KeptPath);
        Assert.Empty(g.AutoTicked);
    }

    [Fact]
    public void Tie_at_step2_ticks_nothing()
    {
        var both = Decide(C(@"C:\DL\a.jpg", LocationClass.DownloadsTempOrBackup, Old), C(@"C:\DL\a - Copy.jpg", LocationClass.DownloadsTempOrBackup, New));
        Assert.Empty(both.AutoTicked);
        var libs = Decide(C(@"C:\Pics\a.jpg", LocationClass.Library, Old), C(@"C:\Docs\a (1).jpg", LocationClass.Library, New));
        Assert.Empty(libs.AutoTicked);
    }

    [Fact]
    public void Step3_clean_name_beats_copy_names()
    {
        var g = Decide(C(@"C:\P\a - Copy.jpg", LocationClass.Library, Old), C(@"C:\P\Copy of a.jpg", LocationClass.Library, Old),
            C(@"C:\P\a (2).jpg", LocationClass.Library, Old), C(@"C:\P\deeper\folder\a.jpg", LocationClass.Library, New));
        Assert.Equal(@"C:\P\deeper\folder\a.jpg", g.KeptPath);
        Assert.Contains("original name", g.KeepReason);
    }

    [Fact]
    public void Step4_oldest_modified_then_step5_shortest_path()
    {
        var g = Decide(C(@"C:\P\x\a.jpg", LocationClass.Library, New), C(@"C:\P\long folder\a.jpg", LocationClass.Library, Old));
        Assert.Equal(@"C:\P\long folder\a.jpg", g.KeptPath);
        Assert.Contains("oldest", g.KeepReason);
        var h = Decide(C(@"C:\P\long folder\a.jpg", LocationClass.Library, Old), C(@"C:\P\x\a.jpg", LocationClass.Library, Old));
        Assert.Equal(@"C:\P\x\a.jpg", h.KeptPath);
        Assert.Contains("shortest path", h.KeepReason);
    }

    [Fact]
    public void Always_keeps_one_even_when_all_copies_are_equal()
    {
        var g = Decide(C(@"C:\P\b.jpg", LocationClass.Library, Old), C(@"C:\Q\b.jpg", LocationClass.Library, Old));
        Assert.Single(g.Copies, c => c.Path == g.KeptPath);
        Assert.Contains("always kept", g.KeepReason);
        Assert.Empty(g.AutoTicked);
    }

    [Fact]
    public void Changing_the_kept_copy_reruns_the_rules_around_the_choice()
    {
        var g = Decide(C(@"C:\DL\a.jpg", LocationClass.DownloadsTempOrBackup, Old), C(@"C:\Pics\a.jpg", LocationClass.Library, New));
        var mine = KeepRules.WithKeeper(g, @"C:\DL\a.jpg");
        Assert.Equal(@"C:\DL\a.jpg", mine.KeptPath);
        Assert.True(mine.KeptByUser);
        Assert.Equal("You chose to keep this copy.", mine.KeepReason);
        Assert.Empty(mine.AutoTicked);                       // never ticks a library copy for you
        Assert.Throws<ArgumentException>(() => KeepRules.WithKeeper(g, @"C:\Nope\a.jpg"));
    }

    [Theory]
    [InlineData("Report - Copy.docx", true)]
    [InlineData("Report - Copy (2).docx", true)]
    [InlineData("IMG_1234 (1).jpg", true)]
    [InlineData("Copy of notes.txt", true)]
    [InlineData("notes copy.txt", true)]
    [InlineData("Report final.docx", false)]
    [InlineData("Copyright notice.txt", false)]
    [InlineData("Track (Live).mp3", false)]
    public void Copy_names_are_recognised(string name, bool isCopy)
        => Assert.Equal(isCopy, ScanScope.HasCopyName(@"C:\x\" + name));
}
