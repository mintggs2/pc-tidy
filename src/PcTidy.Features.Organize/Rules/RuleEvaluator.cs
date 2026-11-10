using PcTidy.Core;
using PcTidy.Features.Organize.Detection;

namespace PcTidy.Features.Organize.Rules;

/// <summary>Evaluates rule conditions against a file. Pure apart from the lazy, read-only facts.</summary>
public static class RuleEvaluator
{
    /// <summary>First enabled rule (in order) whose conditions match, or null. Skips rules listed in <paramref name="blocked"/>.</summary>
    public static OrganizeRule? FirstMatch(IEnumerable<OrganizeRule> rules, FileFacts file, DateTime nowUtc, IReadOnlySet<string>? blocked = null)
    {
        foreach (var rule in rules)
        {
            if (!rule.Enabled || (blocked?.Contains(rule.Id) ?? false)) continue;
            if (Matches(rule.Conditions, file, nowUtc)) return rule;
        }
        return null;
    }

    public static bool Matches(ConditionGroup group, FileFacts file, DateTime nowUtc)
    {
        if (group.IsEmpty) return false;
        var results = group.Conditions.Select(c => (Func<bool>)(() => Matches(c, file, nowUtc)))
            .Concat(group.Groups.Where(g => !g.IsEmpty).Select(g => (Func<bool>)(() => Matches(g, file, nowUtc))));
        // Short-circuit so expensive facts (installer check, EXIF) are only read when needed.
        return group.Mode == MatchMode.All ? results.All(r => r()) : results.Any(r => r());
    }

    public static bool Matches(RuleCondition c, FileFacts file, DateTime nowUtc)
    {
        bool result = c.Kind switch
        {
            ConditionKind.Extension => c.Values.Any(v => NormalizeExtension(v) == file.Extension),
            ConditionKind.NameContains => c.Values.Any(v => !string.IsNullOrWhiteSpace(v) && file.Name.Contains(v.Trim(), StringComparison.OrdinalIgnoreCase)),
            ConditionKind.NamePattern => MatchPattern(c, file),
            ConditionKind.Size => (c.MinBytes is null || file.Entry.Size >= c.MinBytes) && (c.MaxBytes is null || file.Entry.Size <= c.MaxBytes),
            ConditionKind.Age => MatchAge(c, file, nowUtc),
            ConditionKind.SourceFolder => c.Values.Any(v => !string.IsNullOrWhiteSpace(v) && PathUtil.IsUnder(file.Folder, Path.IsPathRooted(v) ? v : Path.Combine(file.Root, v))),
            ConditionKind.DownloadSite => file.Zone is { } zone && zone.Hosts().Any(h => c.Values.Any(site => ZoneIdentifier.HostMatches(h, site))),
            ConditionKind.Category => string.Equals(file.CategoryId, c.Text, StringComparison.OrdinalIgnoreCase),
            ConditionKind.InstallerExe => file.Extension == "exe" && file.Installer.IsInstaller,
            _ => false,
        };
        return c.Negate ? !result : result;
    }

    /// <summary>
    /// Why a matching group matched, in plain words with the file's real values:
    /// "extension .msi, added 45 days ago". For "any of" groups only the part that matched is named.
    /// </summary>
    public static IReadOnlyList<string> Explain(ConditionGroup group, FileFacts file, DateTime nowUtc)
    {
        var parts = new List<string>();
        if (group.Mode == MatchMode.All)
        {
            parts.AddRange(group.Conditions.Select(c => Explain(c, file, nowUtc)));
            foreach (var g in group.Groups.Where(g => !g.IsEmpty)) parts.AddRange(Explain(g, file, nowUtc));
        }
        else if (group.Conditions.FirstOrDefault(c => Matches(c, file, nowUtc)) is { } hit) parts.Add(Explain(hit, file, nowUtc));
        else if (group.Groups.FirstOrDefault(g => !g.IsEmpty && Matches(g, file, nowUtc)) is { } sub) parts.AddRange(Explain(sub, file, nowUtc));
        return parts;
    }

    private static string Explain(RuleCondition c, FileFacts file, DateTime nowUtc)
    {
        if (c.Negate) return "not " + DescribeNegated(c);
        return c.Kind switch
        {
            ConditionKind.Extension => $"extension .{file.Extension}",
            ConditionKind.NameContains => $"name contains \"{c.Values.First(v => !string.IsNullOrWhiteSpace(v) && file.Name.Contains(v.Trim(), StringComparison.OrdinalIgnoreCase)).Trim()}\"",
            ConditionKind.NamePattern => $"name matches {c.Text}",
            ConditionKind.Size => $"size {Sizes.Format(file.Entry.Size)}",
            ConditionKind.Age => $"{c.DateField.ToString().ToLowerInvariant()} {AgeDays(c, file, nowUtc):0} days ago",
            ConditionKind.SourceFolder => $"in {file.Folder}",
            ConditionKind.DownloadSite => $"downloaded from {file.Zone?.Hosts().FirstOrDefault()}",
            // Name the concrete reason the file is in the category, not just the category.
            ConditionKind.Category => OrganizeCategories.Find(c.Text)?.Extensions.Contains(file.Extension) == true
                ? $"extension .{file.Extension}"
                : file.Installer.IsInstaller ? InstallerWhy(file) : $"a {c.Text?.ToLowerInvariant()} file",
            ConditionKind.InstallerExe => InstallerWhy(file),
            _ => c.Kind.ToString(),
        };
    }

    private static string InstallerWhy(FileFacts file)
    {
        var why = file.Installer.Why.TrimEnd('.');
        return "installer .exe (" + (why.Length > 0 ? char.ToLowerInvariant(why[0]) + why[1..] : why) + ")";
    }

    private static string DescribeNegated(RuleCondition c) => c.Kind switch
    {
        ConditionKind.Extension => "extension " + string.Join("/", c.Values.Select(v => "." + NormalizeExtension(v))),
        ConditionKind.NameContains => "name containing " + string.Join(" or ", c.Values),
        ConditionKind.NamePattern => "name matching " + c.Text,
        ConditionKind.Category => "a " + c.Text?.ToLowerInvariant() + " file",
        ConditionKind.InstallerExe => "an installer",
        _ => c.Kind.ToString().ToLowerInvariant() + " condition",
    };

    private static double AgeDays(RuleCondition c, FileFacts file, DateTime nowUtc)
        => (nowUtc - (c.DateField == DateField.Modified ? file.Entry.LastWriteUtc : file.Entry.CreationUtc)).TotalDays;

    public static string NormalizeExtension(string value) => value.Trim().TrimStart('*').TrimStart('.').ToLowerInvariant();

    private static bool MatchPattern(RuleCondition c, FileFacts file)
    {
        var r = NamePattern.Match(file.Name, c.Text, c.Syntax);
        if (r.TimedOut || r.Error is not null)
        {
            var warning = $"Pattern \"{c.Text}\": {r.Error}";
            if (!file.Warnings.Contains(warning)) file.Warnings.Add(warning);
        }
        return r.IsMatch;
    }

    private static bool MatchAge(RuleCondition c, FileFacts file, DateTime nowUtc)
    {
        var when = c.DateField switch
        {
            DateField.Created or DateField.Added => file.Entry.CreationUtc,
            _ => file.Entry.LastWriteUtc,
        };
        var days = (nowUtc - when).TotalDays;
        return (c.MinDays is null || days >= c.MinDays) && (c.MaxDays is null || days <= c.MaxDays);
    }
}
