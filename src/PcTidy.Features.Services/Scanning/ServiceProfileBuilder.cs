using PcTidy.Features.Services.Model;
using PcTidy.UI.Flow;

namespace PcTidy.Features.Services.Scanning;

/// <summary>One named bundle (Gaming / Laptop battery / Privacy).</summary>
public sealed record ServiceProfileInfo(string Id, string Title, string Description);

/// <summary>A profile's state on this PC: what would change, and how many catalog services it has at all.</summary>
public sealed record ServiceProfileState(ServiceProfileInfo Profile, IReadOnlyList<(ServiceInfo Service, ServiceOption Option)> Changes, int CatalogMembers, bool AppliesHere)
{
    /// <summary>Plain-English status line ("No proven service changes; nothing to do.").</summary>
    public string Status => !AppliesHere ? "Only for PCs with a battery."
        : CatalogMembers == 0 ? "No proven service changes; nothing to do."
        : Changes.Count == 0 ? "Nothing to do: every service in it is already set the way PC Tidy recommends (or isn't on this PC)."
        : $"{Changes.Count} service{(Changes.Count == 1 ? "" : "s")} to change.";
}

/// <summary>
/// Profiles are built ONLY from Proven catalog entries that list the profile, and only offer each entry's recommended
/// start type. Gaming and Laptop battery are empty today on purpose: research found no proven service change for them,
/// so they say "nothing to do" rather than inventing members.
/// </summary>
public static class ServiceProfileBuilder
{
    public static readonly IReadOnlyList<ServiceProfileInfo> All = new[]
    {
        new ServiceProfileInfo(ServiceProfiles.Gaming, "Gaming", "Services that measurably get in the way of games."),
        new ServiceProfileInfo(ServiceProfiles.LaptopBattery, "Laptop battery", "Services that measurably cost battery life."),
        new ServiceProfileInfo(ServiceProfiles.Privacy, "Privacy", "Services that send usage data to Microsoft."),
    };

    /// <summary>Catalog entries that may be in a profile (Proven, listing it, with a recommendation).</summary>
    public static IEnumerable<ServiceEntry> CatalogMembers(string profile)
        => ServiceCatalog.All.Where(e => e.Verdict == ServiceVerdict.Proven && e.Recommended is not null && e.Profiles.Contains(profile, StringComparer.OrdinalIgnoreCase));

    /// <summary>Laptop battery only applies with a battery (unknown hardware: shown).</summary>
    public static bool AppliesTo(string profile, ISystemInfo? system)
        => profile != ServiceProfiles.LaptopBattery || system is null || system.HasBattery || system.IsLaptop;

    public static ServiceProfileState State(ServiceProfileInfo profile, IEnumerable<ServiceInfo> services, ISystemInfo? system)
    {
        var applies = AppliesTo(profile.Id, system);
        var changes = !applies ? new List<(ServiceInfo, ServiceOption)>() : services
            .Where(s => s.Entry is { Verdict: ServiceVerdict.Proven } e && e.Profiles.Contains(profile.Id, StringComparer.OrdinalIgnoreCase))
            .Select(s => (Service: s, Option: s.Assessment.Options.FirstOrDefault(o => o.IsRecommended && o.Target == s.Entry!.Recommended)))
            .Where(x => x.Option is not null)
            .Select(x => (x.Service, x.Option!))
            .ToList();
        return new ServiceProfileState(profile, changes, CatalogMembers(profile.Id).Count(), applies);
    }

    /// <summary>The profile an Optimize-all preset ticks (Gamer → gaming, Privacy → privacy); null for the others.</summary>
    public static string? ForPreset(string? preset)
        => string.Equals(preset, OptimizePresets.Gamer, StringComparison.OrdinalIgnoreCase) ? ServiceProfiles.Gaming
            : string.Equals(preset, OptimizePresets.Privacy, StringComparison.OrdinalIgnoreCase) ? ServiceProfiles.Privacy
            : null;
}
