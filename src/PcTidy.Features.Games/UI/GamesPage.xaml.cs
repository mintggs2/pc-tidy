using System.Windows.Controls;
using PcTidy.Catalog.Games;
using PcTidy.Core;
using PcTidy.Features.Games.Cleanup;
using PcTidy.Features.Games.Clips;
using PcTidy.Features.Games.Library;
using PcTidy.UI.Controls;
using PcTidy.UI.Flow;

namespace PcTidy.Features.Games.UI;

/// <summary>
/// The Games page: Library · Clips &amp; screenshots · Optimize · Cleanup · Tips. Each tab scans only when first opened and
/// changes nothing until the user confirms through the shell's flow host. Works at 1000 px wide: only the lists scroll.
/// Optimize is a master/detail view (<see cref="GameOptimizeView"/>): catalog games can be read before they're installed,
/// so it's available even on a PC without launchers.
/// </summary>
public partial class GamesPage : UserControl, IFixedHeightPage
{
    private readonly UserControl[] _tabs;

    public GamesPage(IServiceProvider services)
    {
        InitializeComponent();
        RiskPill.Text = "Check each item";
        RiskPill.Tone = PillTone.Caution;

        var locations = services.Get<IKnownLocations>();
        var hasGames = GamesAvailability.HasLaunchers(locations);
        const string noGames = "No game launchers (Steam, Epic, GOG, EA, Ubisoft, Battle.net, Xbox) were found on this PC. Nothing is wrong: there's just nothing here to manage.";

        _tabs = new UserControl[]
        {
            new GamesTabView(services, new TabSpec("Game library",
                "Every installed game by size or last played. Uninstalls and moves happen in the game's own launcher.",
                new LibraryScanner(), hasGames, noGames, ShowSort: true, AutoScan: true)),
            new GamesTabView(services, new TabSpec("Clips & screenshots",
                "Exact copies, look-alikes and old clips from Game Bar, NVIDIA, OBS, Medal, Steam and Snipping Tool. Pick a row to see its preview.",
                new ClipsScanner(), Available: true, "", ShowPreview: true)),
            new GameOptimizeView(services),
            new GamesTabView(services, new TabSpec("Game cleanup",
                "Logs, crash reports and shader caches games leave behind. Saves and settings are never touched. Shader caches are never ticked for you: a game stutters while it rebuilds one.",
                new GameCleanupScanner(), Available: true, "")),
            new GuidanceCardsView(GuidanceCards.For(CardPlace.GamesTips)),
        };

        Tabs.SelectionChanged += (_, _) => ShowTab(Tabs.SelectedIndex);
        ShowTab(0);
    }

    private void ShowTab(int index)
    {
        if (index < 0 || index >= _tabs.Length) return;
        TabHost.Content = _tabs[index];
        switch (_tabs[index])
        {
            case GamesTabView tab: tab.EnsureScanned(); break;
            case GameOptimizeView optimize: optimize.EnsureScanned(); break;
        }
    }
}
