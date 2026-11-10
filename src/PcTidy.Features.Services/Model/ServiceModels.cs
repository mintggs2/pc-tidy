using PcTidy.Features.Services.Sources;

namespace PcTidy.Features.Services.Model;

/// <summary>Things worth knowing about a service, shown as tags and used by the "Flagged" filter.</summary>
[Flags]
public enum ServiceFlags
{
    None = 0,
    /// <summary>Its file isn't signed by Microsoft.</summary>
    NonMicrosoft = 1,
    /// <summary>Its file has no signature, or one Windows doesn't trust.</summary>
    Unsigned = 2,
    /// <summary>Its program file is missing (often left behind by an uninstalled app).</summary>
    Leftover = 4,
    /// <summary>Runs from a temporary, AppData or Downloads folder, where real services rarely live.</summary>
    SuspiciousPath = 8,
    /// <summary>A third-party service set to start with Windows that isn't running now.</summary>
    AutoStartNotRunning = 16,
}

/// <summary>The "safe to change?" verdict as the page shows it.</summary>
public enum ServiceVerdictKind
{
    /// <summary>Catalog: measured or documented benefit.</summary>
    Proven,
    /// <summary>Catalog: helps only in some setups (Expert mode).</summary>
    Situational,
    /// <summary>Catalog: popular, but doesn't help.</summary>
    Placebo,
    /// <summary>Catalog: changing it breaks something.</summary>
    Harmful,
    /// <summary>Critical, security or anti-cheat: never changed.</summary>
    Protected,
    /// <summary>Not in the catalog, from another company: Manual only.</summary>
    ThirdParty,
    /// <summary>Not in the catalog, part of Windows: left alone.</summary>
    Windows,
    /// <summary>A driver: listed, never changed.</summary>
    Driver,
}

/// <summary>One change PC Tidy can offer for a service.</summary>
/// <param name="Label">Button text ("Set to Manual", "Back to Windows default (Automatic)").</param>
/// <param name="Explanation">One plain sentence: what happens.</param>
public sealed record ServiceOption(ServiceStart Target, string Label, string Explanation, bool IsWindowsDefault, bool IsRecommended);

/// <summary>What PC Tidy may do with a service, and why.</summary>
/// <param name="Options">Offered changes (empty = shown only).</param>
/// <param name="NotOfferedReason">Why nothing is offered (null when something is).</param>
/// <param name="Reasons">The "safe to change?" reasons for the details pane, most important first.</param>
public sealed record ServiceAssessment(
    ServiceVerdictKind Verdict,
    IReadOnlyList<ServiceOption> Options,
    string? NotOfferedReason,
    IReadOnlyList<string> Reasons)
{
    public bool CanChange => Options.Count > 0;
    public ServiceOption? Recommended => Options.FirstOrDefault(o => o.IsRecommended) ?? Options.FirstOrDefault();
}

/// <summary>Memory and CPU of the process a running service lives in.</summary>
/// <param name="SharedWith">Other services in the same process (svchost groups); 0 = its own process.</param>
public sealed record ServiceUsage(long PrivateBytes, TimeSpan? CpuTime, int SharedWith)
{
    public string Text
    {
        get
        {
            var cpu = CpuTime is { } c ? $", {FormatCpu(c)} CPU time" : "";
            var memory = Sizes.Format(PrivateBytes) + " memory" + cpu;
            return SharedWith > 0 ? $"{memory} (shared with {SharedWith} other service{(SharedWith == 1 ? "" : "s")})" : memory;
        }
    }

    private static string FormatCpu(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min"
        : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min {t.Seconds} s" : $"{Math.Max(0, (int)t.TotalSeconds)} s";
}

/// <summary>A service with everything the page shows: raw record, file facts, catalog entry, usage, flags, verdict.</summary>
public sealed record ServiceInfo
{
    public required ServiceRecord Record { get; init; }
    /// <summary>The file that is the service (DLL for shared hosts, else the program; the .sys file for drivers).</summary>
    public string? BinaryPath { get; init; }
    public required FileFacts Facts { get; init; }
    public ServiceEntry? Entry { get; init; }
    public ServiceUsage? Usage { get; init; }
    public ServiceFlags Flags { get; init; }
    /// <summary>Installed services that need this one.</summary>
    public IReadOnlyList<string> Dependents { get; init; } = Array.Empty<string>();
    public required ServiceAssessment Assessment { get; init; }

    public string Name => Record.Name;
    public string DisplayName => string.IsNullOrWhiteSpace(Record.DisplayName) ? Record.Name : Record.DisplayName;
    public bool IsDriver => Record.IsDriver;
    public bool IsThirdParty => Flags.HasFlag(ServiceFlags.NonMicrosoft);
    public bool IsFlagged => (Flags & (ServiceFlags.Unsigned | ServiceFlags.Leftover | ServiceFlags.SuspiciousPath | ServiceFlags.AutoStartNotRunning)) != 0;

    /// <summary>Plain-English "what this does": the catalog's words, else the service's own description, else its file's.</summary>
    public string WhatItDoes => Entry?.WhatItDoes
        ?? Record.Description
        ?? (Facts.Description is { } d ? $"{d}." : null)
        ?? (IsDriver ? "A driver: it lets Windows talk to a device or adds a low-level feature." : "No description was given by its maker.");

    /// <summary>"Automatic (delayed start), starts when needed" etc.</summary>
    public string StartText
    {
        get
        {
            if (Record.StartType is not { } s) return "Unknown";
            var text = ServiceText.Start(s);
            return Record.TriggerStart && s is not ServiceStart.Disabled ? text + ", or when Windows needs it" : text;
        }
    }

    public string StateText => Record.State switch
    {
        ServiceRunState.Running => "Running",
        ServiceRunState.Stopped => "Stopped",
        ServiceRunState.Paused => "Paused",
        ServiceRunState.Starting => "Starting",
        ServiceRunState.Stopping => "Stopping",
        ServiceRunState.Unknown => "Unknown",
        _ => Record.State.ToString(),
    };
}

/// <summary>User-facing names for start types (one place, so the page, plan and repair say the same thing).</summary>
public static class ServiceText
{
    public static string Start(ServiceStart s) => s switch
    {
        ServiceStart.Automatic => "Automatic",
        ServiceStart.AutomaticDelayed => "Automatic (delayed start)",
        ServiceStart.Manual => "Manual",
        ServiceStart.Disabled => "Disabled",
        ServiceStart.Boot => "Boot",
        ServiceStart.System => "System",
        _ => s.ToString(),
    };
}
