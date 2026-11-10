using System.Windows.Controls;
using PcTidy.Core;
using PcTidy.Features.DriveMove.Drives;
using PcTidy.UI.Controls;
using PcTidy.UI.Flow;

namespace PcTidy.Features.DriveMove.UI;

/// <summary>
/// The Move to drive page: Drive balance · Folders · User folders · Games · App caches. Each tab scans only when opened
/// (Drive balance as soon as the page shows) and changes nothing until the user confirms through the shell's flow host.
/// On a PC with one drive every tab shows the friendly "not available" state. Works at 1000 px wide: only the lists scroll.
/// </summary>
public partial class DriveMovePage : UserControl, IFixedHeightPage
{
    private readonly DriveMoveTabView[] _tabs;

    public DriveMovePage(IServiceProvider services)
    {
        InitializeComponent();
        RiskPill.Text = "Check each item";
        RiskPill.Tone = PillTone.Caution;

        var drives = DriveList.From(services.Get<IKnownLocations>(), services.GetService(typeof(ISystemInfo)) as ISystemInfo);
        var available = drives.MovingAvailable;
        var why = drives.UnavailableReason ?? "";

        _tabs = new[]
        {
            new DriveMoveTabView(services, new TabSpec("Drive balance",
                "How full each drive is, the biggest things that could move, and what to move first.",
                new DriveBalanceScanner(), available, why, AutoScan: true, ShowDriveStrip: true)),
            new DriveMoveTabView(services, new TabSpec("Folders",
                "Any folder to another drive: copied, checked, and a link left at the old place so apps still find it.",
                new FolderMoveScanner(), available, why, ShowFolderPicker: true)),
            new DriveMoveTabView(services, new TabSpec("User folders",
                "Downloads, Videos, Music and more, moved with Windows' own Location setting into a new folder.",
                new UserFolderScanner(), available, why)),
            new DriveMoveTabView(services, new TabSpec("Games",
                "Games by drive, size and last played. They move with their launcher's own Move, never by copying.",
                new GameDriveScanner(), available, why)),
            new DriveMoveTabView(services, new TabSpec("App caches",
                "Developer, AI and editing caches, each pointed to another drive with the tool's own setting.",
                new AppCacheScanner(), available, why)),
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
