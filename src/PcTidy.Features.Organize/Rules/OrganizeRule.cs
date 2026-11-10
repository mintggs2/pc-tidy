using PcTidy.Core.Model;

namespace PcTidy.Features.Organize.Rules;

/// <summary>
/// One organize rule. Rules are checked top to bottom; the first enabled rule whose conditions match wins.
/// Plain settable properties so the whole list round-trips through <c>AppSettings.SetFeature("organize", …)</c>.
/// </summary>
public sealed class OrganizeRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New rule";
    public bool Enabled { get; set; } = true;
    /// <summary>Built-in category rule (Pictures, Videos…). Still editable; shown without the "Your rule" pill.</summary>
    public bool IsBuiltIn { get; set; }
    /// <summary>WPF-UI symbol name for the rules list.</summary>
    public string Icon { get; set; } = "Folder24";
    public ConditionGroup Conditions { get; set; } = new();
    public RuleAction Action { get; set; } = new();
    /// <summary>What to do when a file with the same name is already at the destination. Never overwrite.</summary>
    public NameClashPolicy ClashPolicy { get; set; } = NameClashPolicy.AutoNumber;
    /// <summary>Watch mode may run this rule without asking (moves and renames only, unless <see cref="AllowAutomaticQuarantine"/>).</summary>
    public bool RunAutomatically { get; set; }
    /// <summary>Lets an automatic (watch-mode) run quarantine files with this rule. Off by default.</summary>
    public bool AllowAutomaticQuarantine { get; set; }

    public OrganizeRule Clone()
    {
        var copy = (OrganizeRule)MemberwiseClone();
        copy.Conditions = Conditions.Clone();
        copy.Action = Action.Clone();
        return copy;
    }
}

public enum MatchMode { All, Any }

/// <summary>AND (All) / OR (Any) over conditions and nested groups. An empty group matches nothing.</summary>
public sealed class ConditionGroup
{
    public MatchMode Mode { get; set; } = MatchMode.All;
    public List<RuleCondition> Conditions { get; set; } = new();
    public List<ConditionGroup> Groups { get; set; } = new();

    public bool IsEmpty => Conditions.Count == 0 && Groups.All(g => g.IsEmpty);

    public ConditionGroup Clone() => new()
    {
        Mode = Mode,
        Conditions = Conditions.Select(c => c.Clone()).ToList(),
        Groups = Groups.Select(g => g.Clone()).ToList(),
    };

    public static ConditionGroup AllOf(params RuleCondition[] conditions) => new() { Mode = MatchMode.All, Conditions = conditions.ToList() };
    public static ConditionGroup AnyOf(params RuleCondition[] conditions) => new() { Mode = MatchMode.Any, Conditions = conditions.ToList() };
}

public enum ConditionKind
{
    /// <summary>Extension is one of <see cref="RuleCondition.Values"/> (no dots, case-insensitive).</summary>
    Extension,
    /// <summary>File name contains any of <see cref="RuleCondition.Values"/> (case-insensitive).</summary>
    NameContains,
    /// <summary>File name matches <see cref="RuleCondition.Text"/> (glob "a*;b*" or regex, timeout-protected).</summary>
    NamePattern,
    /// <summary>Size between <see cref="RuleCondition.MinBytes"/> and <see cref="RuleCondition.MaxBytes"/>.</summary>
    Size,
    /// <summary>Age of <see cref="RuleCondition.DateField"/> between MinDays and MaxDays.</summary>
    Age,
    /// <summary>The file's folder is (inside) one of <see cref="RuleCondition.Values"/>.</summary>
    SourceFolder,
    /// <summary>Downloaded from one of the sites in <see cref="RuleCondition.Values"/> (Zone.Identifier HostUrl / ReferrerUrl).</summary>
    DownloadSite,
    /// <summary>Belongs to the built-in category named in <see cref="RuleCondition.Text"/> (exact §9 table).</summary>
    Category,
    /// <summary>An .exe that is really an installer (name, version info or installer markers).</summary>
    InstallerExe,
}

public enum PatternSyntax { Glob, Regex }

public enum DateField
{
    Modified,
    Created,
    /// <summary>When the file arrived in the folder. Windows sets the created time when a file is downloaded or copied in.</summary>
    Added,
}

public sealed class RuleCondition
{
    public ConditionKind Kind { get; set; }
    /// <summary>Inverts the result ("is not").</summary>
    public bool Negate { get; set; }
    public List<string> Values { get; set; } = new();
    public string? Text { get; set; }
    public PatternSyntax Syntax { get; set; } = PatternSyntax.Glob;
    public long? MinBytes { get; set; }
    public long? MaxBytes { get; set; }
    public DateField DateField { get; set; } = DateField.Modified;
    /// <summary>At least this many days old ("older than").</summary>
    public double? MinDays { get; set; }
    /// <summary>At most this many days old ("newer than").</summary>
    public double? MaxDays { get; set; }

    public RuleCondition Clone()
    {
        var c = (RuleCondition)MemberwiseClone();
        c.Values = Values.ToList();
        return c;
    }

    public static RuleCondition Ext(params string[] extensions) => new() { Kind = ConditionKind.Extension, Values = extensions.ToList() };
    public static RuleCondition NameHas(params string[] words) => new() { Kind = ConditionKind.NameContains, Values = words.ToList() };
    public static RuleCondition Glob(string pattern) => new() { Kind = ConditionKind.NamePattern, Text = pattern, Syntax = PatternSyntax.Glob };
    public static RuleCondition Regex(string pattern) => new() { Kind = ConditionKind.NamePattern, Text = pattern, Syntax = PatternSyntax.Regex };
    public static RuleCondition InCategory(string categoryId) => new() { Kind = ConditionKind.Category, Text = categoryId };
    public static RuleCondition OlderThan(double days, DateField field) => new() { Kind = ConditionKind.Age, DateField = field, MinDays = days };
    public static RuleCondition IsInstallerExe() => new() { Kind = ConditionKind.InstallerExe };
}

public enum RuleActionKind
{
    /// <summary>Move into <see cref="RuleAction.Destination"/> (relative to the folder being sorted, or absolute).</summary>
    MoveToFolder,
    /// <summary>Move into PC Tidy's quarantine (restorable). Always a "Check" item, never ticked by default.</summary>
    Quarantine,
    /// <summary>Rename in place using <see cref="RuleAction.RenamePattern"/>.</summary>
    Rename,
    /// <summary>Leave the file where it is (the "Other" category).</summary>
    LeaveInPlace,
}

public sealed class RuleAction
{
    public RuleActionKind Kind { get; set; } = RuleActionKind.MoveToFolder;
    /// <summary>Folder template with {year} {month} {monthName} tokens, e.g. "Pictures" or "Documents\Invoices\{year}".</summary>
    public string Destination { get; set; } = "";
    /// <summary>New name (without extension) with {name} {year} {month} {monthName} {day} {date} tokens.</summary>
    public string RenamePattern { get; set; } = "{name}";

    public RuleAction Clone() => (RuleAction)MemberwiseClone();
}
