using PcTidy.Catalog.Tweaks;
using PcTidy.Core.Model;

namespace PcTidy.Catalog.Privacy;

/// <summary>
/// The fixed registry values the Notification manager's tips/ads rows may write (HKCU DWORDs, 0 = off, 1 = on).
/// A value the Tweaks catalog already allows is NOT listed again: the guard uses the first matching allowance, so a
/// second entry could only ever disagree. <see cref="TipsCatalog"/> writes the same data Tweaks allows (0 and 1),
/// which the Privacy tests check. No deletes: undo restores the exact prior state through the registry handler.
/// The per-app notification switches are dynamic and come from <see cref="NotificationAllowances"/>.
/// </summary>
public sealed class PrivacyRegistryAllowances : IRegistryAllowanceSource
{
    public const string Source = "privacy.tips";

    public IEnumerable<RegistryAllowance> Allowances()
    {
        var tweaks = new TweakRegistryAllowances().Allowances()
            .Select(a => TweakRegistryAllowances.KeyOf(a.Hive, a.KeyPath, a.ValueName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return TipsCatalog.AllValues
            .Where(v => !tweaks.Contains(TweakRegistryAllowances.KeyOf(RegistryHive.CurrentUser, v.KeyPath, v.ValueName)))
            .Select(v => new RegistryAllowance(Source, RegistryHive.CurrentUser, v.KeyPath, v.ValueName, RegistryKind.DWord,
                new[] { TipsCatalog.Off, TipsCatalog.On }, AllowDelete: false))
            .ToList();
    }

    /// <summary>The tips values whose allowance comes from the Tweaks catalog (shared data).</summary>
    public static IReadOnlyList<TipsValue> SharedWithTweaks()
    {
        var tweaks = new TweakRegistryAllowances().Allowances()
            .Select(a => TweakRegistryAllowances.KeyOf(a.Hive, a.KeyPath, a.ValueName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return TipsCatalog.AllValues
            .Where(v => tweaks.Contains(TweakRegistryAllowances.KeyOf(RegistryHive.CurrentUser, v.KeyPath, v.ValueName)))
            .ToList();
    }
}
