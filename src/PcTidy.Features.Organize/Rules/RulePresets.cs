namespace PcTidy.Features.Organize.Rules;

/// <summary>A ready-made rule set the user can apply to the rule list (after seeing what would change).</summary>
public sealed record RulePreset(string Id, string Name, string Description, Func<List<OrganizeRule>> Create);

public enum RuleChangeKind { Added, Changed, Removed, Kept }

/// <summary>One line of "what applying this preset does to your rules".</summary>
public sealed record RuleChange(RuleChangeKind Kind, string RuleName, string Text);

/// <summary>
/// Organize presets (Plan census v3): Simple (the §9 defaults), By date (ISO year/month folders), Projects &amp; Areas
/// (PARA-lite) and Numbered (Johnny.Decimal). Every destination is relative to the folder being sorted, so presets
/// work on any PC. Category and starter rules keep their ids across presets, so the processed-files ledger and the
/// "Other" rule keep working; the user's own rules are never touched by a preset.
/// </summary>
public static class RulePresets
{
    public const string SimpleId = "simple";
    public const string ByDateId = "by-date";
    public const string ParaId = "para";
    public const string JohnnyDecimalId = "johnny-decimal";

    private const string IsoMonth = @"{year}\{year}-{month:00}";

    public static readonly IReadOnlyList<RulePreset> All = new RulePreset[]
    {
        new(SimpleId, "Simple", "One folder per kind of file: Pictures, Documents, Videos… (how PC Tidy starts).", DefaultRules.Create),
        new(ByDateId, "By date", @"Each kind of file sorted by year and month, ISO style: Pictures\2026\2026-11.", ByDate),
        new(ParaId, "Projects & Areas", "Four folders: 1 Projects, 2 Areas, 3 Resources, 4 Archive. Old files go to the archive.", Para),
        new(JohnnyDecimalId, "Numbered", @"Numbered folders like 20-29 Media\21 Pictures, so everything has a fixed place.", JohnnyDecimal),
    };

    public static RulePreset? Find(string id) => All.FirstOrDefault(p => p.Id == id);

    /// <summary>True for rules a preset owns (starter, category and preset rules); everything else is the user's own.</summary>
    public static bool IsPresetRule(OrganizeRule r)
        => r.Id.StartsWith("starter.", StringComparison.Ordinal) || r.Id.StartsWith("category.", StringComparison.Ordinal)
           || r.Id.StartsWith("preset.", StringComparison.Ordinal);

    /// <summary>The rule list after applying <paramref name="preset"/>: the user's own rules first (unchanged, in order), then the preset's.</summary>
    public static List<OrganizeRule> Apply(IEnumerable<OrganizeRule> current, RulePreset preset)
        => current.Where(r => !IsPresetRule(r)).Select(r => r.Clone()).Concat(preset.Create()).ToList();

    /// <summary>What applying would change, rule by rule, in plain words (shown before anything is applied).</summary>
    public static IReadOnlyList<RuleChange> Preview(IReadOnlyList<OrganizeRule> current, RulePreset preset)
    {
        var after = Apply(current, preset);
        // Rule ids aren't checked for uniqueness on load or import (a hand-edited settings file can repeat one): the first wins.
        var before = current.GroupBy(r => r.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var changes = new List<RuleChange>();
        foreach (var r in after)
        {
            if (!before.TryGetValue(r.Id, out var old))
                changes.Add(new RuleChange(RuleChangeKind.Added, r.Name, $"New: {Summary(r)}"));
            else if (Summary(old) != Summary(r) || old.Name != r.Name || old.Enabled != r.Enabled)
                changes.Add(new RuleChange(RuleChangeKind.Changed, r.Name, $"Now: {Summary(r)} (was: {Summary(old)})"));
            else
                changes.Add(new RuleChange(RuleChangeKind.Kept, r.Name, IsPresetRule(r) ? $"Same as now: {Summary(r)}" : "Your own rule, kept as it is"));
        }
        var afterIds = after.Select(r => r.Id).ToHashSet();
        foreach (var r in current.Where(r => !afterIds.Contains(r.Id)))
            changes.Add(new RuleChange(RuleChangeKind.Removed, r.Name, $"Removed: {Summary(r)}"));
        return changes;
    }

    /// <summary>"Pictures → Pictures\{year}\…" in plain words, the same text the rules list shows.</summary>
    public static string Summary(OrganizeRule r)
        => $"{UI.RuleText.Conditions(r.Conditions)} → {UI.RuleText.Target(r)}{(r.Enabled ? "" : " (off)")}";

    // ------------------------------------------------------------------ the presets

    private static List<OrganizeRule> ByDate()
    {
        var rules = DefaultRules.Create();
        foreach (var r in rules.Where(r => r.Action.Kind == RuleActionKind.MoveToFolder))
            r.Action.Destination = r.Id == DefaultRules.InvoicesId ? @"Documents\Invoices\{year}" : r.Action.Destination + "\\" + IsoMonth;
        return rules;
    }

    private static List<OrganizeRule> Para()
    {
        var rules = Retarget(DefaultRules.Create(), new Dictionary<string, string>
        {
            [DefaultRules.InvoicesId] = @"2 Areas\Finance",
            [DefaultRules.ScreenshotsId] = @"3 Resources\Screenshots",
            [DefaultRules.TorrentsId] = @"3 Resources\Torrents",
            [Cat(OrganizeCategories.Pictures)] = @"3 Resources\Pictures",
            [Cat(OrganizeCategories.Videos)] = @"3 Resources\Videos",
            [Cat(OrganizeCategories.Music)] = @"3 Resources\Music",
            [Cat(OrganizeCategories.Documents)] = @"3 Resources\Documents",
            [Cat(OrganizeCategories.Archives)] = @"3 Resources\Archives",
            [Cat(OrganizeCategories.Installers)] = @"3 Resources\Installers",
            [Cat(OrganizeCategories.Code)] = @"3 Resources\Code",
        });
        // After the starters (so invoices and old installers keep their own rule), before the categories.
        int at = rules.FindIndex(r => r.Id.StartsWith("category.", StringComparison.Ordinal));
        rules.Insert(at, new OrganizeRule
        {
            Id = "preset.para.archive", Name = "Not changed for 6 months", Icon = "Archive24",
            Conditions = ConditionGroup.AllOf(RuleCondition.OlderThan(180, DateField.Modified)),
            Action = new RuleAction { Kind = RuleActionKind.MoveToFolder, Destination = @"4 Archive\{year}" },
        });
        // Projects can't be guessed from a file, so this starts switched off as an example to edit.
        rules.Insert(0, new OrganizeRule
        {
            Id = "preset.para.projects", Name = "Project files (example)", Icon = "Briefcase24", Enabled = false,
            Conditions = ConditionGroup.AllOf(RuleCondition.NameHas("project")),
            Action = new RuleAction { Kind = RuleActionKind.MoveToFolder, Destination = @"1 Projects\Inbox" },
        });
        return rules;
    }

    private static List<OrganizeRule> JohnnyDecimal()
        => Retarget(DefaultRules.Create(), new Dictionary<string, string>
        {
            [DefaultRules.InvoicesId] = @"10-19 Admin\11 Invoices and receipts",
            [Cat(OrganizeCategories.Documents)] = @"10-19 Admin\12 Documents",
            [Cat(OrganizeCategories.Pictures)] = @"20-29 Media\21 Pictures",
            [DefaultRules.ScreenshotsId] = @"20-29 Media\22 Screenshots",
            [Cat(OrganizeCategories.Videos)] = @"20-29 Media\23 Videos",
            [Cat(OrganizeCategories.Music)] = @"20-29 Media\24 Music",
            [Cat(OrganizeCategories.Installers)] = @"30-39 Software\31 Installers",
            [Cat(OrganizeCategories.Code)] = @"30-39 Software\32 Code",
            [DefaultRules.TorrentsId] = @"30-39 Software\33 Torrents",
            [Cat(OrganizeCategories.Archives)] = @"90-99 Archive\91 Archives",
        });

    private static string Cat(string categoryId) => DefaultRules.CategoryRuleId(categoryId);

    private static List<OrganizeRule> Retarget(List<OrganizeRule> rules, IReadOnlyDictionary<string, string> destinations)
    {
        foreach (var r in rules)
            if (destinations.TryGetValue(r.Id, out var d) && r.Action.Kind == RuleActionKind.MoveToFolder) r.Action.Destination = d;
        return rules;
    }
}
