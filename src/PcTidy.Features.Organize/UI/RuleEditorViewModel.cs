using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcTidy.Core.Model;
using PcTidy.Features.Organize.Rules;

namespace PcTidy.Features.Organize.UI;

public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One editable condition row. Text boxes hold plain text; parsing is culture-invariant.</summary>
public sealed partial class ConditionRowViewModel : ObservableObject
{
    public ConditionRowViewModel(RuleCondition c)
    {
        _kind = c.Kind;
        _negate = c.Negate;
        _valuesText = c.Kind == ConditionKind.NamePattern ? c.Text ?? "" : string.Join(", ", c.Values);
        _syntax = c.Syntax;
        _minMb = c.MinBytes is { } min ? (min / 1048576.0).ToString("0.##", CultureInfo.InvariantCulture) : "";
        _maxMb = c.MaxBytes is { } max ? (max / 1048576.0).ToString("0.##", CultureInfo.InvariantCulture) : "";
        _dateField = c.DateField;
        _olderThanDays = c.MinDays?.ToString(CultureInfo.InvariantCulture) ?? "";
        _newerThanDays = c.MaxDays?.ToString(CultureInfo.InvariantCulture) ?? "";
        _categoryId = c.Text ?? OrganizeCategories.Pictures;
    }

    public static IReadOnlyList<Choice<ConditionKind>> Kinds { get; } = new[]
    {
        new Choice<ConditionKind>(ConditionKind.Extension, "Extension is"),
        new Choice<ConditionKind>(ConditionKind.NameContains, "Name contains"),
        new Choice<ConditionKind>(ConditionKind.NamePattern, "Name matches pattern"),
        new Choice<ConditionKind>(ConditionKind.Size, "Size"),
        new Choice<ConditionKind>(ConditionKind.Age, "Age"),
        new Choice<ConditionKind>(ConditionKind.SourceFolder, "Is in folder"),
        new Choice<ConditionKind>(ConditionKind.DownloadSite, "Downloaded from site"),
        new Choice<ConditionKind>(ConditionKind.Category, "Is a kind of file"),
        new Choice<ConditionKind>(ConditionKind.InstallerExe, "Is an installer .exe"),
    };
    public static IReadOnlyList<Choice<PatternSyntax>> Syntaxes { get; } = new[]
    {
        new Choice<PatternSyntax>(PatternSyntax.Glob, "Wildcards (* ?)"),
        new Choice<PatternSyntax>(PatternSyntax.Regex, "Regular expression"),
    };
    public static IReadOnlyList<Choice<DateField>> DateFields { get; } = new[]
    {
        new Choice<DateField>(DateField.Modified, "Modified"),
        new Choice<DateField>(DateField.Created, "Created"),
        new Choice<DateField>(DateField.Added, "Added to the folder"),
    };
    public static IReadOnlyList<string> Categories { get; } = OrganizeCategories.All.Select(c => c.Id).ToList();

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowValues), nameof(ShowPattern), nameof(ShowSize), nameof(ShowAge), nameof(ShowCategory), nameof(ValuesHint))]
    private ConditionKind _kind;
    [ObservableProperty] private bool _negate;
    [ObservableProperty] private string _valuesText;
    [ObservableProperty] private PatternSyntax _syntax;
    [ObservableProperty] private string _minMb;
    [ObservableProperty] private string _maxMb;
    [ObservableProperty] private DateField _dateField;
    [ObservableProperty] private string _olderThanDays;
    [ObservableProperty] private string _newerThanDays;
    [ObservableProperty] private string _categoryId;

    public bool ShowValues => Kind is ConditionKind.Extension or ConditionKind.NameContains or ConditionKind.SourceFolder or ConditionKind.DownloadSite or ConditionKind.NamePattern;
    public bool ShowPattern => Kind == ConditionKind.NamePattern;
    public bool ShowSize => Kind == ConditionKind.Size;
    public bool ShowAge => Kind == ConditionKind.Age;
    public bool ShowCategory => Kind == ConditionKind.Category;
    public string ValuesHint => Kind switch
    {
        ConditionKind.Extension => "e.g. pdf, docx",
        ConditionKind.NameContains => "e.g. invoice, receipt",
        ConditionKind.NamePattern => "e.g. Screenshot*;IMG_*",
        ConditionKind.SourceFolder => @"e.g. C:\Users\you\Downloads\Work",
        ConditionKind.DownloadSite => "e.g. github.com",
        _ => "",
    };

    public RuleCondition ToCondition()
    {
        var c = new RuleCondition { Kind = Kind, Negate = Negate, Syntax = Syntax, DateField = DateField };
        switch (Kind)
        {
            case ConditionKind.NamePattern: c.Text = ValuesText.Trim(); break;
            case ConditionKind.Category: c.Text = CategoryId; break;
            case ConditionKind.Size: c.MinBytes = Mb(MinMb); c.MaxBytes = Mb(MaxMb); break;
            case ConditionKind.Age: c.MinDays = Number(OlderThanDays); c.MaxDays = Number(NewerThanDays); break;
            case ConditionKind.InstallerExe: break;
            default:
                c.Values = ValuesText.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                break;
        }
        return c;
    }

    private static double? Number(string text)
        => double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : null;

    private static long? Mb(string text) => Number(text) is { } mb ? (long)(mb * 1048576) : null;
}

/// <summary>The rule editor dialog. Works on a copy; <see cref="Result"/> is set only when saved without blocking problems.</summary>
public sealed partial class RuleEditorViewModel : ObservableObject
{
    private readonly OrganizeRule _original;
    private readonly Func<OrganizeRule, IReadOnlyList<RuleProblem>> _validate;
    private readonly Func<OrganizeRule, Task<RuleTestResult>> _test;

    public RuleEditorViewModel(OrganizeRule rule, bool isNew, Func<OrganizeRule, IReadOnlyList<RuleProblem>> validate, Func<OrganizeRule, Task<RuleTestResult>> test)
    {
        _original = rule;
        _validate = validate;
        _test = test;
        IsNew = isNew;
        _name = rule.Name;
        _enabled = rule.Enabled;
        _matchAll = rule.Conditions.Mode == MatchMode.All;
        _actionKind = rule.Action.Kind;
        _destination = rule.Action.Destination;
        _renamePattern = rule.Action.RenamePattern;
        _clashPolicy = rule.ClashPolicy == NameClashPolicy.Skip ? NameClashPolicy.Skip : NameClashPolicy.AutoNumber;
        _runAutomatically = rule.RunAutomatically;
        _allowAutomaticQuarantine = rule.AllowAutomaticQuarantine;
        foreach (var c in rule.Conditions.Conditions) Conditions.Add(new ConditionRowViewModel(c));
        NestedGroupsNote = rule.Conditions.Groups.Count == 0 ? null
            : $"Plus {rule.Conditions.Groups.Count} advanced condition group(s), kept as they are.";
    }

    public bool IsNew { get; }
    public string WindowTitle => IsNew ? "New rule" : $"Edit rule: {_original.Name}";
    public string? NestedGroupsNote { get; }
    public ObservableCollection<ConditionRowViewModel> Conditions { get; } = new();
    public ObservableCollection<string> Problems { get; } = new();
    public ObservableCollection<string> TestMatches { get; } = new();
    public OrganizeRule? Result { get; private set; }

    public static IReadOnlyList<Choice<RuleActionKind>> ActionKinds { get; } = new[]
    {
        new Choice<RuleActionKind>(RuleActionKind.MoveToFolder, "Move to folder"),
        new Choice<RuleActionKind>(RuleActionKind.Rename, "Rename"),
        new Choice<RuleActionKind>(RuleActionKind.Quarantine, "Quarantine (your call, restorable)"),
        new Choice<RuleActionKind>(RuleActionKind.LeaveInPlace, "Leave where it is"),
    };
    public static IReadOnlyList<Choice<NameClashPolicy>> ClashPolicies { get; } = new[]
    {
        new Choice<NameClashPolicy>(NameClashPolicy.AutoNumber, "Add a number: cat (2).jpg"),
        new Choice<NameClashPolicy>(NameClashPolicy.Skip, "Skip the file"),
    };

    [ObservableProperty] private string _name;
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private bool _matchAll;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowDestination), nameof(ShowRename), nameof(ShowQuarantineOption))]
    private RuleActionKind _actionKind;
    [ObservableProperty] private string _destination;
    [ObservableProperty] private string _renamePattern;
    [ObservableProperty] private NameClashPolicy _clashPolicy;
    [ObservableProperty] private bool _runAutomatically;
    [ObservableProperty] private bool _allowAutomaticQuarantine;
    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private string _testSummary = "";

    public bool ShowDestination => ActionKind == RuleActionKind.MoveToFolder;
    public bool ShowRename => ActionKind == RuleActionKind.Rename;
    public bool ShowQuarantineOption => ActionKind == RuleActionKind.Quarantine;

    public event EventHandler<bool>? CloseRequested;

    public OrganizeRule BuildRule()
    {
        var rule = _original.Clone();
        rule.Name = string.IsNullOrWhiteSpace(Name) ? "My rule" : Name.Trim();
        rule.Enabled = Enabled;
        rule.Conditions = new ConditionGroup
        {
            Mode = MatchAll ? MatchMode.All : MatchMode.Any,
            Conditions = Conditions.Select(c => c.ToCondition()).ToList(),
            Groups = _original.Conditions.Groups.Select(g => g.Clone()).ToList(),
        };
        rule.Action = new RuleAction { Kind = ActionKind, Destination = Destination?.Trim() ?? "", RenamePattern = RenamePattern?.Trim() ?? "{name}" };
        rule.ClashPolicy = ClashPolicy;
        rule.RunAutomatically = RunAutomatically;
        rule.AllowAutomaticQuarantine = AllowAutomaticQuarantine && ActionKind == RuleActionKind.Quarantine;
        return rule;
    }

    private bool ShowProblems(OrganizeRule rule)
    {
        Problems.Clear();
        var problems = _validate(rule);
        foreach (var p in problems) Problems.Add((p.Blocking ? "" : "Note: ") + p.Message);
        return problems.All(p => !p.Blocking);
    }

    [RelayCommand]
    private void AddCondition() => Conditions.Add(new ConditionRowViewModel(RuleCondition.Ext("")));

    [RelayCommand]
    private void RemoveCondition(ConditionRowViewModel? row)
    {
        if (row is not null) Conditions.Remove(row);
    }

    [RelayCommand]
    private void BrowseDestination()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Move matching files to…" };
        if (dialog.ShowDialog() == true) Destination = dialog.FolderName;
    }

    /// <summary>"Test this rule": shows what it would catch. Moves nothing.</summary>
    [RelayCommand]
    private async Task TestAsync()
    {
        var rule = BuildRule();
        if (!ShowProblems(rule)) { TestSummary = "Fix the problems above first."; return; }
        IsTesting = true;
        TestMatches.Clear();
        try
        {
            var result = await _test(rule);
            foreach (var m in result.Matches.Take(500)) TestMatches.Add(m);
            TestSummary = result.Matches.Count switch
            {
                0 => "No files match right now.",
                1 => "1 file matches.",
                var n => $"{n:N0} files match.",
            } + (result.CaughtByEarlierRule > 0 ? $" {result.CaughtByEarlierRule:N0} more are caught by a rule higher up." : "") + " Nothing was moved.";
            foreach (var note in result.Notes) Problems.Add("Note: " + note);
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand]
    private void Save()
    {
        var rule = BuildRule();
        if (!ShowProblems(rule)) return;
        Result = rule;
        CloseRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);
}
