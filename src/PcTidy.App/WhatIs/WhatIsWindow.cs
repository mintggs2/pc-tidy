using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using PcTidy.Core;
using PcTidy.UI.Controls;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace PcTidy.App.WhatIs;

/// <summary>
/// The small "What is this?" window Explorer's right-click entry opens: what the item is, which app owns it, its
/// size, and whether it's safe to delete, in plain words. It only explains: there is no delete button, and the only
/// actions are "Open &lt;section&gt; in PC Tidy" (where any change still goes through preview and confirm),
/// "Show in Explorer" and Close.
/// </summary>
public sealed class WhatIsWindow : FluentWindow
{
    private static readonly TimeSpan MeasureLimit = TimeSpan.FromSeconds(20);

    private readonly WhatIsReport _report;
    private readonly IFileWalker _walker;
    private readonly Func<string, bool> _canOpen;
    private readonly CancellationTokenSource _measure = new();
    private readonly TextBlock _size = new();

    /// <param name="canOpen">Whether a section is available here (hidden sections get no button).</param>
    public WhatIsWindow(WhatIsReport report, IFileWalker walker, Func<string, bool> canOpen, bool solidBackground = false)
    {
        _report = report;
        _walker = walker;
        _canOpen = canOpen;
        Title = $"What is this? · {report.Name}";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        MinHeight = 300;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ExtendsContentIntoTitleBar = true;
        WindowCornerPreference = WindowCornerPreference.Round;
        WindowBackdropType = solidBackground ? WindowBackdropType.None : WindowBackdropType.Mica;
        SetResourceReference(FontFamilyProperty, "TidyText");
        AutomationProperties.SetName(this, Title);
        Content = Build(solidBackground);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Loaded += (_, _) => _ = MeasureAsync(); // never throws: every failure ends as a size text
        Closed += (_, _) => { _measure.Cancel(); _measure.Dispose(); };
    }

    public WhatIsReport Report => _report;

    /// <summary>The user wants this section opened in PC Tidy (the shell navigates).</summary>
    public event Action<string>? OpenSectionRequested;
    public event Action<string>? ShowInExplorerRequested;

    /// <summary>Done measuring (screenshots wait for it).</summary>
    public Task Measured => _measured.Task;
    private readonly TaskCompletionSource _measured = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private UIElement Build(bool solid)
    {
        var root = new Grid();
        if (solid) root.SetResourceReference(Panel.BackgroundProperty, "ApplicationBackgroundBrush");
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var bar = new TitleBar { Title = "What is this?", Height = 40, ShowMaximize = false, ShowMinimize = false };
        root.Children.Add(bar);

        var body = new StackPanel { Margin = new Thickness(24, 4, 24, 20) };
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        // Name, path, verdict pill
        var head = new DockPanel();
        var tile = new Border { Width = 44, Height = 44, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top };
        tile.SetResourceReference(StyleProperty, "IconTile");
        var icon = new SymbolIcon { Symbol = _report.IsFolder ? SymbolRegular.Folder24 : SymbolRegular.Document24, FontSize = 22 };
        icon.SetResourceReference(ForegroundProperty, "TidyAccentBrush");
        tile.Child = icon;
        head.Children.Add(tile);
        var pill = new BadgePill { Text = PillText(_report.Verdict), Tone = Tone(_report.Verdict), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 4, 0, 0) };
        AutomationProperties.SetName(pill, PillText(_report.Verdict));
        DockPanel.SetDock(pill, Dock.Right);
        head.Children.Add(pill);
        var names = new StackPanel();
        names.Children.Add(T(_report.Name, "SectionTitle"));
        var path = T(_report.Path, "Caption", new Thickness(0, 2, 0, 0));
        AutomationProperties.SetName(path, "Full path: " + _report.Path);
        names.Children.Add(path);
        head.Children.Add(names);
        body.Children.Add(head);

        // What it is
        var card = new Border { Margin = new Thickness(0, 16, 0, 0), Padding = new Thickness(18, 14, 18, 14) };
        card.SetResourceReference(StyleProperty, "TidyCard");
        var what = new StackPanel();
        what.Children.Add(T(_report.Title, "CardTitle"));
        what.Children.Add(T(_report.Explanation, "Body", new Thickness(0, 4, 0, 0)));
        var facts = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        facts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        facts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Fact(facts, "Belongs to", T(_report.Owner ?? "Not known", "BodyStrong"));
        _size.Text = _report.Kind == WhatIsKind.Missing ? "—" : "Measuring…";
        _size.SetResourceReference(StyleProperty, "BodyStrong");
        _size.TextWrapping = TextWrapping.Wrap;
        Fact(facts, "Size", _size);
        if (LastChanged() is { } changed) Fact(facts, "Last changed", T(changed, "BodyStrong"));
        what.Children.Add(facts);
        card.Child = what;
        body.Children.Add(card);

        body.Children.Add(VerdictBox());

        // Actions (never a delete button)
        var actions = new DockPanel { Margin = new Thickness(0, 18, 0, 0), LastChildFill = false };
        var close = new Button { Content = "Close", MinWidth = 90 };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        actions.Children.Add(close);
        if (_report.Kind != WhatIsKind.Missing)
        {
            var reveal = new Button { Content = "Show in Explorer", Margin = new Thickness(0, 0, 8, 0), Icon = new SymbolIcon(SymbolRegular.FolderOpen24) };
            AutomationProperties.SetName(reveal, "Show it in Explorer");
            reveal.Click += (_, _) => ShowInExplorerRequested?.Invoke(_report.Path);
            DockPanel.SetDock(reveal, Dock.Right);
            actions.Children.Add(reveal);
        }
        if (_report.Section is { } section && _canOpen(section))
        {
            // For "Don't delete" the section is a pointer, not an invitation: a plain button, never the green one.
            var appearance = _report.Verdict == WhatIsVerdict.DontDelete ? ControlAppearance.Secondary : ControlAppearance.Primary;
            var open = new Button { Content = (_report.SectionLabel ?? "Open") + " in PC Tidy", Appearance = appearance, Margin = new Thickness(0, 0, 8, 0), Icon = new SymbolIcon(SymbolRegular.Open24) };
            AutomationProperties.SetName(open, (string)open.Content);
            open.Click += (_, _) => { OpenSectionRequested?.Invoke(section); Close(); };
            DockPanel.SetDock(open, Dock.Right);
            actions.Children.Add(open);
        }
        body.Children.Add(actions);
        body.Children.Add(T("This window only explains. It never deletes or changes anything.", "Caption", new Thickness(0, 12, 0, 0)));
        return root;
    }

    /// <summary>
    /// "Safe to delete?" with the answer always on the line under it (an InfoBar put a short answer beside its title
    /// and a long one under it, so the layout changed from file to file). Coloured like an InfoBar of that severity.
    /// </summary>
    private FrameworkElement VerdictBox()
    {
        var (icon, brush) = _report.Verdict switch
        {
            WhatIsVerdict.SafeToClean => (SymbolRegular.CheckmarkCircle24, "SystemFillColorSuccess"),
            WhatIsVerdict.ThinkFirst => (SymbolRegular.Warning24, "SystemFillColorCaution"),
            WhatIsVerdict.DontDelete => (SymbolRegular.DismissCircle24, "SystemFillColorCritical"),
            _ => (SymbolRegular.Info24, "SystemFillColorAttention"),
        };
        var box = new Border { Padding = new Thickness(14, 12, 14, 12), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Margin = new Thickness(0, 12, 0, 0) };
        box.SetResourceReference(Border.BackgroundProperty, brush + "BackgroundBrush");
        box.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        var dock = new DockPanel();
        var symbol = new SymbolIcon { Symbol = icon, Filled = true, FontSize = 18, Margin = new Thickness(0, 1, 10, 0), VerticalAlignment = VerticalAlignment.Top };
        symbol.SetResourceReference(ForegroundProperty, brush + "Brush");
        DockPanel.SetDock(symbol, Dock.Left);
        dock.Children.Add(symbol);
        var text = new StackPanel();
        text.Children.Add(T("Safe to delete?", "BodyStrong"));
        text.Children.Add(T(_report.VerdictText, "Body", new Thickness(0, 2, 0, 0)));
        dock.Children.Add(text);
        box.Child = dock;
        AutomationProperties.SetName(box, "Safe to delete? " + _report.VerdictText);
        return box;
    }

    private string? LastChanged()
    {
        try
        {
            if (_report.Kind == WhatIsKind.Missing) return null;
            var t = _report.IsFolder ? Directory.GetLastWriteTime(_report.Path) : File.GetLastWriteTime(_report.Path);
            return t.Year < 1990 ? null : $"{t.ToString("d MMM yyyy", CultureInfo.CurrentCulture)}, {t.ToString("t", CultureInfo.CurrentCulture)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>Size on a background thread: never follows links, never opens files, stops after a time limit.</summary>
    private async Task MeasureAsync()
    {
        try
        {
            if (_report.Kind is WhatIsKind.Missing or WhatIsKind.DriveRoot)
            {
                if (_report.Kind == WhatIsKind.DriveRoot) _size.Text = DriveText(_report.Path);
                return;
            }
            if (!_report.IsFolder)
            {
                var (bytes, onPc) = FileSize(_report.Path);
                _size.Text = bytes == onPc ? Sizes.Format(bytes) : $"{Sizes.Format(bytes)} (online-only: it takes no space on this PC)";
                return;
            }
            var ct = _measure.Token;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(MeasureLimit);
            long total = 0, local = 0, files = 0;
            bool stopped = false;
            var progress = new Progress<long>(n => _size.Text = $"Measuring… {n:N0} files so far");
            try
            {
                await Task.Run(() => _walker.Walk(_report.Path, new WalkOptions { IncludeSystem = true }, e =>
                {
                    if (e.IsDirectory) return !e.IsReparsePoint;
                    total += e.Size;
                    if (!e.IsCloudPlaceholder) local += e.Size;
                    if (++files % 5000 == 0) ((IProgress<long>)progress).Report(files);
                    return true;
                }, limit.Token), limit.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { stopped = true; }
            if (ct.IsCancellationRequested) return;
            var text = $"{(stopped ? "More than " : "")}{Sizes.Format(total)} in {files:N0} file{(files == 1 ? "" : "s")}";
            if (local < total) text += $" ({Sizes.Format(local)} on this PC; the rest is online-only)";
            if (stopped) text += ". Still counting when PC Tidy stopped; the Space map measures it fully.";
            _size.Text = text;
        }
        catch (Exception ex)
        {
            _size.Text = "Couldn't measure it (no access).";
            if (ex is not (IOException or UnauthorizedAccessException or OperationCanceledException)) Shell.CrashHandler.Write(ex, "Measuring for What is this?", terminating: false);
        }
        finally { _measured.TrySetResult(); }
    }

    private static (long Bytes, long Local) FileSize(string path)
    {
        var fi = new FileInfo(path);
        return (fi.Length, CloudFile.IsPlaceholder(fi.Attributes) ? 0 : fi.Length);
    }

    private static string DriveText(string root)
    {
        try
        {
            var d = new DriveInfo(root);
            return $"{Sizes.Format(d.TotalSize - d.AvailableFreeSpace)} used of {Sizes.Format(d.TotalSize)} ({Sizes.Format(d.AvailableFreeSpace)} free)";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return "Not known"; }
    }

    private static void Fact(Grid g, string label, FrameworkElement value)
    {
        int row = g.RowDefinitions.Count;
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var l = T(label, "Secondary", new Thickness(0, 4, 0, 0));
        Grid.SetRow(l, row);
        g.Children.Add(l);
        value.Margin = new Thickness(0, 4, 0, 0);
        Grid.SetRow(value, row);
        Grid.SetColumn(value, 1);
        AutomationProperties.SetName(value, label);
        g.Children.Add(value);
    }

    public static string PillText(WhatIsVerdict v) => v switch
    {
        WhatIsVerdict.SafeToClean => "Safe to clean",
        WhatIsVerdict.ThinkFirst => "Think first",
        WhatIsVerdict.DontDelete => "Don't delete",
        _ => "Not there",
    };

    private static PillTone Tone(WhatIsVerdict v) => v switch
    {
        WhatIsVerdict.SafeToClean => PillTone.Success,
        WhatIsVerdict.ThinkFirst => PillTone.Caution,
        WhatIsVerdict.DontDelete => PillTone.Critical,
        _ => PillTone.Neutral,
    };

    private static TextBlock T(string text, string style, Thickness? margin = null)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(StyleProperty, style);
        if (margin is { } m) t.Margin = m;
        return t;
    }
}
