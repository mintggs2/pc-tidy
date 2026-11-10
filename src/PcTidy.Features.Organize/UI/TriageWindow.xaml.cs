using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using PcTidy.UI.Controls;

namespace PcTidy.Features.Organize.UI;

/// <summary>
/// Keyboard triage: the keys work anywhere in the window (except while typing a new name). Each change is announced
/// through a live region, so a screen reader says which file is shown and what was decided. Enter finishes (unless a
/// button has focus: then Enter presses that button); Esc stops, asking first when decisions were made.
/// </summary>
public partial class TriageWindow : TidyWindow
{
    public TriageWindow(TriageViewModel model)
    {
        InitializeComponent();
        Model = model;
        DataContext = model;
        model.PropertyChanged += OnModelChanged;
        // Only ever shown with ShowDialog; a window that was never shown (UI tests) ignores it.
        model.Finished += () => { if (IsVisible) DialogResult = true; };
        model.Cancelled += () => { if (IsVisible) DialogResult = false; };
    }

    public TriageViewModel Model { get; }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Model.IsRenaming) return; // the rename box handles its own Enter / Esc
        // Modifier chords (Alt+F4, Ctrl+…) keep their usual meaning.
        if ((Keyboard.Modifiers & (ModifierKeys.Alt | ModifierKeys.Control)) != 0) return;
        if (e.Key == Key.Escape)
        {
            Model.RequestStop();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter)
        {
            if (Model.IsAskingToStop) Model.ReviewDecisionsCommand.Execute(null);
            else if (Keyboard.FocusedElement is ButtonBase) return; // Enter presses the focused button, as usual
            else Model.FinishCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (!TriageViewModel.TryMap(e.Key, out var key)) return;
        // Left/right browse files here, not move focus; Tab still moves between the buttons.
        Model.Press(key);
        e.Handled = true;
    }

    private void OnRenameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Model.ConfirmRenameCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Escape) { Model.CancelRenameCommand.Execute(null); e.Handled = true; }
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TriageViewModel.IsRenaming) && Model.IsRenaming)
            Dispatcher.BeginInvoke(() => { RenameBox.Focus(); RenameBox.SelectAll(); });
        else if (e.PropertyName == nameof(TriageViewModel.IsAskingToStop) && Model.IsAskingToStop)
            Dispatcher.BeginInvoke(() => ReviewButton.Focus());
        else if (e.PropertyName == nameof(TriageViewModel.Announcement))
        {
            AutomationProperties.SetName(Announcer, Model.Announcement);
            (UIElementAutomationPeer.FromElement(Announcer) ?? UIElementAutomationPeer.CreatePeerForElement(Announcer))
                ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }

    /// <summary>The plan built from every decision (call after the window closed with true).</summary>
    public PcTidy.Core.Model.ActionPlan BuildPlan(string title) => Model.Session.BuildPlan(title);
}
