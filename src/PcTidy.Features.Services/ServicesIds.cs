namespace PcTidy.Features.Services;

/// <summary>Feature and group ids of the Services section (Optimize-all presets match group ids by "contains").</summary>
public static class ServicesIds
{
    /// <summary>The section and its main scanner. The SafetyGuard checks its targets against the service catalog.</summary>
    public const string Module = "services";

    /// <summary>"Bad services" repair: only ever puts a service back to its exact Windows default.</summary>
    public const string Repair = "services.repair";

    public const string GamingGroup = "services.profile-gaming";
    public const string LaptopBatteryGroup = "services.profile-laptop-battery";
    public const string PrivacyGroup = "services.profile-privacy";
    /// <summary>Other catalog services that can be changed (not in a profile, or Situational in Expert mode).</summary>
    public const string OtherGroup = "services.more";
    public const string RepairGroup = "services.repair.defaults";

    /// <summary>The profile group id for a <see cref="ServiceProfiles"/> name.</summary>
    public static string GroupFor(string profile) => profile switch
    {
        ServiceProfiles.Gaming => GamingGroup,
        ServiceProfiles.LaptopBattery => LaptopBatteryGroup,
        ServiceProfiles.Privacy => PrivacyGroup,
        _ => "services.profile-" + profile,
    };
}
