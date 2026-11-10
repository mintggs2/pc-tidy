using System.Globalization;
using System.Text;
using PcTidy.Core.Model;
using PcTidy.Features.Organize.Detection;
using PcTidy.Features.Organize.Planning;
using PcTidy.Features.Organize.Rules;
using Xunit;

namespace PcTidy.Features.Organize.Tests;

public class DateAndLoopTests
{
    /// <summary>A tiny JPEG with an EXIF DateTimeOriginal (big- or little-endian TIFF block).</summary>
    public static byte[] JpegWithExif(string dateTaken, bool littleEndian = false)
    {
        var tiff = new List<byte>();
        void U16(int v) { var b = new[] { (byte)(v >> 8), (byte)v }; if (littleEndian) Array.Reverse(b); tiff.AddRange(b); }
        void U32(int v) { var b = new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v }; if (littleEndian) Array.Reverse(b); tiff.AddRange(b); }
        tiff.AddRange(littleEndian ? "II"u8.ToArray() : "MM"u8.ToArray());
        U16(42); U32(8);
        // IFD0 at 8: one entry (ExifIFD pointer) → Exif IFD at 26.
        U16(1); U16(0x8769); U16(4); U32(1); U32(26); U32(0);
        // Exif IFD at 26: DateTimeOriginal, ASCII, 20 bytes at 44.
        U16(1); U16(0x9003); U16(2); U32(20); U32(44); U32(0);
        tiff.AddRange(Encoding.ASCII.GetBytes(dateTaken + "\0"));

        var app1 = "Exif\0\0"u8.ToArray().Concat(tiff).ToArray();
        var len = app1.Length + 2;
        return new byte[] { 0xFF, 0xD8, 0xFF, 0xE1, (byte)(len >> 8), (byte)len }
            .Concat(app1)
            .Concat(new byte[] { 0xFF, 0xDA, 0x00, 0x02, 0xFF, 0xD9 })
            .ToArray();
    }

    [Fact]
    public void Exif_parser_reads_date_taken_and_survives_garbage()
    {
        Assert.Equal(new DateTime(2019, 7, 4, 10, 20, 30), ExifReader.Parse(JpegWithExif("2019:07:04 10:20:30")));
        Assert.Equal(new DateTime(2021, 12, 31, 23, 59, 58), ExifReader.Parse(JpegWithExif("2021:12:31 23:59:58", littleEndian: true)));
        Assert.Null(ExifReader.Parse(JpegWithExif("0000:00:00 00:00:00")));
        Assert.Null(ExifReader.Parse(new byte[] { 0xFF, 0xD8, 0xFF, 0xE1, 0xFF, 0xFF, 1, 2, 3 })); // length past the end
        Assert.Null(ExifReader.Parse(Encoding.ASCII.GetBytes("not a jpeg at all")));
        var tiffOnly = JpegWithExif("2018:01:02 03:04:05").Skip(12).ToArray(); // raw TIFF block (like a DNG/NEF header)
        Assert.Equal(new DateTime(2018, 1, 2, 3, 4, 5), ExifReader.Parse(tiffOnly[..^6]));

        var random = new byte[5000];
        var rng = new Random(7);
        for (int i = 0; i < 200; i++) { rng.NextBytes(random); random[0] = 0xFF; random[1] = 0xD8; _ = ExifReader.Parse(random); }
    }

    [Fact]
    public async Task Year_month_folders_use_iso_names_from_the_modified_date()
    {
        using var sb = new Sandbox();
        var path = sb.File("beach.jpg");
        var local = new DateTime(2025, 3, 15, 12, 0, 0, DateTimeKind.Local);
        System.IO.File.SetLastWriteTime(path, local);
        System.IO.File.SetCreationTime(path, local);

        var p = await sb.PlanAsync(new OrganizeSettings { UseDateFolders = true });
        Assert.Equal(@"Pictures\2025\2025-03", p.ActionFor("beach.jpg").RelativeDestFolder());
    }

    [Fact]
    public async Task Deep_mode_uses_the_exif_date_taken_quick_mode_does_not()
    {
        using var sb = new Sandbox();
        var path = sb.Bytes("IMG_0001.jpg", JpegWithExif("2019:07:04 10:20:30"));
        var modified = new DateTime(2024, 2, 10, 12, 0, 0, DateTimeKind.Local);
        System.IO.File.SetLastWriteTime(path, modified);
        System.IO.File.SetCreationTime(path, modified);
        var settings = new OrganizeSettings { UseDateFolders = true };

        Assert.Equal(@"Pictures\2019\2019-07", (await sb.PlanAsync(settings, ScanDepth.Deep)).ActionFor("IMG_0001.jpg").RelativeDestFolder());
        Assert.Equal(@"Pictures\2024\2024-02", (await sb.PlanAsync(settings, ScanDepth.Quick)).ActionFor("IMG_0001.jpg").RelativeDestFolder());
    }

    [Fact]
    public void Tokens_are_invariant_except_month_names_from_the_culture()
    {
        var d = new DateTime(2026, 11, 3);
        Assert.Equal(@"2026\2026-11", DestinationResolver.ExpandTokens(DestinationResolver.DateSuffix, d, CultureInfo.InvariantCulture));
        Assert.Equal("2026-11-03 03 11", DestinationResolver.ExpandTokens("{date} {day} {month}", d, new CultureInfo("ar-SA")));
        Assert.Equal("November", DestinationResolver.ExpandTokens("{monthName}", d, CultureInfo.InvariantCulture));
        Assert.Equal("Novembre", DestinationResolver.ExpandTokens("{monthName}", d, new CultureInfo("fr-FR")));
        Assert.Equal("{name}", DestinationResolver.ExpandTokens("{name}", d));
    }

    [Fact]
    public void Relative_destinations_cannot_escape_the_sorted_folder()
    {
        var root = @"C:\sandbox\Downloads";
        Assert.Null(DestinationResolver.ResolveFolder(new RuleAction { Destination = @"..\Elsewhere" }, root, DateTime.Today, false));
        Assert.Null(DestinationResolver.ResolveFolder(new RuleAction { Destination = "" }, root, DateTime.Today, false));
        Assert.Equal(@"C:\sandbox\Downloads\Docs\CON_", DestinationResolver.ResolveFolder(new RuleAction { Destination = @"Docs\CON" }, root, DateTime.Today, false));
    }

    [Fact]
    public void Rules_that_would_loop_are_blocked()
    {
        using var sb = new Sandbox();
        var roots = new[] { sb.Downloads, sb.Locations.Desktop };
        OrganizeRule Move(string dest) => new() { Name = "r", Conditions = ConditionGroup.AllOf(RuleCondition.Ext("pdf")), Action = new RuleAction { Destination = dest } };

        Assert.Contains(RuleValidator.Validate(Move("."), roots, sb.Safety, sb.AppSettings), p => p.Blocking);
        Assert.Contains(RuleValidator.Validate(Move(sb.Locations.Desktop), roots, sb.Safety, sb.AppSettings), p => p.Blocking && p.Message.Contains("again"));
        Assert.Contains(RuleValidator.Validate(Move(sb.Downloads), roots, sb.Safety, sb.AppSettings), p => p.Blocking);
        Assert.Contains(RuleValidator.Validate(Move(@"..\Out"), roots, sb.Safety, sb.AppSettings), p => p.Blocking);
        Assert.Contains(RuleValidator.Validate(Move(System.IO.Path.Combine(sb.Locations.Documents, "X")), new[] { sb.Downloads }, sb.Safety, sb.AppSettings),
            p => p.Blocking && p.Message.Contains("OneDrive"));
        Assert.DoesNotContain(RuleValidator.Validate(Move(@"Documents\Invoices"), roots, sb.Safety, sb.AppSettings), p => p.Blocking);
        Assert.Contains(RuleValidator.Validate(new OrganizeRule { Name = "empty", Action = new RuleAction { Destination = "X" } }, roots, sb.Safety, sb.AppSettings), p => p.Blocking);
        foreach (var rule in DefaultRules.Create())
            Assert.DoesNotContain(RuleValidator.Validate(rule, roots, sb.Safety, sb.AppSettings), p => p.Blocking);
    }

    [Fact]
    public async Task A_rename_rule_that_would_rename_forever_is_paused()
    {
        using var sb = new Sandbox();
        sb.File("a.txt");
        var settings = new OrganizeSettings();
        settings.Rules.Insert(0, new OrganizeRule
        {
            Name = "Append copy", Conditions = ConditionGroup.AllOf(RuleCondition.Ext("txt")),
            Action = new RuleAction { Kind = RuleActionKind.Rename, RenamePattern = "{name} copy" },
        });
        var p = await sb.PlanAsync(settings);
        Assert.Empty(p.Actions);
        Assert.Equal(SkipKind.RuleBlocked, p.SkipFor("a.txt").Kind);
        Assert.Contains(p.Notes, n => n.Contains("renamed forever"));
    }

    [Fact]
    public async Task An_idempotent_rename_rule_runs_once()
    {
        using var sb = new Sandbox();
        sb.File("draft.txt");
        sb.File("final.txt");
        var settings = new OrganizeSettings();
        settings.Rules.Insert(0, new OrganizeRule
        {
            Name = "Prefix drafts", Conditions = ConditionGroup.AllOf(RuleCondition.Glob("draft*")),
            Action = new RuleAction { Kind = RuleActionKind.Rename, RenamePattern = "old {name}" },
        });
        var p = await sb.PlanAsync(settings);
        var rename = p.ActionFor("draft.txt");
        Assert.Equal(PlannedActionKind.Rename, rename.Kind);
        Assert.Equal(System.IO.Path.Combine(sb.Downloads, "old draft.txt"), rename.DestinationPath);
        Assert.Equal(ActionKind.RenamePath, Assert.Single(rename.Items).Kind);
    }

    [Fact]
    public async Task Files_in_the_ledger_are_never_picked_up_again()
    {
        using var sb = new Sandbox();
        var sorted = sb.File("moved-here.pdf");
        sb.File("new.pdf");
        var settings = new OrganizeSettings();
        settings.Ledger.Add(new LedgerEntry { Path = sorted, RuleId = DefaultRules.InvoicesId, Size = 1, Utc = DateTime.UtcNow });

        var p = await sb.PlanAsync(settings);
        var skip = p.SkipFor("moved-here.pdf");
        Assert.Equal(SkipKind.AlreadySorted, skip.Kind);
        Assert.Contains("already sorted", skip.Reason);
        Assert.Equal("new.pdf", Assert.Single(p.Actions).Name);
    }

    [Fact]
    public void Finished_runs_are_recorded_in_the_ledger_and_pruned_when_gone()
    {
        using var sb = new Sandbox();
        var arrived = sb.File(@"Documents\a.pdf");
        var item = new PlanItem
        {
            Kind = ActionKind.MovePath, FeatureId = OrganizeModule.FeatureId, Description = "Move",
            SourcePath = System.IO.Path.Combine(sb.Downloads, "a.pdf"), DestinationPath = arrived, ExpectedSize = 1,
            Args = new() { [OrganizeLedger.RuleIdArg] = "category.documents" },
        };
        var gone = item with { Id = "x", DestinationPath = System.IO.Path.Combine(sb.Downloads, "vanished.pdf") };
        var run = new RunRecord
        {
            RunId = "r1", FeatureId = OrganizeModule.FeatureId, Title = "t", Mode = ExecutionMode.Real,
            Steps = new[] { new StepRecord { Index = 0, Item = item, Status = StepStatus.Done }, new StepRecord { Index = 1, Item = gone, Status = StepStatus.Done } },
        };
        OrganizeLedger.RecordInto(sb.AppSettings, run, DateTime.UtcNow);

        var ledger = OrganizeSettings.Load(sb.AppSettings).Ledger;
        var entry = Assert.Single(ledger);
        Assert.Equal(arrived, entry.Path);
        Assert.Equal("category.documents", entry.RuleId);

        // Simulated runs never count.
        OrganizeLedger.RecordInto(sb.AppSettings, run with { Mode = ExecutionMode.Simulate, Steps = new[] { new StepRecord { Index = 0, Item = item with { Id = "y", DestinationPath = sb.File("z.pdf") }, Status = StepStatus.Done } } }, DateTime.UtcNow);
        Assert.Single(OrganizeSettings.Load(sb.AppSettings).Ledger);
    }
}
