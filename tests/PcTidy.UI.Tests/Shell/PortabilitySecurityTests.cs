using PcTidy.App.Portability;
using PcTidy.App.Shell;
using PcTidy.Core;
using PcTidy.Features.Boost;
using PcTidy.Features.Organize;
using PcTidy.Features.Organize.Rules;
using Xunit;

namespace PcTidy.UI.Tests.Shell;

/// <summary>
/// v3 safety review fixes for export/import and the data folder: EXP-1 (every section setting its own row; anything that
/// acts on its own starts unticked and flagged), EXP-2 (the update feed address), EXP-3 (no user name, data folder or
/// Organize ledger in an export) and UNI-1 (a picked folder that holds other things gets its own "PC Tidy Data" folder).
/// </summary>
public class PortabilitySecurityTests
{
    private static AppSettings Settings(string dataRoot) => new() { DataRoot = dataRoot };

    private static IReadOnlyList<ImportChange> Compare(AppSettings mine, AppSettings theirs, string root)
        => ImportPreview.Compare(mine, new LoadedPackage(new PackageManifest(), theirs, null), new[] { Path.GetPathRoot(root)! }, learningExistsHere: false);

    [Fact]
    public void An_import_never_switches_on_watch_mode_automatic_rules_or_Boost_without_a_tick()
    {
        using var folder = new ShellTestFolder("exp1");
        var data = folder.Folder("Data");
        var watched = folder.Folder("Documents");
        var mine = Settings(data);
        var theirs = Settings(data);
        var org = new OrganizeSettings();
        org.Watch.Enabled = true;
        org.Watch.Folders.Add(watched);
        org.Rules.Add(new OrganizeRule
        {
            Id = "evil", Name = "Tidy everything", RunAutomatically = true, AllowAutomaticQuarantine = true,
            Action = new RuleAction { Kind = RuleActionKind.Quarantine },
        });
        org.Rules[0].RunAutomatically = true; // an existing built-in rule switched to automatic
        org.UseDateFolders = true;           // harmless
        org.Store(theirs);
        var boost = new BoostSettings { Enabled = true };
        boost.AppsToClose.Add(new BoostAppChoice { ProcessName = "notepad", DisplayName = "Notepad" });
        theirs.SetFeature(BoostSettings.FeatureKey, boost);

        var changes = Compare(mine, theirs, folder.Root);

        ImportChange Row(string area, string name) => changes.Single(c => c.Area.StartsWith(area) && c.Name == name);
        var watchOn = Row("Organize", "Watch mode switched on");
        Assert.True(watchOn.InfoOnly); // watch mode stays this PC's choice (IMP-1)
        Assert.DoesNotContain(changes, c => c.Name == "Watch › Enabled");
        var watchFolder = Row("Organize", "Watch › Folders added");
        var newRule = Row("Organize", "Rules › \"Tidy everything\" added");
        var newRuleAuto = Row("Organize", "Rules › \"Tidy everything\" › Run automatically");
        var newRuleQuarantine = Row("Organize", "Rules › \"Tidy everything\" › Allow automatic quarantine");
        var builtInAuto = changes.Single(c => c.Area.StartsWith("Organize") && c.Name.StartsWith("Rules › \"" + org.Rules[0].Name + "\"") && c.Name.EndsWith("Run automatically"));
        var dateFolders = Row("Organize", "Use date folders");
        var boostOn = Row("Boost", "Enabled");
        var boostApp = Row("Boost", "Apps to close › \"Notepad\" added");

        foreach (var risky in new[] { watchFolder, newRuleAuto, newRuleQuarantine, builtInAuto, boostOn, boostApp })
        {
            Assert.True(risky.Automatic, risky.Name);
            Assert.False(risky.Include, risky.Name);
            Assert.Equal("Runs on its own", risky.Flag);
        }
        Assert.Equal(watched, watchFolder.After);
        Assert.True(newRule.Include);  // the rule itself is fine: it only runs when the user starts Organize
        Assert.True(dateFolders.Include);
        Assert.DoesNotContain(changes, c => c.Name.StartsWith("Ledger") || c.Name.StartsWith("Version"));

        // The defaults: the rule and the harmless option arrive, nothing runs on its own.
        ImportPreview.Apply(mine, changes);
        var after = OrganizeSettings.Load(mine);
        Assert.False(after.Watch.Enabled);
        Assert.Empty(after.Watch.Folders);
        Assert.True(after.UseDateFolders);
        var rule = after.Rules.Single(r => r.Id == "evil");
        Assert.False(rule.RunAutomatically);
        Assert.False(rule.AllowAutomaticQuarantine);
        Assert.False(after.Rules[0].RunAutomatically);
        Assert.False(BoostSettings.Load(mine).Enabled);
        Assert.Empty(BoostSettings.Load(mine).AppsToClose);

        // Ticked on purpose, they do apply.
        newRuleAuto.Include = true;
        watchOn.Include = true;
        ImportPreview.Apply(mine, changes);
        after = OrganizeSettings.Load(mine);
        Assert.False(after.Watch.Enabled); // not importable, even with its row ticked
        Assert.True(after.Rules.Single(r => r.Id == "evil").RunAutomatically);
        Assert.False(after.Rules.Single(r => r.Id == "evil").AllowAutomaticQuarantine);
    }

    /// <summary>The file's organize section with the given extra rules (raw JSON, so ids can be missing or repeated).</summary>
    private static void StoreRules(AppSettings theirs, params string[] rawRules)
    {
        var org = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(new OrganizeSettings { Watch = { Enabled = true } }, AppSettings.Json))!.AsObject();
        var rules = org["Rules"]!.AsArray();
        foreach (var r in rawRules) rules.Add(System.Text.Json.Nodes.JsonNode.Parse(r));
        theirs.Features[OrganizeSettings.FeatureKey] = System.Text.Json.JsonSerializer.SerializeToElement(org, AppSettings.Json);
    }

    private const string AutoQuarantineRule = "\"Name\":\"Tidy\",\"RunAutomatically\":true,\"AllowAutomaticQuarantine\":true,\"Action\":{\"Kind\":\"Quarantine\"}";

    /// <summary>
    /// IMP-1: rules that can't be told apart (the same Id and Name, or no Id and the same Name) are never imported as ONE
    /// pre-ticked "whole list" row: each becomes its own added rule with a fresh id, its automatic switches off, and a
    /// flagged, unticked row per switch. Watch mode stays this PC's choice.
    /// </summary>
    [Theory]
    [InlineData("\"Id\":\"dup\",")]   // the same Id and Name twice
    [InlineData("")]                  // no Id at all, the same Name twice
    [InlineData("\"Id\":\"\",")]      // an empty Id
    public void Rules_without_a_unique_id_arrive_one_by_one_with_their_automatic_switches_off(string idPart)
    {
        using var folder = new ShellTestFolder("imp1");
        var data = folder.Folder("Data");
        var mine = Settings(data);
        var theirs = Settings(data);
        StoreRules(theirs, "{" + idPart + AutoQuarantineRule + "}", "{" + idPart + AutoQuarantineRule + "}");

        var changes = Compare(mine, theirs, folder.Root);

        Assert.DoesNotContain(changes, c => c.Area.StartsWith("Organize") && c.Name == "Rules");
        var added = changes.Where(c => c.Name == "Rules › \"Tidy\" added").ToList();
        Assert.Equal(2, added.Count);
        Assert.All(added, a => Assert.True(a.Include));
        var switches = changes.Where(c => c.Name.StartsWith("Rules › \"Tidy\" › ")).ToList();
        Assert.Equal(4, switches.Count);
        Assert.All(switches, s => { Assert.True(s.Automatic, s.Name); Assert.False(s.Include, s.Name); });
        Assert.True(changes.Single(c => c.Name == "Watch mode switched on").InfoOnly);

        // The defaults: both rules arrive with their own ids, nothing runs on its own, watch mode stays off.
        ImportPreview.Apply(mine, changes);
        var after = OrganizeSettings.Load(mine);
        var tidy = after.Rules.Where(r => r.Name == "Tidy").ToList();
        Assert.Equal(2, tidy.Count);
        Assert.NotEqual(tidy[0].Id, tidy[1].Id);
        Assert.All(tidy, r => { Assert.False(r.RunAutomatically); Assert.False(r.AllowAutomaticQuarantine); });
        Assert.False(after.Watch.Enabled);
        Assert.Equal(after.Rules.Count, after.Rules.Select(r => r.Id).Distinct().Count());

        // One switch ticked on purpose applies to that one rule only.
        switches.First(s => s.Name.EndsWith("Run automatically")).Include = true;
        var again = Settings(data);
        ImportPreview.Apply(again, changes);
        var tidyAgain = OrganizeSettings.Load(again).Rules.Where(r => r.Name == "Tidy").ToList();
        Assert.Equal(1, tidyAgain.Count(r => r.RunAutomatically));
        Assert.All(tidyAgain, r => Assert.False(r.AllowAutomaticQuarantine));
    }

    [Fact]
    public void A_list_of_any_section_without_a_unique_key_is_never_one_whole_list_row()
    {
        using var folder = new ShellTestFolder("imp1b");
        var data = folder.Folder("Data");
        var mine = Settings(data);
        var theirs = Settings(data);
        theirs.Features["custom"] = System.Text.Json.JsonDocument.Parse(
            "{\"Things\":[{\"Name\":\"A\",\"RunAutomatically\":true,\"Inner\":{\"AutoStart\":true}},{\"Name\":\"A\",\"RunAutomatically\":true}]}").RootElement.Clone();

        var changes = Compare(mine, theirs, folder.Root);

        Assert.DoesNotContain(changes, c => c.Name == "Things");
        Assert.Equal(2, changes.Count(c => c.Name == "Things › \"A\" added" && c.Include));
        Assert.Equal(2, changes.Count(c => c.Name == "Things › \"A\" › Run automatically" && c.Automatic && !c.Include));
        ImportPreview.Apply(mine, changes);
        var json = mine.Features["custom"].GetRawText();
        Assert.DoesNotContain("\"RunAutomatically\":true", json.Replace(" ", ""));
        Assert.DoesNotContain("\"AutoStart\":true", json.Replace(" ", ""));
        Assert.Equal(2, mine.Features["custom"].GetProperty("Things").GetArrayLength());
    }

    [Fact]
    public void Loading_organize_settings_gives_repeated_or_missing_rule_ids_fresh_ones()
    {
        var s = new AppSettings();
        StoreRules(s, "{\"Id\":\"dup\"," + AutoQuarantineRule + "}", "{\"Id\":\"dup\"," + AutoQuarantineRule + "}", "{\"Id\":null," + AutoQuarantineRule + "}");
        var rules = OrganizeSettings.Load(s).Rules;
        Assert.Equal(rules.Count, rules.Select(r => r.Id).Distinct().Count());
        Assert.All(rules, r => Assert.False(string.IsNullOrEmpty(r.Id)));
        Assert.Contains(rules, r => r.Id == "dup");
    }

    [Fact]
    public void An_imported_file_cant_point_the_updater_anywhere_else()
    {
        using var folder = new ShellTestFolder("exp2");
        var data = folder.Folder("Data");
        var mine = Settings(data);
        // This PC still has the old custom address stored (from before 3.1.1), and the file brings another one.
        mine.Features[ShellPrefs.Key] = System.Text.Json.JsonSerializer.SerializeToElement(new { UpdateFeedUrl = "https://old.example.test/feed.json" });
        var theirs = Settings(data);
        theirs.Features[ShellPrefs.Key] = System.Text.Json.JsonSerializer.SerializeToElement(new { UpdateFeedUrl = "https://updates.example.test/feed.json", LowDiskAlerts = false, AutoUpdateCheck = false });

        var changes = Compare(mine, theirs, folder.Root);

        Assert.DoesNotContain(changes, c => c.Name.Contains("feed", StringComparison.OrdinalIgnoreCase) || c.After.Contains("example.test"));
        Assert.True(changes.Single(c => c.Name == "Low disk alerts").Include);
        foreach (var c in changes) c.Include = c.CanInclude;
        ImportPreview.Apply(mine, changes);
        Assert.DoesNotContain("updates.example.test", mine.Features[ShellPrefs.Key].GetRawText());
        Assert.False(ShellPrefs.Get(mine).LowDiskAlerts);
        Assert.False(ShellPrefs.Get(mine).AutoUpdateCheck); // switching the check off is fine; the address stays GitHub's
        Assert.Equal("https://api.github.com/repos/mintggs2/pc-tidy/releases/latest", PcTidy.App.Updates.ReleaseSource.LatestApi.AbsoluteUri);
    }

    [Fact]
    public void An_export_has_no_user_name_no_data_folder_and_no_list_of_sorted_files()
    {
        using var folder = new ShellTestFolder("exp3");
        var profile = folder.Folder(@"Users\someone");
        var data = Path.Combine(profile, @"AppData\Local\PC Tidy");
        var s = Settings(data);
        s.ExtraProtectedFolders.Add(Path.Combine(profile, "Projects"));
        var org = new OrganizeSettings();
        org.Ledger.Add(new LedgerEntry { Path = Path.Combine(profile, @"Downloads\tax-return-2025.pdf"), RuleId = "x", Size = 1, Utc = DateTime.UtcNow });
        org.Store(s);

        var (json, folderPaths) = SettingsPackage.ExportJson(s, profile);

        Assert.DoesNotContain("someone", json);
        Assert.DoesNotContain("tax-return", json);
        Assert.DoesNotContain("\"DataRoot\"", json);
        Assert.DoesNotContain("\"Ledger\"", json);
        Assert.Contains(@"%USERPROFILE%\\Projects", json);
        Assert.Equal(1, folderPaths);

        // Round trip: the new PC's own profile comes back in.
        var file = Path.Combine(folder.Root, "x.pctidy");
        using (var fs = File.Create(file)) SettingsPackage.Write(fs, s, new[] { @"C:\" }, "1.0.0", null, DateTime.UtcNow, profile);
        var back = SettingsPackage.Read(file);
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Projects");
        Assert.Equal(new[] { expected }, back.Settings.ExtraProtectedFolders);
        Assert.Empty(OrganizeSettings.Load(back.Settings).Ledger);
    }

    [Fact]
    public void A_picked_folder_with_other_things_in_it_gets_its_own_PC_Tidy_Data_folder_with_a_marker()
    {
        using var folder = new ShellTestFolder("uni1");
        var defaultRoot = folder.Folder(@"Local\PC Tidy");
        var empty = folder.Folder("Empty");
        var busy = folder.Folder("Work");
        folder.File(@"Work\report.docx", "mine");

        Assert.Equal(PathUtil.Normalize(empty), DataFolderChoice.Resolve(empty, defaultRoot));
        Assert.Equal(PathUtil.Normalize(Path.Combine(folder.Root, "New")), DataFolderChoice.Resolve(Path.Combine(folder.Root, "New"), defaultRoot));
        Assert.Equal(PathUtil.Normalize(defaultRoot), DataFolderChoice.Resolve(defaultRoot, defaultRoot));
        var own = DataFolderChoice.Resolve(busy, defaultRoot);
        Assert.Equal(Path.Combine(PathUtil.Normalize(busy), DataFolderChoice.SubfolderName), own);

        DataFolderChoice.Claim(own, defaultRoot);
        var id = DataFolderChoice.ReadMarkerId(own);
        Assert.NotNull(id);
        Assert.Equal(id, File.ReadAllText(Path.Combine(defaultRoot, DataFolderChoice.IdFile)));
        // Picking it again (or after a reinstall) keeps it and its id.
        File.WriteAllText(Path.Combine(own, "settings.json"), "{}");
        Assert.Equal(own, DataFolderChoice.Resolve(own, defaultRoot));
        DataFolderChoice.Claim(own, defaultRoot);
        Assert.Equal(id, DataFolderChoice.ReadMarkerId(own));
        // The default folder needs no marker.
        DataFolderChoice.Claim(defaultRoot, defaultRoot);
        Assert.False(File.Exists(Path.Combine(defaultRoot, DataFolderChoice.MarkerFile)));
    }
}
