using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using H.NotifyIcon;

namespace PcTidy.App.Tray;

/// <summary>What the tray menu can ask for. None of these changes anything on its own.</summary>
public sealed record TrayActions(Action Open, Action QuickTidy, Action CheckNow, Action Quit);

/// <summary>
/// The optional tray icon (H.NotifyIcon.Wpf; off by default). Menu: Open, Quick tidy (opens the Optimize everything
/// preview, never cleans silently), Check now, Quit. A left click opens PC Tidy.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly TaskbarIcon _icon;

    public TrayIcon(TrayActions actions)
    {
        _icon = new TaskbarIcon
        {
            ToolTipText = "PC Tidy",
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/PcTidy.ico")),
            ContextMenu = BuildMenu(actions),
            NoLeftClickDelay = true,
        };
        _icon.TrayLeftMouseUp += (_, _) => actions.Open();
        // No "efficiency mode": that would lower PC Tidy's priority for the whole session.
        _icon.ForceCreate(enablesEfficiencyMode: false);
    }

    /// <summary>The tray menu (also used to describe the menu in Settings and the help text).</summary>
    public static ContextMenu BuildMenu(TrayActions a)
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item("Open PC Tidy", a.Open));
        menu.Items.Add(Item("Quick tidy… (shows the preview first)", a.QuickTidy));
        menu.Items.Add(Item("Check now", a.CheckNow));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Quit PC Tidy", a.Quit));
        return menu;
    }

    private static MenuItem Item(string text, Action click)
    {
        var item = new MenuItem { Header = text };
        AutomationProperties.SetName(item, text);
        item.Click += (_, _) => click();
        return item;
    }

    public void Dispose() => _icon.Dispose();
}
