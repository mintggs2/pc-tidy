using PcTidy.Catalog.Speed;
using PcTidy.Features.Services.Model;
using PcTidy.Features.Services.Sources;

namespace PcTidy.Features.Services.Rules;

/// <summary>What the policy needs to know about one service.</summary>
public sealed record PolicyInput(
    ServiceRecord Record,
    FileFacts Facts,
    ServiceEntry? Entry,
    ServiceFlags Flags,
    IReadOnlyList<string> Dependents,
    bool IsWindowsFile);

/// <summary>
/// The "safe to change?" verdict and the offered changes for one service (research: Services verdict table).
/// <list type="bullet">
/// <item>Drivers, per-user copies, critical/security/anti-cheat services (names and publishers) and services whose
/// program file is gone: shown only.</item>
/// <item>Catalog entries: Proven → its recommended start type and back to the Windows default; Situational → the same,
/// only in Expert mode (never in Optimize-all); Placebo, Harmful and Protected → never (the repair handles Harmful and
/// Protected services that were switched off).</item>
/// <item>Not in the catalog: Windows' own → left alone; other companies → Manual only (never Disabled), and only when
/// they start with Windows.</item>
/// </list>
/// The SafetyGuard and the Platform handler enforce the same limits again before anything runs.
/// </summary>
public static class ServicePolicy
{
    public const string DriverReason = "A driver. PC Tidy lists drivers for information only and never changes them.";
    public const string PerUserReason = "Windows makes a fresh copy of this for each person who signs in, so a change here wouldn't stick. Change its template instead.";
    public const string NeverTouchReason = "Protects your PC or keeps Windows working, so PC Tidy never changes it.";
    public const string SecurityPublisherReason = "Security or anti-cheat software. Changing it could leave your PC unprotected or stop games from starting.";
    public const string LeftoverReason = "Its program file is missing, so it can't run. It's probably left over from an app that was removed; PC Tidy only shows it.";
    public const string WindowsUnknownReason = "Part of Windows. PC Tidy has no proven advice for it, so it leaves it alone.";
    public const string ClaimsMicrosoftReason = "Says it's from Microsoft, but its signature doesn't confirm that. PC Tidy leaves it alone; your antivirus can check it.";
    public const string UnknownStartReason = "Windows didn't say how this service starts, so PC Tidy doesn't offer a change.";

    /// <param name="expertAllowed">Expert mode is on and this isn't an Optimize-all run.</param>
    public static ServiceAssessment Assess(PolicyInput p, bool expertAllowed)
    {
        var r = p.Record;
        var reasons = new List<string>();
        var defaults = WindowsDefaults.For(r.Name, p.Entry);

        if (r.IsDriver) return None(ServiceVerdictKind.Driver, DriverReason, reasons);
        if (IsNeverTouch(r.Name) || p.Entry?.Verdict == ServiceVerdict.Protected)
            return None(ServiceVerdictKind.Protected, p.Entry is { } pe ? Sentence(pe.Why) : NeverTouchReason, Explain(p, defaults, reasons));
        if (SpeedProtectedNames.IsProtectedPublisher(p.Facts.Company, p.Facts.Signer))
            return None(ServiceVerdictKind.Protected, SecurityPublisherReason, Explain(p, defaults, reasons));
        if (r.IsPerUser && !IsTemplate(r)) return None(Kind(p), PerUserReason, Explain(p, defaults, reasons));
        if (p.Flags.HasFlag(ServiceFlags.Leftover)) return None(Kind(p), LeftoverReason, Explain(p, defaults, reasons));
        if (r.StartType is not { } current) return None(Kind(p), UnknownStartReason, Explain(p, defaults, reasons));

        Explain(p, defaults, reasons);
        return p.Entry is { } entry
            ? Catalog(entry, current, defaults, expertAllowed, reasons)
            : Unknown(p, current, reasons);
    }

    private static ServiceAssessment Catalog(ServiceEntry e, ServiceStart current, IReadOnlyList<ServiceStart> defaults, bool expertAllowed, List<string> reasons)
    {
        switch (e.Verdict)
        {
            case ServiceVerdict.Placebo:
                return None(ServiceVerdictKind.Placebo, "Doesn't help: " + Sentence(e.Why), reasons);
            case ServiceVerdict.Harmful:
                return None(ServiceVerdictKind.Harmful, "Changing it breaks something. " + Sentence(e.Why), reasons);
            case ServiceVerdict.Situational when !expertAllowed:
                return None(ServiceVerdictKind.Situational, "Expert mode only. " + Sentence(e.Why), reasons);
        }
        var kind = e.Verdict == ServiceVerdict.Proven ? ServiceVerdictKind.Proven : ServiceVerdictKind.Situational;
        var options = new List<ServiceOption>();
        if (e.Recommended is { } rec && rec != current && rec is not (ServiceStart.Boot or ServiceStart.System))
            options.Add(Option(rec, isDefault: defaults.Contains(rec), isRecommended: true));
        if (!defaults.Contains(current) && WindowsDefaults.Preferred(defaults) is { } def && options.All(o => o.Target != def)
            && def is not (ServiceStart.Boot or ServiceStart.System))
            options.Add(Option(def, isDefault: true, isRecommended: false));
        if (options.Count > 0) return new ServiceAssessment(kind, options, null, reasons);
        var already = e.Recommended == current
            ? $"Already {ServiceText.Start(current)}, which is what PC Tidy recommends."
            : "Nothing to change: it's set the way Windows ships it.";
        return new ServiceAssessment(kind, options, already, reasons);
    }

    private static ServiceAssessment Unknown(PolicyInput p, ServiceStart current, List<string> reasons)
    {
        if (p.IsWindowsFile || p.Facts.IsMicrosoft) return None(ServiceVerdictKind.Windows, WindowsUnknownReason, reasons);
        if (p.Facts.ClaimsMicrosoft) return None(ServiceVerdictKind.ThirdParty, ClaimsMicrosoftReason, reasons);
        if (current is not (ServiceStart.Automatic or ServiceStart.AutomaticDelayed))
            return new ServiceAssessment(ServiceVerdictKind.ThirdParty, Array.Empty<ServiceOption>(),
                current == ServiceStart.Disabled ? "It's already switched off." : "It already starts only when something needs it.", reasons);
        reasons.Insert(0, "From another company and not in PC Tidy's list, so the only change offered is Manual: it still starts whenever an app asks for it.");
        return new ServiceAssessment(ServiceVerdictKind.ThirdParty, new[] { Option(ServiceStart.Manual, isDefault: false, isRecommended: true) }, null, reasons);
    }

    /// <summary>The reasons every verdict shows: why, what may break, Windows' default, dependents, signature, flags.</summary>
    private static List<string> Explain(PolicyInput p, IReadOnlyList<ServiceStart> defaults, List<string> reasons)
    {
        if (p.Entry is { } e)
        {
            reasons.Add(Sentence(e.Why));
            if (!string.IsNullOrWhiteSpace(e.BreaksIf)) reasons.Add("Might stop working: " + Sentence(e.BreaksIf!));
        }
        if (defaults.Count > 0) reasons.Add("Windows default: " + string.Join(" or ", defaults.Select(ServiceText.Start)) + ".");
        if (p.Dependents.Count > 0)
            reasons.Add($"{p.Dependents.Count} other service{(p.Dependents.Count == 1 ? " needs" : "s need")} it: {Short(p.Dependents)}.");
        if (p.Flags.HasFlag(ServiceFlags.Unsigned)) reasons.Add("Its file isn't signed, so its maker can't be confirmed.");
        if (p.Flags.HasFlag(ServiceFlags.SuspiciousPath)) reasons.Add("It runs from a temporary, AppData or Downloads folder, where real services rarely live. Your antivirus can check it.");
        if (p.Flags.HasFlag(ServiceFlags.AutoStartNotRunning)) reasons.Add("Set to start with Windows, but it isn't running now (it may have stopped by itself or failed).");
        return reasons;
    }

    /// <summary>A per-user template key (type 0x40 without the instance bit 0x80): changes made here stick.</summary>
    public static bool IsTemplate(ServiceRecord r) => (r.ServiceType & 0x40) != 0 && (r.ServiceType & 0x80) == 0;

    /// <summary>Critical, security and anti-cheat names (exact names and the prefix patterns some anti-cheat drivers use).</summary>
    public static bool IsNeverTouch(string name) => SpeedProtectedNames.Matches(name, SpeedProtectedNames.NeverTouchServices);

    public static ServiceOption Option(ServiceStart target, bool isDefault, bool isRecommended)
    {
        var name = ServiceText.Start(target);
        var label = isDefault && !isRecommended ? $"Back to Windows default ({name})" : isDefault ? $"Set to {name} (Windows default)" : $"Set to {name}";
        var explanation = target switch
        {
            ServiceStart.Manual => "It starts only when an app or Windows asks for it, instead of every time Windows starts.",
            ServiceStart.Disabled => "It can't start at all until you undo this.",
            ServiceStart.AutomaticDelayed => "It starts shortly after Windows has started, the way Windows ships it.",
            _ => "It starts with Windows, the way Windows ships it.",
        };
        return new ServiceOption(target, label, explanation, isDefault, isRecommended);
    }

    private static ServiceVerdictKind Kind(PolicyInput p) => p.Entry?.Verdict switch
    {
        ServiceVerdict.Proven => ServiceVerdictKind.Proven,
        ServiceVerdict.Situational => ServiceVerdictKind.Situational,
        ServiceVerdict.Placebo => ServiceVerdictKind.Placebo,
        ServiceVerdict.Harmful => ServiceVerdictKind.Harmful,
        ServiceVerdict.Protected => ServiceVerdictKind.Protected,
        _ => p.IsWindowsFile || p.Facts.IsMicrosoft ? ServiceVerdictKind.Windows : ServiceVerdictKind.ThirdParty,
    };

    private static ServiceAssessment None(ServiceVerdictKind kind, string why, List<string> reasons)
        => new(kind, Array.Empty<ServiceOption>(), why, reasons);

    private static string Short(IReadOnlyList<string> names)
        => names.Count <= 4 ? string.Join(", ", names) : string.Join(", ", names.Take(4)) + $" and {names.Count - 4} more";

    private static string Sentence(string s)
    {
        var t = s.Trim();
        return t.Length == 0 || t.EndsWith('.') || t.EndsWith('!') || t.EndsWith('?') || t.EndsWith(')') ? t : t + ".";
    }
}
