using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using PcTidy.App.Composition.Demo;
using PcTidy.App.Pages.Now;
using PcTidy.App.Shell;
using PcTidy.Core.Model;
using PcTidy.UI.Flow;
using Xunit;

namespace PcTidy.UI.Tests;

/// <summary>v3.1 review fixes in the shell and shared UI: contrast, the rail's More, the tour, the confirm list, one task at a time, the Now card, the demo pages.</summary>
public class V31FixTests
{
    private static void Sta(Action action)
    {
        ExceptionDispatchInfo? error = null;
        var t = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        error?.Throw();
    }

    private static string SrcRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "PcTidy.sln"))) return Path.Combine(d.FullName, "src");
        throw new InvalidOperationException("Tests must run from inside the PC Tidy repository.");
    }

    // ---------------- contrast (UI review H1) ----------------

    private static double Lum(Color c)
    {
        static double Lin(byte v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    private static Color Over(Color fg, Color bg)
    {
        double a = fg.A / 255.0;
        byte Mix(byte f, byte b) => (byte)Math.Round(f * a + b * (1 - a));
        return Color.FromRgb(Mix(fg.R, bg.R), Mix(fg.G, bg.G), Mix(fg.B, bg.B));
    }

    private static double Contrast(Color fg, Color bg)
    {
        double l1 = Lum(Over(fg, bg)), l2 = Lum(bg);
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }

    private static Color Hex(string s) => (Color)ColorConverter.ConvertFromString(s);

    /// <summary>A brush of TidyLight.xaml / TidyDark.xaml, read from the source file.</summary>
    private static Color TidyBrush(string file, string key)
    {
        Color c = default;
        Sta(() =>
        {
            var dict = (ResourceDictionary)XamlReader.Parse(File.ReadAllText(Path.Combine(SrcRoot(), "PcTidy.UI", "Themes", file)));
            c = ((SolidColorBrush)dict[key]).Color;
        });
        return c;
    }

    // WPF-UI 4.3's theme values (Resources/Theme/Light.xaml and Dark.xaml): text fills and the card over the window background.
    private static readonly (string Theme, Color Secondary, Color Tertiary, Color Background, Color Card)[] Themes =
    {
        ("Light", Hex("#9E000000"), Hex("#72000000"), Hex("#F3F3F3"), Hex("#FFFFFFFF")),
        ("Dark", Hex("#C5FFFFFF"), Hex("#87FFFFFF"), Hex("#202020"), Hex("#2B2B2B")),
    };

    [Fact]
    public void Caption_uses_the_secondary_text_brush_and_every_small_text_brush_reaches_4_5_to_1_in_both_themes()
    {
        var tidy = File.ReadAllText(Path.Combine(SrcRoot(), "PcTidy.UI", "Themes", "Tidy.xaml"));
        var caption = Regex.Match(tidy, @"<Style x:Key=""Caption"".*?</Style>", RegexOptions.Singleline).Value;
        Assert.Contains("TextFillColorSecondaryBrush", caption);
        Assert.DoesNotContain("Tertiary", caption);

        foreach (var (theme, secondary, tertiary, background, card) in Themes)
        {
            var accentText = TidyBrush($"Tidy{theme}.xaml", "TidyAccentTextBrush");
            var soft = Over(TidyBrush($"Tidy{theme}.xaml", "TidyAccentSoftBrush"), card);
            foreach (var bg in new[] { background, card })
            {
                Assert.True(Contrast(secondary, bg) >= 4.5, $"{theme}: secondary text {Contrast(secondary, bg):0.00}:1");
                Assert.True(Contrast(accentText, bg) >= 4.5, $"{theme}: accent text {Contrast(accentText, bg):0.00}:1");
            }
            Assert.True(Contrast(accentText, soft) >= 4.5, $"{theme}: accent pill text {Contrast(accentText, soft):0.00}:1");
            // The tertiary brush is why Caption moved: it fails in light (decoration only from now on).
            if (theme == "Light") Assert.True(Contrast(tertiary, card) < 4.5);
        }
    }

    [Fact]
    public void Small_text_in_the_shell_and_shared_controls_never_uses_the_tertiary_or_icon_accent_brush()
    {
        var src = SrcRoot();
        string Read(params string[] p) => File.ReadAllText(Path.Combine(new[] { src }.Concat(p).ToArray()));
        Assert.Contains("\"TidyAccentTextBrush\"", Read("PcTidy.UI", "Controls", "BadgePill.cs")); // accent pills
        Assert.DoesNotContain("TextFillColorTertiaryBrush", Read("PcTidy.UI", "Controls", "StepBar.cs"));
        Assert.DoesNotContain("TextFillColorTertiaryBrush", Read("PcTidy.App", "Shell", "MainWindow.xaml")); // rail group headers
        Assert.DoesNotContain("TextFillColorTertiaryBrush", Read("PcTidy.App", "Pages", "HistoryPage.xaml"));
        Assert.Contains("TidyAccentTextBrush", Read("PcTidy.App", "Shell", "MainWindow.xaml")); // "Demo · sample data"
    }

    // ---------------- rail and tour (UI review H2, L1) ----------------

    [Fact]
    public void The_real_rail_keeps_More_on_screen_at_1024_by_700()
    {
        var modules = PcTidy.App.Composition.FeatureCatalog.All().OrderBy(m => m.Group).ThenBy(m => m.Order).ToList();
        var rows = RailLayout.Build(modules);
        Assert.Equal(RailEntryKind.More, rows[^1].Kind);
        foreach (var id in new[] { "duplicates", "drivers", "search", "notifications" })
            Assert.Contains(rows[^1].MoreChildren, m => m.Id == id);
        // At 700 px the rail has room for about 23 rows of 24 px above the footer (title bar 48, footer 3 rows).
        int headers = rows.Count(r => r.Kind == RailEntryKind.Header);
        double height = 1 * 24 + headers * 20 + rows.Count(r => r.Kind is RailEntryKind.Module or RailEntryKind.More) * 24;
        Assert.True(height <= 700 - 48 - 3 * 24 - 40, $"The closed rail needs {height} px");
    }

    [Fact]
    public void The_tour_card_tabs_in_the_order_it_shows_its_buttons() => Sta(() =>
    {
        var overlay = new TourOverlay(new TourModel(), _ => null);
        overlay.Model.GoTo(2);
        var buttons = LogicalDescendants(overlay).OfType<Wpf.Ui.Controls.Button>().ToDictionary(b => (string)b.Content == "Next" || (string)b.Content == "Done" ? "Next" : (string)b.Content);
        Assert.True(KeyboardNavigation.GetTabIndex(buttons["Close"]) < KeyboardNavigation.GetTabIndex(buttons["Back"]));
        Assert.True(KeyboardNavigation.GetTabIndex(buttons["Back"]) < KeyboardNavigation.GetTabIndex(buttons["Next"]));
        Assert.Contains("Duplicates", TourModel.DefaultSteps.Single(s => s.Target == "rail.more").Text);
    });

    private static IEnumerable<object> LogicalDescendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            yield return child;
            if (child is DependencyObject d) foreach (var x in LogicalDescendants(d)) yield return x;
        }
    }

    // ---------------- confirm dialog (UI review M4, L2) ----------------

    private static ConfirmModel ManyItems(int n) => new(new[]
    {
        new ActionPlan
        {
            FeatureId = "cleanup", Title = "Clean",
            Items = Enumerable.Range(1, n).Select(i => new PlanItem
            {
                Kind = ActionKind.QuarantinePath, FeatureId = "cleanup", Description = $@"A long invented path C:\Sample\Folder {i}\with\many\levels\file-{i}.tmp", ExpectedSize = 1024,
            }).ToArray(),
        },
    }, null, 7);

    [Fact]
    public void On_a_short_window_the_affected_list_keeps_four_rows_and_the_tip_is_one_line() => Sta(() =>
    {
        var content = new ConfirmDialogContent(ManyItems(40), suggestSimulation: true, compact: true);
        Assert.NotNull(content.FirstTimeTip);
        var list = content.AffectedList!;
        Assert.True(list.MinHeight >= ConfirmDialogContent.ListMinRows * 20, $"list min height {list.MinHeight}");
        Assert.True(list.MaxHeight <= ConfirmDialogContent.ListMaxHeight);

        // 1024×700: the host is about 650 px, so body + list get 400 px; the groups scroll in what the list leaves.
        content.BodyMaxHeight = 650 - 250;
        Assert.True(content.GroupsMaxHeight >= ConfirmDialogContent.MinBodyHeight);
        Assert.True(content.GroupsMaxHeight + ConfirmDialogContent.ListMaxHeight + 30 <= 400 + 1);

        // Rows wrap and carry their full text as a tooltip.
        if (!list.ItemTemplate.IsSealed) list.ItemTemplate.Seal();
        var tb = (TextBlock)list.ItemTemplate.LoadContent();
        Assert.Equal(TextWrapping.Wrap, tb.TextWrapping);
        Assert.NotNull(System.Windows.Data.BindingOperations.GetBinding(tb, FrameworkElement.ToolTipProperty));

        var tall = new ConfirmDialogContent(ManyItems(2), suggestSimulation: true, compact: false);
        Assert.Null(tall.FirstTimeTip); // the full banner on a tall window
    });

    [Fact]
    public void Several_kinds_of_change_read_Apply_these_not_Apply_all()
    {
        var plans = new[]
        {
            new ActionPlan { FeatureId = "cleanup", Title = "Clean", Items = new[] { new PlanItem { Kind = ActionKind.QuarantinePath, FeatureId = "cleanup", Description = "a", ExpectedSize = 1 } } },
            new ActionPlan { FeatureId = "tweaks", Title = "Tweaks", Items = new[] { new PlanItem { Kind = ActionKind.RegistrySetValue, FeatureId = "tweaks", Description = "b" } } },
        };
        var text = PlanFacts.PrimaryAction(plans.SelectMany(p => p.Items).ToList());
        Assert.StartsWith("Apply these", text);
        Assert.DoesNotContain("Apply all", text);
    }

    // ---------------- one task at a time (UI review L3, code review L6) ----------------

    [Fact]
    public void The_busy_reason_names_the_task_still_going()
    {
        var gate = new RunGate();
        Assert.Null(gate.Reason);
        var clean = gate.Begin("Clean");
        Assert.Equal("Available when Clean finishes", gate.Reason);
        var undo = gate.Begin("the undo");
        Assert.Equal("Available when the undo finishes", gate.Reason);
        undo.Dispose();
        Assert.Equal("Available when Clean finishes", gate.Reason); // back to the one still running
        clean.Dispose();
        Assert.Null(gate.Reason);

        // A progress title isn't a name: the general reason.
        using (gate.Begin("Working through every section…")) Assert.Equal(RunGate.BusyReason, gate.Reason);
        Assert.Equal(RunGate.BusyReason, RunGate.ReasonFor(""));
        Assert.Equal(RunGate.BusyReason, RunGate.ReasonFor("Clean: working on it…"));
    }

    [Fact]
    public void Gated_buttons_show_the_named_reason() => Sta(() =>
    {
        var gate = new RunGate();
        var run = new Button { IsEnabled = true };
        using var handle = RunGateButtons.Gate(run, gate);
        using (gate.Begin("Clean"))
        {
            Assert.False(run.IsEnabled);
            Assert.Equal("Available when Clean finishes", run.ToolTip);
        }
        Assert.True(run.IsEnabled);
    });

    [Fact]
    public void A_refused_second_task_says_what_to_wait_for()
    {
        var text = FlowHost.BusyMessage("Available when Clean finishes");
        Assert.StartsWith("Available when Clean finishes.", text);
        Assert.Contains("nothing new was started", text);
        Assert.Equal("One task at a time", FlowHost.OneTaskTitle);
        Assert.StartsWith(RunGate.BusyReason, FlowHost.BusyMessage(null));
    }

    // ---------------- Now card (code review M1, L4; UI review L9) ----------------

    [Fact]
    public async Task A_hung_nvidia_smi_query_is_not_repeated_and_never_runs_twice_at_once()
    {
        var release = new TaskCompletionSource<(NvidiaTemperature.Outcome, int?)>();
        int calls = 0;
        var temperature = new NvidiaTemperature(@"C:\invented\nvidia-smi.exe", (_, timeout) =>
        {
            Interlocked.Increment(ref calls);
            Assert.Equal(NvidiaTemperature.QueryTimeout, timeout);
            return release.Task;
        }, every: TimeSpan.Zero);

        Assert.Null(temperature.Latest());
        for (int i = 0; i < 20; i++) temperature.Latest(); // due every time, but one is still going
        await Task.Delay(50);
        Assert.Equal(1, Volatile.Read(ref calls));

        release.SetResult((NvidiaTemperature.Outcome.TimedOut, null)); // it hung and was killed
        for (int i = 0; i < 50 && !temperature.GaveUp; i++) await Task.Delay(10);
        Assert.True(temperature.GaveUp);
        Assert.False(temperature.IsAvailable);
        for (int i = 0; i < 5; i++) Assert.Null(temperature.Latest());
        await Task.Delay(50);
        Assert.Equal(1, Volatile.Read(ref calls)); // no more queries this session
    }

    [Fact]
    public async Task A_good_nvidia_smi_reading_is_kept_and_the_next_waits_its_turn()
    {
        int calls = 0;
        var temperature = new NvidiaTemperature(@"C:\invented\nvidia-smi.exe", (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<(NvidiaTemperature.Outcome, int?)>((NvidiaTemperature.Outcome.Ok, 61));
        }, every: TimeSpan.FromHours(1));
        temperature.Latest();
        for (int i = 0; i < 50 && temperature.Latest() is null; i++) await Task.Delay(10);
        Assert.Equal(61, temperature.Latest());
        Assert.Equal(1, Volatile.Read(ref calls)); // not due again for an hour
        Assert.Null(new NvidiaTemperature(null).Latest()); // no nvidia-smi: nothing
    }

    private sealed class PrimingSource : INowSource
    {
        private int _n;
        public NowSample? Read() => _n++ == 0 ? null : new NowSample(20, 30, 40);
    }

    [Fact]
    public void A_primed_only_reading_shows_nothing_and_the_line_names_its_peak()
    {
        var sampler = new NowSampler(new PrimingSource());
        var seen = new List<NowSample>();
        sampler.Sampled += seen.Add;
        sampler.SampleOnce();
        Assert.Empty(seen); // the counters were only primed: no dip to 0
        sampler.SampleOnce();
        Assert.Single(seen);

        Assert.Equal("CPU load, last 60 seconds", NowCard.NowCaption("CPU", Array.Empty<double>()));
        Assert.Equal("GPU load, last 60 seconds · peak 42%", NowCard.NowCaption("GPU", new[] { 10, 42.4, 30 }));
    }

    // ---------------- demo pages (UI review M6) ----------------

    [Fact]
    public void The_demo_shows_the_v3_1_pages_on_invented_data()
    {
        var demo = DemoModules.All().Cast<DemoModule>().ToDictionary(m => m.Id);
        foreach (var id in new[] { "tweaks", "measure", "games", "pccare" }) Assert.NotNull(demo[id].PageFactory);
        Assert.Null(demo["cleanup"].PageFactory);
        Assert.True(demo["games"].Scanners.Count > 0); // still in Optimize everything

        var registry = DemoRegistryReader.WithTweaksOn(DemoV31.TweaksOn);
        Assert.Equal(DemoV31.TweaksOn, registry.On.Count);
        var tweak = PcTidy.Catalog.Tweaks.TweakCatalog.Find(registry.On[0])!;
        var change = tweak.ActionableChanges.OfType<PcTidy.Catalog.Tweaks.RegistryValueChange>().First();
        Assert.Equal(change.Data, registry.Read(change.Hive, change.KeyPath, change.ValueName)!.Data);
        Assert.True(registry.KeyExists(change.Hive, change.KeyPath));
        Assert.True(registry.Redirect.IsRedirected); // any plan built from it points at the test area, never a real key
        Assert.Null(registry.Read(PcTidy.Core.Model.RegistryHive.CurrentUser, @"Software\Nothing", "Here"));
    }

    [Fact]
    public void The_demo_recording_and_PC_are_invented_and_stable()
    {
        var a = DemoV31.Recording("x.csv", 9, seed: 5);
        var b = DemoV31.Recording("x.csv", 9, seed: 5);
        Assert.Equal(a.Processes[0].PresentMs, b.Processes[0].PresentMs);
        Assert.Equal("SampleGame.exe", a.Processes[0].ProcessName);

        var report = DemoV31.SystemInfoReport();
        var all = string.Join(" ", report.Categories.SelectMany(c => c.Rows).Select(r => r.Label + " " + r.Value));
        Assert.Contains("Sample 8-core processor", all);
        Assert.DoesNotContain(Environment.MachineName, all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, all, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_demo_System_info_view_shows_the_invented_PC() => Sta(() =>
    {
        var view = DemoV31.SystemInfoView(new NoServices());
        var texts = LogicalDescendants(view).OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains(texts, t => t.Contains("PC Tidy only reads these details"));
        Assert.DoesNotContain(texts, t => t.Contains("Reading this PC"));
    });

    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
