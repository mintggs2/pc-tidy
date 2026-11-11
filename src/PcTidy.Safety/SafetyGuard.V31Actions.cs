using PcTidy.Core.Model;
using PcTidy.Safety.Rules;

namespace PcTidy.Safety;

/// <summary>Rules for the v3.1 action kinds (turning a disabled device back on).</summary>
public sealed partial class SafetyGuard
{
    internal const string DeviceOnlyOn = "PC Tidy only turns devices back on. It turns one off again only when you undo its own change from History.";
    internal const string DeviceIdBad = "This device's id isn't written the way Windows writes device ids, so PC Tidy won't change it.";
    internal const string DeviceBootCritical = "This kind of device (disk, storage controller, firmware, processor...) can affect how Windows starts. PC Tidy never changes it.";
    internal const string DeviceUnknown = "PC Tidy couldn't tell what kind of device this is, so it leaves it alone.";

    /// <summary>
    /// DeviceSetEnabled: Enable = true only. Undo never reaches the guard as an "Enable = false" item: the handler
    /// undoes the step it ran (the same item, Enable = true) from the state it recorded, and the admin helper undoes only
    /// steps in its own ledger. So any plan item asking to turn a device off is refused here. Admin and a restore point
    /// are required. The device's class, from the feature's "class" argument and read fresh from Windows, must not be one
    /// that could affect starting Windows.
    /// </summary>
    private SafetyVerdict CheckDeviceSetEnabled(PlanItem item)
    {
        if (item.Enable != true) return SafetyVerdict.Block(DeviceOnlyOn);
        // The raw value, not trimmed: a line break or other control character anywhere is a no.
        var id = item.Args is { } a && a.TryGetValue("instanceId", out var raw) ? raw : null;
        if (!IsWellFormedInstanceId(id)) return SafetyVerdict.Block(DeviceIdBad);
        if (item.Args!.Keys.Any(k => !k.Equals("instanceId", StringComparison.OrdinalIgnoreCase) && !k.Equals("class", StringComparison.OrdinalIgnoreCase)))
            return SafetyVerdict.Block(Reasons.Incomplete);
        if (!item.NeedsAdmin || !item.IsSystemChange) return SafetyVerdict.Block(Reasons.Incomplete);
        if (ProtectedDeviceClasses.IsProtected(Arg(item, "class"), null)) return SafetyVerdict.Block(DeviceBootCritical);

        var found = _deviceClasses.Find(id!);
        return found.State switch
        {
            DeviceClassState.Found when ProtectedDeviceClasses.IsProtected(found.ClassName, found.ClassGuid) => SafetyVerdict.Block(DeviceBootCritical),
            DeviceClassState.Found => SafetyVerdict.Ok,
            // A practice PC's devices exist only in its practice data; nothing real can change there.
            DeviceClassState.Missing when _locations.IsSandbox => SafetyVerdict.Ok,
            _ => SafetyVerdict.Block(DeviceUnknown),
        };
    }

    /// <summary>"ENUMERATOR\DEVICE\INSTANCE": letters, digits and \ &amp; _ . - { } # only, 2+ non-empty parts, no "." or ".." part.</summary>
    internal static bool IsWellFormedInstanceId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 400) return false;
        if (!id.All(c => char.IsAsciiLetterOrDigit(c) || c is '\\' or '&' or '_' or '.' or '-' or '{' or '}' or '#')) return false;
        var parts = id.Split('\\');
        return parts.Length >= 2 && parts.All(p => p.Length > 0 && p is not ("." or ".."));
    }
}
