using System.Text.RegularExpressions;
using PcTidy.Features.PcCare.Model;

namespace PcTidy.Features.PcCare.Analysis;

/// <summary>Whether a leftover may be offered, and if not, the plain reason it's shown greyed out.</summary>
public sealed record Eligibility(bool Offered, string? DisabledReason)
{
    public static readonly Eligibility Yes = new(true, null);
    public static Eligibility No(string reason) => new(false, reason);
}

/// <summary>Rules for saved Wi-Fi networks and leftover user profiles. Pure; tested with fixed facts.</summary>
public static partial class LeftoverRules
{
    /// <summary>A saved network not used for this long is called "old" (it's still only offered unticked).</summary>
    public static readonly TimeSpan OldWifiAge = TimeSpan.FromDays(180);

    [GeneratedRegex(@"^S-1-5-21(-\d+){3,4}$", RegexOptions.CultureInvariant)]
    private static partial Regex UserSid();

    /// <summary>Every saved network may be removed except the one you're connected to right now.</summary>
    public static Eligibility ForWifi(WlanProfile p)
        => p.IsConnected ? Eligibility.No("You're connected to this network right now, so it stays.") : Eligibility.Yes;

    public static bool IsOld(WlanProfile p, DateTime nowUtc) => p.LastConnectedUtc is not { } last || nowUtc - last > OldWifiAge;

    /// <summary>
    /// A local profile may be removed only when its account is gone: never the current, a signed-in (loaded) or a
    /// special (system) profile, never a work/school account PC Tidy can't check. Profiles of existing accounts are not leftovers.
    /// </summary>
    public static Eligibility ForUserProfile(UserProfileFacts p, string? currentSid)
    {
        if (currentSid is null) return Eligibility.No("PC Tidy couldn't tell which profile is yours, so nothing is offered.");
        if (string.Equals(p.Sid, currentSid, StringComparison.OrdinalIgnoreCase)) return Eligibility.No("This is your own profile.");
        if (p.Special || !UserSid().IsMatch(p.Sid)) return Eligibility.No("A Windows system profile. It always stays.");
        if (p.Loaded) return Eligibility.No("This account is signed in right now, so its profile stays.");
        if (p.AccountExists) return Eligibility.No("The account still exists. Remove the account in Settings > Accounts if you want it gone.");
        if (!p.IsThisPcAccount) return Eligibility.No("A work or school account. PC Tidy can't check whether it's still in use.");
        return Eligibility.Yes;
    }

    /// <summary>Only profiles worth showing: leftovers (offered) and ones we can't check. Existing accounts' profiles are normal.</summary>
    public static bool IsWorthShowing(UserProfileFacts p, string? currentSid)
        => !p.Special && UserSid().IsMatch(p.Sid) && !p.AccountExists
           && !string.Equals(p.Sid, currentSid, StringComparison.OrdinalIgnoreCase);
}
