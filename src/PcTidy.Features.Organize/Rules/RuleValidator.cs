using PcTidy.Core;
using PcTidy.Core.Model;

namespace PcTidy.Features.Organize.Rules;

/// <summary>A problem with a rule. Blocking problems switch the rule off for scans until fixed.</summary>
public sealed record RuleProblem(string RuleId, string RuleName, string Message, bool Blocking);

/// <summary>
/// Checks a rule before it is used: no loops (a rule must never feed its own output back into a scan),
/// no destinations inside protected, cloud or project folders unless the user allowed them, never overwrite.
/// </summary>
public static class RuleValidator
{
    public static IReadOnlyList<RuleProblem> Validate(OrganizeRule rule, IReadOnlyList<string> roots, ISafetyGuard safety, AppSettings settings)
    {
        var problems = new List<RuleProblem>();
        void Add(string message, bool blocking = true) => problems.Add(new RuleProblem(rule.Id, rule.Name, message, blocking));

        if (rule.Conditions.IsEmpty) Add("Add at least one condition.");
        foreach (var c in AllConditions(rule.Conditions))
        {
            if (c.Kind == ConditionKind.NamePattern && NamePattern.Validate(c.Text, c.Syntax) is { } patternError) Add(patternError);
            if (c.Kind is ConditionKind.Extension or ConditionKind.NameContains or ConditionKind.DownloadSite or ConditionKind.SourceFolder
                && c.Values.All(string.IsNullOrWhiteSpace))
                Add("A condition has no value yet.");
        }
        if (rule.ClashPolicy == NameClashPolicy.Overwrite) Add("PC Tidy never overwrites files. Pick \"Add a number\" or \"Skip\".");

        switch (rule.Action.Kind)
        {
            case RuleActionKind.MoveToFolder:
                ValidateMove(rule, roots, safety, settings, Add);
                break;
            case RuleActionKind.Rename:
                ValidateRename(rule, Add);
                break;
            case RuleActionKind.Quarantine when rule.RunAutomatically && !rule.AllowAutomaticQuarantine:
                Add("Watch mode only moves files. This rule will wait for you to review it.", blocking: false);
                break;
        }
        return problems;
    }

    /// <summary>Validates a whole rule list; returns the ids of rules that must not run.</summary>
    public static (IReadOnlySet<string> Blocked, IReadOnlyList<RuleProblem> Problems) ValidateAll(
        IEnumerable<OrganizeRule> rules, IReadOnlyList<string> roots, ISafetyGuard safety, AppSettings settings)
    {
        var problems = rules.Where(r => r.Enabled).SelectMany(r => Validate(r, roots, safety, settings)).ToList();
        return (problems.Where(p => p.Blocking).Select(p => p.RuleId).ToHashSet(), problems);
    }

    private static void ValidateMove(OrganizeRule rule, IReadOnlyList<string> roots, ISafetyGuard safety, AppSettings settings, Action<string, bool> add)
    {
        var template = rule.Action.Destination?.Trim() ?? "";
        if (template.Length == 0) { add("Choose a folder to move files to.", true); return; }

        var sample = new DateTime(2026, 1, 15);
        if (Path.IsPathFullyQualified(DestinationResolver.ExpandTokens(template, sample)))
        {
            var dest = DestinationResolver.ResolveFolder(rule.Action, roots.FirstOrDefault() ?? "", sample, false);
            if (dest is null) { add("That folder path isn't valid.", true); return; }
            // A file moved into a folder that is itself being sorted would be picked up again: a loop.
            if (roots.Any(r => PathUtil.Normalize(r).Equals(dest, StringComparison.OrdinalIgnoreCase)))
                add("Files would land in a folder that is sorted too, and could be moved again and again.", true);
            CheckDestinationSafety(dest, null, safety, settings, add);
            return;
        }

        foreach (var root in roots)
        {
            var dest = DestinationResolver.ResolveFolder(rule.Action, root, sample, false);
            if (dest is null) { add("The folder must stay inside the folder being sorted. Use a full path to go elsewhere.", true); return; }
            if (dest.Equals(PathUtil.Normalize(root), StringComparison.OrdinalIgnoreCase))
                add("Files would be moved back into the same folder, again and again.", true);
            CheckDestinationSafety(dest, root, safety, settings, add);
        }
    }

    private static void CheckDestinationSafety(string dest, string? root, ISafetyGuard safety, AppSettings settings, Action<string, bool> add)
    {
        // Inside an allowed sorted folder (e.g. Desktop\Pictures when Desktop was allowed) is fine.
        bool insideRoot = root is not null && PathUtil.IsUnder(dest, root);
        bool allowed = insideRoot || settings.AllowedAskFirstFolders.Any(f => PathUtil.IsUnder(dest, f));

        if (safety.ClassifyPath(dest).Protection == PathProtection.Never) { add("That folder is protected. PC Tidy never moves files there.", true); return; }
        if (!allowed && safety.IsInCloudRoot(dest)) { add("That folder is in OneDrive. Allow it in Settings first.", true); return; }
        if (!allowed && safety.IsInsideProjectOrAppFolder(dest, out _)) { add("That folder is inside a code project or app folder.", true); return; }
        if (!allowed && safety.ClassifyPath(dest).Protection == PathProtection.AskFirst) add("PC Tidy needs your OK for that folder first (Settings).", true);
    }

    private static void ValidateRename(OrganizeRule rule, Action<string, bool> add)
    {
        var pattern = rule.Action.RenamePattern?.Trim() ?? "";
        if (pattern.Length == 0) { add("Type the new name pattern.", true); return; }
        var once = RenameTokens.Apply(pattern, "Example", DateTime.Today);
        if (WindowsNames.Problem(once + ".txt") is { } problem) { add(problem, true); return; }
        var twice = RenameTokens.Apply(pattern, once, DateTime.Today);
        if (!string.Equals(once, twice, StringComparison.Ordinal))
            add("Renamed files may match this rule again. PC Tidy stops a file being renamed twice.", false);
    }

    private static IEnumerable<RuleCondition> AllConditions(ConditionGroup g)
        => g.Conditions.Concat(g.Groups.SelectMany(AllConditions));
}

/// <summary>Rename-rule pattern tokens: {name} plus the date tokens.</summary>
public static class RenameTokens
{
    public static string Apply(string pattern, string currentStem, DateTime date, System.Globalization.CultureInfo? culture = null)
    {
        // Dates first, so a file name that happens to contain "{year}" is kept literally.
        var dated = DestinationResolver.ExpandTokens(pattern, date, culture);
        return dated.Replace("{name}", currentStem, StringComparison.OrdinalIgnoreCase).Trim();
    }
}
