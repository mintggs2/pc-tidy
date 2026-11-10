using PcTidy.Catalog.PcCare;
using PcTidy.Core.Model;
using PcTidy.Features.PcCare.Analysis;
using PcTidy.Features.PcCare.Model;
using PcTidy.Features.PcCare.Planning;
using T = PcTidy.Features.PcCare.Scanning.PcCareTabs;

namespace PcTidy.Features.PcCare.Scanning;

/// <summary>Security tab: who protects the PC (read-only), Defender quick scan, vulnerable drivers. Never Defender exclusions.</summary>
public static partial class PcCareResultBuilder
{
    /// <summary>Plain text for a Defender switched off by policy (shared with the page and the report).</summary>
    public const string DefenderPolicyNote =
        "Microsoft Defender is turned off by a policy setting, and no other antivirus is on. This is usually left behind by an old " +
        "\"debloat\" or \"optimizer\" tool. PC Tidy doesn't change it from here: Tweaks > Find bad tweaks can put Defender back " +
        "(admin and a restart, with a restore point first).";

    private static AntivirusVerdict? AddSecurity(PcCareSnapshot s, List<ScanGroup> groups, Dictionary<string, string> notes)
    {
        var sec = s.Security;
        var openSecurity = PcCareActions.Open(PcCareLinks.WindowsSecurity, "Open Windows Security");
        if (sec is null)
        {
            // Nothing was read, so nothing can be called fine: show the same "unknown" row as when Windows doesn't say.
            notes[T.Security] = "Windows didn't report its security status this time. Windows Security shows it.";
            groups.Add(Group(T.Security, "antivirus", "Antivirus", "Read from Windows Security Center and Microsoft Defender. PC Tidy doesn't change these settings.",
                Badge.Check, new[] { UnknownAntivirusRow(openSecurity) }));
            return null;
        }

        var verdict = AntivirusAnalyzer.Analyze(sec);
        var rows = new List<ScanItem>();
        switch (verdict.Situation)
        {
            case AvSituation.DefenderOn:
                rows.Add(Info("Microsoft Defender is on", "Real-time protection is running.", state: "On", details: DefenderDetails(sec.Defender, s.TakenUtc)));
                rows.Add(Pick("Quick scan now", "Checks the places malware usually hides. Takes a few minutes in the background; Defender shows anything it finds.",
                    Badge.Check, new[] { PcCareActions.DefenderQuickScan() }, detail: LastScanText(sec.Defender, s.TakenUtc)));
                notes[T.Security] = "Microsoft Defender is protecting this PC.";
                break;
            case AvSituation.ThirdPartyOn:
                var names = string.Join(", ", verdict.ActiveThirdParty);
                rows.Add(Info($"{names} is protecting this PC", "Defender steps aside while another antivirus is on. That's normal.", state: "On"));
                if (verdict.PolicyTurnsDefenderOff)
                    rows.Add(Info("Microsoft Defender is also turned off by a policy",
                        $"Fine while {names} is on. If you ever uninstall it, Defender won't come back by itself: Tweaks > Find bad tweaks can put it back.", state: "Policy"));
                notes[T.Security] = $"{names} is your antivirus, so PC Tidy doesn't offer a Defender scan.";
                break;
            case AvSituation.DefenderOffByPolicy:
                rows.Add(Pick("Microsoft Defender is turned off by a policy",
                    "Nothing is checking the files you download or run. Open Windows Security to see the details; Tweaks can put Defender back.",
                    Badge.Check, new[] { openSecurity }, state: "Off (policy)", details: PolicyDetails(sec.Policy)));
                notes[T.Security] = DefenderPolicyNote;
                break;
            case AvSituation.NoActiveAntivirus:
                rows.Add(Pick("No antivirus is on",
                    "Nothing is checking the files you download or run. Open Windows Security to turn Microsoft Defender's protection on.",
                    Badge.Check, new[] { openSecurity }, state: "Off", details: DefenderDetails(sec.Defender, s.TakenUtc)));
                notes[T.Security] = "No antivirus reported itself as on. Windows Security shows what's going on and can turn Defender on.";
                break;
            default:
                notes[T.Security] = "Windows didn't report which antivirus is on. Windows Security shows it.";
                rows.Add(UnknownAntivirusRow(openSecurity));
                break;
        }
        groups.Add(Group(T.Security, "antivirus", "Antivirus", "Read from Windows Security Center and Microsoft Defender. PC Tidy doesn't change these settings.", Badge.Check, rows));

        var drivers = sec.Drivers
            .Select(d => (Driver: d, Match: VulnerableDriverCatalog.Match(d.ImagePath)))
            .Where(x => x.Match is not null)
            .DistinctBy(x => x.Driver.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => Pick(
                $"{x.Match!.FileName} ({x.Driver.DisplayName ?? x.Driver.Name})",
                $"A driver with a known security hole ({x.Match.Advisory}) that other programs can misuse. It usually comes with {x.Match.ShippedWith}. " +
                "Update or uninstall that app; Windows Security > Device security > Core isolation can also block known-bad drivers. " +
                "If your antivirus flags it, don't add an exception: update the app instead.",
                Badge.Check, new[] { PcCareActions.Open(PcCareLinks.Apps, "Open Settings > Apps") },
                detail: x.Driver.ImagePath, state: x.Driver.Running ? "Running" : "Installed",
                details: new Dictionary<string, string> { ["Service"] = x.Driver.Name, ["Advisory"] = x.Match.Advisory, ["Source"] = x.Match.Source }))
            .ToList();
        if (drivers.Count > 0)
            groups.Add(Group(T.Security, "drivers", "Vulnerable drivers",
                "Signed drivers with a published security hole (Microsoft's driver blocklist, loldrivers.io). Shown so you can update the app that installed them.",
                Badge.Check, drivers));
        else if (sec.Drivers.Count > 0)
            notes[T.Security] = notes[T.Security] + " No known vulnerable drivers were found.";
        return verdict;
    }

    private static ScanItem UnknownAntivirusRow(PlanItem openSecurity)
        => Pick("Antivirus status unknown", "Windows didn't report it to PC Tidy. Windows Security shows the real status.",
            Badge.Check, new[] { openSecurity }, state: "Unknown");

    private static Dictionary<string, string> DefenderDetails(DefenderStatus? d, DateTime nowUtc)
    {
        var map = new Dictionary<string, string>();
        if (d is null) return map;
        if (d.RealTimeProtectionEnabled is { } rt) map["Real-time protection"] = rt ? "On" : "Off";
        if (d.SignaturesUpdatedUtc is { } sig) map["Virus definitions"] = "Updated " + Ago(sig, nowUtc);
        if (d.LastQuickScanUtc is { } scan) map["Last quick scan"] = Ago(scan, nowUtc);
        if (d.TamperProtected is { } tp) map["Tamper Protection"] = tp ? "On" : "Off";
        if (!string.IsNullOrEmpty(d.RunningMode)) map["Mode"] = d.RunningMode!;
        return map;
    }

    private static Dictionary<string, string> PolicyDetails(DefenderPolicy p)
    {
        var map = new Dictionary<string, string>();
        if (p.DisableAntiSpyware) map["Policy"] = "DisableAntiSpyware = 1";
        if (p.DisableAntiVirus) map["Policy (antivirus)"] = "DisableAntiVirus = 1";
        if (p.DisableRealtimeMonitoring) map["Policy (real-time)"] = "DisableRealtimeMonitoring = 1";
        if (p.ServiceDisabled) map["Defender service"] = "Disabled";
        return map;
    }

    private static string LastScanText(DefenderStatus? d, DateTime nowUtc)
        => d?.LastQuickScanUtc is { } t ? "Last quick scan " + Ago(t, nowUtc) : "No quick scan reported yet";
}
