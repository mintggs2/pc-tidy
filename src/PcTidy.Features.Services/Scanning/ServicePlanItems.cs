using PcTidy.Features.Services.Model;

namespace PcTidy.Features.Services.Scanning;

/// <summary>
/// Builds the section's PlanItems and rows. Every change is a <see cref="ActionKind.ServiceSetStartType"/> that needs
/// admin, is a system change (restore point first) and is reversible: the Platform handler saves the old start type,
/// the delayed-start flag and the recovery settings, and undo puts all three back.
/// </summary>
public static class ServicePlanItems
{
    public static PlanItem SetStart(string featureId, ServiceInfo s, ServiceStart target)
    {
        var now = s.Record.StartType is { } c ? ServiceText.Start(c) : "unknown";
        var description = $"Set '{s.DisplayName}' ({s.Name}) to {ServiceText.Start(target)} (now {now}).";
        if (!string.IsNullOrWhiteSpace(s.Entry?.BreaksIf) && target is ServiceStart.Manual or ServiceStart.Disabled)
            description += $" Might stop working: {s.Entry!.BreaksIf!.TrimEnd('.')}.";
        // Only the start type changes (nothing is stopped now), so services that need this one keep running until
        // the next restart, and after that can't start.
        if (target == ServiceStart.Disabled && s.Dependents.Count > 0)
        {
            var names = string.Join(", ", s.Dependents.Take(3)) + (s.Dependents.Count > 3 ? $" and {s.Dependents.Count - 3} more" : "");
            description += s.Dependents.Count == 1
                ? $" 1 service that needs it won't be able to start after a restart: {names}."
                : $" {s.Dependents.Count} services that need it won't be able to start after a restart: {names}.";
        }
        return new PlanItem
        {
            Kind = ActionKind.ServiceSetStartType, FeatureId = featureId, Description = description,
            ServiceName = s.Name, ServiceStartType = target,
            NeedsAdmin = true, IsSystemChange = true, Reversible = true,
            // What the scan saw, so a simulation on a practice PC reports the practice start type, never this PC's
            // (Platform ServiceStartTypeHandler.CurrentStartArg).
            Args = s.Record.StartType is { } current ? new Dictionary<string, string> { ["currentStart"] = current.ToString() } : null,
        };
    }

    /// <summary>A row offering one change. Safe (and tickable by a preset) only for proven changes with nothing known to break.</summary>
    public static ScanItem Row(string featureId, ServiceInfo s, ServiceOption option, bool defaultSelected, string? reason = null)
    {
        var safe = s.Assessment.Verdict == ServiceVerdictKind.Proven && string.IsNullOrWhiteSpace(s.Entry?.BreaksIf);
        return new ScanItem
        {
            Id = $"{featureId}.{s.Name}",
            Title = s.DisplayName,
            Detail = $"{s.Name} · now {s.StartText} · {s.StateText}",
            Reason = reason ?? $"{option.Label}. {s.WhatItDoes}",
            Badge = safe ? Badge.Safe : Badge.Check,
            DefaultSelected = safe && defaultSelected,
            NeedsAdmin = true,
            CurrentState = s.StartText,
            OwnerApp = s.Facts.Publisher,
            Details = Details(s),
            Actions = new[] { SetStart(featureId, s, option.Target) },
        };
    }

    /// <summary>An information-only row (nothing to tick).</summary>
    public static ScanItem Info(string id, string title, string reason) => new()
    {
        Id = id, Title = title, Reason = reason, Badge = Badge.Safe, DisabledReason = reason,
    };

    public static IReadOnlyDictionary<string, string> Details(ServiceInfo s)
    {
        var d = new Dictionary<string, string>
        {
            ["Service name"] = s.Name,
            ["Starts"] = s.StartText,
            ["Now"] = s.StateText,
            ["Publisher"] = s.Facts.Publisher ?? "Unknown",
            ["Signature"] = s.Facts.SignatureText,
        };
        if (s.BinaryPath is { } p) d["File"] = p;
        if (s.Usage is { } u) d["Uses"] = u.Text;
        if (s.Record.DependsOn.Count > 0) d["Needs"] = string.Join(", ", s.Record.DependsOn.Select(x => x.TrimStart('+')));
        if (s.Dependents.Count > 0) d["Needed by"] = string.Join(", ", s.Dependents);
        if (s.Entry?.Source is { } src) d["Source"] = src;
        return d;
    }
}
