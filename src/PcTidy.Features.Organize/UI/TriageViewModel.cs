using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcTidy.Core;
using PcTidy.Features.Organize.Rules;
using PcTidy.Features.Organize.Triage;

namespace PcTidy.Features.Organize.UI;

/// <summary>A number key and the folder it sends the file to.</summary>
public sealed record TriageTargetRow(string KeyText, string Label, string Folder)
{
    public string AutomationName => $"{KeyText}: move to {Label}";
}

/// <summary>
/// The keyboard triage window: one file at a time with a preview. Every key also has a button, and every change is
/// announced to screen readers through <see cref="Announcement"/>. Done (Enter) finishes; the page then shows ONE plan
/// in the standard confirm. Esc means "stop" as everywhere in Windows: with decisions made it asks whether to review
/// them or discard them, with none it just closes.
/// </summary>
public sealed partial class TriageViewModel : ObservableObject
{
    private static readonly IReadOnlySet<string> PictureExtensions = OrganizeCategories.Find(OrganizeCategories.Pictures)!.Extensions;

    public TriageViewModel(TriageSession session)
    {
        Session = session;
        Targets = session.Targets.Select(t => new TriageTargetRow(t.KeyText, t.Label, t.Action.Destination)).ToList();
        Refresh();
    }

    public TriageSession Session { get; }
    public IReadOnlyList<TriageTargetRow> Targets { get; }
    public ObservableCollection<string> Decisions { get; } = new();

    [ObservableProperty] private string _fileName = "";
    [ObservableProperty] private string _fileDetail = "";
    [ObservableProperty] private string _position = "";
    [ObservableProperty] private string _decisionText = "";
    [ObservableProperty] private string? _suggestion;
    [ObservableProperty] private BitmapSource? _preview;
    [ObservableProperty] private string _previewIcon = "Document24";
    [ObservableProperty] private bool _isRenaming;
    [ObservableProperty] private string _renameText = "";
    [ObservableProperty] private string? _renameProblem;
    /// <summary>Esc was pressed with decisions made: the window shows "Stop sorting?" until the user picks.</summary>
    [ObservableProperty] private bool _isAskingToStop;
    [ObservableProperty] private string _stopQuestion = "";
    /// <summary>What a screen reader should say after each key ("3 of 12: invoice.pdf. Move to Documents").</summary>
    [ObservableProperty] private string _announcement = "";
    public bool HasFiles => Session.Files.Count > 0;

    /// <summary>Raised when the user finished (Done, or "Review my decisions" after Esc): the page builds the plan.</summary>
    public event Action? Finished;
    /// <summary>Raised when the user stopped without a plan (Esc with nothing decided, or "Discard them").</summary>
    public event Action? Cancelled;

    /// <summary>Maps a real key to a triage key. Returns false for keys triage doesn't use (they go to the control).</summary>
    public static bool TryMap(Key key, out TriageKey triageKey)
    {
        triageKey = key switch
        {
            >= Key.D1 and <= Key.D9 => TriageKey.Target1 + (key - Key.D1),
            >= Key.NumPad1 and <= Key.NumPad9 => TriageKey.Target1 + (key - Key.NumPad1),
            Key.K => TriageKey.Keep,
            Key.Q => TriageKey.Quarantine,
            Key.R => TriageKey.Rename,
            Key.Left => TriageKey.Previous,
            Key.Right => TriageKey.Next,
            _ => (TriageKey)(-1),
        };
        return (int)triageKey >= 0;
    }

    public TriageOutcome Press(TriageKey key)
    {
        if (IsRenaming || IsAskingToStop) return TriageOutcome.Ignored;
        var outcome = Session.Press(key);
        switch (outcome)
        {
            case TriageOutcome.NeedsName:
                RenameText = Path.GetFileNameWithoutExtension(Session.Current?.Name ?? "");
                RenameProblem = null;
                IsRenaming = true;
                Announcement = "Type the new name, then press Enter. Escape cancels.";
                break;
            case TriageOutcome.Finished:
                Finished?.Invoke();
                break;
            case TriageOutcome.Decided:
            case TriageOutcome.Navigated:
                Refresh();
                break;
        }
        return outcome;
    }

    [RelayCommand] private void Target(string? number) { if (int.TryParse(number, out var n) && n is >= 1 and <= 9) Press(TriageKey.Target1 + (n - 1)); }
    [RelayCommand] private void Keep() => Press(TriageKey.Keep);
    [RelayCommand] private void Quarantine() => Press(TriageKey.Quarantine);
    [RelayCommand] private void Rename() => Press(TriageKey.Rename);
    [RelayCommand] private void Previous() => Press(TriageKey.Previous);
    [RelayCommand] private void Next() => Press(TriageKey.Next);
    [RelayCommand] private void Finish() => Press(TriageKey.Finish);

    /// <summary>Esc: closes at once when nothing was decided, otherwise asks (a second Esc keeps sorting).</summary>
    public void RequestStop()
    {
        if (IsRenaming) return;
        if (IsAskingToStop) { KeepSorting(); return; }
        int decided = Session.DecidedCount;
        if (decided == 0) { Cancelled?.Invoke(); return; }
        StopQuestion = decided == 1
            ? "Stop sorting? You made 1 decision. Review it before anything moves, or discard it."
            : $"Stop sorting? You made {decided:N0} decisions. Review them before anything moves, or discard them.";
        IsAskingToStop = true;
        Announcement = StopQuestion + " Enter reviews, Escape keeps sorting.";
    }

    [RelayCommand]
    private void ReviewDecisions()
    {
        IsAskingToStop = false;
        Press(TriageKey.Finish);
    }

    [RelayCommand]
    private void DiscardDecisions()
    {
        IsAskingToStop = false;
        Cancelled?.Invoke();
    }

    [RelayCommand]
    private void KeepSorting()
    {
        IsAskingToStop = false;
        Refresh();
    }

    [RelayCommand]
    private void ConfirmRename()
    {
        if (Session.Rename(RenameText) is { } problem)
        {
            RenameProblem = problem;
            Announcement = problem;
            return;
        }
        IsRenaming = false;
        Refresh();
    }

    [RelayCommand]
    private void CancelRename()
    {
        IsRenaming = false;
        RenameProblem = null;
        Refresh();
    }

    private void Refresh()
    {
        Decisions.Clear();
        foreach (var f in Session.Files.Where(f => f.Decision.Kind != TriageDecisionKind.Undecided))
            Decisions.Add($"{f.Name}: {TriageSession.Describe(f.Decision)}");
        if (Session.Current is not { } file)
        {
            FileName = "Nothing to sort here";
            FileDetail = "Every loose file is either sorted already or one PC Tidy never moves (shortcuts, unfinished downloads, files changed in the last 10 minutes).";
            Position = "";
            DecisionText = "";
            Suggestion = null;
            Preview = null;
            Announcement = FileName;
            return;
        }
        FileName = file.Name;
        FileDetail = $"{Sizes.Format(file.Entry.Size)} · changed {file.Entry.LastWriteUtc.ToLocalTime():d MMM yyyy}";
        Position = $"{Session.Index + 1} of {Session.Files.Count} · {Session.DecidedCount} decided";
        DecisionText = TriageSession.Describe(file.Decision);
        Suggestion = file.Suggestion;
        PreviewIcon = Planning.PreviewTree.IconFor(file.Name);
        Preview = null;
        Announcement = $"{Session.Index + 1} of {Session.Files.Count}: {file.Name}. {DecisionText}." + (file.Suggestion is null ? "" : $" {file.Suggestion}.");
        _ = LoadPreviewAsync(file);
    }

    /// <summary>Small picture preview, decoded off the UI thread. Online-only files are never opened.</summary>
    private async Task LoadPreviewAsync(TriageFile file)
    {
        if (file.Entry.IsCloudPlaceholder || !PictureExtensions.Contains(OrganizeCategories.ExtensionOf(file.Name))) return;
        var path = file.Path;
        var bitmap = await Task.Run(() =>
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var b = new BitmapImage();
                b.BeginInit();
                b.CacheOption = BitmapCacheOption.OnLoad;
                b.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                b.DecodePixelWidth = 480;
                b.StreamSource = fs;
                b.EndInit();
                b.Freeze();
                return (BitmapSource?)b;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException
                                           or InvalidOperationException or System.Runtime.InteropServices.COMException or FileFormatException)
            {
                return null;
            }
        });
        // Still the same file on screen?
        if (Session.Current == file) Preview = bitmap;
    }
}
