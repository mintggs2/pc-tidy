using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Organize.Planning;
using PcTidy.Features.Organize.Rules;

namespace PcTidy.Features.Organize.UI;

/// <summary>A folder in the "which folder" picker.</summary>
public sealed record FolderOption(string Path, string Label, string? Note, bool IsPicker = false)
{
    public override string ToString() => Note is null ? Label : $"{Label} ({Note})";
}

/// <summary>One rule in the rules list.</summary>
public sealed partial class RuleRowViewModel : ObservableObject
{
    private readonly Action _changed;

    public RuleRowViewModel(OrganizeRule rule, Action changed)
    {
        Rule = rule;
        _changed = changed;
        _enabled = rule.Enabled;
    }

    public OrganizeRule Rule { get; private set; }
    public string Name => Rule.Name;
    public string Icon => Rule.Icon;
    public bool IsCustom => !Rule.IsBuiltIn;
    public string Target => RuleText.Target(Rule);
    public string MatchText => RuleText.Conditions(Rule.Conditions);
    public bool IsAutomatic => Rule.RunAutomatically;

    [ObservableProperty] private bool _enabled;
    /// <summary>Files this rule caught in the last scan ("" before any scan).</summary>
    [ObservableProperty] private string _count = "";
    [ObservableProperty] private string? _problem;

    partial void OnEnabledChanged(bool value)
    {
        Rule.Enabled = value;
        _changed();
    }

    public void Replace(OrganizeRule rule)
    {
        Rule = rule;
        Enabled = rule.Enabled;
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>One file/folder in the detailed "Preview moves" list.</summary>
public sealed partial class ActionRowViewModel : ObservableObject
{
    public ActionRowViewModel(PlannedAction action, IReadOnlyList<string> roots, Action changed)
    {
        Action = action;
        _changed = changed;
        _isChecked = action.Badge == Badge.Safe && action.DefaultSelected;
        Detail = action.DestinationPath is null
            ? DisplayPaths.Short(action.SourcePath, roots)
            : $"{DisplayPaths.Short(action.SourcePath, roots)} → {DisplayPaths.Short(action.DestinationPath, roots)}";
    }

    private readonly Action _changed;
    public PlannedAction Action { get; }
    public string Name => Action.Name;
    public string Detail { get; }
    public string Reason => Action.Reason;
    public string BadgeText => Action.Badge == Badge.Safe ? "Safe" : "Your call";
    public bool IsCheck => Action.Badge != Badge.Safe;
    public string Size => Action.IsDirectory ? Sizes.Format(Action.Size) + " folder" : Sizes.Format(Action.Size);
    public string Icon => Action.IsDirectory ? "Folder24" : PreviewTree.IconFor(Action.Name);

    [ObservableProperty] private bool _isChecked;
    partial void OnIsCheckedChanged(bool value) => _changed();
}

public sealed partial class ActionGroupViewModel : ObservableObject
{
    public ActionGroupViewModel(string title, string description, IEnumerable<ActionRowViewModel> items)
    {
        Title = title;
        Description = description;
        Items = new ObservableCollection<ActionRowViewModel>(items);
    }

    public string Title { get; }
    public string Description { get; }
    public ObservableCollection<ActionRowViewModel> Items { get; }
    public string CountText => Items.Count == 1 ? "1 item" : $"{Items.Count:N0} items";
}

public sealed record SkipRow(string Name, string Reason, string Icon);

/// <summary>Plain-English summaries of rules for the list and the editor.</summary>
public static class RuleText
{
    public static string Target(OrganizeRule r) => r.Action.Kind switch
    {
        RuleActionKind.MoveToFolder => string.IsNullOrWhiteSpace(r.Action.Destination) ? "(no folder yet)" : r.Action.Destination,
        RuleActionKind.Quarantine => "Quarantine (your call, restorable)",
        RuleActionKind.Rename => "Rename to " + r.Action.RenamePattern,
        _ => "Stays where it is",
    };

    /// <summary>
    /// What a rule catches, in plain words: "Pictures · names starting with Screenshot or Snip", "Any name",
    /// ".pdf files · name has “invoice” or “receipt”". "All" conditions are joined with " · ", "Any" with " or ".
    /// </summary>
    public static string Conditions(ConditionGroup g)
    {
        // A category reads best as the subject ("Installers · added more than 30 days ago"), so it goes first.
        var conditions = g.Mode == MatchMode.All ? g.Conditions.OrderBy(c => c.Kind == ConditionKind.Category && !c.Negate ? 0 : 1) : g.Conditions.AsEnumerable();
        var parts = conditions.Select(Condition).Concat(g.Groups.Select(sub => "(" + Uncapitalized(Conditions(sub)) + ")")).ToList();
        if (parts.Count == 0) return "No conditions yet";
        var text = string.Join(g.Mode == MatchMode.All ? " · " : " or ", parts.Select((p, i) => i == 0 ? p : Uncapitalized(p)));
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    public static string Condition(RuleCondition c)
    {
        var not = c.Negate ? "not " : "";
        var text = c.Kind switch
        {
            ConditionKind.Extension => not + Extensions(c.Values),
            ConditionKind.NameContains => (c.Negate ? "name doesn't have " : "name has ") + OrList(c.Values.Select(v => $"“{v}”")),
            ConditionKind.NamePattern => NamePatternText(c),
            ConditionKind.Size => not + SizeRange(c),
            ConditionKind.Age => not + AgeRange(c),
            ConditionKind.SourceFolder => not + "in " + OrList(c.Values),
            ConditionKind.DownloadSite => not + "downloaded from " + OrList(c.Values),
            ConditionKind.Category => c.Negate ? $"not {c.Text}" : c.Text ?? "",
            ConditionKind.InstallerExe => not + "installer .exe files",
            _ => c.Kind.ToString(),
        };
        return text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
    }

    /// <summary>Common types first, so ".jpg, .png" shows instead of ".arw, .avif".</summary>
    private static readonly string[] Popular =
    {
        "jpg", "png", "heic", "webp", "gif", "mp4", "mov", "mkv", "mp3", "m4a", "wav", "flac", "pdf", "docx", "xlsx", "pptx", "txt",
        "zip", "7z", "rar", "iso", "msi", "msix", "cs", "py", "js", "html", "torrent",
    };

    private static string Extensions(IReadOnlyCollection<string> values)
    {
        var exts = values.Select(v => "." + RuleEvaluator.NormalizeExtension(v))
            .OrderBy(e => Array.IndexOf(Popular, e[1..]) is var i and >= 0 ? i : Popular.Length).ThenBy(e => e, StringComparer.Ordinal).ToList();
        if (exts.Count == 1) return exts[0] + " files";
        if (exts.Count <= 4) return OrList(exts);
        return $"{string.Join(", ", exts.Take(4))} and {exts.Count - 4} more";
    }

    /// <summary>"Screenshot*;Snip*" → "names starting with Screenshot or Snip"; "*" → "any name". Regexes are named as such.</summary>
    private static string NamePatternText(RuleCondition c)
    {
        var text = c.Text ?? "";
        if (c.Syntax == PatternSyntax.Regex) return (c.Negate ? "names not matching the pattern " : "names matching the pattern ") + text;
        var parts = text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Any(p => p.Trim('*').Length == 0)) return c.Negate ? "no name" : "any name";
        var not = c.Negate ? "not " : "";
        // "like" keeps the wildcards (they're the point); the others read better without them.
        var phrases = parts.GroupBy(GlobPhrase, p => GlobPhrase(p) == LikePhrase ? p : p.Trim('*'))
            .Select(g => string.Format(g.Key, not, OrList(g)));
        return string.Join(" or ", phrases);
    }

    /// <summary>Phrase template for one glob alternative: {0} = "not " or "", {1} = the names.</summary>
    private const string LikePhrase = "names {0}like {1}";

    private static string GlobPhrase(string part)
    {
        var inner = part.Trim('*');
        if (inner.Contains('*') || inner.Contains('?')) return LikePhrase;
        return (part.EndsWith('*'), part.StartsWith('*')) switch
        {
            (true, true) => "names {0}containing {1}",
            (true, false) => "names {0}starting with {1}",
            (false, true) => "names {0}ending with {1}",
            _ => "{0}named {1}",
        };
    }

    private static string OrList(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count <= 1 ? string.Join("", list) : $"{string.Join(", ", list.Take(list.Count - 1))} or {list[^1]}";
    }

    private static string Uncapitalized(string s)
        => s.Length > 1 && char.IsUpper(s[0]) && !char.IsUpper(s[1]) ? char.ToLowerInvariant(s[0]) + s[1..] : s;

    private static string SizeRange(RuleCondition c) => (c.MinBytes, c.MaxBytes) switch
    {
        ({ } min, { } max) => $"between {Sizes.Format(min)} and {Sizes.Format(max)}",
        ({ } min, null) => $"bigger than {Sizes.Format(min)}",
        (null, { } max) => $"smaller than {Sizes.Format(max)}",
        _ => "any size",
    };

    private static string AgeRange(RuleCondition c)
    {
        var when = c.DateField switch { DateField.Created => "created", DateField.Added => "added", _ => "changed" };
        return (c.MinDays, c.MaxDays) switch
        {
            ({ } min, { } max) => $"{when} {min:0.#} to {max:0.#} days ago",
            ({ } min, null) => $"{when} more than {Days(min)} ago",
            (null, { } max) => $"{when} in the last {Days(max)}",
            _ => $"{when} any time",
        };
    }

    private static string Days(double d) => d == 1 ? "1 day" : $"{d:0.#} days";
}
