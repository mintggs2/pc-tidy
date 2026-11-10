using System.Globalization;

namespace PcTidy.Catalog.Tweaks;

/// <summary>What one Defender policy value does to protection.</summary>
public enum DefenderPolicyEffect
{
    /// <summary>Switches a protection off or excludes something from scans: the finder may offer to remove it.</summary>
    Weakens,
    /// <summary>Adds protection (ASR rules, Controlled Folder Access, network protection, PUA blocking, update settings...): always kept.</summary>
    Protects,
    /// <summary>Neither clearly (schedules, CPU limits, an organisation's own choices): kept, shown as information.</summary>
    Other,
}

/// <summary>
/// Sorts the values under <c>HKLM\SOFTWARE\Policies\Microsoft\Windows Defender</c> into those that weaken Defender and
/// everything else (DEF-1). Matched by key, value name AND data, so "DisableRealtimeMonitoring = 0" (protection kept on)
/// or "SpynetReporting = 2" (cloud protection on) are never offered for removal. Names and data follow Microsoft's
/// Defender Policy CSP group policy mappings (learn.microsoft.com/windows/client-management/mdm/policy-csp-defender).
/// Anything not listed here is kept: only values known to switch protection off are ever offered.
/// </summary>
public static class DefenderPolicyEffects
{
    private sealed record Rule(string Key, string Name, long[] WeakData, string What);

    /// <summary>Value → the data that switches protection off, and what it does then.</summary>
    private static readonly Rule[] SwitchOffs =
    {
        new("", "DisableAntiSpyware", new[] { 1L }, "turns Microsoft Defender Antivirus off"),
        new("", "DisableAntiVirus", new[] { 1L }, "turns Microsoft Defender Antivirus off"),
        new("", "DisableRoutinelyTakingAction", new[] { 1L }, "stops Defender acting on threats it finds"),
        new("", "ServiceKeepAlive", new[] { 0L }, "lets Defender's service be stopped"),
        new("Real-Time Protection", "DisableRealtimeMonitoring", new[] { 1L }, "turns real-time protection off"),
        new("Real-Time Protection", "DisableBehaviorMonitoring", new[] { 1L }, "turns behaviour monitoring off"),
        new("Real-Time Protection", "DisableOnAccessProtection", new[] { 1L }, "stops files being checked when they're opened"),
        new("Real-Time Protection", "DisableIOAVProtection", new[] { 1L }, "stops downloads and attachments being checked"),
        new("Real-Time Protection", "DisableScanOnRealtimeEnable", new[] { 1L }, "turns off the process scan when real-time protection starts"),
        new("Real-Time Protection", "DisableScriptScanning", new[] { 1L }, "turns script scanning off"),
        new("Real-Time Protection", "DisableRawWriteNotification", new[] { 1L }, "turns raw-write notification off"),
        new("Real-Time Protection", "DisableIntrusionPreventionSystem", new[] { 1L }, "turns network intrusion prevention off"),
        new("Real-Time Protection", "DisableInformationProtectionControl", new[] { 1L }, "turns information protection control off"),
        new("Real-Time Protection", "RealtimeScanDirection", new[] { 1L, 2L }, "checks files in one direction only"),
        new("Spynet", "SpynetReporting", new[] { 0L }, "turns cloud-delivered protection (Microsoft MAPS) off"),
        new("Spynet", "SubmitSamplesConsent", new[] { 2L }, "never sends suspicious files for a closer look"),
        new("Spynet", "DisableBlockAtFirstSeen", new[] { 1L }, "turns \"block at first sight\" off"),
        new("Scan", "DisableArchiveScanning", new[] { 1L }, "stops archives (zip files) being scanned"),
        new("Scan", "DisableHeuristics", new[] { 1L }, "turns heuristic detection off"),
        new("Scan", "DisablePackedExeScanning", new[] { 1L }, "stops packed programs being scanned"),
        new("UX Configuration", "Notification_Suppress", new[] { 1L }, "hides Defender's warnings"),
    };

    /// <summary>Lists of things Defender skips when scanning.</summary>
    private static readonly string[] ExclusionLists = { @"Exclusions\Paths", @"Exclusions\Extensions", @"Exclusions\Processes", @"Exclusions\IpAddresses" };

    /// <summary>Remediation actions that let a detected threat stay ("Ignore/Allow" 6, "No action" 9, "None" 11).</summary>
    private static readonly long[] NonRemediating = { 6L, 9L, 11L };

    /// <summary>Subkeys whose policies only ever add protection (or keep it up to date).</summary>
    private static readonly string[] ProtectiveKeys = { "Windows Defender Exploit Guard", "Signature Updates", "MpEngine", "NIS" };

    /// <param name="relativeKey">Key below the Defender policy key ("" for the key itself), e.g. <c>Real-Time Protection</c>.</param>
    /// <param name="data">The value's data in the invariant form the registry reader uses (DWORDs as decimal), or null.</param>
    public static (DefenderPolicyEffect Effect, string What) Classify(string relativeKey, string valueName, string? data)
    {
        var key = relativeKey.Trim('\\');
        bool KeyIs(string k) => string.Equals(key, k, StringComparison.OrdinalIgnoreCase);
        bool KeyUnder(string k) => KeyIs(k) || key.StartsWith(k + "\\", StringComparison.OrdinalIgnoreCase);
        var number = Number(data);

        if (ProtectiveKeys.Any(KeyUnder))
            return (DefenderPolicyEffect.Protects, KeyUnder("Windows Defender Exploit Guard")
                ? "attack surface reduction, Controlled Folder Access or network protection"
                : KeyUnder("Signature Updates") ? "how Defender keeps its security intelligence up to date" : "extra cloud and engine protection");
        if (KeyIs("") && valueName.Equals("PUAProtection", StringComparison.OrdinalIgnoreCase))
            return (DefenderPolicyEffect.Protects, "blocks potentially unwanted apps");
        if (KeyIs("") && valueName.Equals("DisableLocalAdminMerge", StringComparison.OrdinalIgnoreCase))
            return (DefenderPolicyEffect.Protects, "stops local exclusions being added to the policy's");

        if (ExclusionLists.Any(KeyIs))
            return (DefenderPolicyEffect.Weakens, $"makes Defender skip this when scanning ({key[(key.IndexOf('\\') + 1)..].ToLowerInvariant()} exclusion)");
        if (KeyIs("Exclusions") && valueName.StartsWith("Exclusions_", StringComparison.OrdinalIgnoreCase) && number == 1)
            return (DefenderPolicyEffect.Weakens, "switches the exclusion list on");
        if ((KeyIs(@"Threats\ThreatSeverityDefaultAction") || KeyIs(@"Threats\ThreatIdDefaultAction")) && number is { } action)
            return NonRemediating.Contains(action)
                ? (DefenderPolicyEffect.Weakens, "lets threats through without removing them")
                : (DefenderPolicyEffect.Protects, "quarantines or removes these threats");

        foreach (var r in SwitchOffs)
        {
            if (!KeyIs(r.Key) || !valueName.Equals(r.Name, StringComparison.OrdinalIgnoreCase)) continue;
            return number is { } n && r.WeakData.Contains(n)
                ? (DefenderPolicyEffect.Weakens, r.What)
                : (DefenderPolicyEffect.Protects, "keeps this protection on");
        }
        return (DefenderPolicyEffect.Other, "a Defender setting chosen by you or your organisation");
    }

    private static long? Number(string? data)
    {
        if (data is null) return null;
        var t = data.Trim();
        if (long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return n;
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && long.TryParse(t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h)) return h;
        return null;
    }
}
