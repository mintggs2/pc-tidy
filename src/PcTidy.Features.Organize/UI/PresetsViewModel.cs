using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcTidy.Features.Organize.Rules;

namespace PcTidy.Features.Organize.UI;

/// <summary>One line of the preset preview.</summary>
public sealed record PresetChangeRow(string Kind, string RuleName, string Text, string Icon)
{
    public string AutomationName => $"{Kind}: {RuleName}. {Text}";
}

/// <summary>"Presets…": pick a rule set, see exactly what it changes in the rule list, then apply (or don't).</summary>
public sealed partial class PresetsViewModel : ObservableObject
{
    private readonly IReadOnlyList<OrganizeRule> _current;

    public PresetsViewModel(IReadOnlyList<OrganizeRule> current)
    {
        _current = current;
        Presets = RulePresets.All;
        SelectedPreset = Presets[0];
    }

    public IReadOnlyList<RulePreset> Presets { get; }
    public ObservableCollection<PresetChangeRow> Changes { get; } = new();

    [ObservableProperty] private RulePreset? _selectedPreset;
    [ObservableProperty] private string _changeSummary = "";

    /// <summary>The new rule list after Apply; null while nothing was applied.</summary>
    public List<OrganizeRule>? Result { get; private set; }

    /// <summary>Raised when the dialog should close (true = applied).</summary>
    public event Action<bool>? CloseRequested;

    partial void OnSelectedPresetChanged(RulePreset? value)
    {
        Changes.Clear();
        if (value is null) { ChangeSummary = ""; return; }
        var changes = RulePresets.Preview(_current, value);
        foreach (var c in changes.OrderBy(c => c.Kind == RuleChangeKind.Kept ? 1 : 0))
            Changes.Add(new PresetChangeRow(KindText(c.Kind), c.RuleName, c.Text, c.Kind switch
            {
                RuleChangeKind.Added => "AddCircle24",
                RuleChangeKind.Removed => "SubtractCircle24",
                RuleChangeKind.Changed => "Edit24",
                _ => "CheckmarkCircle24",
            }));
        int changed = changes.Count(c => c.Kind != RuleChangeKind.Kept);
        ChangeSummary = changed == 0
            ? "Your rules already match this preset. Nothing would change."
            : $"{changed} rule change{(changed == 1 ? "" : "s")}. Your own rules stay at the top, as they are. Nothing moves until you scan and confirm.";
        ApplyCommand.NotifyCanExecuteChanged();
    }

    private static string KindText(RuleChangeKind k) => k switch
    {
        RuleChangeKind.Added => "New",
        RuleChangeKind.Removed => "Removed",
        RuleChangeKind.Changed => "Changed",
        _ => "Same",
    };

    private bool CanApply() => SelectedPreset is not null && Changes.Any(c => c.Kind != "Same");

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        if (SelectedPreset is null) return;
        Result = RulePresets.Apply(_current, SelectedPreset);
        CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);
}
