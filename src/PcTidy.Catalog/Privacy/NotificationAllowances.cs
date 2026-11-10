using PcTidy.Core.Model;

namespace PcTidy.Catalog.Privacy;

/// <summary>
/// DYNAMIC allowance source for the Notification manager's per-app switches. App ids aren't known in advance
/// (every PC has different apps), so the list is built from the CURRENT subkeys of
/// <c>HKCU\…\Notifications\Settings</c>, read once (read-only) when <see cref="RegistryCatalog"/> first loads.
/// For each app it allows exactly one value: DWORD <c>Enabled</c>, data 0 or 1. Nothing else under the key,
/// no deletes (undo restores the recorded prior state through the registry handler, not through a new plan item),
/// and never the security-alert apps in <see cref="NotificationApps.ProtectedAppIds"/>.
/// An app that registers after PC Tidy started has no allowance until the next start, so the guard refuses it
/// (the scanner shows a note; nothing is written).
/// </summary>
public sealed class NotificationAllowances : IRegistryAllowanceSource
{
    public const string Source = "privacy.notifications";

    private readonly string _settingsKeyPath;

    /// <summary>The real per-app notification key (used by <see cref="RegistryCatalog"/>).</summary>
    public NotificationAllowances() : this(NotificationApps.SettingsKey) { }

    /// <param name="settingsKeyPath">HKCU key holding one subkey per app (tests: a key under Software\PCTidyTest).</param>
    public NotificationAllowances(string settingsKeyPath) => _settingsKeyPath = settingsKeyPath.Trim('\\');

    public IEnumerable<RegistryAllowance> Allowances()
        => NotificationApps.ReadAppIds(_settingsKeyPath)
            .Where(id => !NotificationApps.IsProtected(id))
            .Select(id => new RegistryAllowance(Source, RegistryHive.CurrentUser, NotificationApps.AppKey(_settingsKeyPath, id),
                NotificationApps.EnabledValue, RegistryKind.DWord, new[] { NotificationApps.Off, NotificationApps.On }, AllowDelete: false))
            .ToList();
}
