using PcTidy.Catalog.Drivers;
using PcTidy.Features.Drivers.Model;

namespace PcTidy.Features.Drivers.Analysis;

/// <summary>A present device with a Device Manager problem code, explained.</summary>
public sealed record ProblemFinding(
    string InstanceId, string Name, string ClassName, ProblemCodeInfo Code, IReadOnlyList<string> HardwareIds,
    string DriverInf, DriverUpdateOffer? WindowsUpdateOffer)
{
    public bool IsMissingDriver => Code.Code == ProblemCodes.NoDriver;
    public bool IsInfoOnly => Code.Kind == ProblemKind.Info;
}

/// <summary>
/// Combines pnputil's problem list with WMI's ConfigManagerErrorCode (either may be missing on a given PC).
/// "Not connected" (45) belongs to Old devices, not Problems; "turned off" (22) is information, not a problem.
/// </summary>
public static class ProblemAnalyzer
{
    public static IReadOnlyList<ProblemFinding> Analyze(DriverInventory inv)
    {
        var byId = new Dictionary<string, ProblemFinding>(StringComparer.OrdinalIgnoreCase);
        var offers = inv.Updates.Updates;

        foreach (var d in inv.ProblemDevices.Concat(inv.Devices.Where(x => x.ProblemCode != 0 && x.IsPresent)))
        {
            if (!d.IsPresent || d.ProblemCode is 0 or ProblemCodes.NotPresent || byId.ContainsKey(d.InstanceId)) continue;
            byId[d.InstanceId] = new ProblemFinding(d.InstanceId, Name(d.Description), d.ClassName,
                ProblemCodes.Describe(d.ProblemCode), d.HardwareIds, d.DriverInf, MatchOffer(d.HardwareIds, offers));
        }
        // pnputil reports a turned-off device with Status "Disabled" and no ProblemCode element.
        foreach (var d in inv.ProblemDevices.Where(x => x.IsPresent && x.ProblemCode == 0 && x.State == DeviceState.Disabled))
        {
            if (byId.ContainsKey(d.InstanceId)) continue;
            byId[d.InstanceId] = new ProblemFinding(d.InstanceId, Name(d.Description), d.ClassName,
                ProblemCodes.Describe(ProblemCodes.Disabled), d.HardwareIds, d.DriverInf, null);
        }
        foreach (var w in inv.WmiProblems)
        {
            if (w.Code is 0 or ProblemCodes.NotPresent) continue;
            if (byId.TryGetValue(w.InstanceId, out var existing))
            {
                // pnputil had the device but not its hardware ids: borrow WMI's.
                if (existing.HardwareIds.Count == 0 && w.HardwareIds.Count > 0)
                    byId[w.InstanceId] = existing with { HardwareIds = w.HardwareIds, WindowsUpdateOffer = existing.WindowsUpdateOffer ?? MatchOffer(w.HardwareIds, offers) };
                continue;
            }
            byId[w.InstanceId] = new ProblemFinding(w.InstanceId, Name(w.Name), w.ClassName,
                ProblemCodes.Describe(w.Code), w.HardwareIds, "", MatchOffer(w.HardwareIds, offers));
        }
        return byId.Values.OrderBy(p => p.IsInfoOnly).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>A Windows Update driver offer whose hardware id matches one of the device's ids.</summary>
    internal static DriverUpdateOffer? MatchOffer(IReadOnlyList<string> hardwareIds, IReadOnlyList<DriverUpdateOffer> offers)
        => offers.FirstOrDefault(o => o.HardwareId.Length > 0
            && hardwareIds.Any(h => string.Equals(h.Trim(), o.HardwareId.Trim(), StringComparison.OrdinalIgnoreCase)));

    private static string Name(string description) => string.IsNullOrWhiteSpace(description) ? "Unknown device" : description.Trim();
}
