using PcTidy.Catalog.Drivers;
using PcTidy.Features.Drivers.Model;

namespace PcTidy.Features.Drivers.Analysis;

/// <summary>Why a driver package is (or isn't) offered for driver-store cleanup.</summary>
public enum StoreVerdict
{
    /// <summary>Older than another package with the same name, provider and class, and no device uses it.</summary>
    Old,
    /// <summary>Old, but in a class PC Tidy leaves alone unless the user picks it one by one.</summary>
    OldImportant,
    InUse,
    Newest,
    OnlyOne,
    /// <summary>Not a third-party "oemNN.inf" package (in-box drivers are never touched).</summary>
    NotThirdParty,
    /// <summary>Its version couldn't be read, so "older" can't be proven.</summary>
    UnknownVersion,
    /// <summary>Another package has exactly the same version and date: neither is "older".</summary>
    SameVersion,
}

public sealed record StoreDecision(DriverPackage Package, StoreVerdict Verdict, DriverPackage? NewerPackage)
{
    public bool IsOld => Verdict is StoreVerdict.Old or StoreVerdict.OldImportant;
}

/// <summary>
/// The RAPR rule (Research: "Driver store cleanup"): a package is old only when a NEWER version with the same original
/// name + provider + class exists and NO device uses it (present or disconnected). Never the only or the newest package,
/// only oemNN.inf, never in-box drivers. Removal itself is `pnputil /delete-driver` without /force (Windows then refuses
/// in-use packages: a second safety net), after an export. Not offered within 14 days of a driver change.
/// </summary>
public static class StoreCleanupRules
{
    public static IReadOnlyList<StoreDecision> Evaluate(IReadOnlyList<DriverPackage> packages, IEnumerable<DeviceRecord> devices)
    {
        var inUse = InUseInfs(packages, devices);
        var groups = packages.GroupBy(GroupKey, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var decisions = new List<StoreDecision>(packages.Count);
        foreach (var p in packages)
        {
            var family = groups[GroupKey(p)];
            decisions.Add(Decide(p, family, inUse));
        }
        return decisions;
    }

    /// <summary>Packages that may be offered (old ones, important classes included): the ones worth measuring.</summary>
    public static IEnumerable<DriverPackage> Candidates(IReadOnlyList<DriverPackage> packages, IEnumerable<DeviceRecord> devices)
        => Evaluate(packages, devices).Where(d => d.IsOld).Select(d => d.Package);

    private static StoreDecision Decide(DriverPackage p, List<DriverPackage> family, IReadOnlySet<string> inUse)
    {
        if (!DriverClassRules.IsOemInf(p.PublishedName)) return new(p, StoreVerdict.NotThirdParty, null);
        if (inUse.Contains(p.PublishedName) || p.Devices.Count > 0) return new(p, StoreVerdict.InUse, null);
        if (family.Count == 1) return new(p, StoreVerdict.OnlyOne, null);
        if (p.Version.Version is null) return new(p, StoreVerdict.UnknownVersion, null);

        var newer = family
            .Where(o => !ReferenceEquals(o, p) && o.Version.Version is not null && Compare(o.Version, p.Version) > 0)
            .OrderByDescending(o => o.Version.Version).ThenByDescending(o => o.Version.Date)
            .FirstOrDefault();
        if (newer is null)
        {
            bool tie = family.Any(o => !ReferenceEquals(o, p) && o.Version.Version is not null && Compare(o.Version, p.Version) == 0);
            return new(p, tie ? StoreVerdict.SameVersion : StoreVerdict.Newest, null);
        }
        return new(p, DriverClassRules.IsImportantStoreClass(p.ClassName) ? StoreVerdict.OldImportant : StoreVerdict.Old, newer);
    }

    /// <summary>
    /// By version only. The INF date comes from pnputil's text, which may be in the regional date format, so it never
    /// decides which package is older: same version = same age (neither is offered).
    /// </summary>
    internal static int Compare(DriverVersionInfo a, DriverVersionInfo b)
        => (a.Version ?? new Version()).CompareTo(b.Version ?? new Version());

    /// <summary>"Same package family": original INF name + provider + class (exact text, case-insensitive).</summary>
    internal static string GroupKey(DriverPackage p)
        => string.Join('|', p.OriginalName.Trim(), p.Provider.Trim(), p.ClassGuid.Length > 0 ? p.ClassGuid.Trim() : p.ClassName.Trim());

    /// <summary>Every package a device (present or disconnected) uses as its driver or extension driver.</summary>
    internal static IReadOnlySet<string> InUseInfs(IEnumerable<DriverPackage> packages, IEnumerable<DeviceRecord> devices)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in packages)
            if (p.Devices.Count > 0) set.Add(p.PublishedName);
        foreach (var d in devices)
        {
            if (d.DriverInf.Length > 0) set.Add(d.DriverInf);
            foreach (var e in d.ExtensionInfs) set.Add(e);
            foreach (var m in d.MatchingDrivers)
                if (m.IsInstalled) set.Add(m.PublishedName);
        }
        return set;
    }

    /// <summary>
    /// Cleanup is held back for 14 days after any driver change (the old package is the rollback copy).
    /// An unknown last-change time also holds it back: PC Tidy can't prove it's been long enough.
    /// </summary>
    public static bool IsInQuietPeriod(DateTime? lastDriverChangeUtc, DateTime nowUtc, out DateTime? availableFromUtc)
    {
        if (lastDriverChangeUtc is not { } last) { availableFromUtc = null; return true; }
        availableFromUtc = last + DriverClassRules.StoreCleanupQuietPeriod;
        return nowUtc < availableFromUtc;
    }
}
