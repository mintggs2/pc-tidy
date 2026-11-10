using PcTidy.UI.Controls;

namespace PcTidy.Features.Organize.UI;

public partial class RuleEditorWindow : TidyWindow
{
    public RuleEditorWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is RuleEditorViewModel old) old.CloseRequested -= OnCloseRequested;
            if (e.NewValue is RuleEditorViewModel vm) vm.CloseRequested += OnCloseRequested;
        };
    }

    private void OnCloseRequested(object? sender, bool saved) => DialogResult = saved;
}
