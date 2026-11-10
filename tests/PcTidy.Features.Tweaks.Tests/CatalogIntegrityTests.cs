using PcTidy.Catalog;
using PcTidy.Catalog.Tweaks;
using PcTidy.Core.Model;
using PcTidy.Safety.Rules;
using PcTidy.UI.Flow;
using Xunit;

namespace PcTidy.Features.Tweaks.Tests;

/// <summary>The tweak and bad-tweak catalogs are the spec: every row must be complete, sourced, undoable and allowed.</summary>
public class CatalogIntegrityTests
{
    private static IEnumerable<TweakDefinition> Offered => TweakCatalog.All.Where(t => t.IsOffered);

    [Fact]
    public void Ids_are_unique_and_start_with_their_group()
    {
        Assert.Equal(TweakCatalog.All.Count, TweakCatalog.All.Select(t => t.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(BadTweakCatalog.All.Count, BadTweakCatalog.All.Select(c => c.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var t in TweakCatalog.All) Assert.Equal(TweakCatalog.GroupFromId(t.Id), t.Group);
        Assert.True(TweakCatalog.All.Count >= 60, "every tweak from the research is in the catalog");
        Assert.True(BadTweakCatalog.All.Sum(c => c.Signals.Count) >= 120, "the finder covers the ~120 community checks");
    }

    [Fact]
    public void Every_non_harmful_tweak_has_plain_text_a_source_and_an_undo()
    {
        foreach (var t in TweakCatalog.Shown)
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Title), t.Id);
            Assert.False(string.IsNullOrWhiteSpace(t.Effect), t.Id);
            Assert.False(string.IsNullOrWhiteSpace(t.Undo), t.Id);
            Assert.NotEmpty(t.Sources);
            foreach (var s in t.Sources)
                Assert.True(Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps, $"{t.Id}: bad source {s}");
            Assert.NotEmpty(t.Changes);
            if (t.Verdict == TweakVerdict.Placebo) Assert.False(string.IsNullOrWhiteSpace(t.Caveat), t.Id + " must say why it doesn't help");
        }
    }

    [Fact]
    public void Every_offered_registry_change_has_an_exact_allowance()
    {
        foreach (var t in Offered)
        {
            foreach (var c in t.ActionableChanges)
            {
                switch (c)
                {
                    case RegistryValueChange r:
                        var a = RegistryCatalog.Find(r.Hive, r.KeyPath, r.ValueName);
                        Assert.True(a is not null, $"{t.Id}: no allowance for {r.Label}");
                        Assert.Equal(r.Kind, a!.Kind);
                        Assert.Contains(r.Data, a.AllowedData);
                        if (r.WindowsDefault is { } d) Assert.Contains(d, a.AllowedData);
                        Assert.Equal(PcTidy.Features.Tweaks.Planning.TweakPlanItems.NeedsAdmin(r.Hive, r.KeyPath), a.NeedsAdmin);
                        break;
                    case RegistryTokenChange tok:
                        var ta = RegistryCatalog.Find(tok.Hive, tok.KeyPath, tok.ValueName);
                        Assert.True(ta is not null, $"{t.Id}: no allowance for {tok.Label}");
                        Assert.Equal(RegistryKind.String, ta!.Kind);
                        break;
                }
            }
        }
    }

    [Fact]
    public void Every_finder_registry_reset_has_an_exact_allowance_and_delete_only_entries_are_marked()
    {
        foreach (var check in BadTweakCatalog.All)
        {
            foreach (var signal in check.Signals)
            {
                foreach (var fix in signal.Fixes)
                {
                    if (fix is RegistrySetFix set && signal.Probe is RegistryProbe p)
                    {
                        var a = RegistryCatalog.Find(p.Hive, p.KeyPath, p.ValueName);
                        Assert.True(a is not null, $"{check.Id}: no allowance for {p.Label}");
                        Assert.Contains(set.Data, a!.AllowedData);
                    }
                    if (fix is RegistryDeleteFix del)
                    {
                        var hive = del.Hive ?? (signal.Probe as RegistryProbe)?.Hive;
                        var key = del.KeyPath ?? (signal.Probe as RegistryProbe)?.KeyPath;
                        var value = del.ValueName ?? (signal.Probe as RegistryProbe)?.ValueName;
                        Assert.True(hive is not null && key is not null && value is not null, check.Id);
                        var a = RegistryCatalog.Find(hive!.Value, key!, value);
                        Assert.True(a is not null && a.AllowDelete, $"{check.Id}: no delete allowance for {key}\\{value}");
                    }
                }
            }
        }
        Assert.NotEmpty(TweakRegistryAllowances.DeleteOnly);
        // A delete-only entry never lists data to write.
        var mine = new TweakRegistryAllowances().Allowances().ToList();
        foreach (var a in mine.Where(a => TweakRegistryAllowances.DeleteOnly.Contains(TweakRegistryAllowances.KeyOf(a.Hive, a.KeyPath, a.ValueName))))
            Assert.Empty(a.AllowedData);
    }

    [Fact]
    public void Registry_allowances_are_unique_and_point_only_at_listed_values()
    {
        var mine = new TweakRegistryAllowances().Allowances().ToList();
        Assert.NotEmpty(mine);
        var dupes = mine.GroupBy(a => TweakRegistryAllowances.KeyOf(a.Hive, a.KeyPath, a.ValueName)).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.Empty(dupes);
        // ValueName "" is a key's (Default) value; null would mean the whole key, which tweaks never delete.
        Assert.All(mine, a => Assert.True(a.ValueName is not null, "tweaks never delete or create whole keys by allowance"));
        // Only the windowed-games settings string may take any text (it's merged with the user's own tokens).
        var anyData = mine.Where(a => a.AllowedData.Count == 0 && !TweakRegistryAllowances.DeleteOnly.Contains(TweakRegistryAllowances.KeyOf(a.Hive, a.KeyPath, a.ValueName))).ToList();
        Assert.All(anyData, a => Assert.Equal("DirectXUserGlobalSettings", a.ValueName));
        // Every allowance is visible to the SafetyGuard's aggregate.
        Assert.All(mine, a => Assert.NotNull(RegistryCatalog.Find(a.Hive, a.KeyPath, a.ValueName)));
    }

    [Fact]
    public void No_offered_tweak_touches_security_anti_cheat_or_update_services()
    {
        foreach (var t in Offered)
        {
            foreach (var c in t.Changes)
            {
                Assert.True(TweakSafetyRules.WhyForbidden(c) is null, $"{t.Id}: {TweakSafetyRules.WhyForbidden(c)}");
                if (c is ServiceStartChange s)
                {
                    Assert.False(SafetyPolicy.MatchesAny(s.ServiceName, SafetyPolicy.Default.NeverTouchServices), $"{t.Id} touches {s.ServiceName}");
                    Assert.NotEqual(ServiceStart.Disabled, s.Target);
                }
                if (c is AppxRemoveChange a) Assert.False(SafetyPolicy.MatchesAny(a.PackageName, SafetyPolicy.Default.CriticalStorePackages), $"{t.Id} removes {a.PackageName}");
            }
        }
        // Nothing offered writes the Defender, Windows Update, SmartScreen, UAC, firewall or mitigation values.
        string[] forbidden = { "DisableAntiSpyware", "DisableAntiVirus", "NoAutoUpdate", "EnableSmartScreen", "EnableLUA", "EnableFirewall", "FeatureSettingsOverride", "VulnerableDriverBlocklistEnable", "HVCI" };
        var written = Offered.SelectMany(t => t.ActionableChanges).OfType<RegistryValueChange>().Select(r => r.ValueName).ToList();
        Assert.DoesNotContain(written, w => forbidden.Contains(w, StringComparer.OrdinalIgnoreCase));
        // Game Bar / Xbox are never removed (X3D steering), and the guard's own list agrees with the catalog's.
        Assert.DoesNotContain(TweakCatalog.ConsumerApps, a => a.Package.Contains("Xbox", StringComparison.OrdinalIgnoreCase) || a.Package.Contains("Gaming", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Harmful_rows_are_data_only_and_placebo_rows_explain_why()
    {
        Assert.Contains(TweakCatalog.All, t => t.Verdict == TweakVerdict.Harmful);
        Assert.DoesNotContain(TweakCatalog.Shown, t => t.Verdict == TweakVerdict.Harmful);
        foreach (var p in TweakCatalog.All.Where(t => t.Verdict == TweakVerdict.Placebo)) Assert.False(p.IsOffered);
        // The research's named placebo rows are present (Plan §8: Nagle, NetworkThrottlingIndex, Win32PrioritySeparation, timers, Ultimate Performance, Chat, Recall).
        foreach (var id in new[] { "network.nagle", "gaming.network-throttling", "gaming.priority-separation", "gaming.timer-resolution", "power.ultimate-performance", "ui.chat-button", "privacy.recall" })
            Assert.Equal(TweakVerdict.Placebo, TweakCatalog.Find(id)!.Verdict);
    }

    [Fact]
    public void Presets_tick_only_proven_safe_rows_and_match_the_optimize_all_group_names()
    {
        foreach (var preset in new[] { TweakPreset.SafeGaming, TweakPreset.PrivacyBasic, TweakPreset.CleanExplorer })
        {
            var ticks = TweakPresets.DefaultTicks(preset).ToList();
            Assert.NotEmpty(ticks);
            Assert.All(ticks, t => Assert.True(t.Verdict == TweakVerdict.Proven && t.Reversible && !t.IsExpertOnly && t.Risk == TweakRisk.Low, t.Id));
        }
        Assert.True(OptimizePresets.IncludesGroup(OptimizePresets.Gamer, "tweaks", "tweaks.safe-gaming"));
        Assert.True(OptimizePresets.IncludesGroup(OptimizePresets.Recommended, "tweaks", "tweaks.clean-explorer"));
        Assert.True(OptimizePresets.IncludesGroup(OptimizePresets.Privacy, "tweaks", "tweaks.privacy-basic"));
        Assert.False(OptimizePresets.IncludesGroup(OptimizePresets.Privacy, "tweaks", "tweaks.gaming"));
        // Expert rows are never in a preset.
        Assert.DoesNotContain(TweakCatalog.All, t => t.IsExpertOnly && t.Preset != TweakPreset.None);
    }

    [Fact]
    public void Finder_checks_are_complete_and_boot_resets_carry_the_bitlocker_rule()
    {
        foreach (var c in BadTweakCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(c.WhyBad), c.Id);
            Assert.False(string.IsNullOrWhiteSpace(c.WindowsDefault), c.Id);
            Assert.NotEmpty(c.Sources);
            Assert.NotEmpty(c.Signals);
            Assert.All(c.Signals, s => Assert.NotEmpty(s.Fixes));
            var bootNames = c.Signals.Select(s => s.Probe).OfType<BootValueProbe>().Select(b => b.Name).ToList();
            var watched = bootNames.Any(n => BadTweakCatalog.BitLockerWatchedBootValues.Contains(n));
            Assert.Equal(watched, c.Flags.HasFlag(BadTweakFlags.BitLocker));
        }
        // The research's headline leftovers are all covered.
        foreach (var id in new[] { "security.defender-off", "security.uac-off", "security.firewall-off", "security.smartscreen-off", "update.automatic-updates-off",
                     "boot.useplatformclock", "network.autotuning-off", "storage.pagefile-off", "storage.large-system-cache", "network.ipv6-disabled",
                     "network.hosts-blocks-microsoft", "services.broken", "security.test-signing", "scheduler.system-responsiveness" })
            Assert.NotNull(BadTweakCatalog.Find(id));
    }
}
