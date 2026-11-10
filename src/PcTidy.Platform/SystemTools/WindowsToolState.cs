using Microsoft.Win32;
using PcTidy.Core.Model;

namespace PcTidy.Platform;

/// <summary>Hibernation state from the registry (language-neutral, readable without admin).</summary>
public sealed record HibernationState(bool Enabled, int? HiberFileType);

/// <summary>Registry facts about Windows tools, read before and after they run (never their text output).</summary>
internal static class WindowsToolState
{
    private const string PowerKey = @"SYSTEM\CurrentControlSet\Control\Power";
    internal const string VolumeCachesKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches";
    internal static string SageValueName => $"StateFlags{SystemToolCatalog.CleanmgrSageSet:D4}";

    public static HibernationState? ReadHibernation()
    {
        try
        {
            using var lm = RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = lm.OpenSubKey(PowerKey);
            if (key?.GetValue("HibernateEnabled") is not int enabled) return null;
            // HiberFileType: 1 = reduced, 2 = full (absent on PCs that never changed it).
            return new HibernationState(enabled != 0, key.GetValue("HiberFileType") is int t ? t : null);
        }
        catch
        {
            return null;
        }
    }

    public static string? HiberTypeName(int? raw) => raw switch { 1 => "reduced", 2 => "full", _ => null };

    public static bool CleanmgrHandlerExists(string handler)
    {
        using var lm = RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = lm.OpenSubKey($@"{VolumeCachesKey}\{handler}");
        return key is not null;
    }

    /// <summary>
    /// Makes our sageset slot select exactly one handler: records every existing StateFlags value in that slot,
    /// clears the others and sets ours to 2 ("run"). Returns the prior states so <see cref="RestoreCleanmgrFlags"/> can put them back.
    /// </summary>
    public static Dictionary<string, RegistryPriorState> PrepareCleanmgrFlags(string handler)
    {
        var priors = new Dictionary<string, RegistryPriorState>(StringComparer.OrdinalIgnoreCase);
        using (var lm = RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, RegistryView.Registry64))
        using (var caches = lm.OpenSubKey(VolumeCachesKey))
        {
            foreach (var name in caches?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                var path = $@"{VolumeCachesKey}\{name}";
                var prior = RegistryAccess.Capture(Core.Model.RegistryHive.LocalMachine, true, path, SageValueName);
                if (prior.ValueExisted || name.Equals(handler, StringComparison.OrdinalIgnoreCase)) priors[name] = prior;
            }
        }
        foreach (var name in priors.Keys)
        {
            var path = $@"{VolumeCachesKey}\{name}";
            if (name.Equals(handler, StringComparison.OrdinalIgnoreCase))
                RegistryAccess.SetValue(Core.Model.RegistryHive.LocalMachine, true, path, SageValueName, RegistryKind.DWord, "2");
            else
                RegistryAccess.DeleteValue(Core.Model.RegistryHive.LocalMachine, true, path, SageValueName);
        }
        return priors;
    }

    public static void RestoreCleanmgrFlags(Dictionary<string, RegistryPriorState> priors)
    {
        foreach (var (name, prior) in priors)
        {
            try
            {
                RegistryAccess.Restore(Core.Model.RegistryHive.LocalMachine, true, $@"{VolumeCachesKey}\{name}", SageValueName, prior, null);
            }
            catch
            {
                // Best effort: a leftover StateFlags value only affects this sageset slot.
            }
        }
    }
}
