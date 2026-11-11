using System.Windows;
using PcTidy.Catalog;
using PcTidy.Catalog.Tweaks;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Tweaks.Reading;
using PcTidy.Features.Tweaks.Tweaks;
using PcTidy.Features.Tweaks.UI;
using PcTidy.Hosting;
using Xunit;
using CoreHive = PcTidy.Core.Model.RegistryHive;

namespace PcTidy.Features.Tweaks.Tests;

/// <summary>
/// v3.1 Tweaks page (layout spec §4.1), the rail counter, the "Doesn't help (measured)" shelf (build list C10) and the measured
/// numbers on offered rows. Everything runs on a fake registry under HKCU\Software\PCTidyTest\tweaks and folders under
/// testbed\tweaks-v31; nothing on this PC is read for a result or changed.
/// </summary>
public class TweaksPageV31Tests
{
    private const string Area = "tweaks-v31";
    private const string Adv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    private static async Task<IReadOnlyList<TweakItemRow>> Rows(TestRegistry reg, TestFolder folder, bool expert = false, FixedSystemProbe? probe = null)
    {
        var env = TestEnv.Create(reg, folder.Path, probe);
        var outcome = await new TweaksScanner(_ => env).ScanRowsAsync(TestEnv.Context(folder.Path, expert), null, CancellationToken.None);
        return outcome.Rows.Select(r => TweakItemRow.ForTweak(r, _ => { }, () => { })).ToList();
    }

    private static TweakItemRow Find(IEnumerable<TweakItemRow> rows, string id) => Assert.Single(rows, r => r.Tweak?.Id == id);

    // ------------------------------------------------------------------ filter, search, sort, chip counts

    [Fact]
    public async Task Chips_split_the_rows_and_the_counts_add_up()
    {
        using var reg = new TestRegistry("v31");
        using var folder = new TestFolder("chips", Area);
        var rows = await Rows(reg, folder);
        var counts = TweakFilters.Counts(rows, null);

        Assert.Equal(rows.Count, counts[TweakChip.All]);
        // Every row is in exactly one category chip (Gaming · Privacy · Interface · Network · Power · Doesn't help).
        var categories = new[] { TweakChip.Gaming, TweakChip.Privacy, TweakChip.Interface, TweakChip.Network, TweakChip.Power, TweakChip.DoesntHelp };
        Assert.Equal(rows.Count, categories.Sum(c => counts[c]));
        foreach (var c in TweakFilters.ChipOrder) Assert.Equal(counts[c], TweakFilters.Filter(rows, c, null).Count());

        Assert.All(TweakFilters.Filter(rows, TweakChip.DoesntHelp, null), r => Assert.True(r.IsShelf, r.Title));
        Assert.DoesNotContain(TweakFilters.Filter(rows, TweakChip.Gaming, null), r => r.IsShelf);
        Assert.All(TweakFilters.Filter(rows, TweakChip.Gaming, null), r => Assert.Equal(TweakGroup.Gaming, r.Tweak!.Group));
        // Store-app removal sits under Interface with Explorer and the taskbar.
        Assert.Contains(TweakFilters.Filter(rows, TweakChip.Interface, null), r => r.Tweak!.Group == TweakGroup.Debloat || r.Tweak!.Group == TweakGroup.UiExplorer);
        Assert.All(TweakFilters.Filter(rows, TweakChip.Interface, null), r => Assert.Contains(r.Tweak!.Group, new[] { TweakGroup.Debloat, TweakGroup.UiExplorer }));

        // Expert chip: Expert rows, and without Expert mode they still can't be ticked.
        var expert = TweakFilters.Filter(rows, TweakChip.Expert, null).ToList();
        Assert.NotEmpty(expert);
        Assert.All(expert, r => { Assert.True(r.IsExpert); Assert.False(r.IsSelectable, r.Title); });
        Assert.True(counts[TweakChip.DoesntHelp] >= 40, $"the shelf holds the research's placebo rows ({counts[TweakChip.DoesntHelp]})");
    }

    [Fact]
    public async Task Search_matches_every_word_in_titles_values_and_measured_notes_and_counts_follow_it()
    {
        using var reg = new TestRegistry("v31");
        using var folder = new TestFolder("search", Area);
        var rows = await Rows(reg, folder);

        Assert.Contains(TweakFilters.Filter(rows, TweakChip.All, "nagle"), r => r.Tweak!.Id == "network.nagle");
        Assert.Contains(TweakFilters.Filter(rows, TweakChip.All, "HideFileExt"), r => r.Tweak!.Id == "ui.file-extensions"); // exact value
        Assert.Contains(TweakFilters.Filter(rows, TweakChip.All, "6.1 vs 6.0"), r => r.Tweak!.Id == "gaming.fso-off-global"); // measured note
        Assert.Contains(TweakFilters.Filter(rows, TweakChip.All, "PCWORLD"), r => r.Tweak!.Id == "gaming.background-recording-off"); // source, any case
        Assert.Empty(TweakFilters.Filter(rows, TweakChip.All, "nagle explorer"));
        Assert.Empty(TweakFilters.Filter(rows, TweakChip.All, "zzzz-no-such-tweak"));
        Assert.Equal(rows.Count, TweakFilters.Filter(rows, TweakChip.All, "   ").Count());

        var counts = TweakFilters.Counts(rows, "nagle");
        Assert.Equal(1, counts[TweakChip.All]);
        Assert.Equal(1, counts[TweakChip.DoesntHelp]);
        Assert.Equal(0, counts[TweakChip.Gaming]);
    }

    [Fact]
    public async Task Sort_by_evidence_name_and_changed_first()
    {
        using var reg = new TestRegistry("v31");
        using var folder = new TestFolder("sort", Area);
        reg.Cu(Adv, "HideFileExt", 0); // file extensions already shown (applied)
        reg.Lm(@"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", 0x26); // a placebo value set
        var rows = await Rows(reg, folder);

        var evidence = TweakFilters.Sort(rows, TweakSort.Evidence).ToList();
        var ranks = evidence.Select(TweakFilters.EvidenceRank).ToList();
        Assert.Equal(ranks.OrderBy(x => x).ToList(), ranks);
        Assert.Equal(TweakVerdict.Proven, evidence[0].Tweak!.Verdict);
        Assert.True(evidence[^1].IsShelf);
        // On the shelf, rows with measured (A-C) evidence come before "no evidence" (D) rows.
        var shelf = evidence.Where(r => r.IsShelf).ToList();
        var firstNoEvidence = shelf.FindIndex(r => r.Tweak!.ShelfLabel == TweakDefinition.NoEvidenceLabel);
        Assert.True(firstNoEvidence > 0);
        Assert.All(shelf.Skip(firstNoEvidence), r => Assert.Equal(TweakDefinition.NoEvidenceLabel, r.Tweak!.ShelfLabel));

        var byName = TweakFilters.Sort(rows, TweakSort.Name).Select(r => r.Title).ToList();
        Assert.Equal(byName.OrderBy(t => t, StringComparer.CurrentCultureIgnoreCase).ToList(), byName);

        var changed = TweakFilters.Sort(rows, TweakSort.ChangedFirst).ToList();
        var lastChanged = changed.FindLastIndex(r => r.IsChanged);
        Assert.True(lastChanged >= 1);
        Assert.All(changed.Take(lastChanged + 1), r => Assert.True(r.IsChanged, r.Title));
        Assert.Contains(changed.Take(lastChanged + 1), r => r.Tweak!.Id == "ui.file-extensions");
        Assert.Contains(changed.Take(lastChanged + 1), r => r.Tweak!.Id == "gaming.priority-separation");

        // Sections: page groups first, the shelf last, nothing lost or doubled.
        var sections = TweakFilters.Sections(rows, TweakChip.All, null, TweakSort.Evidence);
        Assert.Equal(TweakFilters.ShelfTitle, sections[^1].Header.Title);
        Assert.Equal(rows.Count, sections.Sum(s => s.Rows.Count));
        Assert.Equal(rows.Count, sections.SelectMany(s => s.Rows).Distinct().Count());
    }

    // ------------------------------------------------------------------ state-aware cards

    [Fact]
    public async Task Cards_show_now_on_off_from_the_read_back_and_evidence_and_safety_pills()
    {
        using var reg = new TestRegistry("v31");
        using var folder = new TestFolder("cards", Area);
        reg.Cu(Adv, "HideFileExt", 0);
        var rows = await Rows(reg, folder);

        var ext = Find(rows, "ui.file-extensions");
        Assert.Equal("Now: On", ext.NowText);
        Assert.Equal("Proven", ext.Evidence.Text);
        Assert.Equal("Safe", ext.Safety.Text);
        Assert.Contains("HideFileExt = 0", ext.WhatChanges.Replace(" (0x0)", ""));

        var hidden = Find(rows, "ui.hidden-files");
        Assert.Equal("Now: Off", hidden.NowText);
        Assert.True(hidden.IsSelectable);

        var hags = rows.FirstOrDefault(r => r.Tweak?.Id == "gaming.hags-on"); // needs a known GPU vendor in this env
        Assert.Null(hags);

        var fso = Find(rows, "gaming.fso-off-global");
        Assert.Equal(TweakDefinition.DoesntHelpLabel, fso.Evidence.Text);
        Assert.Equal("Info only", fso.Safety.Text);
        Assert.StartsWith("Measured: fullscreen optimizations 6.1 vs 6.0 ms · 9800X3D + RTX 5070 Ti, CS2 · CS2 Kitchen (YouTube), 2026-07-12", fso.MeasuredSummary);

        var mtu = Find(rows, "network.mtu");
        Assert.Equal(TweakDefinition.NoEvidenceLabel, mtu.Evidence.Text);
        Assert.StartsWith("No evidence it helps: ", mtu.Item.DisabledReason);
        Assert.Contains("Can't be ticked", mtu.CardAutomationName);
        Assert.Contains("weak evidence", mtu.MeasuredSummary); // plain English (UI review L8); the grade letter stays in Details
        Assert.DoesNotContain("evidence D", mtu.MeasuredSummary);
    }

    [Fact]
    public void A_toggle_on_a_card_never_plans_or_runs_anything()
    {
        using var reg = new TestRegistry("v31");
        using var folder = new TestFolder("toggle", Area);
        using var host = PcTidyHost.CreateSandbox(Path.Combine(folder.Path, "pc"), SandboxProfile.Minimal);
        var flow = new FakeFlowHost();
        var module = new TweaksModule(ctx => TestEnv.Create(reg, folder.Path));

        Sta.Run(() =>
        {
            var page = (FrameworkElement)module.CreatePage(new Services(host, flow))!;
            var vm = (TweaksViewModel)page.DataContext;
            Sta.Pump(vm.ScanCommand.ExecuteAsync(null));
            vm.ChooseView(1); // Cards
            Assert.True(vm.ShowCards);
            page.Measure(new Size(1280, 800));
            page.Arrange(new Rect(0, 0, 1280, 800));
            page.UpdateLayout();
            Assert.NotEmpty(vm.Sections);

            foreach (var card in vm.Sections.SelectMany(s => s.Rows)) { card.IsChecked = true; card.IsChecked = false; card.IsChecked = true; }
            Sta.Pump(Task.Delay(25));
            Assert.Empty(flow.Reviewed);
            Assert.Empty(flow.Simulated);
            Assert.Empty(flow.Approved);
            // Shelf cards stay unticked; ticked cards only become a plan when Preview is pressed.
            Assert.All(vm.Sections.SelectMany(s => s.Rows).Where(r => r.IsShelf), r => Assert.False(r.IsChecked));
            Assert.True(vm.HasSelection);

            Sta.Pump(vm.PreviewCommand.ExecuteAsync(null));
            var plan = Assert.Single(flow.Reviewed);
            Assert.Equal("tweaks", plan.FeatureId);
            Assert.DoesNotContain(plan.Items, i => vm.AllRows.Where(r => r.IsShelf).SelectMany(r => r.Item.Actions).Contains(i));
        });
    }

    // ------------------------------------------------------------------ the shelf: never planned

    [Fact]
    public void No_plan_item_can_come_from_a_shelf_row_in_any_state()
    {
        using var reg = new TestRegistry("v31");
        using var folder = new TestFolder("shelf", Area);
        var env = TestEnv.Create(reg, folder.Path);
        var shelf = TweakCatalog.All.Where(t => t.IsShelf).ToList();
        Assert.True(shelf.Count >= 50);
        foreach (var t in shelf)
        {
            Assert.False(t.IsOffered, t.Id);
            Assert.DoesNotContain(t, TweakPresets.DefaultTicks(t.Preset == TweakPreset.None ? TweakPreset.SafeGaming : t.Preset));
            foreach (var status in new[] { TweakStatus.Available, TweakStatus.Differs, TweakStatus.Applied, TweakStatus.Information })
            {
                // A state that says "everything could be changed": the builder must still plan nothing.
                var changes = t.Changes.Select(c => new ChangeState(c, status == TweakStatus.Information ? TweakStatus.Available : status, "x", PlannedData: "1")).ToList();
                var row = TweakRowBuilder.Build(t, new TweakState(status, changes, null), env, expertAllowed: true);
                Assert.Empty(row.Item.Actions);
                Assert.False(row.IsSelectable, t.Id);
                Assert.False(row.Item.DefaultSelected, t.Id);
                var item = TweakItemRow.ForTweak(row, _ => { }, () => { });
                item.IsChecked = true;
                Assert.False(item.IsChecked, t.Id);
            }
        }
    }

    [Fact]
    public void Every_shelf_row_shows_its_number_hardware_source_date_and_strength()
    {
        foreach (var t in TweakCatalog.All.Where(t => t.IsShelf))
        {
            Assert.NotEmpty(t.Measured);
            foreach (var m in t.Measured)
            {
                Assert.False(string.IsNullOrWhiteSpace(m.Number), t.Id);
                Assert.False(string.IsNullOrWhiteSpace(m.Hardware), t.Id);
                Assert.False(string.IsNullOrWhiteSpace(m.Source), t.Id);
                Assert.False(string.IsNullOrWhiteSpace(m.SourceDate), t.Id);
                Assert.False(string.IsNullOrWhiteSpace(m.StrengthText), t.Id);
                Assert.Contains(m.Number, m.Describe());
                if (m.Snippet) Assert.Contains("(snippet)", m.Describe());
            }
            // "No evidence it helps" exactly when every note is strength D; never "proven zero".
            var allD = t.Measured.All(m => m.Strength == EvidenceStrength.D);
            Assert.Equal(allD ? TweakDefinition.NoEvidenceLabel : TweakDefinition.DoesntHelpLabel, t.ShelfLabel);
            Assert.DoesNotContain("proven zero", (t.Effect + t.Caveat + t.ShelfLabel).ToLowerInvariant());
        }
        // The research's named folklore rows are all on the shelf.
        foreach (var id in new[]
        {
            "gaming.system-responsiveness", "gaming.mmcss-games-task", "gaming.priority-separation", "gaming.timer-resolution", "gaming.hpet",
            "power.ultimate-performance", "power.min-max-processor-state", "gaming.power-throttling-off", "gaming.svchost-split",
            "gaming.fso-off-global", "ui.visual-effects-off", "gaming.search-indexing-off", "gaming.game-mode-toggle",
            "gaming.nvidia-prefer-max-performance", "gaming.texture-filtering", "gaming.nvcp-image-slider", "gaming.debloated-driver", "gaming.ddu-speed-fix",
            "gaming.network-throttling", "network.nagle", "network.qos-reserve", "network.rss", "network.rsc", "network.ecn", "network.timestamps",
            "network.optimize-buttons", "network.mtu", "gaming.ram-cleaners", "gaming.standby-list-cleaners", "gaming.prefetch-delete", "gaming.sysmain-off",
            "gaming.xbox-services-off", "gaming.services-presets", "gaming.game-boosters", "gaming.launch-high", "gaming.launch-threads", "gaming.launch-cpucount",
            "gaming.roblox-fflags", "power.readyboost", "gaming.data-queue-size", "power.pcie-aspm-off", "power.c-states-off", "power.asus-xmp-tweaked",
            "gaming.reflex-boost",
        })
            Assert.True(TweakCatalog.Find(id)?.IsShelf == true, id);
        Assert.Equal(EvidenceStrength.D, TweakCatalog.Find("gaming.system-responsiveness")!.Measured[0].Strength); // "A (doc) / D"
        Assert.Equal("A (doc) / D", TweakCatalog.Find("gaming.system-responsiveness")!.Measured[0].StrengthText);
        Assert.Contains("902.6 vs 900.7", TweakCatalog.Find("gaming.launch-high")!.Measured[0].Number);
    }

    [Fact]
    public void Shelf_values_are_never_writable_and_offered_rows_keep_their_allowances()
    {
        var allowances = new TweakRegistryAllowances().Allowances().ToList();
        var offeredKeys = TweakCatalog.All.Where(t => t.IsOffered).SelectMany(t => t.ActionableChanges).OfType<RegistryValueChange>()
            .Select(r => TweakRegistryAllowances.KeyOf(r.Hive, r.KeyPath, r.ValueName)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var t in TweakCatalog.All.Where(t => t.IsShelf))
        foreach (var r in t.Changes.OfType<RegistryValueChange>())
        {
            var key = TweakRegistryAllowances.KeyOf(r.Hive, r.KeyPath, r.ValueName);
            if (offeredKeys.Contains(key)) continue; // the same value is also an offered row's
            Assert.DoesNotContain(allowances, a => a.Source.StartsWith(TweakRegistryAllowances.TweakSourcePrefix + t.Id, StringComparison.Ordinal));
        }
        foreach (var t in TweakCatalog.All.Where(t => t.IsOffered))
        foreach (var r in t.ActionableChanges.OfType<RegistryValueChange>())
        {
            var a = RegistryCatalog.Find(r.Hive, r.KeyPath, r.ValueName);
            Assert.True(a is not null, $"{t.Id}: no allowance for {r.Label}");
            Assert.Contains(r.Data, a!.AllowedData);
        }
        // The new display-only rows have nothing to write.
        Assert.False(TweakCatalog.Find("power.high-performance-plan")!.HasActions);
        Assert.False(TweakCatalog.Find("gaming.mpo-off")!.HasActions);
        // OverlayTestMode is never written by a tweak (the bad-tweak finder may only remove it).
        var mpo = RegistryCatalog.Find(CoreHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode");
        Assert.True(mpo is null || (mpo.Source.StartsWith(TweakRegistryAllowances.FinderSourcePrefix, StringComparison.Ordinal) && mpo.AllowedData.Count == 0));
    }

    [Fact]
    public void Offered_rows_carry_the_research_numbers()
    {
        var hags = TweakCatalog.Find("gaming.hags-on")!;
        Assert.Contains(hags.Measured, m => m.Number.Contains("+/-2%") && m.Number.Contains("required for DLSS FG"));
        Assert.Contains(hags.Measured, m => m.Number.Contains("26 games"));
        Assert.Contains("±2%", hags.Effect);
        var recording = TweakCatalog.Find("gaming.background-recording-off")!;
        Assert.Contains(recording.Measured, m => m.Number.Contains("-6.9%") && m.Source == "PCWorld");
        Assert.Contains(TweakCatalog.Find("gaming.capture-off")!.Measured, m => m.Number.Contains("only while recording"));
        Assert.NotEmpty(TweakCatalog.Find("network.delivery-optimization-no-upload")!.Measured);
        var plan = TweakCatalog.Find("power.high-performance-plan")!;
        Assert.Contains("25H2", plan.Measured.Single().Number);
        Assert.True(plan.IsOffered);
        foreach (var t in TweakCatalog.All.Where(t => t.IsOffered))
        foreach (var m in t.Measured)
            Assert.False(string.IsNullOrWhiteSpace(m.Hardware + m.Source + m.SourceDate), t.Id);
    }

    [Fact]
    public async Task Dual_ccd_x3d_hides_rows_that_fight_core_steering()
    {
        using var reg = new TestRegistry("v31");
        using var folder = new TestFolder("x3d", Area);
        var x3d = await Rows(reg, folder, expert: true, probe: new FixedSystemProbe { CpuKind = CpuKind.DualCcdX3D });
        var plain = await Rows(reg, folder, expert: true);
        foreach (var id in new[] { "gaming.gamedvr-policy", "power.core-parking-off", "power.high-performance-plan" })
        {
            Assert.DoesNotContain(x3d, r => r.Tweak?.Id == id);
            Assert.Contains(plain, r => r.Tweak?.Id == id);
        }
        // Nothing offered removes Game Bar or the Xbox overlay.
        Assert.DoesNotContain(TweakCatalog.All.Where(t => t.IsOffered).SelectMany(t => t.ActionableChanges).OfType<AppxRemoveChange>(),
            a => TweakSafetyRules.MatchesAny(a.PackageName, new[] { "Microsoft.XboxGamingOverlay", "Microsoft.Xbox*", "Microsoft.GamingApp" }));
    }

    // ------------------------------------------------------------------ counter and rail badge

    private static TweakState S(TweakStatus s) => new(s, Array.Empty<ChangeState>(), null);

    [Fact]
    public void Counter_counts_readable_on_off_settings_only()
    {
        var rows = new (TweakDefinition, TweakState)[]
        {
            (TweakCatalog.Find("ui.file-extensions")!, S(TweakStatus.Applied)),
            (TweakCatalog.Find("ui.hidden-files")!, S(TweakStatus.Available)),
            (TweakCatalog.Find("privacy.advertising-id")!, S(TweakStatus.Differs)),
            (TweakCatalog.Find("ui.taskbar-left")!, S(TweakStatus.Unknown)),           // couldn't read: not counted
            (TweakCatalog.Find("gaming.fps-cap")!, S(TweakStatus.Information)),       // guidance: not counted
            (TweakCatalog.Find("gaming.priority-separation")!, S(TweakStatus.Applied)), // shelf: not counted
            (TweakCatalog.Find("gaming.memory-integrity")!, S(TweakStatus.Information)), // keep it on: not counted
            (TweakCatalog.All.First(t => t.Id.StartsWith("debloat.app.", StringComparison.Ordinal)), S(TweakStatus.Available)), // Store app: not counted
        };
        var c = TweakCounter.Count(rows);
        Assert.Equal(new TweakCount(1, 3), c);
        Assert.Equal("1 of 3 on", c.Text);
        Assert.Null(TweakCounter.Count(Array.Empty<(TweakDefinition, TweakState)>()).Text);
    }

    private sealed class ThrowingRegistry : IRegistryReader
    {
        public RegistryRedirect Redirect => RegistryRedirect.None;
        public bool KeyExists(CoreHive hive, string keyPath) => throw new InvalidOperationException("unreadable");
        public RegValue? Read(CoreHive hive, string keyPath, string valueName) => throw new InvalidOperationException("unreadable");
        public IReadOnlyList<string> ValueNames(CoreHive hive, string keyPath) => throw new InvalidOperationException("unreadable");
        public IReadOnlyList<string> SubKeyNames(CoreHive hive, string keyPath) => throw new InvalidOperationException("unreadable");
    }

    [Fact]
    public async Task Rail_badge_matches_the_page_counter_and_is_empty_when_nothing_can_be_read()
    {
        using var reg = new TestRegistry("v31");
        using var folder = new TestFolder("badge", Area);
        reg.Cu(Adv, "HideFileExt", 0);
        reg.Cu(Adv, "Hidden", 1);
        var env = TestEnv.Create(reg, folder.Path);
        var outcome = await new TweaksScanner(_ => env).ScanRowsAsync(TestEnv.Context(folder.Path), null, CancellationToken.None);
        var page = TweakCounter.Count(outcome.Rows.Select(r => (r.Tweak, r.State)));
        Assert.True(page.On >= 2 && page.Total > page.On);

        INavBadgeSource module = new TweaksModule(_ => env, () => env);
        Assert.Equal(page.Text, await module.GetNavBadgeAsync(CancellationToken.None));

        // Unreadable settings: no badge (never a guess).
        var broken = TestEnv.Create(reg, folder.Path);
        var unreadable = new TweakEnvironment
        {
            Registry = new ThrowingRegistry(), Probe = broken.Probe, Boot = broken.Boot, Apps = broken.Apps, Tasks = broken.Tasks,
            HostsPath = broken.HostsPath, LocalAppData = broken.LocalAppData, WindowsDir = broken.WindowsDir, WindowsBuild = broken.WindowsBuild,
        };
        Assert.Null(await new TweaksModule(null, () => unreadable).GetNavBadgeAsync(CancellationToken.None));
    }

    // ------------------------------------------------------------------ presets: popover first, plan path unchanged

    [Fact]
    public void Preset_ticks_are_unchanged()
    {
        Assert.Equal(new[] { "gaming.game-mode", "gaming.background-recording-off", "gaming.windowed-optimizations" },
            TweakPresets.DefaultTicks(TweakPreset.SafeGaming).Select(t => t.Id).ToArray());
        Assert.Equal(new[] { "privacy.advertising-id", "privacy.tailored-experiences", "privacy.tips-suggestions", "privacy.lock-screen-fun-facts", "privacy.optional-diagnostics-off", "privacy.feedback-never", "privacy.inking-typing" },
            TweakPresets.DefaultTicks(TweakPreset.PrivacyBasic).Select(t => t.Id).ToArray());
        Assert.Equal(new[] { "privacy.web-results-in-start", "ui.file-extensions", "ui.hidden-files", "ui.open-this-pc", "ui.end-task" },
            TweakPresets.DefaultTicks(TweakPreset.CleanExplorer).Select(t => t.Id).ToArray());
    }

    [Fact]
    public void Preset_popover_shows_what_will_change_and_ticks_nothing_until_confirmed()
    {
        using var reg = new TestRegistry("v31");
        using var folder = new TestFolder("preset", Area);
        reg.Cu(Adv, "HideFileExt", 0); // already on: not in the Clean Explorer list
        using var host = PcTidyHost.CreateSandbox(Path.Combine(folder.Path, "pc"), SandboxProfile.Minimal);
        var flow = new FakeFlowHost();
        var module = new TweaksModule(ctx => TestEnv.Create(reg, folder.Path));

        Sta.Run(() =>
        {
            var vm = (TweaksViewModel)((FrameworkElement)module.CreatePage(new Services(host, flow))!).DataContext;
            Sta.Pump(vm.ScanCommand.ExecuteAsync(null));

            foreach (var choice in vm.Presets)
            {
                vm.PreviewPresetCommand.Execute(choice);
                Assert.True(vm.IsPresetPopoverOpen);
                var p = vm.PresetPreview!;
                Assert.Equal(choice.Preset, p.Preset);
                Assert.DoesNotContain(vm.AllRows, r => r.IsChecked); // the popover alone ticks nothing

                var expected = vm.AllRows.Where(r => r.Preset == choice.Preset && r.Item.DefaultSelected && r.IsSelectable).ToList();
                Assert.Equal(expected.Count, p.Count);
                Assert.Equal(expected.Take(PresetPreview.ShortListLength).Select(r => r.Title).OrderBy(x => x), p.Items.Select(i => i.Title).OrderBy(x => x));
                Assert.All(p.Items, i => Assert.Contains(i.Badge.Text, new[] { "Safe", "Check", "Permanent" }));
                Assert.Contains(expected.Count == 1 ? "Ticks 1 change" : $"Ticks {expected.Count} changes", p.CountText);
                Assert.Contains("Nothing changes until you preview and confirm", p.Notes);

                vm.CancelPresetCommand.Execute(null);
                Assert.False(vm.IsPresetPopoverOpen);
                Assert.DoesNotContain(vm.AllRows, r => r.IsChecked);

                vm.PreviewPresetCommand.Execute(choice);
                vm.ConfirmPresetCommand.Execute(null);
                Assert.Equal(expected.Select(r => r.Item.Id).OrderBy(x => x), vm.AllRows.Where(r => r.IsChecked).Select(r => r.Item.Id).OrderBy(x => x));
                vm.ClearSelectionCommand.Execute(null);
            }

            var clean = vm.Presets.Single(p => p.Preset == TweakPreset.CleanExplorer);
            vm.PreviewPresetCommand.Execute(clean);
            Assert.DoesNotContain(vm.PresetPreview!.Items, i => i.Title == "Show file extensions");
            Assert.Contains("already on", vm.PresetPreview.Notes);
            vm.ConfirmPresetCommand.Execute(null);

            // The plan path is the same as before: Preview hands one "tweaks" plan with exactly the ticked rows' actions.
            Sta.Pump(vm.PreviewCommand.ExecuteAsync(null));
            var plan = Assert.Single(flow.Reviewed);
            Assert.Equal(vm.BuildPlans().Single().Items.Count, plan.Items.Count);
            Assert.All(plan.Items, i => Assert.True(host.Safety.Check(i).Allowed, i.Description));
        });
    }

    // ------------------------------------------------------------------ page: chips, counter, view choice

    [Fact]
    public void Page_filters_by_chip_and_search_shows_the_counter_and_remembers_the_view()
    {
        using var reg = new TestRegistry("v31");
        using var folder = new TestFolder("page", Area);
        reg.Cu(Adv, "HideFileExt", 0);
        using var host = PcTidyHost.CreateSandbox(Path.Combine(folder.Path, "pc"), SandboxProfile.Minimal);
        var flow = new FakeFlowHost();
        var module = new TweaksModule(ctx => TestEnv.Create(reg, folder.Path));

        Sta.Run(() =>
        {
            var page = (FrameworkElement)module.CreatePage(new Services(host, flow))!;
            var vm = (TweaksViewModel)page.DataContext;

            // Default view: List below ~1100 px, Cards on wide windows (until the user picks).
            vm.SetWindowWidth(1024);
            Assert.False(vm.IsCardView);
            vm.SetWindowWidth(1400);
            Assert.True(vm.IsCardView);

            Sta.Pump(vm.ScanCommand.ExecuteAsync(null));
            Assert.Matches(@"^\d+ of \d+ on$", vm.CounterText);
            Assert.True(vm.HasCounter);
            Assert.Equal(TweakCounter.Count(vm.AllRows.Where(r => r.Tweak is not null).Select(r => (r.Tweak!, r.State!))).Text, vm.CounterText);

            var all = vm.Chips.Single(c => c.Chip == TweakChip.All);
            Assert.True(all.IsSelected);
            Assert.Equal(vm.AllRows.Count(r => r.Tweak is not null), all.Count);
            Assert.All(vm.Chips, c => Assert.False(string.IsNullOrWhiteSpace(c.AutomationName)));

            vm.Chips.Single(c => c.Chip == TweakChip.DoesntHelp).IsSelected = true;
            Assert.False(all.IsSelected);
            Assert.Equal(TweakChip.DoesntHelp, vm.SelectedChip);
            Assert.All(vm.Rows.OfType<TweakItemRow>(), r => Assert.True(r.IsShelf));
            Assert.Equal(TweakFilters.ShelfTitle, Assert.Single(vm.Rows.OfType<TweakHeaderRow>()).Title);

            vm.SearchText = "nagle";
            Assert.Equal("network.nagle", Assert.Single(vm.Rows.OfType<TweakItemRow>()).Tweak!.Id);
            Assert.Equal(1, vm.Chips.Single(c => c.Chip == TweakChip.All).Count);
            vm.SearchText = "zzzz-no-such-tweak";
            Assert.True(vm.ShowEmpty);
            Assert.Contains("Nothing matches", vm.EmptyText);
            vm.SearchText = "";
            all.IsSelected = true;

            vm.SortIndex = (int)TweakSort.ChangedFirst;
            var firstSectionRows = vm.Sections.First(s => s.Rows.Any(r => r.IsChanged)).Rows;
            Assert.True(firstSectionRows[0].IsChanged);

            // The finder tab keeps its own list (no chips or cards there).
            vm.Tab = TweaksViewModel.FinderTab;
            Assert.False(vm.ShowCards);
            Assert.False(vm.HasCounter);
            vm.Tab = TweaksViewModel.TweaksTab;

            // A picked view is remembered per user (in settings) and wins over the window width.
            vm.ViewChoice = 0;
            Assert.False(vm.IsCardView);
            vm.SetWindowWidth(1600);
            Assert.False(vm.IsCardView);
            Assert.Equal("List", host.Settings.GetFeature<TweaksViewModel.ViewPrefs>(TweaksViewModel.PrefsKey)!.View);
            var again = (TweaksViewModel)((FrameworkElement)module.CreatePage(new Services(host, flow))!).DataContext;
            again.SetWindowWidth(1600);
            Assert.False(again.IsCardView);

            // Readable at 1024 px in both views.
            foreach (var view in new[] { 0, 1 })
            {
                vm.ViewChoice = view;
                page.Measure(new Size(1024, 760));
                page.Arrange(new Rect(0, 0, 1024, 760));
                page.UpdateLayout();
            }
        });
    }

    private sealed class Services(PcTidyHost host, IFlowHost flow) : IServiceProvider
    {
        public object? GetService(Type t) => t == typeof(IFlowHost) ? flow : host.GetService(t);
    }
}
