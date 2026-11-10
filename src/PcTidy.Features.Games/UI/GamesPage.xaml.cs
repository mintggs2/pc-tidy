using System.Windows.Controls;
using PcTidy.Core;
using PcTidy.Features.Games.Cleanup;
using PcTidy.Features.Games.Clips;
using PcTidy.Features.Games.Library;
using PcTidy.Features.Games.Optimize;
using PcTidy.UI.Controls;
using PcTidy.UI.Flow;

namespace PcTidy.Features.Games.UI;

/// <summary>
/// The Games page: Library · Clips &amp; screenshots · Optimize · Cleanup. Each tab scans only when first opened and
/// changes nothing until the user confirms through the shell's flow host. Works at 1000 px wide: only the lists scroll.
/// </summary>
public partial class GamesPage : UserControl, IFixedHeightPage
{
    private readonly GamesTabView[] _tabs;

    public GamesPage(IServiceProvider services)
    {
        InitializeComponent();
        RiskPill.Text = "Check each item";
        RiskPill.Tone = PillTone.Caution;

        var locations = services.Get<IKnownLocations>();
        var hasGames = GamesAvailability.HasLaunchers(locations);
        const string noGames = "No game launchers (Steam, Epic, GOG, EA, Ubisoft, Battle.net, Xbox) were found on this PC. Nothing is wrong: there's just nothing here to manage.";

        _tabs = new[]
        {
            new GamesTabView(services, new TabSpec("Game library",
                "Every installed game by size or last played. Uninstalls and moves happen in the game's own launcher.",
                new LibraryScanner(), hasGames, noGames, ShowSort: true, AutoScan: true)),
            new GamesTabView(services, new TabSpec("Clips & screenshots",
                "Exact copies, look-alikes and old clips from Game Bar, NVIDIA, OBS, Medal, Steam and Snipping Tool. Pick a row to see its preview.",
                new ClipsScanner(), Available: true, "", ShowPreview: true)),
            new GamesTabView(services, new TabSpec("Game settings",
                "Proven launch options and settings for your games, each with its source. Games with a kernel anti-cheat get advice only.",
                new OptimizerScanner(), hasGames, noGames)),
            new GamesTabView(services, new TabSpec("Game cleanup",
                "Logs, crash reports and shader caches games leave behind. Saves and settings are never touched.",
                new GameCleanupScanner(), Available: true, "")),
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
