using System.Text.Json;
using System.Text.RegularExpressions;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Organize.Planning;
using PcTidy.Features.Organize.Rules;
using Xunit;

namespace PcTidy.Features.Organize.Tests;

public class ModuleAndContractTests
{
    private static string SourceRoot => Path.Combine(Path.GetDirectoryName(Sandbox.TestbedRoot)!, "..", "src", "PcTidy.Features.Organize");

    [Fact]
    public void Module_identity_matches_the_brief()
    {
        var m = new OrganizeModule();
        Assert.Equal("organize", m.Id);
        Assert.Equal("Organize", m.Title);
        Assert.Equal(NavGroup.Organize, m.Group);
        Assert.Equal("FolderArrowRight24", m.Icon);
        Assert.Equal(Release.V1, m.Release);
        var scanner = Assert.Single(m.Scanners);
        Assert.Equal("organize", scanner.FeatureId);
        Assert.True(scanner.IncludedInOptimizeAll);
        Assert.Equal(Release.V1, scanner.Release);
    }

    [Fact]
    public async Task Scanner_groups_by_category_lists_skips_and_respects_badges_and_presets()
    {
        using var sb = new Sandbox();
        sb.File("a.jpg"); sb.File("b.pdf"); sb.File("old.msi", ageDays: 45); sb.File("setup.lnk");
        var scanner = new OrganizeScanner(sb.Clock);

        var result = await scanner.ScanAsync(sb.Context(), null, CancellationToken.None);

        Assert.Equal("organize", result.FeatureId);
        Assert.Contains(result.Groups, g => g.Title == "Pictures" && g.Items.Single().DefaultSelected);
        Assert.Contains(result.Groups, g => g.Title == "Documents");
        var quarantine = result.Groups.Single(g => g.Id == "organize.quarantine");
        Assert.Equal(Badge.Check, quarantine.Badge);
        Assert.All(result.AllItems.Where(i => i.Badge != Badge.Safe), i => Assert.False(i.DefaultSelected));
        var skipped = result.Groups.Single(g => g.Id == OrganizeResultBuilder.SkippedGroupId);
        Assert.All(skipped.Items, i => { Assert.NotNull(i.DisabledReason); Assert.Empty(i.Actions); });
        Assert.Contains(skipped.Items, i => i.Title == "setup.lnk");
        Assert.All(result.AllItems, i => Assert.False(string.IsNullOrWhiteSpace(i.Reason)));

        // "Safe" preset: nothing sorted unless the user ticks it.
        var safe = await scanner.ScanAsync(sb.Context("Safe"), null, CancellationToken.None);
        Assert.DoesNotContain(safe.AllItems, i => i.DefaultSelected);

        // The standard PlanBuilder still yields a valid plan (folders first after our own assembler).
        var plan = PlanAssembler.FromScanItems("t", result.AllItems.Where(i => i.DefaultSelected));
        Assert.Equal(ActionKind.CreateDirectory, plan.Items[0].Kind);
        Assert.DoesNotContain(plan.Items, i => i.Kind == ActionKind.QuarantinePath);
    }

    [Fact]
    public async Task Scanner_uses_option_settings_and_saved_settings()
    {
        using var sb = new Sandbox();
        sb.File("a.jpg");
        var custom = new OrganizeSettings();
        custom.Rules.Single(r => r.Name == "Pictures").Action.Destination = "Fotos";
        custom.Store(sb.AppSettings);
        var fromSaved = await new OrganizeScanner(sb.Clock).ScanAsync(sb.Context(), null, CancellationToken.None);
        Assert.Contains(fromSaved.AllItems, i => i.Detail!.Contains(@"Downloads\Fotos"));

        var ctx = new ScanContext
        {
            Locations = sb.Locations, Safety = sb.Safety, InUse = sb.InUse, Settings = sb.AppSettings, Walker = new ManagedFileWalker(),
            Options = new Dictionary<string, object> { [OrganizeSettings.FeatureKey] = new OrganizeSettings() },
        };
        var fromOptions = await new OrganizeScanner(sb.Clock).ScanAsync(ctx, null, CancellationToken.None);
        Assert.Contains(fromOptions.AllItems, i => i.Detail!.Contains(@"Downloads\Pictures"));
    }

    [Fact]
    public void Settings_round_trip_through_app_settings_json()
    {
        var app = new AppSettings();
        var s = new OrganizeSettings { UseDateFolders = true, ExtraFolders = { @"C:\x" } };
        s.Rules[0].Enabled = false;
        s.Rules.Insert(0, new OrganizeRule { Name = "Mine", Conditions = ConditionGroup.AnyOf(RuleCondition.Regex("^a"), RuleCondition.OlderThan(3, DateField.Added)), Action = new RuleAction { Kind = RuleActionKind.Rename, RenamePattern = "{date} {name}" }, RunAutomatically = true });
        s.Ledger.Add(new LedgerEntry { Path = @"C:\x\a.pdf", RuleId = "r", Size = 3 });
        s.Store(app);

        var json = JsonSerializer.Serialize(app, AppSettings.Json);
        var back = OrganizeSettings.Load(JsonSerializer.Deserialize<AppSettings>(json, AppSettings.Json)!);

        Assert.True(back.UseDateFolders);
        Assert.Equal(s.Rules.Count, back.Rules.Count);
        Assert.Equal("Mine", back.Rules[0].Name);
        Assert.Equal(MatchMode.Any, back.Rules[0].Conditions.Mode);
        Assert.Equal(PatternSyntax.Regex, back.Rules[0].Conditions.Conditions[0].Syntax);
        Assert.Equal(RuleActionKind.Rename, back.Rules[0].Action.Kind);
        Assert.False(back.Rules[1].Enabled);
        Assert.Single(back.Ledger);
        Assert.Contains("\"Rename\"", json); // enums stored as names
    }

    [Fact]
    public void Corrupt_or_missing_settings_fall_back_to_defaults()
    {
        var app = new AppSettings();
        Assert.Equal(DefaultRules.Create().Count, OrganizeSettings.Load(app).Rules.Count);
        app.Features[OrganizeSettings.FeatureKey] = JsonDocument.Parse("{\"Rules\": 5}").RootElement.Clone();
        Assert.NotEmpty(OrganizeSettings.Load(app).Rules);
    }

    /// <summary>Brief rule 5: feature projects are read-only.</summary>
    [Fact]
    public void Feature_source_never_calls_file_changing_apis()
    {
        var forbidden = new Regex(@"\b(File\.(Delete|Move|Copy|Replace|WriteAll\w*|AppendAll\w*|SetAttributes|Create|Encrypt)|Directory\.(Delete|Move|CreateDirectory)|\.MoveTo\(|\.CopyTo\(|\.Delete\(|Process\.Start|Registry\w*\.(SetValue|DeleteValue|DeleteSubKey\w*|CreateSubKey)|\.Save\(\))");
        var files = Directory.EnumerateFiles(Path.GetFullPath(SourceRoot), "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains(@"\obj\") && !f.Contains(@"\bin\")).ToList();
        Assert.NotEmpty(files);
        foreach (var f in files)
            foreach (var (line, i) in System.IO.File.ReadLines(f).Select((l, i) => (l, i)))
                Assert.False(forbidden.IsMatch(line), $"{Path.GetFileName(f)}:{i + 1}: {line.Trim()}");
    }

    /// <summary>Every icon name used in XAML or code must exist in WPF-UI (a wrong one only fails at runtime).</summary>
    [Fact]
    public void Every_symbol_name_exists_in_wpf_ui()
    {
        var names = new HashSet<string>();
        var xamlSymbol = new Regex(@"(?:Symbol=""|\{ui:SymbolIcon )(\w+)");
        var codeIcon = new Regex(@"""(\w+(?:16|20|24))""");
        foreach (var f in Directory.EnumerateFiles(Path.GetFullPath(SourceRoot), "*.*", SearchOption.AllDirectories).Where(f => (f.EndsWith(".xaml") || f.EndsWith(".cs")) && !f.Contains(@"\obj\")))
        {
            var text = System.IO.File.ReadAllText(f);
            foreach (Match m in xamlSymbol.Matches(text)) if (!m.Groups[1].Value.StartsWith("Binding")) names.Add(m.Groups[1].Value);
            foreach (Match m in codeIcon.Matches(text)) names.Add(m.Groups[1].Value);
        }
        Assert.NotEmpty(names);
        foreach (var n in names) Assert.True(Enum.TryParse<Wpf.Ui.Controls.SymbolRegular>(n, out _), $"Unknown WPF-UI symbol: {n}");
    }
}
