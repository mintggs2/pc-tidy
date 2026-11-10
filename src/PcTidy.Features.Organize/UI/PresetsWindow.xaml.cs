using PcTidy.UI.Controls;

namespace PcTidy.Features.Organize.UI;

/// <summary>"Presets…": what a ready-made rule set would change; only "Use this preset" changes the rules.</summary>
public partial class PresetsWindow : TidyWindow
{
    public PresetsWindow(PresetsViewModel model)
    {
        InitializeComponent();
        DataContext = model;
        // Only ever shown with ShowDialog; a window that was never shown (UI tests) just ignores it.
        model.CloseRequested += applied => { if (IsVisible) DialogResult = applied; };
    }
}
