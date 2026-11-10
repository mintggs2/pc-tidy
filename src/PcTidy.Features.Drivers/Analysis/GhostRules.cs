using PcTidy.Catalog.Drivers;
using PcTidy.Features.Drivers.Model;

namespace PcTidy.Features.Drivers.Analysis;

public enum GhostVerdict
{
    /// <summary>May be offered: unticked, one at a time, with the "settings are lost" warning.</summary>
    Removable,
    /// <summary>Created by Windows or an app (ROOT\, SWD\…): Windows recreates it, so removing it does nothing useful.</summary>
    SoftwareDevice,
    /// <summary>System devices, disks, volumes, snapshots, storage controllers.</summary>
    ProtectedClass,
    /// <summary>Seen in the last 30 days: probably just unplugged for now.</summary>
    SeenRecently,
    /// <summary>Windows didn't say when it was last seen, so it might be recent.</summary>
    UnknownLastSeen,
    /// <summary>Its device id has characters PC Tidy's safety rules don't accept.</summary>
    UnsafeId,
}

public sealed record GhostDecision(DeviceRecord Device, GhostVerdict Verdict);

/// <summary>
/// Ghost (disconnected) devices (Research: Ghost devices). Removal is situational: never software/ROOT devices, system
/// devices, disks, volumes or snapshots, never anything seen in the last 30 days (or of unknown age), one at a time.
/// </summary>
public static class GhostRules
{
    public static IReadOnlyList<GhostDecision> Evaluate(IEnumerable<DeviceRecord> disconnected, DateTime nowUtc)
        => disconnected.Where(d => !d.IsPresent).Select(d => new GhostDecision(d, Decide(d, nowUtc))).ToList();

    internal static GhostVerdict Decide(DeviceRecord d, DateTime nowUtc)
    {
        if (DriverClassRules.GhostNeverEnumerators.Any(p => d.InstanceId.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return GhostVerdict.SoftwareDevice;
        if (DriverClassRules.GhostNeverClasses.Contains(d.ClassName.Trim())) return GhostVerdict.ProtectedClass;
        if (!DriverClassRules.IsSafeInstanceId(d.InstanceId)) return GhostVerdict.UnsafeId;
        if (d.LastSeenUtc is not { } seen) return GhostVerdict.UnknownLastSeen;
        if (nowUtc - seen < DriverClassRules.GhostQuietPeriod) return GhostVerdict.SeenRecently;
        return GhostVerdict.Removable;
    }
}
