using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace PcTidy.UI.Flow;

/// <summary>
/// The scan's notes above a preview, as ONE calm row (3.2) instead of a yellow bar per note (up to nine pushed the
/// list off the screen):
///   "Chrome, Edge and Steam are open · 4 more notes"   [Rescan] [Show]
/// Show opens every note, word for word (the open-app notes too), grouped by section. The row is amber when an app is
/// open or any note reports a problem ("couldn't be read"), neutral otherwise. A single note is shown as it is, in the
/// row itself. Scan errors are not notes: the pages keep their own red bars for those.
/// </summary>
public static class ScanNotes
{
    /// <summary>A note and the section it came from (null on a single-section page).</summary>
    public readonly record struct Note(string? Section, string Text);

    // ScanSession: "Brave is open: close it first (check the system tray too), or its cache files will be skipped. ..."
    private static readonly Regex AppOpen = new(@"^(?<names>.+?) (?:is|are) open: close (?:it|them) first", RegexOptions.CultureInvariant);

    /// <summary>The apps a note says are open, or null when it isn't that kind of note.</summary>
    internal static IReadOnlyList<string>? OpenApps(string note)
    {
        var m = AppOpen.Match(note);
        if (!m.Success) return null;
        return m.Groups["names"].Value.Replace(" and ", ", ").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>Notes that say part of the scan didn't work (they keep the amber look even when folded).</summary>
    internal static bool IsProblem(string note)
    {
        bool Has(string s) => note.Contains(s, StringComparison.OrdinalIgnoreCase);
        return Has("couldn't") || Has("could not") || Has("can't") || Has("failed") || Has("wasn't") || Has("not checked") || Has("error");
    }

    private static IReadOnlyList<string> AllOpenApps(IEnumerable<Note> notes)
        => notes.Select(n => OpenApps(n.Text)).Where(a => a is not null).SelectMany(a => a!)
            .Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(a => a, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>"Chrome, Edge and Steam are open, so their cache files will be skipped unless…" (null when none).</summary>
    internal static string? OpenAppsLine(IEnumerable<Note> notes)
    {
        var apps = AllOpenApps(notes);
        if (apps.Count == 0) return null;
        var names = PlanFacts.JoinAnd(apps);
        return apps.Count == 1
            ? $"{names} is open, so its cache files will be skipped unless you close it first (check the system tray too), then rescan. PC Tidy never closes apps for you."
            : $"{names} are open, so their cache files will be skipped unless you close them first (check the system tray too), then rescan. PC Tidy never closes apps for you.";
    }

    /// <summary>The row's one line: "Brave and Steam are open · 4 more notes", "6 notes from the scan", or the only note.</summary>
    internal static string Summary(IReadOnlyList<Note> notes)
    {
        var apps = AllOpenApps(notes);
        int others = notes.Count(n => OpenApps(n.Text) is null);
        if (apps.Count == 0) return others == 1 ? Line(notes[0]) : $"{others} notes from the scan";
        var open = $"{PlanFacts.JoinAnd(apps)} {(apps.Count == 1 ? "is" : "are")} open, so {(apps.Count == 1 ? "its" : "their")} cache files will be skipped";
        return others == 0 ? open : $"{open} · {others} more note{PlanFacts.S(others)}";
    }

    /// <summary>Adds the one notes row to <paramref name="panel"/>. False (nothing added) when there are no notes.</summary>
    /// <param name="rescan">What Rescan does (the page's own scan again); null hides the button.</param>
    /// <param name="lead">Put in front of the row ("Nothing is changed yet."), so the page needs no second bar for it.</param>
    /// <param name="leadDetail">The lead's full text, first under Show.</param>
    public static bool AddTo(Panel panel, IReadOnlyList<Note> notes, Action? rescan, string? lead = null, string? leadDetail = null)
    {
        if (notes.Count == 0) return false;
        bool apps = AllOpenApps(notes).Count > 0;
        bool caution = apps || notes.Any(n => IsProblem(n.Text));
        // One plain note needs no "Show": it is the row (unless the lead's detail has to go somewhere).
        bool fold = apps || notes.Count > 1 || leadDetail is not null;

        var card = new Border { Padding = new Thickness(14, 8, 10, 8), Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty, caution ? "TidyCautionSoftBrush" : "CardBackgroundFillColorDefaultBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        var stack = new StackPanel();

        var head = new DockPanel();
        var icon = new SymbolIcon { Symbol = caution ? SymbolRegular.Warning24 : SymbolRegular.Info24, FontSize = 18, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(Control.ForegroundProperty, caution ? "SystemFillColorCautionBrush" : "TextFillColorSecondaryBrush");
        DockPanel.SetDock(icon, Dock.Left);
        head.Children.Add(icon);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Right);
        if (apps && rescan is not null)
        {
            var again = new Button { Content = "Rescan", Icon = new SymbolIcon(SymbolRegular.ArrowClockwise24), Margin = new Thickness(0, 0, 8, 0) };
            AutomationProperties.SetName(again, "Rescan after closing the apps");
            again.Click += (_, _) => rescan();
            buttons.Children.Add(again);
        }
        System.Windows.Controls.Button? toggle = null;
        if (fold)
        {
            toggle = new System.Windows.Controls.Button { Content = "Show", VerticalAlignment = VerticalAlignment.Center };
            toggle.SetResourceReference(FrameworkElement.StyleProperty, "LinkButton");
            buttons.Children.Add(toggle);
        }
        head.Children.Add(buttons);

        var text = apps || notes.Count > 1 ? Summary(notes) : Line(notes[0]);
        if (lead is not null) text = $"{lead.TrimEnd('.')} · {text}";
        var title = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextWrapping = fold ? TextWrapping.NoWrap : TextWrapping.Wrap };
        title.SetResourceReference(FrameworkElement.StyleProperty, "Body");
        title.TextWrapping = fold ? TextWrapping.NoWrap : TextWrapping.Wrap; // the Body style wraps; the folded summary stays one line
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.ToolTip = title.Text;
        head.Children.Add(title);
        stack.Children.Add(head);

        if (fold && toggle is not null)
        {
            var body = new StackPanel { Margin = new Thickness(28, 8, 0, 2), Visibility = Visibility.Collapsed };
            if (leadDetail is not null) body.Children.Add(Para($"{lead} {leadDetail}".Trim(), "Body", 0));
            if (OpenAppsLine(notes) is { } line) body.Children.Add(Para(line, "Body", leadDetail is null ? 0 : 6));
            var sections = notes.Select(n => n.Section).Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
            foreach (var group in notes.GroupBy(n => n.Section ?? ""))
            {
                if (sections.Count > 1 && group.Key.Length > 0) body.Children.Add(Para(group.Key, "BodyStrong", 8));
                foreach (var n in group) body.Children.Add(Para("• " + n.Text, "Secondary", 3)); // every note, word for word
            }
            stack.Children.Add(body);

            void Name(bool open)
            {
                toggle.Content = open ? "Hide" : "Show";
                AutomationProperties.SetName(toggle, (open ? "Hide" : "Show") + $" the {notes.Count} note{PlanFacts.S(notes.Count)} from the scan");
                AutomationProperties.SetItemStatus(toggle, open ? "Expanded" : "Collapsed");
            }
            Name(false);
            toggle.Click += (_, _) =>
            {
                bool open = body.Visibility != Visibility.Visible;
                body.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                Name(open);
            };
        }
        AutomationProperties.SetName(card, "Notes from the scan: " + title.Text);
        card.Child = stack;
        panel.Children.Add(card);
        return true;
    }

    private static string Line(Note n) => n.Section is { Length: > 0 } s ? $"{s}: {n.Text}" : n.Text;

    private static TextBlock Para(string text, string style, double top)
    {
        var t = new TextBlock { Text = text, Margin = new Thickness(0, top, 0, 2) };
        t.SetResourceReference(FrameworkElement.StyleProperty, style);
        t.TextWrapping = TextWrapping.Wrap;
        return t;
    }
}
