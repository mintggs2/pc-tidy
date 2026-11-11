using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using PcTidy.App.Shell;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Platform;
using PcTidy.UI.Flow;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace PcTidy.App.Pages;

/// <summary>
/// Quarantine page: what PC Tidy's admin helper quarantined (Windows temp, error reports, crash dumps…). Those files
/// sit in an admin-only folder, so even listing them needs the helper: the section stays closed until the user asks
/// ("Show them"), and says each action asks Windows for admin rights once. Restore = undo of that run (the normal
/// undo, through the admin helper); "Delete for good" = typed confirmation, then the helper's purge (it refuses runs
/// its own ledger doesn't show it quarantined). Hidden when there is no admin helper (demo mode).
/// </summary>
public sealed class AdminQuarantineSection : UserControl
{
    private readonly ElevationService _elevation;
    private readonly IRunJournal _journal;
    private readonly FlowHost _host;
    private readonly Func<MainWindow> _window;
    private readonly bool _practice;
    private readonly StackPanel _runs = new();

    private AdminQuarantineSection(ElevationService elevation, IRunJournal journal, FlowHost host, Func<MainWindow> window, bool practice)
    {
        _elevation = elevation;
        _journal = journal;
        _host = host;
        _window = window;
        _practice = practice;
        Content = BuildClosed();
        AutomationProperties.SetName(this, "Quarantined with admin rights");
    }

    /// <summary>Null when this PC Tidy has no admin helper (then there is no admin quarantine to show).</summary>
    public static AdminQuarantineSection? TryCreate(IServiceProvider services, FlowHost host, Func<MainWindow> window, bool practice)
        => services.GetService(typeof(IElevationService)) is ElevationService e && services.GetService(typeof(IRunJournal)) is IRunJournal j
            ? new AdminQuarantineSection(e, j, host, window, practice)
            : null;

    private string PromptNote => _practice
        ? "In practice mode no admin prompt appears; the practice PC's admin quarantine is shown directly."
        : "Showing, restoring or deleting them asks Windows for admin rights (one prompt).";

    private UIElement BuildClosed()
    {
        var show = new Button { Content = "Show them", Icon = new SymbolIcon(SymbolRegular.ShieldKeyhole24) };
        AutomationProperties.SetName(show, "Show the items quarantined with admin rights (asks for admin rights)");
        show.Click += (_, _) => LoadAsync().Forget(_host, "Couldn't read the admin quarantine");
        return Card("Quarantined with admin rights (Windows temp, error reports…)",
            "Items from protected Windows folders wait in a folder only admins can open, so they aren't listed above. " + PromptNote, show);
    }

    private async Task LoadAsync()
    {
        AdminQuarantineListing listing;
        IReadOnlyList<RunRecord> runs;
        var window = _window();
        window.ShowProgress("Asking PC Tidy's admin helper…", canStop: false);
        try
        {
            listing = await Task.Run(() => _elevation.ListAdminQuarantineAsync(CancellationToken.None));
            runs = await Task.Run(() => _journal.ListAsync(CancellationToken.None));
        }
        finally { window.HideProgress(); }

        if (listing.Problem is { } problem && listing.Entries.Count == 0)
        {
            _host.ShowMessage("Couldn't show them", problem + " Nothing was changed.", isError: true);
            return;
        }
        var live = listing.Entries.Where(e => !e.Restored && !e.Purged).ToList();
        _runs.Children.Clear();
        foreach (var g in live.GroupBy(e => e.RunId).OrderByDescending(g => g.Max(e => e.QuarantinedUtc)))
        {
            var run = runs.FirstOrDefault(r => r.RunId == g.Key);
            _runs.Children.Add(RunRow(g.Key, run, g.ToList()));
        }
        var refresh = new Button { Content = "Refresh", Appearance = ControlAppearance.Transparent };
        refresh.Click += (_, _) => LoadAsync().Forget(_host, "Couldn't read the admin quarantine");
        var body = new StackPanel();
        body.Children.Add(live.Count == 0
            ? Styled(new TextBlock { Text = "Nothing here right now." }, "Secondary")
            : _runs);
        Content = Card($"Quarantined with admin rights · {Sizes.Format(live.Sum(e => e.SizeBytes))}",
            "Restoring puts a run back exactly where it was (like Undo in History). " + PromptNote, refresh, body);
    }

    private UIElement RunRow(string runId, RunRecord? run, IReadOnlyList<QuarantineEntry> entries)
    {
        var title = run?.Title ?? "Earlier run";
        var when = run?.CreatedUtc ?? entries.Min(e => e.QuarantinedUtc);
        var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var restore = new Button { Content = "Restore", Icon = new SymbolIcon(SymbolRegular.ArrowUndo24), Margin = new Thickness(0, 0, 6, 0), IsEnabled = run is not null };
        AutomationProperties.SetName(restore, $"Restore {title} (undo that run, asks for admin rights)");
        restore.Click += (_, _) => RestoreAsync(run!).Forget(_host, "Restore didn't finish");
        var delete = new Button { Content = "Delete for good…", Appearance = ControlAppearance.Transparent };
        AutomationProperties.SetName(delete, $"Delete {title} for good (asks for admin rights)");
        delete.Click += (_, _) => PurgeAsync(runId, title, entries).Forget(_host, "Delete didn't finish");
        actions.Children.Add(restore);
        actions.Children.Add(delete);
        DockPanel.SetDock(actions, Dock.Right);
        row.Children.Add(actions);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Styled(new TextBlock { Text = title }, "BodyStrong"));
        text.Children.Add(Styled(new TextBlock { Text = $"{entries.Count:N0} item{PlanFacts.S(entries.Count)} · {Sizes.Format(entries.Sum(e => e.SizeBytes))} · quarantined {RunSummary.WhenInSentence(when)}" }, "Secondary"));
        row.Children.Add(text);
        return row;
    }

    private async Task RestoreAsync(RunRecord run)
    {
        if (await _host.UndoRunsAsync(new[] { run }, CancellationToken.None)) await LoadAsync();
    }

    private async Task PurgeAsync(string runId, string title, IReadOnlyList<QuarantineEntry> entries)
    {
        var window = _window();
        if (!await Dialogs.ConfirmTypedAsync(_host, window.DialogHost, $"Delete \"{title}\" for good?",
                $"{entries.Count:N0} items ({Sizes.Format(entries.Sum(e => e.SizeBytes))}) quarantined with admin rights will be deleted permanently. Windows asks for admin rights first.", "Delete for good"))
            return;
        string? problem;
        using (_host.Working("the delete"))
        {
            window.ShowProgress("Deleting for good…", canStop: false);
            try { problem = await Task.Run(() => _elevation.PurgeAdminQuarantineAsync(runId, CancellationToken.None)); }
            finally { window.HideProgress(); }
        }
        if (problem is not null) _host.ShowMessage("Not deleted", problem, isError: true);
        await LoadAsync();
    }

    private static Border Card(string title, string text, UIElement action, UIElement? body = null)
    {
        var card = new Border { Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 0, 0, 10) };
        card.SetResourceReference(StyleProperty, "TidyCard");
        var stack = new StackPanel();
        var head = new DockPanel();
        DockPanel.SetDock(action, Dock.Right);
        head.Children.Add(action);
        var titles = new StackPanel();
        titles.Children.Add(Styled(new TextBlock { Text = title }, "CardTitle"));
        titles.Children.Add(Styled(new TextBlock { Text = text, Margin = new Thickness(0, 2, 12, 0) }, "Secondary"));
        head.Children.Add(titles);
        stack.Children.Add(head);
        if (body is not null) { body.SetValue(MarginProperty, new Thickness(0, 6, 0, 0)); stack.Children.Add(body); }
        card.Child = stack;
        return card;
    }

    private static T Styled<T>(T el, string style) where T : FrameworkElement
    {
        el.SetResourceReference(StyleProperty, style);
        if (el is TextBlock tb) tb.TextWrapping = TextWrapping.Wrap;
        return el;
    }
}
