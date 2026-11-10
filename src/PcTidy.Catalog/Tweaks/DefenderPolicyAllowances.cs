using Microsoft.Win32;
using PcTidy.Core.Model;
using CoreHive = PcTidy.Core.Model.RegistryHive;
using CoreKind = PcTidy.Core.Model.RegistryKind;

namespace PcTidy.Catalog.Tweaks;

/// <summary>
/// DYNAMIC, DELETE-ONLY allowance source for Microsoft Defender policies that WEAKEN it. A value under
/// <c>HKLM\SOFTWARE\Policies\Microsoft\Windows Defender</c> (and its subkeys) that <see cref="DefenderPolicyEffects"/>
/// classes as switching protection off (exclusions, Disable* switches set to on, cloud protection off...) may be deleted,
/// which puts Windows' own default back. Policies that ADD protection (ASR rules, Controlled Folder Access, network
/// protection, PUA blocking, update settings) and anything not known get no allowance, so the guard refuses removing
/// them (DEF-1). Exclusion names can't be listed in advance, so the list is read once, read-only, when
/// <see cref="RegistryCatalog"/> first loads.
/// Nothing may ever be WRITTEN here: AllowedData is empty and only deletion is allowed. A value that appears after
/// PC Tidy started has no allowance until the next start, so the guard refuses it and nothing changes.
/// </summary>
public sealed class DefenderPolicyAllowances : IRegistryAllowanceSource
{
    public const string Source = "tweaks.badtweaks.security.defender-off";

    /// <summary>The policy key (64-bit view). Same constant the finder's catalog checks use.</summary>
    public const string PolicyKey = BadTweakCatalog.Defender;

    /// <summary>Limits so a huge or looping key can't slow the app down at start.</summary>
    public const int MaxDepth = 4;
    public const int MaxValues = 500;

    private readonly Microsoft.Win32.RegistryHive _physicalHive;
    private readonly string _physicalKey;

    /// <summary>The real policy key (used by <see cref="RegistryCatalog"/>).</summary>
    public DefenderPolicyAllowances() : this(Microsoft.Win32.RegistryHive.LocalMachine, PolicyKey) { }

    /// <param name="physicalHive">Where to read (tests: HKCU).</param>
    /// <param name="physicalKey">Key to read (tests: a key under Software\PCTidyTest). Allowances are always issued for the real HKLM policy path.</param>
    public DefenderPolicyAllowances(Microsoft.Win32.RegistryHive physicalHive, string physicalKey)
    {
        _physicalHive = physicalHive;
        _physicalKey = physicalKey.Trim('\\');
    }

    public IEnumerable<RegistryAllowance> Allowances()
    {
        var list = new List<RegistryAllowance>();
        try
        {
            using var root = RegistryKey.OpenBaseKey(_physicalHive, RegistryView.Registry64);
            using var key = root.OpenSubKey(_physicalKey, writable: false);
            if (key is not null) Walk(key, "", 0, list);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Unreadable: no leftover allowances (the finder then shows the values as information only).
        }
        return list;
    }

    /// <summary>Values the static finder allowances already cover (one allowance per value, never two).</summary>
    private static readonly Lazy<IReadOnlySet<string>> CoveredByCatalog = new(() => new TweakRegistryAllowances().Allowances()
        .Select(a => TweakRegistryAllowances.KeyOf(a.Hive, a.KeyPath, a.ValueName)).ToHashSet(StringComparer.OrdinalIgnoreCase));

    private static void Walk(RegistryKey key, string relative, int depth, List<RegistryAllowance> list)
    {
        var keyPath = relative.Length == 0 ? PolicyKey : PolicyKey + "\\" + relative;
        foreach (var name in Safe(key.GetValueNames))
        {
            if (list.Count >= MaxValues) return;
            if (name.Length == 0) continue; // the key's (Default) value is never a Defender policy
            if (CoveredByCatalog.Value.Contains(TweakRegistryAllowances.KeyOf(CoreHive.LocalMachine, keyPath, name))) continue;
            // Only a policy that switches protection off may be removed; one that adds protection never gets an allowance (DEF-1).
            if (DefenderPolicyEffects.Classify(relative, name, DataOf(key, name)).Effect != DefenderPolicyEffect.Weakens) continue;
            list.Add(new RegistryAllowance(Source, CoreHive.LocalMachine, keyPath, name, KindOf(key, name), Array.Empty<string>(),
                AllowDelete: true, NeedsAdmin: true));
        }
        if (depth >= MaxDepth) return;
        foreach (var sub in Safe(key.GetSubKeyNames))
        {
            try
            {
                using var child = key.OpenSubKey(sub, writable: false);
                if (child is not null) Walk(child, relative.Length == 0 ? sub : relative + "\\" + sub, depth + 1, list);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // Skip one unreadable subkey.
            }
        }
    }

    private static CoreKind KindOf(RegistryKey key, string name)
    {
        try
        {
            return key.GetValueKind(name) switch
            {
                RegistryValueKind.String => CoreKind.String,
                RegistryValueKind.ExpandString => CoreKind.ExpandString,
                RegistryValueKind.MultiString => CoreKind.MultiString,
                RegistryValueKind.QWord => CoreKind.QWord,
                RegistryValueKind.Binary => CoreKind.Binary,
                _ => CoreKind.DWord,
            };
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return CoreKind.DWord;
        }
    }

    /// <summary>The value's data as the finder's reader shows it (DWORDs as unsigned decimal), or null when unreadable.</summary>
    private static string? DataOf(RegistryKey key, string name)
    {
        try
        {
            return key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) switch
            {
                int i => unchecked((uint)i).ToString(System.Globalization.CultureInfo.InvariantCulture),
                long l => unchecked((ulong)l).ToString(System.Globalization.CultureInfo.InvariantCulture),
                string s => s,
                _ => null,
            };
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static string[] Safe(Func<string[]> read)
    {
        try { return read(); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return Array.Empty<string>(); }
    }
}
