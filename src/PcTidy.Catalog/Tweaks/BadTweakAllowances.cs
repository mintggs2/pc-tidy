using PcTidy.Core.Model;

namespace PcTidy.Catalog.Tweaks;

/// <summary>
/// PATTERNED allowances for bad-tweak resets whose key can't be listed exactly (one network adapter's class subkey, one
/// program's Image File Execution Options key). Each is the narrowest anchored pattern that names the one value, and
/// each only allows putting Windows' default back:
///  - <c>*JumboPacket</c> under a network-adapter class subkey may only be SET to "1514" (jumbo packets off);
///  - <c>PerfOptions\CpuPriorityClass</c> under an IFEO program key may only be DELETED (no forced priority).
/// HKLM, so the guard also wants NeedsAdmin and IsSystemChange on the item. Undo restores the recorded prior value.
/// </summary>
public sealed class BadTweakAllowances : IRegistryAllowanceSource
{
    /// <summary>Network adapters' device class (GUID_DEVCLASS_NET).</summary>
    public const string NetClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";
    public const string JumboValue = "*JumboPacket";
    /// <summary>Jumbo packets off: the standard Ethernet frame (1500 bytes of data plus the 14-byte header).</summary>
    public const string JumboDefault = "1514";
    /// <summary>One adapter's subkey: four ASCII digits ([0-9], not \d, which also matches other scripts' digits).</summary>
    public const string JumboKeyPattern = @"^SYSTEM\\CurrentControlSet\\Control\\Class\\\{4d36e972-e325-11ce-bfc1-08002be10318\}\\[0-9]{4}$";

    public const string IfeoKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    public const string PriorityValue = "CpuPriorityClass";

    /// <summary>
    /// The only CpuPriorityClass data the finder removes: 4, Realtime (verify: Microsoft doesn't document the numbers;
    /// 1 Idle, 2 Normal, 3 High, 5 Below normal, 6 Above normal per community references). Other priorities are a user's
    /// own choice and are left alone.
    /// <para>
    /// The allowance below can't say "delete only when the data is 4": AllowedData lists data that may be WRITTEN, so
    /// putting "4" there would allow setting Realtime. The finder therefore checks the data twice (at scan time and again
    /// when it builds the step, <c>FoundValue.OnlyIfData</c>), and the allowance's <c>OnlyWhenDataIs</c> makes the
    /// registry handler check it a third time right before the delete (safety review L2).
    /// </para>
    /// </summary>
    public const string PriorityRealtime = "4";
    public const string PriorityKeyPattern = @"^SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Image File Execution Options\\[^\\]{1,260}\\PerfOptions$";

    public IEnumerable<RegistryAllowance> Allowances() => new[]
    {
        new RegistryAllowance(TweakRegistryAllowances.FinderSourcePrefix + "network.jumbo-frames", RegistryHive.LocalMachine,
            NetClassKey + @"\0000", JumboValue, RegistryKind.String, new[] { JumboDefault }, AllowDelete: false, NeedsAdmin: true)
        {
            KeyPathPattern = JumboKeyPattern,
        },
        new RegistryAllowance(TweakRegistryAllowances.FinderSourcePrefix + "scheduler.realtime-priority", RegistryHive.LocalMachine,
            IfeoKey + @"\example.exe\PerfOptions", PriorityValue, RegistryKind.DWord, Array.Empty<string>(), AllowDelete: true, NeedsAdmin: true)
        {
            KeyPathPattern = PriorityKeyPattern,
            // Checked by the registry handler right before the delete: only a Realtime value is ever removed.
            OnlyWhenDataIs = new[] { PriorityRealtime },
        },
    };
}
