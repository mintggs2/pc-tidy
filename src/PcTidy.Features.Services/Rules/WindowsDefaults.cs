using PcTidy.Safety.Rules;

namespace PcTidy.Features.Services.Rules;

/// <summary>Windows' default start types and how "strong" a start type is (used by the verdicts and the repair).</summary>
public static class WindowsDefaults
{
    private static readonly HashSet<string> GuardDefaults = new(ServiceDefaults.KnownServices, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The service's Windows default start type(s): the catalog's, else the SafetyGuard's own table
    /// (<see cref="ServiceDefaults"/>), else none known. Where Windows 10 and 11 differ, both are listed.
    /// </summary>
    public static IReadOnlyList<ServiceStart> For(string name, ServiceEntry? entry)
    {
        if (entry is { WindowsDefaults.Count: > 0 }) return entry.WindowsDefaults;
        if (!GuardDefaults.Contains(name)) return Array.Empty<ServiceStart>();
        // ServiceDefaults only answers "is X a default?" and always says yes to Automatic (switching on can't weaken
        // anything), so the listed defaults are the weaker ones it accepts, or Automatic when it accepts none.
        var list = new[] { ServiceStart.AutomaticDelayed, ServiceStart.Manual }.Where(s => ServiceDefaults.IsDefaultFor(name, s)).ToList();
        return list.Count == 0 ? new[] { ServiceStart.Automatic } : list;
    }

    /// <summary>True if the guard's own table knows this service (protected services the repair may restore).</summary>
    public static bool IsGuardListed(string name) => GuardDefaults.Contains(name);

    /// <summary>Disabled 0 · Manual 1 · Automatic (delayed) 2 · Automatic 3 (boot/system drivers above that).</summary>
    public static int Strength(ServiceStart s) => s switch
    {
        ServiceStart.Disabled => 0,
        ServiceStart.Manual => 1,
        ServiceStart.AutomaticDelayed => 2,
        ServiceStart.Automatic => 3,
        _ => 4,
    };

    /// <summary>Set weaker than every Windows default (e.g. Manual when Windows has it Automatic, or Disabled).</summary>
    public static bool IsWeakerThanDefault(ServiceStart current, IReadOnlyList<ServiceStart> defaults)
        => defaults.Count > 0 && defaults.All(d => Strength(current) < Strength(d));

    /// <summary>The default to put back: the first listed (the catalog lists the Windows 11 value first when they differ).</summary>
    public static ServiceStart? Preferred(IReadOnlyList<ServiceStart> defaults) => defaults.Count > 0 ? defaults[0] : null;
}
