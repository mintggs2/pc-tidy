using PcTidy.Core.Model;
using PcTidy.Features.Organize.Planning;
using PcTidy.Features.Organize.Rules;
using Xunit;

namespace PcTidy.Features.Organize.Tests;

public class CategoryAndRuleTests
{
    /// <summary>Plan §9 table, verbatim. "ts" is in Videos and Code: Videos comes first, so first match wins.</summary>
    public static readonly (string Category, string Extensions)[] PlanTable =
    {
        ("Pictures", "jpg jpeg png gif bmp webp heic heif avif tif tiff svg ico psd raw cr2 cr3 nef arw dng orf rw2"),
        ("Videos", "mp4 mkv mov avi wmv webm m4v mpg mpeg 3gp flv ts mts m2ts"),
        ("Music", "mp3 m4a aac flac wav ogg opus wma aiff alac mid midi"),
        ("Documents", "pdf doc docx odt rtf txt md xls xlsx xlsm ods csv ppt pptx odp epub mobi one xps oxps"),
        ("Archives", "zip 7z rar tar gz tgz bz2 xz zst cab iso img"),
        ("Installers", "msi msix msixbundle appx appxbundle"),
        ("Code", "cs py js ts jsx tsx java c cpp h hpp go rs rb php sh ps1 bat cmd json yaml yml xml toml sql ipynb html css"),
    };

    [Fact]
    public async Task Every_extension_in_the_plan_table_sorts_into_its_category()
    {
        using var sb = new Sandbox();
        var expected = new Dictionary<string, string>();
        foreach (var (category, list) in PlanTable)
            foreach (var ext in list.Split(' '))
                expected.TryAdd(ext, category); // first match wins (ts → Videos)

        foreach (var ext in expected.Keys) sb.File($"sample-{ext}.{ext.ToUpperInvariant()}");
        var preview = await sb.PlanAsync();

        foreach (var (ext, category) in expected)
        {
            var action = preview.ActionFor($"sample-{ext}.{ext.ToUpperInvariant()}");
            Assert.Equal(PlannedActionKind.Move, action.Kind);
            Assert.Equal(category, action.RelativeDestFolder());
            Assert.Equal(Badge.Safe, action.Badge);
        }
        Assert.Equal("Videos", preview.ActionFor("sample-ts.TS").Group);
        Assert.Equal(expected.Count, preview.Actions.Count);
    }

    [Fact]
    public void Category_table_matches_the_plan_exactly()
    {
        foreach (var (category, list) in PlanTable)
        {
            var cat = OrganizeCategories.Find(category)!;
            Assert.Equal(list.Split(' ').OrderBy(x => x), cat.Extensions.OrderBy(x => x));
        }
        Assert.Equal(PlanTable.Select(p => p.Category), OrganizeCategories.All.Select(c => c.Id));
    }

    [Fact]
    public async Task Unknown_types_stay_put_as_other()
    {
        using var sb = new Sandbox();
        sb.File("model.blend");
        var preview = await sb.PlanAsync();
        Assert.Empty(preview.Actions);
        Assert.Equal(SkipKind.LeaveInPlace, preview.SkipFor("model.blend").Kind);
    }

    [Fact]
    public async Task Installer_exes_go_to_installers_and_portable_exes_stay()
    {
        using var sb = new Sandbox();
        var random = new byte[4096];
        new Random(42).NextBytes(random);
        sb.Bytes("rufus-portable.exe", random);
        sb.Bytes("VSCodeUserSetup-x64.exe", random);
        sb.Bytes("Some-App-Install.exe", random);
        sb.Bytes("unins000.exe", random);
        sb.Bytes("obs-studio-31.exe", random.Concat("....Nullsoft.NSIS.exehead...."u8.ToArray()).Concat(random).ToArray());
        sb.Bytes("tool-x.exe", random.Concat("Inno Setup Setup Data (6.2.0)"u8.ToArray()).ToArray());
        sb.Bytes("bundle.exe", random.Concat(".wixburn"u8.ToArray()).ToArray());
        sb.Bytes("legacy.exe", random.Concat("InstallShield"u8.ToArray()).ToArray());

        var preview = await sb.PlanAsync();

        foreach (var name in new[] { "VSCodeUserSetup-x64.exe", "Some-App-Install.exe", "obs-studio-31.exe", "tool-x.exe", "bundle.exe", "legacy.exe" })
            Assert.Equal("Installers", preview.ActionFor(name).RelativeDestFolder());
        Assert.Equal(SkipKind.PortableApp, preview.SkipFor("rufus-portable.exe").Kind);
        Assert.Equal(SkipKind.PortableApp, preview.SkipFor("unins000.exe").Kind);
        Assert.Contains("NSIS", preview.ActionFor("obs-studio-31.exe").Why);
    }

    [Fact]
    public async Task Starter_rules_route_invoices_screenshots_old_installers_and_torrents()
    {
        using var sb = new Sandbox();
        sb.File("Invoice-2026-10.pdf");
        sb.File("acme receipt.pdf");
        sb.File("bank STATEMENT.pdf");
        sb.File("invoice-notes.txt");          // not a pdf: plain Documents
        sb.File("Screenshot 2026-11-01.png");
        sb.File("Screen Shot 2026.jpg");
        sb.File("Snip_001.png");
        sb.File("Screenshot tool.zip");        // name matches but not a picture
        sb.File("old-installer.msi", ageDays: 40);
        sb.File("new-installer.msi", ageDays: 2);
        sb.File("ubuntu.iso.torrent");

        var p = await sb.PlanAsync();

        foreach (var n in new[] { "Invoice-2026-10.pdf", "acme receipt.pdf", "bank STATEMENT.pdf" })
            Assert.Equal(@"Documents\Invoices", p.ActionFor(n).RelativeDestFolder());
        Assert.Equal("Documents", p.ActionFor("invoice-notes.txt").RelativeDestFolder());
        foreach (var n in new[] { "Screenshot 2026-11-01.png", "Screen Shot 2026.jpg", "Snip_001.png" })
            Assert.Equal(@"Pictures\Screenshots", p.ActionFor(n).RelativeDestFolder());
        Assert.Equal("Archives", p.ActionFor("Screenshot tool.zip").RelativeDestFolder());

        var old = p.ActionFor("old-installer.msi");
        Assert.Equal(PlannedActionKind.Quarantine, old.Kind);
        Assert.Equal(Badge.Check, old.Badge);
        Assert.False(old.DefaultSelected);
        Assert.Equal(ActionKind.QuarantinePath, Assert.Single(old.Items).Kind);
        Assert.Contains("extension .msi", old.Why);
        Assert.Contains("added 40 days ago", old.Why);
        Assert.Contains("Installers older than 30 days", old.Why);

        Assert.Equal("Installers", p.ActionFor("new-installer.msi").RelativeDestFolder());
        Assert.Equal(@"Archives\Torrents", p.ActionFor("ubuntu.iso.torrent").RelativeDestFolder());
    }

    [Fact]
    public async Task First_match_wins_and_reordering_changes_the_result()
    {
        using var sb = new Sandbox();
        sb.File("invoice.pdf");
        var settings = new OrganizeSettings();
        Assert.Equal(@"Documents\Invoices", (await sb.PlanAsync(settings)).ActionFor("invoice.pdf").RelativeDestFolder());

        // Move the broad Documents rule above the invoices rule: it now wins.
        var docs = settings.Rules.Single(r => r.Id == DefaultRules.CategoryRuleId("Documents"));
        settings.Rules.Remove(docs);
        settings.Rules.Insert(0, docs);
        Assert.Equal("Documents", (await sb.PlanAsync(settings)).ActionFor("invoice.pdf").RelativeDestFolder());

        // A disabled rule is passed over.
        docs.Enabled = false;
        Assert.Equal(@"Documents\Invoices", (await sb.PlanAsync(settings)).ActionFor("invoice.pdf").RelativeDestFolder());
    }

    [Fact]
    public async Task Custom_rule_conditions_and_or_size_site_and_folder()
    {
        using var sb = new Sandbox();
        var big = sb.File("big.bin", new string('x', 5000));
        sb.File("small.bin", "x");
        var gh = sb.File("from-github.zip");
        System.IO.File.WriteAllText(gh + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://codeload.github.com/x/y/zip\r\n");
        Sandbox.SetAge(gh, 1); // writing the stream touches the file
        sb.File("from-elsewhere.zip");

        var settings = new OrganizeSettings();
        settings.Rules.Insert(0, new OrganizeRule
        {
            Id = "big", Name = "Big bins",
            Conditions = ConditionGroup.AllOf(RuleCondition.Ext(".BIN"), new RuleCondition { Kind = ConditionKind.Size, MinBytes = 1000 }),
            Action = new RuleAction { Destination = "Big" },
        });
        settings.Rules.Insert(0, new OrganizeRule
        {
            Id = "gh", Name = "From GitHub",
            Conditions = new ConditionGroup
            {
                Mode = MatchMode.Any,
                Conditions = { new RuleCondition { Kind = ConditionKind.DownloadSite, Values = { "github.com" } } },
                Groups = { ConditionGroup.AllOf(RuleCondition.NameHas("never-matches")) },
            },
            Action = new RuleAction { Destination = @"Code\{year}" },
        });

        var p = await sb.PlanAsync(settings);

        Assert.Equal("Big", p.ActionFor("big.bin").RelativeDestFolder());
        Assert.Equal(SkipKind.LeaveInPlace, p.SkipFor("small.bin").Kind);
        Assert.Equal(Path.Combine("Code", System.IO.File.GetLastWriteTime(gh).Year.ToString()), p.ActionFor("from-github.zip").RelativeDestFolder());
        Assert.Contains("codeload.github.com", p.ActionFor("from-github.zip").Why);
        Assert.Equal("Archives", p.ActionFor("from-elsewhere.zip").RelativeDestFolder());
        Assert.Contains("size", p.ActionFor("big.bin").Why);
        _ = big;
    }

    [Fact]
    public async Task Source_folder_and_negated_conditions()
    {
        using var sb = new Sandbox();
        sb.File("a.pdf");
        sb.File("b.txt");
        var settings = new OrganizeSettings();
        settings.Rules.Insert(0, new OrganizeRule
        {
            Name = "Not PDFs in Downloads",
            Conditions = ConditionGroup.AllOf(
                new RuleCondition { Kind = ConditionKind.SourceFolder, Values = { sb.Downloads } },
                new RuleCondition { Kind = ConditionKind.Extension, Values = { "pdf" }, Negate = true }),
            Action = new RuleAction { Destination = "Other stuff" },
        });
        var p = await sb.PlanAsync(settings);
        Assert.Equal("Other stuff", p.ActionFor("b.txt").RelativeDestFolder());
        Assert.Equal("Documents", p.ActionFor("a.pdf").RelativeDestFolder());
        Assert.Contains("not extension .pdf", p.ActionFor("b.txt").Why);
    }

    [Fact]
    public void Regex_patterns_time_out_instead_of_hanging()
    {
        var evil = new string('a', 40) + "!";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = NamePattern.Match(evil, "^(a+)+$", PatternSyntax.Regex, TimeSpan.FromMilliseconds(50));
        Assert.True(r.TimedOut);
        Assert.False(r.IsMatch);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
        Assert.NotNull(NamePattern.Validate("([unclosed", PatternSyntax.Regex));
        Assert.Null(NamePattern.Validate("Screenshot*;Snip?", PatternSyntax.Glob));
        Assert.True(NamePattern.Match("Snip1.png", "Screenshot*;Snip?.png", PatternSyntax.Glob).IsMatch);
        Assert.False(NamePattern.Match("MySnip1.png", "Snip*", PatternSyntax.Glob).IsMatch);
    }

    [Fact]
    public async Task A_slow_user_regex_rule_counts_as_no_match_and_leaves_a_note()
    {
        using var sb = new Sandbox();
        sb.File(new string('a', 40) + "!.txt");
        var settings = new OrganizeSettings();
        settings.Rules.Insert(0, new OrganizeRule { Name = "Evil", Conditions = ConditionGroup.AllOf(RuleCondition.Regex("^(a+)+$")), Action = new RuleAction { Destination = "Evil" } });
        var p = await sb.PlanAsync(settings);
        Assert.Equal("Documents", Assert.Single(p.Actions).RelativeDestFolder());
        Assert.Contains(p.Notes, n => n.Contains("took too long"));
    }
}
