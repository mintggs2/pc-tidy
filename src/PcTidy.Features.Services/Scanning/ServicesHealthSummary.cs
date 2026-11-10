using PcTidy.Features.Services.Model;

namespace PcTidy.Features.Services.Scanning;

/// <summary>
/// The Services lines of the PC health report (label, value), from one inventory. Counts only, no service paths or
/// account names, so the report stays shareable.
/// </summary>
public static class ServicesHealthSummary
{
    public const string Heading = "Services";

    public static IReadOnlyList<(string Label, string Value)> Lines(ServiceInventoryResult inventory)
    {
        var services = inventory.Services;
        var thirdParty = services.Count(s => s.IsThirdParty);
        var autoThirdParty = services.Count(s => s.IsThirdParty && s.Record.StartType is ServiceStart.Automatic or ServiceStart.AutomaticDelayed);
        var lines = new List<(string, string)>
        {
            ("Services", $"{services.Count} ({services.Count - thirdParty} from Microsoft, {thirdParty} from other companies)"),
            ("Running now", services.Count(s => s.Record.State == Sources.ServiceRunState.Running).ToString(System.Globalization.CultureInfo.CurrentCulture)),
            ("Other companies' services that start with Windows", autoThirdParty.ToString(System.Globalization.CultureInfo.CurrentCulture)),
            ("Not signed", Count(services, ServiceFlags.Unsigned)),
            ("Program file missing (left over)", Count(services, ServiceFlags.Leftover)),
            ("Running from a temporary, AppData or Downloads folder", Count(services, ServiceFlags.SuspiciousPath)),
            ("Windows services switched off that shouldn't be", ServiceRepairScanner.Find(services).Count.ToString(System.Globalization.CultureInfo.CurrentCulture)),
            ("Drivers", inventory.Drivers.Count.ToString(System.Globalization.CultureInfo.CurrentCulture)),
        };
        return lines;
    }

    private static string Count(IEnumerable<ServiceInfo> services, ServiceFlags flag)
        => services.Count(s => s.Flags.HasFlag(flag)).ToString(System.Globalization.CultureInfo.CurrentCulture);
}
