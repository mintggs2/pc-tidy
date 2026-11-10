using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Win32;
using PcTidy.App.Portability;
using PcTidy.App.Shell;
using PcTidy.App.Updates;
using PcTidy.App.WhatIs;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Assistant;
using PcTidy.Features.Schedule;
using PcTidy.UI.Flow;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace PcTidy.App.Pages;

/// <summary>
/// Settings that reach outside PC Tidy's window: tray and alerts, start with Windows, Explorer's right-click entries
/// (each a plan item through the normal confirm, never a silent registry write), export/import, updates and
/// uninstall. Demo and practice mode simulate every Windows change and never show a real notification.
/// </summary>
public sealed partial class SettingsPage
{
    private bool IsPractice => _services.IsDemo || _services.Args.SandboxRoot is not null;
    private DesktopIntegration? Desktop => _services.GetService(typeof(DesktopIntegration)) as DesktopIntegration;
    private static string AppVersion => Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
                                        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    // ---------------- Ask PC Tidy ----------------

    private void BuildAssistant()
    {
        if (!_window.HasPage(AssistantModule.ModuleId)) return;
        var a = AssistantSettings.From(S);
        Card(SymbolRegular.ChatSparkle24, "Ask PC Tidy (local AI helper)",
            "Off by default. Answers questions from PC Tidy's own scan, using Ollama on this PC only (127.0.0.1). Nothing is sent online.",
            Toggle(a.Enabled, on => { var x = AssistantSettings.From(S); x.Enabled = on; x.Store(S); Save(); }));
    }

    // ---------------- Move to a new PC ----------------

    private void BuildMoveToNewPc()
    {
        var export = new Button { Content = "Export settings…", Icon = new SymbolIcon(SymbolRegular.ArrowExportUp24) };
        AutomationProperties.SetName(export, "Export settings, rules and learned data to one file");
        export.Click += (_, _) => ExportSettingsAsync().Forget(Host, "Export didn't finish");
        var import = new Button { Content = "Import…", Margin = new Thickness(6, 0, 0, 0), Icon = new SymbolIcon(SymbolRegular.ArrowImport24) };
        AutomationProperties.SetName(import, "Import settings from a PC Tidy file");
        import.Click += (_, _) => ImportSettingsAsync().Forget(Host, "Import didn't finish");
        Card(SymbolRegular.ArrowSwap24, "Move to a new PC",
            "One .pctidy file with your settings, each section's rules and what PC Tidy learned. Import shows what changes before anything is applied.",
            Row(export, import));
    }

    /// <summary>
    /// Says plainly what goes into the file before writing it (EXP-3): settings and rules, how many folder paths they
    /// name (the user name replaced), and, only if ticked, what PC Tidy learned (which names folders on this PC).
    /// </summary>
    private async Task ExportSettingsAsync()
    {
        if (_services.IsDemo)
        {
            Host.ShowMessage("Not in demo mode", "Demo mode has only sample settings, so there's nothing to export.");
            return;
        }
        var store = new SettingsPackageStore(S.DataRoot);
        var (_, folderPaths) = SettingsPackage.ExportJson(S, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var hasLearning = File.Exists(store.LearningFile);
        // The reason to think twice is the last part of the sentence, so the label wraps instead of being cut.
        var learningBox = new CheckBox
        {
            Content = new TextBlock { Text = "Also include what PC Tidy learned. It lists folder names you use, which can include your Windows user name.", TextWrapping = TextWrapping.Wrap },
            IsChecked = false, IsEnabled = hasLearning, Margin = new Thickness(0, 12, 0, 0),
        };
        AutomationProperties.SetName(learningBox, "Include what PC Tidy learned in the export");
        var body = new StackPanel { Width = 520 };
        body.Children.Add(FlowHost.Body(
            "The file has your settings and each section's rules. "
            + (folderPaths == 0 ? "" : $"It names {folderPaths} folder{PlanFacts.S(folderPaths)} (protected folders, rule folders and so on). Folders in your user folder are saved without your user name. ")
            + "Not in it: your files, this PC's data folder, and the list of files Organize sorted."));
        body.Children.Add(learningBox);
        var ask = new ContentDialog(_window.DialogHost) { Title = "Export settings?", Content = body, PrimaryButtonText = "Export", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
        if (await Host.ShowDialogAsync(ask) != ContentDialogResult.Primary) return;
        try
        {
            var drives = _services.Get<IKnownLocations>().FixedDrives;
            var withLearning = learningBox.IsChecked == true;
            var path = store.Export(S, drives, AppVersion, withLearning);
            ShowExported(path, withLearning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Host.ShowMessage("Couldn't export", "PC Tidy couldn't write the file: " + ex.Message, isError: true);
        }
    }

    private void ShowExported(string path, bool withLearning)
    {
        var show = new Button { Content = "Show in Explorer", Icon = new SymbolIcon(SymbolRegular.FolderOpen24), Margin = new Thickness(0, 12, 0, 0) };
        var body = new StackPanel { Width = 480 };
        body.Children.Add(FlowHost.Body($"Saved as \"{Path.GetFileName(path)}\" in PC Tidy's exports folder. Copy it to the new PC (a USB stick or a cloud folder) and choose Import there.\n\n" +
            (withLearning ? "It contains your settings and rules, and what PC Tidy learned (folder names you use most)." : "It contains your settings and rules.") + " It never contains your files."));
        body.Children.Add(show);
        var dialog = new ContentDialog(_window.DialogHost) { Title = "Settings exported", Content = body, CloseButtonText = "OK" };
        show.Click += (_, _) => { dialog.Hide(); Host.OpenInExplorer(path); };
        Host.ShowDialogAsync(dialog).Forget(Host);
    }

    private async Task ImportSettingsAsync()
    {
        var pick = new OpenFileDialog { Title = "Choose a PC Tidy settings file", Filter = $"PC Tidy settings (*{SettingsPackage.Extension})|*{SettingsPackage.Extension}", Multiselect = false };
        if (pick.ShowDialog() != true) return;
        LoadedPackage package;
        try { package = await Task.Run(() => SettingsPackage.Read(pick.FileName)); }
        catch (PackageException ex) { Host.ShowMessage("Can't import that file", ex.Message, isError: true); return; }

        await ImportPackageAsync(package);
    }

    /// <summary>Preview → confirm → apply for a package already read (public for screenshots, which only preview).</summary>
    public async Task ImportPackageAsync(LoadedPackage package)
    {
        var drives = _services.Get<IKnownLocations>().FixedDrives;
        var store = new SettingsPackageStore(S.DataRoot);
        var changes = ImportPreview.Compare(S, package, drives, File.Exists(store.LearningFile));
        if (changes.Count == 0)
        {
            Host.ShowMessage("Nothing to import", "That file has the same settings as this PC.");
            return;
        }
        if (!await ConfirmImportAsync(changes, package)) return;

        if (_services.IsDemo)
        {
            int n = ImportPreview.Apply(S, changes);
            Host.ShowMessage("Imported (demo)", $"{n} change{PlanFacts.S(n)} applied for this session only. Demo mode saves nothing.");
            return;
        }
        var result = store.Import(S, _services.Get<ISettingsStore>(), changes, package);
        _window.RefreshSummaryContext();
        var msg = $"{result.Applied} change{PlanFacts.S(result.Applied)} applied." + (result.LearningReplaced ? " What PC Tidy learned was brought over too (fully used after a restart)." : "")
                  + (result.BackupPath is { } b ? $" Your previous settings were kept as {Path.GetFileName(b)} in the backups folder." : "")
                  + " Some pages show the new settings after you reopen PC Tidy.";
        Host.ShowMessage(result.Problem is null ? "Settings imported" : "Imported, with a problem", result.Problem is null ? msg : msg + " " + result.Problem, isError: result.Problem is not null);
    }

    /// <summary>The preview: every difference with its own tick; risky ones unticked and flagged.</summary>
    private async Task<bool> ConfirmImportAsync(IReadOnlyList<ImportChange> changes, LoadedPackage package)
    {
        var list = new StackPanel();
        string? area = null;
        foreach (var c in changes)
        {
            if (c.Area != area)
            {
                area = c.Area;
                list.Children.Add(Styled(new TextBlock { Text = area, Margin = new Thickness(0, list.Children.Count == 0 ? 0 : 10, 0, 2) }, "CaptionStrong"));
            }
            list.Children.Add(ChangeRow(c));
        }
        var made = package.Manifest.CreatedUtc == default ? "" : $" Made {package.Manifest.CreatedUtc.ToLocalTime():d MMM yyyy} with PC Tidy {package.Manifest.AppVersion}.";
        var body = new StackPanel { Width = 600 };
        body.Children.Add(FlowHost.Body($"{changes.Count} difference{PlanFacts.S(changes.Count)} from this PC.{made} Untick anything you don't want. Changes that would make PC Tidy less careful or let it act on its own (watch mode, automatic rules, Boost closing apps, the update address), and folders that aren't on this PC, start unticked."));
        body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 380, Margin = new Thickness(0, 12, 0, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        body.Children.Add(Styled(new TextBlock { Text = "A copy of your current settings is kept in the backups folder first.", Margin = new Thickness(0, 10, 0, 0) }, "Caption"));
        var dialog = new ContentDialog(_window.DialogHost)
        {
            Title = "Import these settings?", Content = body, PrimaryButtonText = "Import ticked changes", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close, DialogMaxWidth = 700,
        };
        return await Host.ShowDialogAsync(dialog) == ContentDialogResult.Primary;
    }

    private static FrameworkElement ChangeRow(ImportChange c)
    {
        var box = new CheckBox { IsChecked = c.Include, IsEnabled = c.CanInclude, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 8, 0), MinWidth = 0 };
        box.Checked += (_, _) => c.Include = true;
        box.Unchecked += (_, _) => c.Include = false;
        AutomationProperties.SetName(box, $"{c.Name}: {c.Before} to {c.After}" + (c.Flag.Length > 0 ? $", {c.Flag}" : ""));
        var text = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(Styled(new TextBlock { Text = c.Name }, "BodyStrong"));
        if (c.Flag.Length > 0)
            head.Children.Add(new PcTidy.UI.Controls.BadgePill { Text = c.Flag, Margin = new Thickness(8, 0, 0, 0), Tone = c.InfoOnly ? PcTidy.UI.Controls.PillTone.Neutral : PcTidy.UI.Controls.PillTone.Caution });
        text.Children.Add(head);
        text.Children.Add(Styled(new TextBlock { Text = $"{c.Before}  →  {c.After}", TextTrimming = TextTrimming.CharacterEllipsis }, "Secondary"));
        foreach (var p in c.OffPcPaths.Take(3)) text.Children.Add(Styled(new TextBlock { Text = "Not on this PC: " + p }, "Caption"));
        if (c.Note is { } note) text.Children.Add(Styled(new TextBlock { Text = note }, "Caption"));
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
        row.Children.Add(box);
        row.Children.Add(text);
        return row;
    }

    // ---------------- Running in the background ----------------

    private ToggleSwitch? _trayToggle, _startToggle, _whatIsToggle, _tidyToggle;
    private bool _reverting;

    private void BuildBackground()
    {
        _sections["background"] = Group("Running in the background", "Background");
        Card(SymbolRegular.AppsListDetail24, "Tray icon",
            "An icon next to the clock: Open, Quick tidy (shows the preview first), Check now. It tells you when a drive is nearly full. Never cleans on its own. Off by default.",
            Toggle(S.TrayEnabled, on => { if (_reverting) return; S.TrayEnabled = on; Save(); Desktop?.SetTray(on); UpdateLowDiskCard(); }), out _trayToggle);
        Card(SymbolRegular.Power24, "Start with Windows",
            "Starts PC Tidy in the tray when you sign in (no window). Off by default: PC Tidy doesn't need to run all the time.",
            Toggle(S.StartWithWindows, on => { if (!_reverting) SetStartWithWindowsAsync(on).Forget(Host, "Start with Windows not changed"); }), out _startToggle);

        var prefs = ShellPrefs.Get(S);
        // Stored as "22:00"; shown in this PC's own time format ("10:00 PM" or "22:00"), like Schedule & reports.
        var times = Enumerable.Range(0, 48).Select(i => $"{i / 2:00}:{(i % 2) * 30:00}").ToList();
        var from = TimePicker(times, times.Contains(prefs.QuietFrom) ? prefs.QuietFrom : "22:00");
        var to = TimePicker(times, times.Contains(prefs.QuietTo) ? prefs.QuietTo : "08:00");
        to.Margin = new Thickness(6, 0, 12, 0);
        AutomationProperties.SetName(from, "Quiet hours start");
        AutomationProperties.SetName(to, "Quiet hours end");
        var quietOn = Toggle(prefs.QuietHoursOn, on => UpdatePrefs(p => p with { QuietHoursOn = on }));
        AutomationProperties.SetName(quietOn, "Quiet hours");
        from.SelectionChanged += (_, _) => { if (from.SelectedValue is string v) UpdatePrefs(p => p with { QuietFrom = v }); };
        to.SelectionChanged += (_, _) => { if (to.SelectedValue is string v) UpdatePrefs(p => p with { QuietTo = v }); };
        Card(SymbolRegular.WeatherMoon24, "Quiet hours",
            "No notifications between these times. PC Tidy also stays quiet during full-screen games, presentations and Focus.",
            Row(Styled(new TextBlock { Text = "From", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) }, "Secondary"), from,
                Styled(new TextBlock { Text = "to", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) }, "Secondary"), to, quietOn));

        Card(SymbolRegular.HardDrive20, "Tell me when a drive is nearly full", LowDiskText,
            Toggle(prefs.LowDiskAlerts, on => UpdatePrefs(p => p with { LowDiskAlerts = on })), out _lowDiskToggle);
        UpdateLowDiskCard();

        var sched = ScheduleSettings.Load(S);
        var gb = new NumberBox { Minimum = 0.5, Maximum = 500, Value = Math.Round(sched.NotifyMinBytes / (double)(1L << 30), 1), SmallChange = 0.5, LargeChange = 5, MinWidth = 120, ClearButtonEnabled = false, Margin = new Thickness(0, 0, 12, 0) };
        AutomationProperties.SetName(gb, "Notify when at least this many gigabytes can be cleaned");
        gb.ValueChanged += (_, _) =>
        {
            if (gb.Value is not { } v) return;
            var x = ScheduleSettings.Load(S);
            x.NotifyMinBytes = (long)(Math.Clamp(v, 0.5, 500) * (1L << 30));
            x.Store(S);
            Save();
        };
        var scanToggle = Toggle(prefs.ScanReportAlerts, on => UpdatePrefs(p => p with { ScanReportAlerts = on }));
        AutomationProperties.SetName(scanToggle, "Notify after a scheduled scan");
        Card(SymbolRegular.Alert24, "After a scheduled scan, tell me when there's this much to clean (GB)",
            "One notification per scan at most, and only when it found at least this much. Schedules are set in Schedule & reports.",
            Row(gb, scanToggle));

        if (IsPractice)
            Card(SymbolRegular.Beaker24, "Notifications in this mode",
                _services.IsDemo ? "Demo mode never shows real notifications." : "Practice mode never shows real notifications: they're written to the logs folder of the practice PC instead.",
                null);
    }

    private ToggleSwitch? _lowDiskToggle;
    private const string LowDiskText = "When a drive has less than 10 % free, at most once a day. Needs the tray icon (above).";

    /// <summary>
    /// The drive-full alert comes from the tray icon: while the tray is off the switch is greyed and says why, so it
    /// never looks on while it can't fire. The saved choice is kept for when the tray comes back.
    /// </summary>
    private void UpdateLowDiskCard()
    {
        if (_lowDiskToggle is null) return;
        _lowDiskToggle.IsEnabled = S.TrayEnabled;
        _lowDiskToggle.ToolTip = S.TrayEnabled ? null : "Needs the tray icon: turn on Tray icon above first.";
        ToolTipService.SetShowOnDisabled(_lowDiskToggle, true);
        AutomationProperties.SetHelpText(_lowDiskToggle, S.TrayEnabled ? "" : "Needs the tray icon. Turn on Tray icon first.");
    }

    /// <summary>Half-hour times stored as "HH:mm", shown in this PC's own short time format.</summary>
    private static ComboBox TimePicker(IReadOnlyList<string> times, string selected)
    {
        var box = new ComboBox { MinWidth = 100, SelectedValuePath = "Tag" };
        foreach (var t in times)
            box.Items.Add(new ComboBoxItem
            {
                Content = DateTime.Today.Add(TimeSpan.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToString("t", System.Globalization.CultureInfo.CurrentCulture),
                Tag = t,
            });
        box.SelectedValue = selected;
        return box;
    }

    private void UpdatePrefs(Func<ShellPrefs, ShellPrefs> change)
    {
        ShellPrefs.Set(S, change(ShellPrefs.Get(S)));
        Save();
    }

    private async Task SetStartWithWindowsAsync(bool on)
    {
        var ok = await RunOwnPlanAsync(on ? "Start PC Tidy with Windows" : "Don't start PC Tidy with Windows", new[] { ShellVerbs.StartWithWindows(on) });
        if (ok)
        {
            S.StartWithWindows = on;
            Save();
        }
        else Revert(_startToggle, S.StartWithWindows);
    }

    // ---------------- Explorer's right-click menu ----------------

    private void BuildRightClickMenu()
    {
        Group("Right-click menu");
        var prefs = ShellPrefs.Get(S);
        Card(SymbolRegular.QuestionCircle24, "\"What is this?\" in Explorer",
            "Right-click any file or folder (Windows 11: Show more options) to see what it is, which app owns it, its size and whether it's safe to delete. Only explains, never deletes.",
            Toggle(prefs.WhatIsMenu, on => { if (!_reverting) SetMenuAsync(on, tidy: false).Forget(Host, "Right-click menu not changed"); }), out _whatIsToggle);
        if (_window.HasPage("organize"))
            Card(SymbolRegular.FolderArrowRight24, "\"Tidy this folder\" in Explorer",
                "Right-click a folder to open Organize for just that folder. You see the preview first; nothing moves until you confirm.",
                Toggle(prefs.TidyFolderMenu, on => { if (!_reverting) SetMenuAsync(on, tidy: true).Forget(Host, "Right-click menu not changed"); }), out _tidyToggle);
    }

    private async Task SetMenuAsync(bool on, bool tidy)
    {
        var exe = Environment.ProcessPath ?? "";
        var items = tidy ? ShellVerbs.TidyFolder(exe, on) : ShellVerbs.WhatIs(exe, on);
        var label = tidy ? ShellVerbs.TidyLabel : ShellVerbs.WhatIsLabel;
        var ok = await RunOwnPlanAsync(on ? $"Add \"{label}\" to the right-click menu" : $"Remove \"{label}\" from the right-click menu", items);
        var prefs = ShellPrefs.Get(S);
        if (ok)
        {
            ShellPrefs.Set(S, tidy ? prefs with { TidyFolderMenu = on } : prefs with { WhatIsMenu = on });
            Save();
        }
        else Revert(tidy ? _tidyToggle : _whatIsToggle, tidy ? prefs.TidyFolderMenu : prefs.WhatIsMenu);
    }

    /// <summary>One of PC Tidy's own Windows changes, through the normal confirm, journal and undo.</summary>
    private async Task<bool> RunOwnPlanAsync(string title, IReadOnlyList<PlanItem> items)
    {
        var plan = new ActionPlan { FeatureId = ShellVerbs.FeatureId, Title = title, Items = items };
        var outcome = await Host.ReviewAndRunAsync(new[] { plan }, new FlowOptions { Title = title + "?", ShowResult = false }, CancellationToken.None);
        if (outcome.Kind != FlowOutcomeKind.Ran) return false;
        var run = outcome.Records.FirstOrDefault();
        if (ShellVerbs.Succeeded(run))
        {
            if (run!.Steps.Any(s => s.Status == StepStatus.Simulated))
                Host.ShowMessage("Practised, not changed", "In this mode PC Tidy only simulates changes to Windows. The switch shows what you chose; Windows itself wasn't touched.");
            return true;
        }
        var why = run?.Steps.FirstOrDefault(s => s.Status is StepStatus.Failed or StepStatus.Skipped)?.Message ?? "It didn't finish.";
        Host.ShowMessage("Not changed", why + " You can see the details in History.", isError: true);
        return false;
    }

    private void Revert(ToggleSwitch? toggle, bool value)
    {
        if (toggle is null) return;
        _reverting = true;
        try { toggle.IsChecked = value; }
        finally { _reverting = false; }
    }

    // ---------------- Updates ----------------

    private void BuildUpdates()
    {
        var updates = Group("Updates");
        _sections["updates"] = updates;
        var feed = new TextBox { Text = ShellPrefs.Get(S).UpdateFeedUrl ?? "", PlaceholderText = "Paste the address from the PC Tidy download page", MinWidth = 320, ClearButtonEnabled = true };
        AutomationProperties.SetName(feed, "Update address");
        feed.LostFocus += (_, _) => UpdatePrefs(p => p with { UpdateFeedUrl = string.IsNullOrWhiteSpace(feed.Text) ? null : feed.Text.Trim() });
        // Nothing to check until there's an address.
        var check = new Button { Content = "Check now", Margin = new Thickness(6, 0, 0, 0), Icon = new SymbolIcon(SymbolRegular.ArrowSync24), IsEnabled = !string.IsNullOrWhiteSpace(feed.Text) };
        feed.TextChanged += (_, _) => check.IsEnabled = !string.IsNullOrWhiteSpace(feed.Text);
        AutomationProperties.SetName(check, "Check the update address for an update now");
        check.Click += (_, _) =>
        {
            UpdatePrefs(p => p with { UpdateFeedUrl = string.IsNullOrWhiteSpace(feed.Text) ? null : feed.Text.Trim() });
            CheckForUpdateAsync(feed.Text).Forget(Host, "Update check didn't finish");
        };
        Card(SymbolRegular.ArrowDownload24, "Update address",
            "PC Tidy doesn't phone home. Leave this empty and it never checks for updates. If you add an address, \"Check now\" fetches only that small file (nothing about this PC is sent); an update is installed only if it is signed by the same publisher as this PC Tidy.",
            Row(feed, check));
    }

    private async Task CheckForUpdateAsync(string url)
    {
        if (UpdateChecker.FeedProblem(url) is { } bad) { Host.ShowMessage("Can't check for updates", bad); return; }
        if (_services.IsDemo) { Host.ShowMessage("Not in demo mode", "Demo mode never goes online."); return; }
        UpdateChecker.TryParseVersion(AppVersion, out var current);
        using var handler = UpdateChecker.CreateHandler();
        var checker = new UpdateChecker(handler, current);
        _window.ShowProgress("Checking the release feed…", canStop: false);
        UpdateCheck result;
        try { result = await checker.CheckAsync(url, CancellationToken.None); }
        finally { _window.HideProgress(); }
        if (result.State != UpdateState.Available) { Host.ShowMessage(result.State == UpdateState.UpToDate ? "No update" : "Update check", result.Message, isError: result.State == UpdateState.Problem); return; }

        // Who made an update is decided by its signature, not by the feed's checksum (the feed names both). An unsigned
        // PC Tidy (this beta) can't tell, so it only offers the release page and never installs anything by itself.
        var own = await Task.Run(Authenticode.Own);
        var changelog = new ScrollViewer { MaxHeight = 280, Content = FlowHost.Body(result.Manifest!.Changelog!), Margin = new Thickness(0, 10, 0, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var body = new StackPanel { Width = 520 };
        body.Children.Add(FlowHost.Body(result.Message + " What's new:"));
        body.Children.Add(changelog);
        if (own.State != SignatureState.Valid)
        {
            var page = result.Manifest.ReleasePage();
            body.Children.Add(Styled(new TextBlock { Text = UpdateTrust.UnsignedSelf + " Get it from the release page and run it yourself.", Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap }, "Caption"));
            var manual = new ContentDialog(_window.DialogHost) { Title = "Update available", Content = body, PrimaryButtonText = page is null ? "" : "Open the release page", CloseButtonText = "Not now", DefaultButton = ContentDialogButton.Close };
            if (await Host.ShowDialogAsync(manual) != ContentDialogResult.Primary || page is null) return;
            if (IsPractice) { Host.ShowMessage("Practice mode", $"Here PC Tidy would open {page.AbsoluteUri} in your browser. In practice mode it doesn't."); return; }
            try { OwnProcesses.OpenReleasePage(page); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { Host.ShowMessage("Couldn't open the release page", ex.Message, isError: true); }
            return;
        }
        body.Children.Add(Styled(new TextBlock { Text = "Download only: nothing is installed until you confirm once more.", Margin = new Thickness(0, 10, 0, 0) }, "Caption"));
        var ask = new ContentDialog(_window.DialogHost) { Title = "Update available", Content = body, PrimaryButtonText = "Download", CloseButtonText = "Not now", DefaultButton = ContentDialogButton.Close };
        if (await Host.ShowDialogAsync(ask) != ContentDialogResult.Primary) return;

        var store = new UpdateStore(S.DataRoot);
        // Stop cancels the download (the overlay's token); a connection that goes quiet ends it by itself after 30 s.
        var stop = _window.ShowProgress("Downloading the update…", canStop: true, stopText: "Stop the download");
        string? problem;
        try { problem = await checker.DownloadAsync(result, store, new Progress<double>(f => _window.ReportProgress(new RunProgress((int)(f * 99), 100, $"{f:P0}"))), stop); }
        finally { _window.HideProgress(); }
        if (problem == UpdateChecker.StoppedMessage) { Host.ShowMessage("Download stopped", problem); return; }
        if (problem is not null) { Host.ShowMessage("Update not downloaded", problem, isError: true); return; }

        // From here the installer is held open (no writes, renames or deletes) until Windows has started it.
        var installer = store.FinalPath(result.NewVersion!);
        VerifiedInstaller verified;
        try { verified = await Task.Run(() => VerifiedInstaller.Open(installer, result.Manifest.Sha256!, own)); }
        catch (InvalidOperationException ex)
        {
            store.Discard(installer);
            Host.ShowMessage("Update refused", ex.Message + " The download was thrown away. Nothing was installed.", isError: true);
            return;
        }
        using (verified)
        {
            var confirm = new ContentDialog(_window.DialogHost)
            {
                Title = $"Install PC Tidy {result.NewVersion!.ToString(3)}?",
                Content = FlowHost.Body($"The download matches the release's checksum. {Authenticode.Describe(verified.Signature)}\n\nPC Tidy closes and the installer updates it. Windows asks for permission first, because PC Tidy is installed for everyone on this PC. Your settings, history and quarantine stay as they are."),
                PrimaryButtonText = "Install and restart", CloseButtonText = "Later", DefaultButton = ContentDialogButton.Close,
            };
            if (await Host.ShowDialogAsync(confirm) != ContentDialogResult.Primary) return;
            if (IsPractice)
            {
                Host.ShowMessage("Practice mode", $"Here PC Tidy would now start \"{Path.GetFileName(installer)}\" --silent and close. In practice mode it doesn't.");
                return;
            }
            try
            {
                verified.Start();
                _window.Quit();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
            {
                Host.ShowMessage("Update not started", ex.Message, isError: true);
            }
        }
    }

    // ---------------- Uninstall ----------------

    private void BuildUninstall()
    {
        var b = new Button { Content = "Uninstall PC Tidy…", Appearance = ControlAppearance.Secondary };
        AutomationProperties.SetName(b, "Uninstall PC Tidy");
        b.Click += (_, _) => UninstallAsync().Forget(Host, "Uninstall didn't start");
        Card(SymbolRegular.Delete24, "Uninstall PC Tidy", "Removes PC Tidy and everything it added to Windows. You choose whether to keep your history and quarantine.", b);
    }

    private async Task UninstallAsync()
    {
        var body = new StackPanel { Width = 520 };
        body.Children.Add(FlowHost.Body(
            "The uninstaller removes:\n" +
            "•  PC Tidy and its Start menu shortcuts\n" +
            "•  its right-click menu entries (What is this?, Tidy this folder)\n" +
            "•  the start-with-Windows entry\n" +
            "•  its scheduled scans (the \\PC Tidy\\ tasks)\n" +
            "•  its Windows notification registration\n" +
            "•  its data folder, only if you choose so in the uninstaller\n\n" +
            "Keep my history & quarantine is the default: your data folder (history, quarantine and a copy of PC Tidy Rescue) stays, so earlier runs can still be undone."));
        body.Children.Add(Styled(new TextBlock { Text = "PC Tidy closes and the uninstaller opens. It asks once more before removing anything. Windows may ask for permission too, because PC Tidy is installed for everyone on this PC.", Margin = new Thickness(0, 10, 0, 0) }, "Caption"));
        var dialog = new ContentDialog(_window.DialogHost)
        {
            Title = "Uninstall PC Tidy?", Content = body, PrimaryButtonText = "Open the uninstaller", CloseButtonText = "Keep PC Tidy",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await Host.ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;

        if (IsPractice)
        {
            Host.ShowMessage("Practice mode", "Here PC Tidy would remove its notification registration, start its uninstaller (uninstall.exe --uninstall) and close. In practice mode it doesn't.");
            return;
        }
        if (OwnProcesses.FindUninstaller() is not { } entry)
        {
            Host.ShowMessage("Uninstaller not found", "PC Tidy wasn't installed with its installer here (for example, it runs from a download folder), so there's nothing to uninstall: closing it and deleting its folder is enough. You can also use Windows Settings → Apps.");
            return;
        }
        try
        {
            Notifications.ToastNotifier.Unregister();
            OwnProcesses.StartUninstaller(entry);
            _window.Quit();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            Host.ShowMessage("Couldn't start the uninstaller", ex.Message + " You can uninstall PC Tidy from Windows Settings → Apps.", isError: true);
        }
    }
}
