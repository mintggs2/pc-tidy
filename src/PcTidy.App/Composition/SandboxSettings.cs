using System.Reflection;
using PcTidy.Core;

namespace PcTidy.App.Composition;

/// <summary>
/// Practice mode (--sandbox) keeps its settings inside the practice folder, like a real PC keeps them in its data
/// folder. PcTidyHost.CreateSandbox always starts from fresh in-memory settings (with the welcome marked done), so
/// the shell restores what was saved there: the first start of a new practice PC shows the welcome once, later
/// starts remember every choice.
/// </summary>
public static class SandboxSettings
{
    /// <summary>
    /// Returns the settings the shell should use. Normally that is <paramref name="hostSettings"/> itself (the host's
    /// safety guard reads the same object), filled from the practice folder's settings.json. If that file can't be
    /// read, the loaded copy (with <see cref="AppSettings.LoadProblem"/> set) is returned instead, so the shell's
    /// "settings couldn't be read" path can be practised too; the host then keeps its defaults.
    /// </summary>
    public static AppSettings Restore(AppSettings hostSettings)
    {
        var dataRoot = hostSettings.DataRoot;
        var path = AppSettings.SettingsPath(dataRoot);
        if (!File.Exists(path) && !File.Exists(path + ".bak"))
        {
            hostSettings.OnboardingDone = false; // a new practice PC: show the welcome once
            return hostSettings;
        }
        var saved = AppSettings.Load(dataRoot);
        if (saved.LoadProblem is not null) return saved;
        CopyInto(saved, hostSettings);
        return hostSettings;
    }

    /// <summary>Copies every saved setting except where the data lives (the practice folder decides that).</summary>
    private static void CopyInto(AppSettings from, AppSettings to)
    {
        foreach (var p in typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.Name == nameof(AppSettings.DataRoot) || !p.CanRead || p.SetMethod is not { IsPublic: true } || p.GetIndexParameters().Length > 0) continue;
            p.SetValue(to, p.GetValue(from));
        }
    }
}
