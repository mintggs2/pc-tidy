using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using PcTidy.Features.Privacy.Common;
using PcTidy.UI.Flow;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace PcTidy.Features.Privacy.UI;

/// <summary>A button in the page's top strip: a Settings page (OpenExternal through the shell) or another PC Tidy page.</summary>
public sealed record Shortcut(string Label, SymbolRegular Icon, string Help, string? SettingsTarget = null, NavigationRequest? Navigate = null);

/// <summary>
/// The standard Scan → Preview → Confirm → Done page (<see cref="StandardFlowPage"/>, which confirms through the shell's
/// flow host) with a slim strip of shortcuts under its title for the things only Windows can clear, plus (Privacy sweep) the Cookie
/// keeper's "sites that stay signed in" list. Only the flow page's list scrolls; it works at 1000 px wide.
/// </summary>
public sealed class PrivacyFlowPage : UserControl, IFixedHeightPage
{
    private readonly IServiceProvider _services;
    private readonly string _featureId;

    /// <param name="readyMessage">What the ready card says scanning reads (these sections deal in settings, not file sizes).</param>
    public PrivacyFlowPage(IServiceProvider services, IFeatureModule module, ModuleRisk risk, string stripLabel, IReadOnlyList<Shortcut> shortcuts, bool showKeepList, string readyMessage)
    {
        _services = services;
        _featureId = module.Id;
        var icon = Enum.TryParse<SymbolRegular>(module.Icon, out var s) ? s : SymbolRegular.ShieldKeyhole24;
        Flow = new StandardFlowPage(services, module.Title, module.Summary, icon, module.Scanners, risk, (module as IModuleFlowText)?.PreviewNote)
        {
            // Like every section: title and subtitle first, then this strip, then the steps.
            HeaderContent = Strip(stripLabel, shortcuts, showKeepList),
            // These scanners read settings and short lists: Quick and Deep would be the same scan.
            ShowDepthSwitch = false,
            ReadyMessage = readyMessage,
        };
        Content = Flow;
        AutomationProperties.SetName(this, module.Title);
    }

    /// <summary>The hosted standard flow (tests and screenshots).</summary>
    public StandardFlowPage Flow { get; }

    /// <summary>Raised before the page asks the shell to open another section (a shell that knows groups can scroll to it).</summary>
    public event Action<NavigationRequest>? NavigationRequested;

    private IFlowHost? Host => _services.GetService(typeof(IFlowHost)) as IFlowHost;

    // ------------------------------------------------------------------ strip

    private FrameworkElement Strip(string stripLabel, IReadOnlyList<Shortcut> shortcuts, bool showKeepList)
    {
        var card = new Border { Padding = new Thickness(16, 12, 16, 12) };
        card.SetResourceReference(StyleProperty, "TidyCard");
        var stack = new StackPanel();

        var row = new DockPanel { LastChildFill = true };
        var label = new TextBlock
        {
            Text = stripLabel, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
            TextWrapping = TextWrapping.NoWrap,
        };
        label.SetResourceReference(StyleProperty, "BodyStrong");
        DockPanel.SetDock(label, Dock.Left);
        row.Children.Add(label);
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var sc in shortcuts) buttons.Children.Add(ShortcutButton(sc));
        row.Children.Add(buttons);
        stack.Children.Add(row);

        if (showKeepList) stack.Children.Add(new KeepListEditor(_services.Get<AppSettings>()));
        card.Child = stack;
        return card;
    }

    private Button ShortcutButton(Shortcut sc)
    {
        var b = new Button
        {
            Content = sc.Label, Icon = new SymbolIcon(sc.Icon), Appearance = ControlAppearance.Secondary,
            Margin = new Thickness(0, 2, 8, 2), ToolTip = sc.Help,
        };
        AutomationProperties.SetName(b, sc.Label);
        AutomationProperties.SetHelpText(b, sc.Help);
        b.Click += (_, _) => Activate(sc);
        return b;
    }

    private void Activate(Shortcut sc)
    {
        if (sc.SettingsTarget is { } target && Host is { } host)
        {
            _ = LinkOpener.OpenAsync(host, _featureId, target, sc.Label);
            return;
        }
        if (sc.Navigate is { } nav)
        {
            NavigationRequested?.Invoke(nav);
            if (!nav.Go(_services)) Host?.ShowMessage(sc.Label, nav.Hint);
        }
    }
}

/// <summary>
/// The Cookie keeper's keep-list: common sites as ticks (none ticked until you choose) plus your own. Saved in
/// AppSettings ("privacy"); the shell saves settings. The cookie rows use the list from the next scan.
/// </summary>
internal sealed class KeepListEditor : StackPanel
{
    private readonly AppSettings _settings;
    private readonly PcTidy.Features.Privacy.Sweep.PrivacySettings _state;
    private readonly WrapPanel _sites = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 0) };
    private readonly StackPanel _body = new() { Visibility = Visibility.Collapsed };

    public KeepListEditor(AppSettings settings)
    {
        _settings = settings;
        _state = PcTidy.Features.Privacy.Sweep.PrivacySettings.Load(settings);
        Margin = new Thickness(0, 10, 0, 0);

        var header = new DockPanel();
        var toggle = new Button { Content = "Choose sites", Appearance = ControlAppearance.Transparent, Icon = new SymbolIcon(SymbolRegular.Cookies24) };
        AutomationProperties.SetName(toggle, "Choose sites that stay signed in");
        toggle.Click += (_, _) =>
        {
            var open = _body.Visibility != Visibility.Visible;
            _body.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            toggle.Content = open ? "Done choosing" : "Choose sites";
        };
        DockPanel.SetDock(toggle, Dock.Right);
        header.Children.Add(toggle);
        _summary.VerticalAlignment = VerticalAlignment.Center;
        _summary.SetResourceReference(StyleProperty, "Secondary");
        header.Children.Add(_summary);
        Children.Add(header);

        var add = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var addButton = new Button { Content = "Add", Appearance = ControlAppearance.Secondary, Margin = new Thickness(8, 0, 0, 0) };
        AutomationProperties.SetName(addButton, "Add site to keep");
        DockPanel.SetDock(addButton, Dock.Right);
        var box = new TextBox { PlaceholderText = "Another site, e.g. example.com", MaxWidth = 360, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(box, "Site to keep");
        addButton.Click += (_, _) => AddSite(box);
        box.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) AddSite(box); };
        add.Children.Add(addButton);
        add.Children.Add(box);

        var note = new TextBlock
        {
            Text = "Cookies for these sites (and their subdomains) are kept, so you stay signed in. Scan again to use a changed list.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
        };
        note.SetResourceReference(StyleProperty, "Caption");
        _body.Children.Add(_sites);
        _body.Children.Add(add);
        _body.Children.Add(note);
        Children.Add(_body);
        Rebuild();
    }

    private void AddSite(TextBox box)
    {
        var host = PcTidy.Features.Privacy.Sweep.CookieKeeper.NormalizeSite(box.Text);
        if (host.Length == 0) return;
        if (!_state.KeepSites.Contains(host, StringComparer.OrdinalIgnoreCase)) _state.KeepSites.Add(host);
        box.Text = "";
        Save();
        Rebuild();
    }

    private void Rebuild()
    {
        _sites.Children.Clear();
        var kept = _state.KeepHosts();
        var all = PcTidy.Features.Privacy.Sweep.CookieKeeper.CommonSites.Concat(kept).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var site in all)
        {
            var cb = new CheckBox { Content = site, IsChecked = kept.Contains(site, StringComparer.OrdinalIgnoreCase), Margin = new Thickness(0, 0, 16, 4), MinWidth = 150 };
            AutomationProperties.SetName(cb, "Keep cookies for " + site);
            cb.Checked += (_, _) => Toggle(site, true);
            cb.Unchecked += (_, _) => Toggle(site, false);
            _sites.Children.Add(cb);
        }
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var kept = _state.KeepHosts();
        _summary.Text = kept.Count == 0
            ? "Sites that stay signed in when cookies are cleared: none chosen yet."
            : $"Sites that stay signed in when cookies are cleared: {string.Join(", ", kept.Take(4))}{(kept.Count > 4 ? $" and {kept.Count - 4} more" : "")}.";
    }

    private void Toggle(string site, bool keep)
    {
        _state.KeepSites.RemoveAll(s => string.Equals(PcTidy.Features.Privacy.Sweep.CookieKeeper.NormalizeSite(s), site, StringComparison.OrdinalIgnoreCase));
        if (keep) _state.KeepSites.Add(site);
        Save();
        UpdateSummary();
    }

    private void Save() => _state.Store(_settings);
}
