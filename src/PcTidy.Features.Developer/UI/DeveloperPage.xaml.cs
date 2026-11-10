using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Developer.Archive;
using PcTidy.Features.Developer.Caches;
using PcTidy.Features.Developer.Common;
using PcTidy.Features.Developer.Containers;
using PcTidy.Features.Developer.Projects;
using PcTidy.UI.Controls;
using PcTidy.UI.Flow;

namespace PcTidy.Features.Developer.UI;

/// <summary>
/// The Developer page: Build folders · Package caches · Docker &amp; WSL · Archive old projects. Each tab scans only when
/// asked and changes nothing until the user confirms through the shell's flow host. Choices (folders to look in, idle
/// days, archive folder) are kept in the "developer" settings blob; the shell saves settings. Works at 1000 px wide:
/// only the lists scroll.
/// </summary>
public partial class DeveloperPage : UserControl, IFixedHeightPage
{
    private static readonly int[] IdleDayChoices = { 30, 90, 180, 365 };
    private static readonly int[] ArchiveMonthChoices = { 6, 12, 24, 36 };

    private readonly IServiceProvider _services;
    private readonly AppSettings _settings;
    private readonly IKnownLocations _locations;
    private readonly DevTabView[] _tabs;
    private TextBlock? _rootsText;
    private TextBlock? _archiveText;

    internal DeveloperPage(IServiceProvider services, IReadOnlyDictionary<string, object> overrides)
    {
        InitializeComponent();
        _services = services;
        _settings = services.Get<AppSettings>();
        _locations = services.Get<IKnownLocations>();
        RiskPill.Text = "Check each item";
        RiskPill.Tone = PillTone.Caution;

        Func<IReadOnlyDictionary<string, object>> options = () => overrides;
        var probe = ProbeContext(overrides);
        var hasTools = DeveloperAvailability.HasPackageTools(probe);
        var hasContainers = DeveloperAvailability.HasDockerOrWsl(probe);

        _tabs = new[]
        {
            new DevTabView(services, new TabSpec("Build folders",
                "node_modules, bin/obj, .vs, target, __pycache__, .venv and build folders of projects you haven't worked on for a while. Your tools rebuild them.",
                new BuildFoldersScanner(), true, "", options), BuildFolderOptions()),
            new DevTabView(services, new TabSpec("Package caches",
                "Download caches of npm, pnpm, Yarn, pip, NuGet and Gradle, cleaned by each tool's own command.",
                new PackageCacheScanner(), hasTools,
                "No package managers (npm, pnpm, Yarn, pip, NuGet, Gradle) were found on this PC. Nothing is wrong: there's just nothing to clean here.", options)),
            new DevTabView(services, new TabSpec("Docker & WSL",
                "Docker's unused images, stopped containers and build cache, and the virtual disks of your Linux (WSL) distributions.",
                new DockerWslScanner(), hasContainers,
                "Neither Docker nor WSL (Linux on Windows) was found on this PC. Nothing is wrong: there's just nothing to clean here.", options)),
            new DevTabView(services, new TabSpec("Archive old projects",
                "Zip projects you haven't touched for months into an archive folder. Each zip is checked file by file before the original goes to quarantine.",
                new ArchiveScanner(), true, "", options), ArchiveOptions()),
        };

        Tabs.SelectionChanged += (_, _) => ShowTab(Tabs.SelectedIndex);
        ShowTab(0);
    }

    private ScanContext ProbeContext(IReadOnlyDictionary<string, object> overrides)
    {
        var c = ScanRunner.CreateContext(_services, ScanDepth.Quick);
        return new ScanContext
        {
            Locations = c.Locations, Safety = c.Safety, InUse = c.InUse, Settings = c.Settings, Walker = c.Walker,
            System = c.System, RunningProcesses = c.RunningProcesses, Options = overrides,
        };
    }

    private void ShowTab(int index)
    {
        if (index >= 0 && index < _tabs.Length) TabHost.Content = _tabs[index];
    }

    private DeveloperSettings Saved => DeveloperSettings.From(_settings);

    // ---------------- Build folders: where to look, how idle ----------------

    private FrameworkElement BuildFolderOptions()
    {
        _rootsText = OptionText();
        var choose = OptionButton("Choose folders…", "Choose the folders to look for projects in", OnChooseRoots);
        var reset = OptionButton("Use defaults", "Look in your user folder and other drives again", (_, _) =>
        {
            var s = Saved;
            s.Roots.Clear();
            s.Store(_settings);
            ShowRoots();
        });
        var idle = Choice("Offer build folders after", IdleDayChoices, Saved.IdleDays, d => $"{d} days without changes", d =>
        {
            var s = Saved;
            s.IdleDays = d;
            s.Store(_settings);
        });
        ShowRoots();
        return OptionsRow(_rootsText, idle, choose, reset);
    }

    private void ShowRoots()
    {
        if (_rootsText is null) return;
        var roots = Saved.Roots;
        _rootsText.Text = roots.Count == 0
            ? "Looking in: your user folder and your other drives"
            : "Looking in: " + string.Join("; ", roots);
        _rootsText.ToolTip = string.Join("\n", roots.Count == 0 ? DevContext.DefaultRoots(_locations) : roots);
    }

    private void OnChooseRoots(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Folders to look for projects in", Multiselect = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true || dialog.FolderNames.Length == 0) return;
        var s = Saved;
        s.Roots = dialog.FolderNames.ToList();
        s.Store(_settings);
        ShowRoots();
    }

    // ---------------- Archive: where zips go, how old ----------------

    private FrameworkElement ArchiveOptions()
    {
        _archiveText = OptionText();
        var change = OptionButton("Change folder…", "Choose the folder archive zips go to", OnChooseArchiveFolder);
        var months = Choice("Suggest projects untouched for", ArchiveMonthChoices, Saved.ArchiveMonths, m => $"{m} months", m =>
        {
            var s = Saved;
            s.ArchiveMonths = m;
            s.Store(_settings);
        });
        ShowArchiveFolder();
        return OptionsRow(_archiveText, months, change);
    }

    private void ShowArchiveFolder()
    {
        if (_archiveText is null) return;
        var folder = Saved.ArchiveFolder ?? DevContext.DefaultArchiveFolder(_locations);
        _archiveText.Text = "Archive folder: " + folder;
        _archiveText.ToolTip = folder;
    }

    private void OnChooseArchiveFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Folder for archive zips" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true || string.IsNullOrWhiteSpace(dialog.FolderName)) return;
        var s = Saved;
        s.ArchiveFolder = dialog.FolderName;
        s.Store(_settings);
        ShowArchiveFolder();
    }

    // ---------------- small builders ----------------

    private static TextBlock OptionText() => new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
        Style = Application.Current?.TryFindResource("Secondary") as Style,
    };

    private static Button OptionButton(string text, string name, RoutedEventHandler click)
    {
        var b = new Wpf.Ui.Controls.Button { Content = text, Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(b, name);
        b.Click += click;
        return b;
    }

    private static ComboBox Choice(string label, int[] values, int current, Func<int, string> format, Action<int> changed)
    {
        var box = new ComboBox { Margin = new Thickness(8, 0, 0, 0), MinWidth = 180, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(box, label);
        foreach (var v in values) box.Items.Add(new ComboBoxItem { Content = format(v), Tag = v });
        var index = Array.IndexOf(values, current);
        box.SelectedIndex = index >= 0 ? index : Math.Min(1, values.Length - 1); // an odd saved value shows the default
        box.SelectionChanged += (_, _) => { if (box.SelectedItem is ComboBoxItem { Tag: int v }) changed(v); };
        return box;
    }

    /// <summary>Text on the left (trimmed when narrow), controls on the right: fits at 1000 px.</summary>
    private static FrameworkElement OptionsRow(TextBlock text, params FrameworkElement[] controls)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(text);
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var c in controls) panel.Children.Add(c);
        Grid.SetColumn(panel, 1);
        grid.Children.Add(panel);
        return grid;
    }
}
