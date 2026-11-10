using System.Windows.Controls;
using PcTidy.UI.Controls;
using PcTidy.UI.Flow;

namespace PcTidy.Features.Speed.UI;

/// <summary>
/// The Speed page: Startup · Running now · Settings · Drives · Right-click menu · What's locking. Each tab reads
/// only when first opened (What's locking waits for you to pick something) and changes nothing until you confirm
/// through the shell's flow host. Works at 1000 px wide: only the lists scroll.
/// </summary>
public partial class SpeedPage : UserControl, IFixedHeightPage
{
    private readonly SpeedTabView[] _tabs;

    public SpeedPage(IServiceProvider services, SpeedModule module)
    {
        InitializeComponent();
        RiskPill.Text = "Check each item";
        RiskPill.Tone = PillTone.Caution;

        _tabs = new[]
        {
            new SpeedTabView(services, new SpeedTabSpec("Startup",
                "Apps, tasks and services that start with Windows, with who made them and how heavy they are. Switching one off works like Task Manager.",
                module.Startup, "Rocket24", ShowHideMicrosoft: true, ShowCopyPath: true)),
            new SpeedTabView(services, new SpeedTabSpec("Running now",
                "What's using your processor and memory right now. Close asks an app to close, like clicking its X.",
                module.Running, "TopSpeed24")),
            new SpeedTabView(services, new SpeedTabSpec("Settings",
                "Power plan, Game Mode, GPU scheduling and visual effects: how each is set and what we recommend.",
                module.SettingsCards, "Options24", ShowCopyPath: true)),
            new SpeedTabView(services, new SpeedTabSpec("Drives",
                "Health of each disk, free space, TRIM and Windows' own optimizer. An SSD is never defragmented.",
                module.Drives, "HardDrive20")),
            new SpeedTabView(services, new SpeedTabSpec("Right-click menu",
                "What apps added to Explorer's right-click menu, and entries whose file is gone. Hiding never deletes anything.",
                module.ContextMenu, "TextBulletListSquare24", ShowHideMicrosoft: true, ShowCopyPath: true)),
            new SpeedTabView(services, new SpeedTabSpec("What's locking",
                "Can't delete or move something because it's in use? Pick it to see which app has it open.",
                module.Locking, "LockClosed24", PicksPath: true, AutoScan: false)),
        };

        Tabs.SelectionChanged += (_, _) => ShowTab(Tabs.SelectedIndex);
        ShowTab(0);
    }

    private void ShowTab(int index)
    {
        if (index < 0 || index >= _tabs.Length) return;
        TabHost.Content = _tabs[index];
        _tabs[index].EnsureScanned();
    }
}
