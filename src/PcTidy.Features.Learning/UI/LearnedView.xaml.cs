using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace PcTidy.Features.Learning.UI;

/// <summary>"What PC Tidy has learned" page. Asks before forgetting everything; edits a suggested rule in a small dialog.</summary>
public partial class LearnedView : UserControl, ILearnedDialogs
{
    private readonly LearnedViewModel _model;

    public LearnedView(LearnedViewModel model)
    {
        InitializeComponent();
        _model = model;
        _model.Dialogs = this;
        DataContext = model;
        Loaded += async (_, _) =>
        {
            _model.Attach();
            await _model.LoadAsync();
        };
        Unloaded += (_, _) => _model.Detach();
        ForgetButton.IsEnabledChanged += (_, _) => UpdateForgetColor();
        UpdateForgetColor();
    }

    /// <summary>Red text only while "Forget everything" can be pressed; greyed like any other button when nothing is learned.</summary>
    private void UpdateForgetColor()
    {
        if (ForgetButton.IsEnabled) ForgetButton.SetResourceReference(ForegroundProperty, "SystemFillColorCriticalBrush");
        else ForgetButton.ClearValue(ForegroundProperty);
    }

    public async Task<bool> ConfirmForgetAsync()
    {
        var box = new MessageBox
        {
            Title = "Forget everything?",
            Content = "PC Tidy deletes everything it learned: things you skip, suggested rules, folders, apps and drive readings. "
                      + "This can't be undone. Your files, rules and settings aren't touched.",
            PrimaryButtonText = "Forget everything",
            PrimaryButtonAppearance = ControlAppearance.Danger,
            CloseButtonText = "Cancel",
            Owner = Window.GetWindow(this),
        };
        return await box.ShowDialogAsync() == MessageBoxResult.Primary;
    }

    public async Task<(string Name, string Destination)?> EditRuleAsync(string name, string destination)
    {
        var nameBox = new TextBox { Text = name, Margin = new Thickness(0, 4, 0, 12) };
        var destBox = new TextBox { Text = destination, Margin = new Thickness(0, 4, 0, 0) };
        AutomationProperties.SetName(nameBox, "Rule name");
        AutomationProperties.SetName(destBox, "Destination folder");
        var panel = new StackPanel { MinWidth = 380 };
        panel.Children.Add(new TextBlock { Text = "Name" });
        panel.Children.Add(nameBox);
        panel.Children.Add(new TextBlock { Text = "Move files to this folder" });
        panel.Children.Add(destBox);
        var box = new MessageBox
        {
            Title = "Edit suggested rule",
            Content = panel,
            PrimaryButtonText = "Save",
            PrimaryButtonAppearance = ControlAppearance.Primary,
            CloseButtonText = "Cancel",
            Owner = Window.GetWindow(this),
        };
        return await box.ShowDialogAsync() == MessageBoxResult.Primary ? (nameBox.Text ?? "", destBox.Text ?? "") : null;
    }

    public void Show(string title, string message)
    {
        if (Window.GetWindow(this) is null) return;
        var box = new MessageBox { Title = title, Content = message, CloseButtonText = "OK", Owner = Window.GetWindow(this) };
        _ = box.ShowDialogAsync();
    }
}
