using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Organize.Rename;

namespace PcTidy.Features.Organize.UI;

public sealed partial class RenamePreviewRow : ObservableObject
{
    private readonly Action _changed;

    public RenamePreviewRow(RenameRow row, Action changed)
    {
        Row = row;
        _changed = changed;
        _isChecked = row.CanRename;
    }

    public RenameRow Row { get; }
    public string OldName => Row.OldName;
    public string NewName => Row.NewName;
    public bool CanRename => Row.CanRename;
    public string? Note => Row.Problem ?? Row.Warning ?? (Row.Changed ? null : "No change");
    public bool HasProblem => Row.Problem is not null;

    [ObservableProperty] private bool _isChecked;
    partial void OnIsCheckedChanged(bool value) => _changed();
}

/// <summary>Rename helper tab: live before/after preview with per-file ticks. Renames run through the shell's confirm.</summary>
public sealed partial class RenameViewModel : ObservableObject
{
    private readonly IFlowHost _flow;
    private readonly Func<string?> _folder;
    private List<RenameSource> _files = new();
    private string? _loadedFolder;

    public RenameViewModel(IFlowHost flow, Func<string?> folder)
    {
        _flow = flow;
        _folder = folder;
    }

    public ObservableCollection<RenamePreviewRow> Rows { get; } = new();
    public IReadOnlyList<NameCase> CaseOptions { get; } = Enum.GetValues<NameCase>();
    public IReadOnlyList<NumberPosition> NumberPositions { get; } = Enum.GetValues<NumberPosition>();

    [ObservableProperty] private string _find = "";
    [ObservableProperty] private string _replace = "";
    [ObservableProperty] private bool _useRegex;
    [ObservableProperty] private bool _matchCase;
    [ObservableProperty] private bool _includeExtension;
    [ObservableProperty] private NameCase _case = NameCase.Unchanged;
    [ObservableProperty] private bool _addNumber;
    [ObservableProperty] private int _numberStart = 1;
    [ObservableProperty] private int _numberDigits = 2;
    [ObservableProperty] private NumberPosition _numberPosition = NumberPosition.Suffix;
    [ObservableProperty] private bool _fixMessyNames = true;
    [ObservableProperty] private bool _lowercaseExtension;
    [ObservableProperty] private string _summary = "Load a folder to start.";
    [ObservableProperty] private string _renameButtonText = "Rename files";
    [ObservableProperty] private string _folderText = "";

    partial void OnFindChanged(string value) => Refresh();
    partial void OnReplaceChanged(string value) => Refresh();
    partial void OnUseRegexChanged(bool value) => Refresh();
    partial void OnMatchCaseChanged(bool value) => Refresh();
    partial void OnIncludeExtensionChanged(bool value) => Refresh();
    partial void OnCaseChanged(NameCase value) => Refresh();
    partial void OnAddNumberChanged(bool value) => Refresh();
    partial void OnNumberStartChanged(int value) => Refresh();
    partial void OnNumberDigitsChanged(int value) => Refresh();
    partial void OnNumberPositionChanged(NumberPosition value) => Refresh();
    partial void OnFixMessyNamesChanged(bool value) => Refresh();
    partial void OnLowercaseExtensionChanged(bool value) => Refresh();

    /// <summary>Lists the loose files of the chosen folder (read-only, off the UI thread).</summary>
    [RelayCommand]
    private async Task LoadAsync()
    {
        var folder = _folder();
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) { Summary = "Pick a folder at the top first."; return; }
        _loadedFolder = folder;
        FolderText = folder;
        _files = await Task.Run(() =>
        {
            var list = new List<RenameSource>();
            new ManagedFileWalker().Walk(folder, new WalkOptions { Recurse = false, IncludeHidden = false }, e =>
            {
                if (!e.IsDirectory && !e.IsReparsePoint && !e.IsCloudPlaceholder) list.Add(RenameSource.FromEntry(e));
                return false;
            }, CancellationToken.None);
            return list.OrderBy(f => Path.GetFileName(f.Path), StringComparer.CurrentCultureIgnoreCase).ToList();
        });
        Refresh();
    }

    private RenameOptions Options() => new()
    {
        Find = Find, Replace = Replace, UseRegex = UseRegex, MatchCase = MatchCase, IncludeExtension = IncludeExtension,
        Case = Case, AddNumber = AddNumber, NumberStart = NumberStart, NumberDigits = NumberDigits, NumberPosition = NumberPosition,
        FixMessyNames = FixMessyNames, LowercaseExtension = LowercaseExtension,
    };

    private void Refresh()
    {
        Rows.Clear();
        if (_files.Count == 0) { Summary = _loadedFolder is null ? "Load a folder to start." : "No loose files here."; UpdateButton(); return; }
        foreach (var row in RenameHelper.Preview(_files, Options())) Rows.Add(new RenamePreviewRow(row, UpdateButton));
        UpdateButton();
    }

    private void UpdateButton()
    {
        var ticked = Rows.Count(r => r.IsChecked && r.CanRename);
        var problems = Rows.Count(r => r.HasProblem);
        Summary = $"{Rows.Count(r => r.CanRename):N0} of {Rows.Count:N0} names change" + (problems > 0 ? $" · {problems:N0} can't (see notes)" : "");
        RenameButtonText = ticked == 1 ? "Rename 1 file" : $"Rename {ticked:N0} files";
        RenameCommand.NotifyCanExecuteChanged();
    }

    private bool CanRename() => Rows.Any(r => r.IsChecked && r.CanRename);

    [RelayCommand(CanExecute = nameof(CanRename))]
    private async Task RenameAsync()
    {
        var items = RenameHelper.ToPlanItems(Rows.Where(r => r.IsChecked).Select(r => r.Row));
        var plan = new ActionPlan { FeatureId = OrganizeModule.FeatureId, Title = $"Rename files in {Path.GetFileName(_loadedFolder)}", Items = items };
        var run = await _flow.ReviewAndRunAsync(plan, CancellationToken.None);
        if (run is not null) await LoadAsync();
    }
}
