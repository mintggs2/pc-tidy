using PcTidy.Features.PcCare.Model;
using PcTidy.Features.PcCare.Parsing;

namespace PcTidy.Features.PcCare.Analysis;

public enum AvSituation
{
    /// <summary>Microsoft Defender is on and in charge.</summary>
    DefenderOn,
    /// <summary>Another antivirus is on (Defender steps aside, which is normal).</summary>
    ThirdPartyOn,
    /// <summary>Defender is switched off by a policy and nothing else is protecting the PC.</summary>
    DefenderOffByPolicy,
    /// <summary>No antivirus is active (and no policy explains it).</summary>
    NoActiveAntivirus,
    /// <summary>Windows didn't say (Security Center and Defender status both unavailable).</summary>
    Unknown,
}

public sealed record AntivirusVerdict(AvSituation Situation, IReadOnlyList<string> ActiveThirdParty, bool PolicyTurnsDefenderOff)
{
    /// <summary>"Quick scan now" is only offered when Defender itself is the active antivirus.</summary>
    public bool CanQuickScan => Situation == AvSituation.DefenderOn;
}

/// <summary>Works out who protects this PC from Security Center (productState) and Defender's own status. Read-only.</summary>
public static class AntivirusAnalyzer
{
    public static AntivirusVerdict Analyze(SecurityFacts facts)
    {
        var decoded = facts.Products.Select(p => (Product: p, State: SecurityCenterState.Decode(p.ProductState),
            IsDefender: SecurityCenterState.IsDefender(p.ReportingExe, p.Name))).ToList();
        var thirdParty = decoded.Where(d => !d.IsDefender && d.State.IsActive).Select(d => d.Product.Name).Distinct().ToList();
        bool policyOff = facts.Policy.TurnsDefenderOff;

        if (thirdParty.Count > 0) return new AntivirusVerdict(AvSituation.ThirdPartyOn, thirdParty, policyOff);
        if (policyOff) return new AntivirusVerdict(AvSituation.DefenderOffByPolicy, thirdParty, true);

        var d = facts.Defender;
        bool defenderReportsOn = d is { AntivirusEnabled: true, RealTimeProtectionEnabled: true }
                                 && !string.Equals(d.RunningMode, "Passive", StringComparison.OrdinalIgnoreCase)
                                 && !string.Equals(d.RunningMode, "Not running", StringComparison.OrdinalIgnoreCase);
        bool centerSaysDefenderOn = decoded.Any(x => x.IsDefender && x.State.IsActive);
        if (defenderReportsOn || (d is null && centerSaysDefenderOn)) return new AntivirusVerdict(AvSituation.DefenderOn, thirdParty, false);

        if (d is null && !facts.SecurityCenterAvailable) return new AntivirusVerdict(AvSituation.Unknown, thirdParty, false);
        return new AntivirusVerdict(AvSituation.NoActiveAntivirus, thirdParty, false);
    }
}
