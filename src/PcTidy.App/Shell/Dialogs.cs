using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace PcTidy.App.Shell;

/// <summary>Small shared dialogs.</summary>
public static class Dialogs
{
    /// <summary>
    /// Typed confirmation for permanent deletes: the user must type the word shown. The safe choice
    /// ("Keep them") is the default button; the delete button stays disabled until the word matches.
    /// </summary>
    public static async Task<bool> ConfirmTypedAsync(FlowHost host, ContentDialogHost dialogHost, string title, string message, string actionText, string word = "delete")
    {
        var box = new Wpf.Ui.Controls.TextBox { PlaceholderText = $"Type {word}", Margin = new Thickness(0, 12, 0, 0) };
        AutomationProperties.SetName(box, $"Type {word} to confirm");
        var body = new StackPanel { Width = 480 };
        body.Children.Add(new InfoBar { Title = "This can't be undone.", Message = message, IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Error });
        var hint = new TextBlock { Text = $"To confirm, type \"{word}\" below.", Margin = new Thickness(0, 14, 0, 0), TextWrapping = TextWrapping.Wrap };
        hint.SetResourceReference(FrameworkElement.StyleProperty, "Secondary");
        body.Children.Add(hint);
        body.Children.Add(box);
        var dialog = new ContentDialog(dialogHost)
        {
            Title = title,
            Content = body,
            PrimaryButtonText = actionText,
            PrimaryButtonAppearance = ControlAppearance.Danger,
            CloseButtonText = "Keep them",
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false,
            DialogMaxWidth = 560,
        };
        box.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = string.Equals(box.Text?.Trim(), word, StringComparison.CurrentCultureIgnoreCase);
        dialog.Loaded += (_, _) => box.Focus();
        return await host.ShowDialogAsync(dialog) == ContentDialogResult.Primary;
    }
}
