using PcTidy.Features.Speed.Common;

namespace PcTidy.Features.Speed.Startup;

/// <summary>What the Startup list does with an auto-start service.</summary>
public enum ServiceEligibility
{
    /// <summary>Windows' own (or Microsoft-signed): not listed at all.</summary>
    Hidden,
    /// <summary>Shown so you know, can't be ticked (security, anti-cheat, a file that only claims to be Microsoft).</summary>
    ShownOnly,
    /// <summary>Can be set to Manual.</summary>
    Offered,
}

/// <summary>
/// Research §4 service rules: only non-Microsoft auto-start services are listed; they can only be set to Manual
/// (never Disabled); drivers, Microsoft services, Windows Update/BITS, Defender and other security software and
/// anti-cheat are never offered. The SafetyGuard and the service handler enforce the same rules again.
/// </summary>
public static class ServiceRules
{
    private const int KernelDriver = 0x1, FileSystemDriver = 0x2, OwnProcess = 0x10, ShareProcess = 0x20, UserService = 0x40, UserServiceInstance = 0x80;
    private const int AutoStart = 2;

    /// <summary>A Win32 service (own or shared process) that starts automatically. Drivers and per-user service templates are excluded.</summary>
    public static bool IsAutoStartProcessService(int type, int start)
        => (type & (KernelDriver | FileSystemDriver)) == 0
           && (type & (OwnProcess | ShareProcess)) != 0
           && (type & (UserService | UserServiceInstance)) == 0
           && start == AutoStart;

    public static (ServiceEligibility Eligibility, string? Why) Evaluate(string serviceName, FileFacts facts, string windowsDir)
    {
        if (facts.IsMicrosoft || (facts.Path is { } p && PathUtil.IsUnder(p, windowsDir)))
            return (ServiceEligibility.Hidden, null);
        if (SpeedProtectedNames.Matches(serviceName, SpeedProtectedNames.NeverTouchServices))
            return (ServiceEligibility.ShownOnly, "Protects your PC or keeps Windows working, so PC Tidy never changes it.");
        if (SpeedProtectedNames.IsProtectedPublisher(facts.Company, facts.Signer))
            return (ServiceEligibility.ShownOnly, "Security or anti-cheat software. Changing it could leave your PC unprotected or stop games from starting.");
        if (facts.Company?.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) == true)
            return (ServiceEligibility.ShownOnly, "Says it's from Microsoft, but its signature doesn't confirm that. PC Tidy leaves it alone; your antivirus can check it.");
        return (ServiceEligibility.Offered, null);
    }
}
