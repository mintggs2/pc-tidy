using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcTidy.Core;
using PcTidy.Features.Learning.Advisor;
using Wpf.Ui.Controls;

namespace PcTidy.Features.Learning.UI;

/// <summary>The questions the page asks the user (implemented by the view; faked in tests).</summary>
public interface ILearnedDialogs
{
    Task<bool> ConfirmForgetAsync();
    /// <summary>Edit a suggested rule's name and destination; null = cancelled.</summary>
    Task<(string Name, string Destination)?> EditRuleAsync(string name, string destination);
    void Show(string title, string message);
}

/// <summary>A heading between groups of cards.</summary>
public sealed record SectionRow(string Title, string Subtitle);

/// <summary>"What PC Tidy has learned": every learned item as a card, plus Pause learning and Forget everything.</summary>
public sealed partial class LearnedViewModel : ObservableObject
{
    private static readonly (LearnedKind Kind, string Title, string Subtitle)[] Sections =
    {
        (LearnedKind.Rule, "Suggested rules", "Patterns in files you moved yourself. Nothing moves until you make one a rule and confirm it."),
        (LearnedKind.Skip, "Things you usually skip", "These start unticked. They are still listed, and you can tick them any time."),
        (LearnedKind.Folder, "Folders you use most", "Offered first in Favorites and folder suggestions. Older visits fade over two weeks."),
        (LearnedKind.App, "Apps you don't open", "Only apps PC Tidy has watched for 30 days or more, and never apps that start with Windows. Nothing is uninstalled."),
        (LearnedKind.Drive, "How fast your drives fill", "From one free-space reading a day. PC Tidy's own clean-ups are left out."),
        (LearnedKind.QuietHours, "When your PC is free", "Used to suggest a time for long scans."),
    };

    private readonly LearningAdvisor _advisor;
    private readonly IServiceProvider _services;
    private int _loadVersion;

    public LearnedViewModel(IServiceProvider services, LearningAdvisor advisor)
    {
        _services = services;
        _advisor = advisor;
    }

    /// <summary>Follows changes while the page is shown (the view calls this on Loaded, <see cref="Detach"/> on Unloaded).</summary>
    public void Attach()
    {
        _advisor.Changed -= OnAdvisorChanged;
        _advisor.Changed += OnAdvisorChanged;
    }

    public void Detach() => _advisor.Changed -= OnAdvisorChanged;

    private void OnAdvisorChanged(object? sender, EventArgs e) => RunOnUi(() => _ = LoadAsync());

    /// <summary>Set by the view.</summary>
    public ILearnedDialogs? Dialogs { get; set; }

    /// <summary>Section headings and cards, in page order (one virtualized list).</summary>
    public ObservableCollection<object> Rows { get; } = new();

    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private bool _isOffInSettings;
    [ObservableProperty] private bool _readWindowsRecent;
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string? _problem;
    [ObservableProperty] private string _watchingNote = "";

    public string PauseText => IsPaused ? "Resume learning" : "Pause learning";
    public SymbolRegular PauseIcon => IsPaused ? SymbolRegular.Play24 : SymbolRegular.Pause24;
    public string PausedNote => IsOffInSettings
        ? "Learning is switched off in Settings. What was learned below is kept until you delete it."
        : "Learning is paused: nothing new is recorded. What was learned below still applies.";

    partial void OnIsPausedChanged(bool value)
    {
        OnPropertyChanged(nameof(PauseText));
        OnPropertyChanged(nameof(PauseIcon));
        OnPropertyChanged(nameof(PausedNote));
    }

    partial void OnIsOffInSettingsChanged(bool value) => OnPropertyChanged(nameof(PausedNote));

    partial void OnReadWindowsRecentChanged(bool value)
    {
        if (_advisor.ReadWindowsRecent != value) _advisor.ReadWindowsRecent = value;
    }

    public async Task LoadAsync()
    {
        int version = Interlocked.Increment(ref _loadVersion);
        IsLoading = Rows.Count == 0;
        var (items, apps, paused, offInSettings, recent, since, problem) = await Task.Run(() =>
        {
            var list = _advisor.LearnedItems();
            return (list, _advisor.UnusedAppsReport(), _advisor.IsPaused, _advisor.IsOffInSettings, _advisor.ReadWindowsRecent, _advisor.StartedUtc, _advisor.Problem);
        });
        if (version != _loadVersion) return; // a newer load is on its way

        Rows.Clear();
        foreach (var (kind, title, subtitle) in Sections)
        {
            var cards = items.Where(i => i.Kind == kind).ToList();
            if (cards.Count == 0) continue;
            Rows.Add(new SectionRow(title, subtitle));
            foreach (var item in cards) Rows.Add(new LearnedCardModel(item, this));
        }
        IsPaused = paused;
        IsOffInSettings = offInSettings;
        ReadWindowsRecent = recent;
        Problem = problem;
        IsEmpty = items.Count == 0;
        Subtitle = $"Learning on this PC since {since.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture)}. Nothing learned ever changes anything by itself.";
        WatchingNote = apps.DaysUntilReady > 0
            ? $"Apps you don't open: PC Tidy needs {apps.DaysUntilReady} more day{(apps.DaysUntilReady == 1 ? "" : "s")} of use before it can tell."
            : "";
        IsLoading = false;
    }

    [RelayCommand]
    private void TogglePause()
    {
        if (IsOffInSettings) return;
        _advisor.SetPaused(!IsPaused);
    }

    /// <summary>Nothing to forget while nothing is learned, so the button is off then.</summary>
    private bool CanForget() => !IsEmpty;

    partial void OnIsEmptyChanged(bool value) => ForgetEverythingCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanForget))]
    private async Task ForgetEverythingAsync()
    {
        if (Dialogs is not null && !await Dialogs.ConfirmForgetAsync()) return;
        try
        {
            await Task.Run(_advisor.ForgetEverything);
            Dialogs?.Show("Everything forgotten", "PC Tidy deleted what it learned. It starts learning again from now (unless learning is paused).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dialogs?.Show("Couldn't delete it", "The learning file is in use or can't be changed right now. Close other PC Tidy windows and try again.");
        }
    }

    internal void TurnOff(LearnedCardModel card) => _advisor.SetOff(card.Item, !card.Item.Off);

    internal void Delete(LearnedCardModel card) => _advisor.Delete(card.Item);

    internal async Task EditAsync(LearnedCardModel card)
    {
        if (Dialogs is null || _advisor.RuleOf(card.Item) is not { } rule) return;
        var result = await Dialogs.EditRuleAsync(rule.Name, rule.Destination);
        if (result is not { } edit) return;
        if (!Path.IsPathFullyQualified(edit.Destination.Trim()))
        {
            Dialogs.Show("Pick a full folder path", "The destination has to be a full folder path, like the one PC Tidy suggested.");
            return;
        }
        _advisor.EditRule(card.Item, edit.Name, edit.Destination);
    }

    internal void MakeRule(LearnedCardModel card)
    {
        var sink = _services.GetService(typeof(IOrganizeRuleSink)) as IOrganizeRuleSink
                   ?? new OrganizeSettingsRuleSink(_services.Get<AppSettings>());
        var result = _advisor.MakeRule(card.Item, sink);
        Dialogs?.Show(result.Added ? "Rule added to Organize" : "Not added", result.Message);
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}

/// <summary>One learned item as a card: what, why, counts, examples, since when, and its buttons.</summary>
public sealed partial class LearnedCardModel : ObservableObject
{
    private readonly LearnedViewModel _owner;

    public LearnedCardModel(LearnedItem item, LearnedViewModel owner)
    {
        Item = item;
        _owner = owner;
    }

    public LearnedItem Item { get; }
    public string Title => Item.Title;
    public string Reason => Item.Reason;
    public string Counts => Item.Counts;
    public string Effect => Item.Effect;
    public string ExamplesText => Item.Examples.Count == 0 ? "" : "For example: " + string.Join(", ", Item.Examples);
    public bool HasExamples => Item.Examples.Count > 0;
    public string SinceText => "Since " + Item.SinceUtc.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture);
    public bool IsOff => Item.Off;
    public string OffText => Item.Off ? "Turn on" : "Turn off";
    public bool CanEdit => Item.Kind == LearnedKind.Rule;
    public bool CanMakeRule => Item.Kind == LearnedKind.Rule && !Item.Off;
    public SymbolRegular Icon => Item.Kind switch
    {
        LearnedKind.Rule => SymbolRegular.FolderArrowRight24,
        LearnedKind.Skip => SymbolRegular.CheckboxUnchecked24,
        LearnedKind.Folder => SymbolRegular.Folder24,
        LearnedKind.App => SymbolRegular.Apps24,
        LearnedKind.Drive => SymbolRegular.Storage24,
        _ => SymbolRegular.WeatherMoon24,
    };

    public string AutomationName => $"{Title}. {Reason}";

    [RelayCommand] private void MakeRule() => _owner.MakeRule(this);
    [RelayCommand] private Task EditAsync() => _owner.EditAsync(this);
    [RelayCommand] private void ToggleOff() => _owner.TurnOff(this);
    [RelayCommand] private void Delete() => _owner.Delete(this);
}
